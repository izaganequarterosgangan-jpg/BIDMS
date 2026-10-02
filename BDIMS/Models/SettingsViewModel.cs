using System;
using System.Collections.Generic;
using System.Linq;

namespace BDIMS.Models
{
    public class SettingsViewModel
    {
        // Barangay Information
        public string BarangayName { get; set; } = "Governor Boyles";
        public string Municipality { get; set; } = "Ubay";
        public string Province { get; set; } = "Bohol";
        public string ContactEmail { get; set; } = "contact@govboyles-ubay.gov.ph";
        public string ContactPhone { get; set; } = "+63 912 345 6789";
        public string OfficeHours { get; set; } = "8:00 AM - 5:00 PM (Mon-Fri)";
        public string SignatoryName { get; set; } = "Hon. Celes P. Pondavilla";
        public string SignatoryRole { get; set; } = "Punong Barangay";

        // System & Security
        public bool AutoApproveLowRisk { get; set; } = true;
        public bool EmailNotifications { get; set; } = true;
        public bool SmsNotifications { get; set; } = false;
        public string TwoFactorAuth { get; set; } = "Required";
        public string SessionTimeout { get; set; } = "30";

        // Current tab
        public string ActiveTab { get; set; } = "general";

        // Printable certificate layouts maintained in the Template Editor
        public List<CertificateTemplate> CertificateTemplates { get; set; } =
            new List<CertificateTemplate>();

        // Placeholder palette shown alongside the Template Editor entry point
        public List<CertificateTemplateToken> TemplateTokens { get; set; } =
            new List<CertificateTemplateToken>();

        /// <summary>
        /// The saved layout that renders a fee-table row, or null when the row has no
        /// template of its own and falls back to a generated layout.
        ///
        /// The fee table and the template store use different labels for the same
        /// certificate ("Certificate of Indigency (Medical / ...)" vs the seeded
        /// "Barangay Indigency" layout), so the same exact/alias/prefix rules the
        /// renderer applies have to be applied here too, otherwise the fee table
        /// would report a template as missing for a row that in fact has one.
        /// </summary>
        public CertificateTemplate? TemplateFor(string? certificateType)
        {
            if (string.IsNullOrWhiteSpace(certificateType))
            {
                return null;
            }

            var type = certificateType.Trim();

            var exact = CertificateTemplates.FirstOrDefault(t =>
                t.CertificateName.Equals(type, StringComparison.OrdinalIgnoreCase));

            if (exact != null)
            {
                return exact;
            }

            var alias = CertificateTemplateAliases.TryGetValue(type, out var aliasName)
                ? CertificateTemplates.FirstOrDefault(t =>
                    t.CertificateName.Equals(aliasName, StringComparison.OrdinalIgnoreCase))
                : null;

            if (alias != null)
            {
                return alias;
            }

            return CertificateTemplates.FirstOrDefault(t =>
                type.StartsWith(t.CertificateName, StringComparison.OrdinalIgnoreCase));
        }

        // Fee-table labels mapped to the template names administrators use in the
        // Template Editor. Kept in step with the renderer's alias table.
        private static readonly Dictionary<string, string> CertificateTemplateAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Certificate of Indigency (Medical / Educational / Legal Aid)"] = "Barangay Indigency"
            };

        // Certificate Rules
        public List<CertificateRule> Certificates { get; set; } =
            new List<CertificateRule>
            {
                new CertificateRule
                {
                    Id = 1,
                    Type = "Barangay Clearance",
                    Fee = 50,
                    RequiresResidency = true,
                    Status = "Active"
                },

                new CertificateRule
                {
                    Id = 2,
                    Type = "Certificate of Indigency",
                    Fee = 0,
                    RequiresResidency = true,
                    Status = "Active"
                },

                new CertificateRule
                {
                    Id = 3,
                    Type = "Certificate of Residency",
                    Fee = 30,
                    RequiresResidency = true,
                    Status = "Active"
                },

                new CertificateRule
                {
                    Id = 4,
                    Type = "Business Permit Clearance",
                    Fee = 150,
                    RequiresResidency = false,
                    Status = "Active"
                }
            };
    }
}