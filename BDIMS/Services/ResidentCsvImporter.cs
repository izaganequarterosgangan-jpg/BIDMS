using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BDIMS.Models;

namespace BDIMS.Services
{
    /// <summary>
    /// Parses and validates the bulk resident CSV import.
    ///
    /// Deliberately free of MVC and EF dependencies so the parsing rules can be read
    /// and changed without touching the controller. The controller only reads the
    /// upload, calls in here, and persists the rows that came back valid.
    /// </summary>
    public static class ResidentCsvImporter
    {
        /// <summary>
        /// Columns the template is published with. The parser is tolerant about spelling
        /// ("Full Name", "full_name" and "fullname" all map to the same column) but these
        /// are the names written into the downloadable template.
        /// </summary>
        public static readonly string[] TemplateHeaders =
        {
            "FullName", "Age", "Sex", "Purok", "Contact", "VoterStatus", "Status"
        };

        /// <summary>
        /// Hard cap on accepted data rows. A registry import is a data-entry convenience,
        /// not a bulk load, and an unbounded read would let one request tie up memory and
        /// the database.
        /// </summary>
        public const int MaxRows = 5000;

        /// <summary>Hard cap on upload size (5 MB), checked before the file is read.</summary>
        public const long MaxFileBytes = 5L * 1024 * 1024;

        /// <summary>Outcome of parsing a single data row.</summary>
        public sealed class RowResult
        {
            /// <summary>Populated only when the row was valid.</summary>
            public ResidentModel? Resident { get; init; }

            /// <summary>Human-readable reason the row was skipped; null when valid.</summary>
            public string? Error { get; init; }

            /// <summary>1-based line number in the uploaded file, for the error report.</summary>
            public int LineNumber { get; init; }
        }

        /// <summary>Result of parsing a whole file.</summary>
        public sealed class ParseResult
        {
            public List<RowResult> Rows { get; } = new();

            /// <summary>Fatal problem that stopped parsing (bad headers, unreadable file).</summary>
            public string? FatalError { get; set; }

            /// <summary>Template columns the file did not provide, if any were missing.</summary>
            public List<string> MissingHeaders { get; } = new();

            public IEnumerable<RowResult> Valid => Rows.Where(r => r.Resident != null);
            public IEnumerable<RowResult> Skipped => Rows.Where(r => r.Resident == null);
        }

