using BDIMS.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace BDIMS.Services
{
    /// <summary>
    /// Fills certificate templates with dynamic data.
    /// Primary syntax is {{ TokenName }}. The legacy single-brace syntax
    /// ({RESIDENT}, {PURPOSE}, ...) used by older certificate formats is
    /// still resolved so previously saved templates keep working.
    /// Unknown tokens are deliberately left untouched so typos stay visible
    /// in the template editor preview instead of silently printing as blanks.
    /// </summary>
    public static class CertificateTemplateRenderer
    {
        private static readonly Regex TokenPattern =
            new(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

        private static readonly Regex LegacyTokenPattern =
            new(@"\{([A-Z][A-Z0-9_]*)\}", RegexOptions.Compiled);

        // Baked-in identity literals sitting alone in their own paragraph, e.g.
        // "<p>Province of Bohol</p>". Anchored to the full phrase ("Municipality of
        // Ubay", never bare "Ubay") so a paragraph that is not a letterhead line is
        // left alone. Optional inline <span>/<strong> wrappers are allowed because a
        // .docx import wraps nearly every run.
        private static readonly Regex LegacyProvincePattern = new(
            @"<p\b[^>]*>(?:<span\b[^>]*>)?(?:<strong\b[^>]*>)?"
            + @"Province\s+of\s+[^<]+?(?:</strong>)?(?:</span>)?</p>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LegacyMunicipalityPattern = new(
            @"<p\b[^>]*>(?:<span\b[^>]*>)?(?:<strong\b[^>]*>)?"
            + @"Municipality\s+of\s+[^<]+?(?:</strong>)?(?:</span>)?</p>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // The barangay name on its own line is followed by a comma in a letterhead
        // ("<p>Barangay Governor Boyles,</p>"); the comma is required so a sentence
        // beginning "Barangay ..." is not swallowed.
        private static readonly Regex LegacyBarangayPattern = new(
            @"<p\b[^>]*>(?:<span\b[^>]*>)?(?:<strong\b[^>]*>)?"
            + @"Barangay\s+[^<]+?(?:</strong>)?(?:</span>)?</p>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // The same identity inside a sentence, e.g. "a resident of Purok 2,
        // Governor Boyles, Ubay, Bohol" or "at Barangay Governor Boyles, Ubay,
        // Bohol". The optional leading "Barangay" is part of the phrase rather than
        // of the name, so it is captured separately and kept in the replacement -
        // without this, the greedy name group swallows the word "Barangay" and the
        // whole line fails to match, leaving a stale literal on a printed document.
        private static readonly Regex LegacyAddressPattern = new(
            @"\b(?:Barangay\s+)?(?<barangay>[A-Z][A-Za-z.]*(?:\s+[A-Z][A-Za-z.]*)*)"
            + @",\s*(?<municipality>[A-Z][A-Za-z.]*),\s*(?<province>[A-Z][A-Za-z.]*)\b",
            RegexOptions.Compiled);

        // The identity values that shipped in appsettings.json. Only text equal to one
        // of these is treated as a baked-in literal; anything else is left untouched so
        // a layout that legitimately names a different place is not corrupted.
        private static string[] _legacyIdentity = Array.Empty<string>();

        /// <summary>
        /// Supplies the identity values to recognise as baked-in literals. Called once
        /// at startup from the BDIMS:Barangay configuration section.
        /// </summary>
        public static void ConfigureLegacyIdentity(BarangayOptions? options)
        {
            _legacyIdentity = new[]
                {
                    options?.BarangayName,
                    options?.Municipality,
                    options?.Province
                }
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .ToArray();
        }

        /// <summary>
        /// Replaces known baked-in identity literals with their tokens so an
        /// already-saved layout follows a profile edit. The visible wording is
        /// preserved: only the identity value becomes a token, the surrounding
        /// "Province of" / "Barangay" phrasing is kept. The stored template is never
        /// rewritten - this runs at render time, per request.
        /// </summary>
        public static string NormalizeLegacyIdentity(string? templateHtml)
        {
            if (string.IsNullOrEmpty(templateHtml) || _legacyIdentity.Length == 0)
            {
                return templateHtml ?? string.Empty;
            }

            var result = LegacyAddressPattern.Replace(templateHtml, ReplaceAddress);

            result = ReplaceLetterhead(result, LegacyProvincePattern, "Province of ", "{{ Province }}");
            result = ReplaceLetterhead(result, LegacyMunicipalityPattern, "Municipality of ", "{{ Municipality }}");
            result = ReplaceLetterhead(result, LegacyBarangayPattern, "Barangay ", "{{ BarangayName }}");

            return PromoteSignatory(result);
        }

        /// <summary>
        /// True when the text is one of the configured legacy identity values.
        /// </summary>
        private static bool IsLegacy(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();

            return _legacyIdentity.Any(id => string.Equals(id, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        // Closing tags that can sit between the identity value and the end of the
        // matched paragraph. Ordered longest-first is not required because the
        // stripping loop repeats until nothing more can be removed.
        private static readonly string[] TrailingTags = { "</p>", "</strong>", "</span>" };

        // A letterhead line usually ends in punctuation: "Barangay Governor
        // Boyles,". The comparison in ReplaceLetterhead trims these off the end
        // before deciding whether the text is a recognised identity. Without this
        // the captured value is "Governor Boyles," which fails the exact-match
        // check, the literal survives, and a profile change never reaches the
        // printed document. Only trailing characters are affected, and the match
        // is returned with them intact, so the printed wording is unchanged.
        private static readonly char[] TrailingPunctuation = { ',', '.', ';', ':', ' ' };

        /// <summary>
        /// Swaps the literal identity value inside a whole-paragraph match for
        /// <paramref name="token"/>, keeping the paragraph's tags and prefix intact.
        /// The match is returned unchanged unless the value is a recognised literal,
        /// so an unrelated paragraph is never altered.
        /// </summary>
        private static string ReplaceLetterhead(string html, Regex pattern, string prefix, string token)
        {
            return pattern.Replace(html, match =>
            {
                var literal = match.Value;
                var start = literal.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);

                if (start < 0)
                {
                    return match.Value;
                }

                var valueStart = start + prefix.Length;
                var valueEnd = literal.Length;

                // Strip the paragraph's trailing inline and block closing tags before
                // comparing, so "Bohol</p>" and "Governor Boyles</strong></span></p>"
                // are both recognised as the bare value. This has to loop: the tags
                // nest, so a single pass over a fixed list leaves "</strong>" behind
                // whenever "</span>" is stripped first, and the value then fails to
                // match and the literal survives onto the printed document.
                bool trimmedTag;
                do
                {
                    trimmedTag = false;

                    foreach (var suffix in TrailingTags)
                    {
                        if (valueEnd - suffix.Length >= valueStart
                            && string.CompareOrdinal(literal, valueEnd - suffix.Length, suffix, 0, suffix.Length) == 0)
                        {
                            valueEnd -= suffix.Length;
                            trimmedTag = true;
                        }
                    }
                }
                while (trimmedTag);

                // Trim trailing punctuation and whitespace for the comparison only.
                // The replacement keeps literal[valueEnd..], so the printed line
                // still ends with its original comma.
                while (valueEnd > valueStart
                    && Array.IndexOf(TrailingPunctuation, literal[valueEnd - 1]) >= 0)
                {
                    valueEnd--;
                }

                if (!IsLegacy(literal[valueStart..valueEnd]))
                {
                    return match.Value;
                }

                return literal[..valueStart] + token + literal[valueEnd..];
            });
        }

        private static string ReplaceAddress(Match match)
        {
            // Require all three parts to be recognised legacy values. A partial
            // match is far more likely to be a person's own address than a
            // letterhead, so it is left alone.
            if (!IsLegacy(match.Groups["barangay"].Value)
                || !IsLegacy(match.Groups["municipality"].Value)
                || !IsLegacy(match.Groups["province"].Value))
            {
                return match.Value;
            }

            // The leading "Barangay" is part of the phrase, not the name, so it is
            // re-emitted. Without this, "at Barangay Governor Boyles, Ubay, Bohol"
            // would print as "at San Isidro, Tagbilaran, Bohol" and lose the word
            // that identifies the address as a barangay.
            var prefix = match.Value.StartsWith("Barangay ", StringComparison.OrdinalIgnoreCase)
                ? "Barangay "
                : string.Empty;

            return prefix + "{{ BarangayName }}, {{ Municipality }}, {{ Province }}";
        }

        // A signature line: a paragraph whose entire visible text is the configured
        // signatory's name (optionally carrying an honorific) or role. Matching the
        // whole paragraph is what keeps this from rewriting the name wherever else it
        // happens to appear in the body text.
        private static readonly Regex SignatureLinePattern = new(
            @"<p\b[^>]*>(?<body>(?:<[^>]+>|[^<])*?)</p>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string[] _legacySignatoryName = Array.Empty<string>();
        private static string[] _legacySignatoryRole = Array.Empty<string>();

        /// <summary>
        /// Records the signatory values that shipped in configuration so a template
        /// which hardcodes them (typically from a .docx import) starts following the
        /// editable profile.
        /// </summary>
        public static void ConfigureLegacySignatory(string? signatoryName, string? signatoryRole)
        {
            _legacySignatoryName = Flatten(signatoryName);
            _legacySignatoryRole = Flatten(signatoryRole);
        }

        private static string[] Flatten(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : new[] { value.Trim() };
        }

        private static string PromoteSignatory(string html)
        {
            if (_legacySignatoryName.Length == 0 && _legacySignatoryRole.Length == 0)
            {
                return html;
            }

            return SignatureLinePattern.Replace(html, match =>
            {
                // Compare the paragraph's visible text with tags removed, so the
                // styling differences in an imported layout do not hide the match.
                var text = Regex.Replace(match.Groups["body"].Value, "<[^>]+>", string.Empty);
                text = System.Net.WebUtility.HtmlDecode(text);
                text = text.Replace('\u00a0', ' ').Trim();

                foreach (var name in _legacySignatoryName)
                {
                    // Tolerate an honorific the author typed on the line, e.g.
                    // "HON. CELES P. PONDAVILLA" for a configured "Celes P. Pondavilla".
                    if (text.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || text.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return ReplaceParagraphBody(match, "{{ BarangayCaptain }}");
                    }
                }

                foreach (var role in _legacySignatoryRole)
                {
                    if (text.Equals(role, StringComparison.OrdinalIgnoreCase))
                    {
                        return ReplaceParagraphBody(match, "{{ SignatoryRole }}");
                    }
                }

                return match.Value;
            });
        }

        /// <summary>
        /// Swaps a paragraph's inner content for <paramref name="token"/>, keeping the
        /// opening tag. Inline formatting the author applied is deliberately dropped
        /// for this one substitution: a token expands to plain text, so the
        /// surrounding &lt;u&gt;/&lt;strong&gt; would otherwise be left dangling.
        /// </summary>
        private static string ReplaceParagraphBody(Match match, string token)
        {
            var openTag = match.Value.Substring(0, match.Value.IndexOf('>') + 1);

            return openTag + token + "</p>";
        }

        private static readonly Dictionary<string, Func<CertificateTemplateData, string>> Tokens =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["ResidentName"] = d => Or(
                    CertificateTemplateFormatter.ResidentName(d.ResidentName),
                    "[ Resident Name ]"),

                ["ResidentNameRaw"] = d => Or(
                    (d.ResidentName ?? "").Trim(),
                    "[ Resident Name ]"),

                ["ResidentAge"] = d => Or(
                    CertificateTemplateFormatter.Age(d.ResidentAge),
                    "[ Age ]"),

                ["Purok"] = d => Or(d.Purok, "[ Purok ]"),

                ["Address"] = d => Or(
                    string.IsNullOrWhiteSpace(d.Address) ? d.Purok : d.Address,
                    "[ Address ]"),

                ["Purpose"] = d => Or(d.Purpose, "[ Purpose ]"),

                ["IssueDate"] = d => CertificateTemplateFormatter.IssueDate(d.IssueDate),

                ["IssueDateShort"] = d => CertificateTemplateFormatter.IssueDateShort(d.IssueDate),

                ["BarangayCaptain"] = d => Or(
                    CertificateTemplateFormatter.ResidentName(d.BarangayCaptain),
                    "[ Punong Barangay ]"),

                ["BarangayCaptainRaw"] = d => Or(d.BarangayCaptain, "[ Punong Barangay ]"),

                ["SignatoryRole"] = d => Or(d.SignatoryRole, "Punong Barangay"),

                ["BarangayName"] = d => Or(
                    CertificateTemplateFormatter.ResidentName(d.BarangayName),
                    "[ Barangay ]"),

                ["Municipality"] = d => Or(d.Municipality, "[ Municipality ]"),

                ["Province"] = d => Or(d.Province, "[ Province ]"),

                ["OrNumber"] = d => Or(d.OrNumber, "[ O.R. No. ]"),

                ["ControlNumber"] = d => Or(d.CertificateId, "[ Control No. ]"),

                ["AmountPaid"] = d => d.AmountPaid.ToString("N2", CultureInfo.InvariantCulture),

                ["CertificateId"] = d => Or(d.CertificateId, "[ Control No. ]"),

                ["CertificateType"] = d => Or(d.CertificateType, "[ Certificate Type ]")
            };

        private static readonly Dictionary<string, string> LegacyAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["RESIDENT"] = "ResidentName",
                ["PURPOSE"] = "Purpose",
                ["ISSUED"] = "IssueDate",
                ["ORNUMBER"] = "OrNumber",
                ["ORNO"] = "OrNumber",
                ["AMOUNT"] = "AmountPaid",
                ["ID"] = "CertificateId",
                ["TITLE"] = "CertificateType"
            };

        public static IReadOnlyList<CertificateTemplateToken> SupportedTokens { get; } =
            new List<CertificateTemplateToken>
            {
                new CertificateTemplateToken { Name = "ResidentName",        Description = "Resident full name, uppercased",            Sample = "MR. DEN MARK B. ETORMA" },
                new CertificateTemplateToken { Name = "ResidentNameRaw",     Description = "Resident full name as recorded",           Sample = "Den Mark B. Etorma" },
                new CertificateTemplateToken { Name = "ResidentAge",         Description = "Resident age in words",                     Sample = "17 years old" },
                new CertificateTemplateToken { Name = "Purok",               Description = "Resident purok of residence",               Sample = "Purok 2" },
                new CertificateTemplateToken { Name = "Address",             Description = "Resident street address",                   Sample = "Purok 2, Governor Boyles, Ubay, Bohol" },
                new CertificateTemplateToken { Name = "Purpose",             Description = "Stated purpose of the request",             Sample = "ENROLLMENT IN TAGBILARAN CITY COLLEGE (TCC)" },
                new CertificateTemplateToken { Name = "IssueDate",           Description = "Date issued, formal wording",               Sample = "23rd day of JULY, 2024" },
                new CertificateTemplateToken { Name = "IssueDateShort",      Description = "Date issued, short wording",                Sample = "23 July 2024" },
                new CertificateTemplateToken { Name = "BarangayCaptain",     Description = "Punong Barangay name, uppercased",          Sample = "HON. CELES P. PONDAVILLA" },
                new CertificateTemplateToken { Name = "BarangayCaptainRaw",  Description = "Punong Barangay name as recorded",          Sample = "Celes P. Pondavilla" },
                new CertificateTemplateToken { Name = "SignatoryRole",       Description = "Signatory title under the signature line",  Sample = "Punong Barangay" },
                new CertificateTemplateToken { Name = "BarangayName",        Description = "Barangay name",                            Sample = "GOVERNOR BOYLES" },
                new CertificateTemplateToken { Name = "Municipality",        Description = "Municipality name",                        Sample = "Ubay" },
                new CertificateTemplateToken { Name = "Province",            Description = "Province name",                            Sample = "Bohol" },
                new CertificateTemplateToken { Name = "OrNumber",            Description = "Official receipt number",                  Sample = "OR-2026-0900" },
                new CertificateTemplateToken { Name = "ControlNumber",       Description = "Certificate tracking / control number",     Sample = "CERT-042" },
                new CertificateTemplateToken { Name = "AmountPaid",          Description = "Fee paid, two decimal places",             Sample = "50.00" },
                new CertificateTemplateToken { Name = "CertificateId",       Description = "Certificate control number",               Sample = "CERT-042" },
                new CertificateTemplateToken { Name = "CertificateType",     Description = "Name of the certificate type",             Sample = "Barangay Indigency" }
            };

        public static string Render(string? templateHtml, CertificateTemplateData? data)
        {
            var context = data ?? new CertificateTemplateData();

            // Promote any baked-in identity literal ("Province of Bohol") to its token
            // first, so a layout that predates the editable profile still follows a
            // profile change. The stored template on disk is never modified.
            templateHtml = NormalizeLegacyIdentity(templateHtml);

            if (string.IsNullOrEmpty(templateHtml))
            {
                return string.Empty;
            }

            // An unrecognised token is left in place rather than replaced with an empty
            // string. Encode(null) produced an empty string, so a typo such as
            // {{ ResidenName }} printed a certificate with a blank resident name and no
            // error anywhere - the worst possible failure for a legal document, and the
            // opposite of what the class contract promises. Known tokens always resolve
            // to a non-null value (empty string when the data is genuinely absent), so a
            // null result here means the token name is not in the table above.
            var result = TokenPattern.Replace(templateHtml, match =>
            {
                var value = Resolve(match.Groups[1].Value, context);

                return value == null ? match.Value : Encode(value);
            });

            result = LegacyTokenPattern.Replace(result, match =>
            {
                var value = ResolveLegacy(match.Groups[1].Value, context);

                return value == null ? match.Value : Encode(value);
            });

            return result;
        }

        public static IEnumerable<string> ExtractTokens(string? templateHtml)
        {
            if (string.IsNullOrWhiteSpace(templateHtml))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in TokenPattern.Matches(templateHtml))
            {
                var name = match.Groups[1].Value;
                if (seen.Add(name))
                {
                    yield return name;
                }
            }

            foreach (Match match in LegacyTokenPattern.Matches(templateHtml))
            {
                var name = match.Groups[1].Value;
                if (LegacyAliases.ContainsKey(name) && seen.Add(LegacyAliases[name]))
                {
                    yield return LegacyAliases[name];
                }
            }
        }

        public static IEnumerable<string> UnknownTokens(string? templateHtml)
        {
            var known = new HashSet<string>(
                SupportedTokens.Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            return ExtractTokens(templateHtml).Where(t => !known.Contains(t));
        }

        private static string? Resolve(string token, CertificateTemplateData data)
        {
            return Tokens.TryGetValue(token, out var formatter) ? formatter(data) : null;
        }

        private static string? ResolveLegacy(string token, CertificateTemplateData data)
        {
            if (!LegacyAliases.TryGetValue(token, out var target))
            {
                return null;
            }

            return Tokens.TryGetValue(target, out var formatter) ? formatter(data) : null;
        }

        private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        private static string Or(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    public static class CertificateTemplateFormatter
    {
        public static string ResidentName(string? name)
        {
            var trimmed = (name ?? string.Empty).Trim();
            return trimmed.Length == 0 ? string.Empty : trimmed.ToUpperInvariant();
        }

        public static string Age(int? age)
        {
            if (age is null)
            {
                return string.Empty;
            }

            return age.Value == 1 ? "1 year old" : age.Value + " years old";
        }

        public static string IssueDate(DateTime date) =>
            Ordinal(date.Day) + " day of " + MonthName(date) + ", " + date.Year.ToString(CultureInfo.InvariantCulture);

        public static string IssueDateShort(DateTime date) =>
            date.Day.ToString(CultureInfo.InvariantCulture) + " " + MonthName(date) + " " + date.Year.ToString(CultureInfo.InvariantCulture);

        public static string Ordinal(int day)
        {
            var suffix = (day % 100) switch
            {
                11 or 12 or 13 => "th",
                _ => (day % 10) switch
                {
                    1 => "st",
                    2 => "nd",
                    3 => "rd",
                    _ => "th"
                }
            };

            return day.ToString(CultureInfo.InvariantCulture) + suffix;
        }

        private static string MonthName(DateTime date) =>
            date.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
    }
}
