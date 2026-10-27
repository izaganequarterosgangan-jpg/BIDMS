using System.ComponentModel.DataAnnotations.Schema;

namespace BDIMS.Models
{
    public class DocumentRequestModel
    {
        public string Id { get; set; } = string.Empty;

        // Nullable keeps older requests valid; new requests are linked to the signed-in account.
        [Column("UserAccountId")]
        public int? UserAccountId { get; set; }
        public string ResidentId { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public int? Age { get; set; }
        public string Purok { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Resident { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending";
        public string Purpose { get; set; } = string.Empty;

        /// <summary>
        /// Groupable creation timestamp for the Dashboard "Document Request Trends"
        /// chart (grouped by CreatedAt.Month for the current year). The legacy
        /// display-only <see cref="Date"/> string ("MMM d, yyyy") cannot be grouped
        /// in the database, so this is the queryable source of truth going forward.
        /// Defaults to now so newly bound rows are counted in the current month.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Drives the "My Requests" sidebar badge: false means the resident has not seen
        // the request (or a status update to it) since it was last raised.
        public bool IsRead { get; set; }

        /// <summary>
        /// Claim format chosen on the request form:
        /// "Digital Soft Copy" (downloadable PDF for remote residents) or
        /// "Printed Hard Copy" (in-person pickup at the Barangay Hall).
        /// Defaults to the printed hard copy so legacy rows and unbound posts
        /// never store null or an unknown value.
        /// </summary>
        public string FulfillmentMode { get; set; } = "Printed Hard Copy";

        /// <summary>GCash reference number supplied by the resident as payment proof.</summary>
        public string ReferenceNumber { get; set; } = string.Empty;

        /// <summary>
        /// Public URL of the uploaded receipt image under
        /// <c>wwwroot/uploads/receipts</c>, or empty when no proof was submitted.
        /// </summary>
        public string ReceiptImageUrl { get; set; } = string.Empty;

        /// <summary>Optional one-sentence note accompanying the payment proof.</summary>
        public string PaymentNote { get; set; } = string.Empty;

        /// <summary>Feedback sent by the Barangay Secretary for this request.</summary>
        public string AdminReply { get; set; } = string.Empty;

        /// <summary>
        /// Note from the Barangay Secretary accompanying the attached certificate PDF.
        /// Separate from AdminReply so document feedback and certificate delivery notes
        /// are stored in different columns.
        /// </summary>
        public string CertificateNote { get; set; } = string.Empty;

        /// <summary>
        /// True once the Barangay Secretary posts or edits a reply and until the
        /// resident opens the Barangay Feedback modal for this request. Drives the
        /// red dot on the feedback icon so a new reply is never missed.
        /// </summary>
        public bool HasUnreadReply { get; set; } = false;

        /// <summary>
        /// Public URL of the digital PDF the Barangay Secretary sent for this
        /// request. Empty until the soft copy is uploaded.
        /// </summary>
        public string DocumentPdfUrl { get; set; } = string.Empty;

        public string AttachmentFileName { get; set; } = string.Empty;
        public string AttachmentMime { get; set; } = string.Empty;
        public string AttachmentData { get; set; } = string.Empty;

        public bool HasAttachment =>
            !string.IsNullOrEmpty(AttachmentData) && !string.IsNullOrEmpty(AttachmentFileName);

        public string AttachmentUrl =>
            HasAttachment ? $"data:{AttachmentMime};base64,{AttachmentData}" : string.Empty;
    }
}