        /// <summary>
        /// Reduces a header cell to a comparison key: lower-cased with everything except
        /// letters and digits removed, so "Full Name", "full_name" and "FullName" all
        /// collapse to the same token and a re-ordered or hand-edited file still maps.
        /// </summary>
        private static string NormalizeHeader(string? header) =>
            new string((header ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

        /// <summary>
        /// Splits CSV text into rows of fields.
        ///
        /// This is a full RFC 4180 reader rather than a naive <c>line.Split(',')</c>:
        /// a quoted field may legitimately contain the delimiter, a line break, and a
        /// doubled quote (""), and spreadsheet exports produce all three routinely. Naive
        /// splitting would silently shift a row's columns and import a phone number into
        /// the Purok field.
        /// </summary>
        public static List<List<string>> SplitRecords(string content)
        {
            var records = new List<List<string>>();
            var fields = new List<string>();
            var field = new StringBuilder();

            var inQuotes = false;
            var index = 0;

            // A UTF-8 byte-order mark is invisible but would otherwise become part of
            // the first header name and break the column lookup.
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                index = 1;
            }

            void EndField()
            {
                fields.Add(field.ToString());
                field.Clear();
            }

            void EndRecord()
            {
                EndField();
                records.Add(fields);
                fields = new List<string>();
            }

            for (; index < content.Length; index++)
            {
                var c = content[index];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // A doubled quote inside a quoted field is one literal quote;
                        // otherwise the quote closes the field.
                        if (index + 1 < content.Length && content[index + 1] == '"')
                        {
                            field.Append('"');
                            index++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        // Kept verbatim, so a quoted multi-line value survives intact.
                        field.Append(c);
                    }

                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;

                    case ',':
                        EndField();
                        break;

                    case '\r':
                        // Swallowed; the following LF (if any) terminates the record.
                        break;

                    case '\n':
                        EndRecord();
                        break;

                    default:
                        field.Append(c);
                        break;
                }
            }

            // Flush a final record that was not newline-terminated.
            if (field.Length > 0 || fields.Count > 0)
            {
                EndRecord();
            }

            // Drop blank lines produced by trailing newlines or empty spacer rows.
            return records
                .Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v)))
                .ToList();
        }

        /// <summary>
        /// Parses <paramref name="content"/> into validated residents, collecting a
        /// per-row reason for anything skipped.
        /// </summary>
        /// <param name="content">Full CSV text, including the header row.</param>
        /// <param name="existingNames">
        /// Names already in the registry, trimmed and upper-cased. Rows repeating one are
        /// skipped, so importing the same file twice cannot quietly create a second
        /// record for the same person.
        /// </param>
        public static ParseResult Parse(string? content, ISet<string>? existingNames = null)
        {
            var result = new ParseResult();

            if (string.IsNullOrWhiteSpace(content))
            {
                result.FatalError = "The selected file is empty.";
                return result;
            }

            var records = SplitRecords(content);

            if (records.Count == 0)
            {
                result.FatalError = "The selected file does not contain any rows.";
                return result;
            }

            // ---- Header row --------------------------------------------------
            var headerRecord = records[0];
            var indexByHeader = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < headerRecord.Count; i++)
            {
                var key = NormalizeHeader(headerRecord[i]);
                if (key.Length > 0 && !indexByHeader.ContainsKey(key))
                {
                    indexByHeader[key] = i;
                }
            }

            // FullName is the only genuinely required column: without it there is nothing
            // to store. Every other column has a documented default.
            if (!indexByHeader.ContainsKey(NormalizeHeader("FullName")))
            {
                result.FatalError =
                    "The CSV header row is missing the FullName column. " +
                    "Download the template to get the expected headers.";
                return result;
            }

            // Any template column the file omitted is reported back, so a user who left
            // Status out learns why their rows defaulted rather than silently getting a
            // different result from the template.
            foreach (var header in TemplateHeaders)
            {
                if (!indexByHeader.ContainsKey(NormalizeHeader(header)))
                {
                    result.MissingHeaders.Add(header);
                }
            }

            // ---- Data rows ---------------------------------------------------
            var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var since = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);

            for (var r = 1; r < records.Count; r++)
            {
                // Line numbers are 1-based and count the header, so they match what a
                // spreadsheet shows the user.
                var lineNumber = r + 1;

                if (result.Rows.Count >= MaxRows)
                {
                    result.FatalError =
                        $"Only the first {MaxRows:N0} rows were read. " +
                        "Split the file into smaller batches and import them separately.";
                    break;
                }

                var record = records[r];

                string Cell(string header) =>
                    indexByHeader.TryGetValue(NormalizeHeader(header), out var idx) &&
                    idx < record.Count
                        ? (record[idx] ?? string.Empty).Trim()
                        : string.Empty;

                var name = Cell("FullName");

                if (name.Length == 0)
                {
                    result.Rows.Add(new RowResult
                    {
                        LineNumber = lineNumber,
                        Error = "Full name is required."
                    });
                    continue;
                }

                // Duplicate guard: against the registry, and against earlier rows in the
                // same file.
                var nameKey = name.ToUpperInvariant();

                if (existingNames != null && existingNames.Contains(nameKey))
                {
                    result.Rows.Add(new RowResult
                    {
                        LineNumber = lineNumber,
                        Error = $"A resident named \"{name}\" already exists."
                    });
                    continue;
                }

                if (!seenInFile.Add(nameKey))
                {
                    result.Rows.Add(new RowResult
                    {
                        LineNumber = lineNumber,
                        Error = $"Duplicate of \"{name}\" within this file."
                    });
                    continue;
                }

                // ---- Age -------------------------------------------------------
                // A blank age defaults to 0. A present but unparseable or out-of-range
                // value is a data error and skips the row rather than being coerced.
                var ageText = Cell("Age");
                var age = 0;
                if (ageText.Length > 0 &&
                    (!int.TryParse(ageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out age) ||
                     age < 0 || age > 120))
                {
                    result.Rows.Add(new RowResult
                    {
                        LineNumber = lineNumber,
                        Error = $"Age \"{ageText}\" is not valid. Use a number between 0 and 120."
                    });
                    continue;
                }

                // ---- Sex -------------------------------------------------------
                // Defaults match the single-entry Add Resident form, so a CSV row and a
                // manually typed row produce identical stored data.
                var sexText = Cell("Sex");
                var sex = sexText.ToUpperInvariant() switch
                {
                    "M" or "MALE" => "Male",
                    "F" or "FEMALE" => "Female",
                    "" => "Female",
                    _ => null
                };

                if (sex == null)
                {
                    result.Rows.Add(new RowResult
                    {
                        LineNumber = lineNumber,
                        Error = $"Sex \"{sexText}\" is not valid. Use Male or Female."
                    });
                    continue;
                }

                // ---- Status ----------------------------------------------------
                var statusText = Cell("Status");
                var status = statusText.ToUpperInvariant() switch
                {
                    "ACTIVE" => "Active",
                    "INACTIVE" => "Inactive",
                    "" => "Active",
                    _ => null
                };

                if (status == null)
                {
                    result.Rows.Add(new RowResult
                    {
                        LineNumber = lineNumber,
                        Error = $"Status \"{statusText}\" is not valid. Use Active or Inactive."
                    });
                    continue;
                }

                // ---- Voter status ------------------------------------------------
                var voterText = Cell("VoterStatus");
                var isVoter = voterText.ToUpperInvariant() switch
                {
                    "REGISTERED" or "REGISTERED VOTER" or "VOTER" or "YES" or "Y" or "TRUE" or "1" => true,
                    "NON-VOTER" or "NON VOTER" or "NONVOTER" or "NO" or "N" or "FALSE" or "0" or "" => false,
                    _ => (bool?)null
                };

                if (isVoter == null)
                {
                    result.Rows.Add(new RowResult
                    {
                        LineNumber = lineNumber,
                        Error = $"VoterStatus \"{voterText}\" is not valid. " +
                                "Use \"Registered Voter\" or \"Non-Voter\"."
                    });
                    continue;
                }

                var purok = Cell("Purok");
                if (purok.Length == 0) purok = "Purok 1";

                var contact = Cell("Contact");
                if (contact.Length == 0) contact = "N/A";

                // Mirrors the initials rule used by the Add Resident form: the first
                // letter of each of the first two words.
                var initials = string.Concat(
                    name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Take(2)
                        .Select(w => char.ToUpperInvariant(w[0])));

                if (initials.Length == 0) initials = "NR";

                result.Rows.Add(new RowResult
                {
                    LineNumber = lineNumber,
                    Resident = new ResidentModel
                    {
                        Name = name,
                        Since = since,
                        Initials = initials,
                        AgeSex = $"{age} / {(sex == "Male" ? "M" : "F")}",
                        Sex = sex,
                        Purok = purok,
                        Contact = contact,
                        IsVoter = isVoter.Value,
                        Status = status
                    }
                });
            }

            return result;
        }

        /// <summary>
        /// Builds the downloadable template: the header row plus a few worked examples, so
        /// the accepted values for Sex, VoterStatus and Status are obvious rather than
        /// something the user has to guess or discover through rejected rows.
        /// </summary>
        public static byte[] BuildTemplateCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", TemplateHeaders));

            // The first example deliberately quotes a name containing a comma and quotes a
            // multi-word voter value, so the file doubles as a demonstration of the
            // quoting rules the parser accepts.
            sb.AppendLine("\"Dela Cruz, Juan\",35,Male,\"Purok 1\",09171234567,\"Registered Voter\",Active");
            sb.AppendLine("Maria Santos,28,Female,\"Purok 2\",09181234567,Non-Voter,Active");
            sb.AppendLine("Jose Rizal,65,Male,\"Purok 3\",09191234567,\"Registered Voter\",Inactive");

            // UTF-8 with BOM so Excel opens the file with the correct encoding on
            // double-click, matching what the on-page Export CSV produces.
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
        }
    }
}