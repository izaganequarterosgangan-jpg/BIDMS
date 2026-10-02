using System;
using System.Collections.Generic;
using System.Globalization;

namespace BDIMS.Models
{
    public class CertificateModel
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Resident { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string OrNumber { get; set; } = string.Empty;
        public string AmountPaid { get; set; } = string.Empty;
        public string Issued { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public string TemplateHtml { get; set; } = string.Empty;

        // Snapshot of the resident details the certificate layout needs. Walk-ins are
        // not necessarily on the roster, so these are captured at issuance rather than
        // looked up when the document is printed.
        public string ResidentAge { get; set; } = string.Empty;
        public string Purok { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;

        /// <summary>Date the certificate was issued. Issued is the display string kept
        /// for the list view; this is the value the date placeholders render from.</summary>
        public DateTime? IssueDate { get; set; }

        /// <summary>
        /// The issue date in the unambiguous form the renderer parses. Falls back to
        /// the legacy Issued string so records seeded before IssueDate was captured
        /// still render a date instead of today's.
        /// </summary>
        public string IssuedDateDisplay =>
            IssueDate?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) ?? Issued;
    }

    public class CertificatesViewModel
    {
        public List<CertificateModel> Certificates { get; set; } = new List<CertificateModel>();
        public List<CertificateRule> CertificateTypes { get; set; } = new List<CertificateRule>();
        public int IssuedThisWeekCount { get; set; }
        public int AwaitingPickupCount { get; set; }
        public int PrintedCount { get; set; }
        public string SearchQuery { get; set; } = string.Empty;
        // Bridge prefill from Documents -> Certificates issue modal
        public bool OpenIssueModal { get; set; } = false;
        public string PrefillResident { get; set; } = string.Empty;
        public string PrefillTitle { get; set; } = string.Empty;
        public string PrefillPurpose { get; set; } = string.Empty;

        // Residents offered in the issue modal so age and purok can auto-fill
        public List<ResidentModel> Residents { get; set; } = new List<ResidentModel>();

        // Purok options for the issue modal, taken from the roster so the dropdown
        // never offers a purok that has no residents.
        public List<string> Puroks { get; set; } = new List<string>();
    }
}
