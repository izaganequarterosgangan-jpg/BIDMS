namespace BDIMS.Models
{
    public class DashboardViewModel
    {
        public string ActiveTab { get; set; } = "Dashboard";
        public string SearchQuery { get; set; } = string.Empty;
        public int TotalResidents { get; set; } = 0;
        public int PendingRequestsCount { get; set; } = 0;
        public int CertificatesIssuedCount { get; set; } = 0;
        public int BlotterCasesCount { get; set; } = 0;
        public List<DocumentRequestViewModel> Requests { get; set; } = new();
    }
    public class DocumentRequestViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Resident { get; set; } = string.Empty;
        public string Avatar { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
