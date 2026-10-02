using System.Collections.Generic;

namespace BDIMS.Models
{
    public class ReportModel
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class ReportsViewModel
    {
        public List<ReportModel> Reports { get; set; } = new List<ReportModel>();
        public int PreparedCount { get; set; }
        public int PendingCount { get; set; }
        public int TotalCount { get; set; }
        public string SearchQuery { get; set; } = string.Empty;
    }
}