namespace BDIMS.Models
{
    public class DocumentRequestModel
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Resident { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending";
        public string Purpose { get; set; } = string.Empty;
        public string AttachmentFileName { get; set; } = string.Empty;
        public string AttachmentMime { get; set; } = string.Empty;
        public string AttachmentData { get; set; } = string.Empty;

        public bool HasAttachment =>
            !string.IsNullOrEmpty(AttachmentData) && !string.IsNullOrEmpty(AttachmentFileName);

        public string AttachmentUrl =>
            HasAttachment ? $"data:{AttachmentMime};base64,{AttachmentData}" : string.Empty;
    }
}