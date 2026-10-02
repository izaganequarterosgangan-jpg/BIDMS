using System.Collections.Generic;

namespace BDIMS.Models
{
    public class DocumentsViewModel
    {
        public List<DocumentRequestModel> Requests { get; set; } = new List<DocumentRequestModel>();
        public int TotalRequests => Requests.Count;
        public int PendingCount { get; set; }
        public int InReviewCount { get; set; }
        public int ProcessingCount { get; set; }
        public int ApprovedCount { get; set; }
        public int RejectedCount { get; set; }
        public int CompletedCount { get; set; }
        public string SearchQuery { get; set; } = string.Empty;
        public List<CertificateRule> CertificateTypes { get; set; } = new List<CertificateRule>();
        public List<string> GeneralDocumentTypes { get; set; } = new List<string>();
        public List<ResidentModel> Residents { get; set; } = new List<ResidentModel>();
        // UI helpers
        public bool OpenNewRequest { get; set; } = false;
        public string PrefillTitle { get; set; } = string.Empty;
        public string PrefillResident { get; set; } = string.Empty;
        public string PrefillPurpose { get; set; } = string.Empty;
    }
}