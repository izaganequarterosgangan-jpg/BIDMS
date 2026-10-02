namespace BDIMS.Models
{
    public class AnnouncementModel
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string TargetAudience { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
    }
}