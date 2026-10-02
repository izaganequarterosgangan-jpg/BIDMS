using System.Collections.Generic;
using System.Linq;

namespace BDIMS.Models
{
    public class CertificateTemplate
    {
        public int Id { get; set; }

        /// <summary>
        /// The certificate type this layout belongs to, e.g. "Barangay Indigency".
        /// </summary>
        public string CertificateName { get; set; } = "";

        /// <summary>
        /// Alias for <see cref="CertificateName"/> kept for the fee-table code paths
        /// that refer to certificate types. Both properties read and write one field.
        /// </summary>
        public string CertificateType
        {
            get => CertificateName;
            set => CertificateName = value;
        }

        public decimal Fee { get; set; }

        /// <summary>
        /// The active layout structure: HTML, inline styling, images/logos and headers,
        /// including any {{ Token }} placeholders.
        /// </summary>
        public string TemplateHtml { get; set; } = "";

        /// <summary>
        /// Where the current layout came from, e.g. "Template Editor" or "Imported from file.docx".
        /// </summary>
        public string Source { get; set; } = "Template Editor";

        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Detached copy. CertificateTemplateStore keeps a cached list of live instances
        /// and hands them out from its read methods; without a copy, a caller mutating a
        /// template it fetched would change the cache without persisting to disk, so the
        /// change silently vanished on the next load.
        /// </summary>
        public CertificateTemplate Clone() => new CertificateTemplate
        {
            Id = Id,
            CertificateName = CertificateName,
            Fee = Fee,
            TemplateHtml = TemplateHtml,
            Source = Source,
            UpdatedAt = UpdatedAt
        };
    }

    public class CertificateTemplateToken
    {
        public string Name { get; set; } = "";

        public string Description { get; set; } = "";

        public string Sample { get; set; } = "";

        public string Placeholder => "{{ " + Name + " }}";
    }

    public class CertificateTemplateData
    {
        public string ResidentName { get; set; } = "";

        public int? ResidentAge { get; set; }

        public string Purok { get; set; } = "";

        /// <summary>
        /// Street address. The barangay roster only records a purok, so this is derived.
        /// </summary>
        public string Address { get; set; } = "";

        public string Purpose { get; set; } = "";

        public System.DateTime IssueDate { get; set; } = System.DateTime.Today;

        public string BarangayCaptain { get; set; } = "";

        public string SignatoryRole { get; set; } = "Punong Barangay";

        public string BarangayName { get; set; } = "";

        public string Municipality { get; set; } = "";

        public string Province { get; set; } = "";

        public string OrNumber { get; set; } = "";

        public decimal AmountPaid { get; set; }

        public string CertificateId { get; set; } = "";

        public string CertificateType { get; set; } = "";
    }

    public class CertificateTemplateEditorViewModel
    {
        public CertificateTemplate Template { get; set; } = new CertificateTemplate();

        public List<CertificateTemplate> Templates { get; set; } = new List<CertificateTemplate>();

        public List<string> CertificateTypes { get; set; } = new List<string>();

        public List<CertificateTemplateToken> Tokens { get; set; } = new List<CertificateTemplateToken>();

        public List<ResidentModel> Residents { get; set; } = new List<ResidentModel>();

        public string PreviewHtml { get; set; } = "";

        public string PreviewResidentId { get; set; } = "";

        public string PreviewPurpose { get; set; } = "";

        public string DefaultHtml { get; set; } = "";

        public string[] TokensInUse => CertificateTemplateHelpers.TokensInUse(Template?.TemplateHtml);
    }

    public static class CertificateTemplateHelpers
    {
        public static string[] TokensInUse(string? templateHtml)
        {
            if (string.IsNullOrWhiteSpace(templateHtml))
            {
                return System.Array.Empty<string>();
            }

            return BDIMS.Services.CertificateTemplateRenderer.ExtractTokens(templateHtml).ToArray();
        }
    }
}
