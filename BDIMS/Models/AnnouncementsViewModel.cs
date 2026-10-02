using System.Collections.Generic;

namespace BDIMS.Models
{
    public class AnnouncementsViewModel
    {
        public List<AnnouncementModel> Announcements { get; set; } = new List<AnnouncementModel>();
        public int ScheduledCount { get; set; }
        public int HighPriorityCount { get; set; }
        public string ResidentsReach { get; set; } = "0";
        public string SearchQuery { get; set; } = string.Empty;
        public string ActiveTab { get; set; } = "Announcements";
    }
}
