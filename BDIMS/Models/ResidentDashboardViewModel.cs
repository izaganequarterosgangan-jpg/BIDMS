namespace BDIMS.Models
{
    /// <summary>
    /// Resident portal dashboard. Separate from the admin
    /// <see cref="DashboardViewModel"/> used by Home/Dashboard so the two
    /// screens can evolve independently.
    /// </summary>
    public class ResidentDashboardViewModel
    {
        public string ResidentName { get; set; } = string.Empty;
        public string ResidentId { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string VerificationStatus { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string? MiddleName { get; set; }
        public string LastName { get; set; } = string.Empty;
        public string? Suffix { get; set; }
        public int? Age { get; set; }
        public string Gender { get; set; } = string.Empty;
        public DateTime? DateOfBirth { get; set; }
        public string CivilStatus { get; set; } = string.Empty;
        public string Purok { get; set; } = string.Empty;
        public string VoterStatus { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string ContactNumber { get; set; } = string.Empty;
        public DateTime? AccountCreatedUtc { get; set; }
        public string ProfilePhotoUrl { get; set; } = string.Empty;

        /// <summary>
        /// Full address shown on the profile card. Resolved from the resident's purok
        /// plus the admin-configured barangay/municipality/province so it never falls
        /// back to the "No address on file" placeholder for a real barangay.
        /// </summary>
        public string RegisteredAddress { get; set; } = string.Empty;

        public bool HasActiveAlert { get; set; }
        public string AlertMessage { get; set; } = string.Empty;

        public int PendingRequestsCount { get; set; }
        public int ReadyForPickupCount { get; set; }
        public int DocumentsReceivedCount { get; set; }

        public List<ResidentRequestItem> RecentRequests { get; set; } = new();
        public List<ResidentAnnouncementItem> Announcements { get; set; } = new();
        public List<CertificateRule> AvailableCertificateTypes { get; set; } = new();
        public List<EmergencyHotline> EmergencyHotlines { get; set; } = new();
    }

    public class ResidentRequestItem
    {
        public string RequestNo { get; set; } = string.Empty;
        public string DocumentName { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string PurposeCategory { get; set; } = string.Empty;
        public string SpecificRemarks { get; set; } = string.Empty;
        public string ClaimFormat { get; set; } = string.Empty;
        public string SubmittedDate { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string AdminReply { get; set; } = string.Empty;

        /// <summary>
        /// Mirrors <see cref="DocumentRequestModel.HasUnreadReply"/>: the secretary
        /// replied and the resident has not opened the feedback yet, so the request
        /// row shows a red dot on its feedback icon.
        /// </summary>
        public bool HasUnreadReply { get; set; } = false;

        /// <summary>
        /// Public URL of the digital PDF the barangay sent for this request. Empty
        /// until the secretary uploads the file.
        /// </summary>
        public string DocumentPdfUrl { get; set; } = string.Empty;

        /// <summary>
        /// Note from the Barangay Secretary accompanying the attached certificate PDF.
        /// Separate from AdminReply so document feedback and certificate delivery notes
        /// are stored in different columns.
        /// </summary>
        public string CertificateNote { get; set; } = string.Empty;
    }

    public class ResidentAnnouncementItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string DateString { get; set; } = string.Empty;
        public string FullDate { get; set; } = string.Empty;
        public string ScheduleTime { get; set; } = string.Empty;
    }
}
