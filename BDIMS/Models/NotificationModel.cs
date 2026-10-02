using System;

namespace BDIMS.Models
{
    public class NotificationModel
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; }
        public string RelatedId { get; set; } = string.Empty;
        public string RelatedUrl { get; set; } = string.Empty;
    }
}
