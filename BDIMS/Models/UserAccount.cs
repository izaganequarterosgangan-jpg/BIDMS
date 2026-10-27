using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace BDIMS.Models
{
    /// <summary>
    /// Sign-in account for the BDIMS back office.
    ///
    /// BDIMS does not use ASP.NET Core Identity's built-in <c>IdentityUser</c> /
    /// <c>IdentityRole</c> pair: the application is a single-barangay deployment that
    /// only ever needs a handful of named staff accounts, not the full user-store,
    /// token-provider and lockout machinery. The store is therefore a plain EF Core
    /// entity, while the one security-critical piece - password storage - still uses
    /// ASP.NET Core Identity's <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>,
    /// so hashes are PBKDF2 with a per-password random salt and are never stored or
    /// compared in plaintext.
    /// </summary>
    public class UserAccount
    {
        /// <summary>Primary key.</summary>
        public int Id { get; set; }

        /// <summary>Login identifier, stored as the user typed it (display/audit only).</summary>
        [Required]
        [MaxLength(256)]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Upper-cased <see cref="Email"/>. Lookups go through this column rather than
        /// <see cref="Email"/> so matching does not depend on the collation of the
        /// database (which differs between MySQL installations) or on culture-sensitive
        /// comparison rules.
        /// </summary>
        [Required]
        [MaxLength(256)]
        public string NormalizedEmail { get; set; } = string.Empty;

        /// <summary>
        /// PBKDF2 hash produced by Identity's <c>PasswordHasher</c>, including its
        /// embedded salt and algorithm metadata. Never log or return this value.
        /// </summary>
        [Required]
        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>Full name shown in the UI (e.g. "Isagane Quarteros").</summary>
        [Required]
        [MaxLength(256)]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Given name, editable from the My Profile page (e.g. "Isagane").
        ///
        /// Kept separate from <see cref="LastName"/> rather than split out of
        /// <see cref="DisplayName"/> on read, because "Quarteros Isagane" and
        /// "Isagane Quarteros" are indistinguishable once concatenated - the badge
        /// initials differ ("QI" vs "IQ"), so the order has to be stored.
        /// </summary>
        [MaxLength(128)]
        public string FirstName { get; set; } = string.Empty;

        /// <summary>Family name, editable from the My Profile page (e.g. "Quarteros").</summary>
        [MaxLength(128)]
        public string LastName { get; set; } = string.Empty;

        /// <summary>Middle name captured at registration (e.g. "Santos"). Optional.</summary>
        [MaxLength(128)]
        public string? MiddleName { get; set; }

        /// <summary>Generational suffix captured at registration (e.g. "Jr.", "III"). Optional.</summary>
        [MaxLength(16)]
        public string? Suffix { get; set; }

        /// <summary>Age declared at registration. Informational only; never used for access control.</summary>
        public int? Age { get; set; }

        /// <summary>Sex declared at registration ("Male" / "Female"). Editable from My Profile.</summary>
        [MaxLength(16)]
        public string Gender { get; set; } = string.Empty;

        /// <summary>
        /// Date of birth captured from My Profile. Informational only; never used for
        /// access control. Optional so accounts created before the field existed stay
        /// valid and the resident can fill it in later.
        /// </summary>
        public DateTime? DateOfBirth { get; set; }

        /// <summary>
        /// Civil status captured from My Profile (e.g. "Single", "Married"). Optional;
        /// validated against a fixed list before it is written.
        /// </summary>
        [MaxLength(32)]
        public string CivilStatus { get; set; } = string.Empty;

        /// <summary>Purok / zone of residence (e.g. "Purok 2").</summary>
        [MaxLength(64)]
        public string Purok { get; set; } = string.Empty;

        /// <summary>Voter status declared at registration ("Registered Voter" / "Non-Voter").</summary>
        [MaxLength(32)]
        public string VoterStatus { get; set; } = string.Empty;

        /// <summary>
        /// Office identifier issued by the barangay (e.g. "EMP-0015"). Presentation
        /// only, so it is not unique-enforced; it is shown on the profile card.
        /// </summary>
        [MaxLength(64)]
        public string EmployeeId { get; set; } = string.Empty;

        /// <summary>
        /// Mobile number the office can reach the staff member on (e.g. "09171234567").
        /// Stored as free text rather than a numeric type because Philippine mobile
        /// numbers are commonly written with a leading "+63" or a "0" prefix.
        /// </summary>
        [MaxLength(32)]
        public string ContactNumber { get; set; } = string.Empty;

        /// <summary>Job title shown under the display name (e.g. "Barangay Secretary").</summary>
        [Required]
        [MaxLength(128)]
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// Whether the resident wants to receive request update notifications.
        /// Defaults to true so new accounts are opted-in by default.
        /// </summary>
        public bool NotifyRequestUpdates { get; set; } = true;

        /// <summary>
        /// Whether the resident wants to receive barangay announcement notifications.
        /// Defaults to true so new accounts are opted-in by default.
        /// </summary>
        public bool NotifyAnnouncements { get; set; } = true;

        /// <summary>
        /// Avatar badge text (e.g. "IQ"). Persisted rather than recomputed per request so
        /// the badge a user sees is stable and auditable, but it is always derived from
        /// <see cref="DisplayName"/> by <see cref="DeriveInitials"/> when written.
        /// </summary>
        [MaxLength(8)]
        public string Initials { get; set; } = string.Empty;

        /// <summary>
        /// File name of the uploaded profile photo inside the application's private
        /// photo folder (see <c>BDIMS.Services.ProfilePhotoStore</c>), or <c>null</c>
        /// when the account has never uploaded one.
        ///
        /// Only the generated file name is stored, never a client-supplied path: the
        /// name is rebuilt from the account id and a timestamp, so a tampered request
        /// cannot make the application read or overwrite a file outside the photo
        /// folder. The image itself is served by <c>AccountController.ProfilePhoto</c>
        /// rather than from <c>wwwroot</c>, because these are staff photographs and
        /// static files would be readable by anyone who guessed the URL.
        ///
        /// The badges fall back to <see cref="Initials"/> while this is null, so
        /// accounts created before the My Profile page existed keep rendering exactly
        /// as they always have.
        /// </summary>
        [MaxLength(128)]
        public string? ProfilePhoto { get; set; }

        /// <summary>
        /// Grants the full administrative policy (every module: Blotter, Announcements,
        /// Documents, Certificates, Reports, Settings).
        /// </summary>
        public bool IsAdmin { get; set; }

        /// <summary>
        /// Inactive accounts are refused at sign-in without being deleted.
        ///
        /// Kept for compatibility with code and seeded rows that predate
        /// <see cref="Status"/>; it is a mirror of it rather than an independent
        /// switch: <see cref="ApplyStatus"/> writes both together, and every reader
        /// (login, the user list) treats <see cref="Status"/> as the source of truth.
        /// New registrations default to pending approval (inactive) so an
        /// administrator must approve them before they can sign in.
        /// </summary>
        public bool IsActive { get; set; } = false;

        /// <summary>
        /// Registration/access lifecycle: <see cref="UserAccountStatus.Pending"/>,
        /// <see cref="UserAccountStatus.Active"/>, <see cref="UserAccountStatus.Rejected"/>,
        /// <see cref="UserAccountStatus.Blocked"/> or <see cref="UserAccountStatus.Deactivated"/>.
        /// Only <c>Active</c> may sign in. New user registrations default to <c>Pending</c>
        /// so they require admin approval.
        /// </summary>
        public UserAccountStatus Status { get; set; } = UserAccountStatus.Pending;

        /// <summary>
        /// Moment (UTC) a temporary block set by an administrator expires, or
        /// <c>null</c> when the account is not blocked. Set together with
        /// <see cref="Status"/> = <see cref="UserAccountStatus.Blocked"/> and cleared
        /// by <see cref="ApplyStatus"/> as soon as the status moves on, so the column
        /// never disagrees with the status the UI renders.
        /// </summary>
        public DateTime? LockoutEnd { get; set; }

        /// <summary>
        /// Free-text note the administrator typed when blocking the account
        /// (e.g. "shared workstation misuse"). Optional; shown in the user list and
        /// in the block modal so the reason survives the conversation it came from.
        /// </summary>
        [MaxLength(256)]
        public string? LockoutReason { get; set; }

        /// <summary>
        /// A temporary block whose expiry has not been reached yet.
        /// </summary>
        public bool IsLockActive =>
            Status == UserAccountStatus.Blocked
            && LockoutEnd.HasValue
            && LockoutEnd.Value > DateTime.UtcNow;

        /// <summary>
        /// A temporary block whose expiry has passed but whose row has not been
        /// rewritten yet - the account is due to become <see cref="UserAccountStatus.Active"/>
        /// again. Login and the user list both call <see cref="ApplyStatus"/> when they
        /// see this, so no background job is needed for the block to lift on time.
        /// </summary>
        public bool HasExpiredLock =>
            Status == UserAccountStatus.Blocked
            && LockoutEnd.HasValue
            && LockoutEnd.Value <= DateTime.UtcNow;

        /// <summary>
        /// Moves the account to <paramref name="status"/> and keeps the legacy
        /// <see cref="IsActive"/> flag and the temporary-lock columns consistent with
        /// it, so no reader can be left with a half-updated account: a blocked or
        /// rejected account is never sign-in-able through the old flag, and a
        /// lingering <see cref="LockoutEnd"/> can never re-block an account that has
        /// already been approved again.
        /// </summary>
        public void ApplyStatus(UserAccountStatus status)
        {
            Status = status;
            IsActive = status == UserAccountStatus.Active;

            if (status != UserAccountStatus.Blocked)
            {
                LockoutEnd = null;
                LockoutReason = null;
            }
        }

        /// <summary>Creation timestamp (UTC), for audit purposes.</summary>
        public DateTime CreatedUtc { get; set; }

        // ---- Claim type names shared by the sign-in code and the views ------------

        public const string DisplayNameClaim = "bdims:display_name";
        public const string RoleClaim = "bdims:role";
        public const string InitialsClaim = "bdims:initials";

        /// <summary>
        /// File name of the signed-in user's uploaded photo, or an empty string when
        /// they have none. Carried in the cookie for the same reason as the initials:
        /// every badge on every page reads the claim instead of hitting the database.
        /// </summary>
        public const string ProfilePhotoClaim = "bdims:profile_photo";

        /// <summary>
        /// Builds the lookup key for an email/username. Ordinal casing is forced with
        /// <c>ToUpperInvariant</c> so the same input always produces the same key on
        /// every machine, regardless of the server's regional settings.
        /// </summary>
        public static string NormalizeEmail(string? email) =>
            (email ?? string.Empty).Trim().ToUpperInvariant();

        /// <summary>
        /// Derives the avatar badge from a display name: the first letter of the first
        /// two words, upper-cased. "Isagane Quarteros" therefore yields "IQ". Falls back
        /// to the first letter of the whole name for single-word names, and to "?" when
        /// there is nothing to derive from, so the avatar is never blank.
        /// </summary>
        public static string DeriveInitials(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return "?";
            }

            var words = displayName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (words.Length == 0)
            {
                return "?";
            }

            var letters = words
                .Take(2)
                .Select(word => char.ToUpperInvariant(word[0]))
                .ToArray();

            return new string(letters);
        }

        /// <summary>
        /// Badge text for this account: the first letter of <see cref="FirstName"/> plus
        /// the first letter of <see cref="LastName"/>.
        ///
        /// The structured names win when both are present, because they carry the true
        /// order ("Quarteros Isagane" -> "QI", "Isagane Quarteros" -> "IQ") whereas a
        /// display name that has been typed in freehand does not. Accounts created
        /// before the My Profile page existed have both columns blank, so the display
        /// name is used as the fallback and those badges keep rendering as they always
        /// have rather than collapsing to "?".
        /// </summary>
        public string ResolveInitials()
        {
            var initials = ComposeInitials(FirstName, LastName);

            return initials ?? DeriveInitials(DisplayName);
        }

        /// <summary>
        /// Badge text for a first/last name pair, or <c>null</c> when the pair does not
        /// carry both halves.
        ///
        /// Centralised so the profile save, the seeder and the layout all agree on how
        /// "IQ" is produced; a badge that disagreed between screens would look like the
        /// identity had failed to refresh.
        /// </summary>
        public static string? ComposeInitials(string? firstName, string? lastName)
        {
            var first = firstName?.Trim() ?? string.Empty;
            var last = lastName?.Trim() ?? string.Empty;

            if (first.Length == 0 || last.Length == 0)
            {
                return null;
            }

            return string.Concat(
                char.ToUpperInvariant(first[0]),
                char.ToUpperInvariant(last[0]));
        }

        /// <summary>
        /// Joins the structured names into the <see cref="DisplayName"/> the layout and
        /// the header badge render, trimming the separator when only one half is filled
        /// in. Returns <paramref name="fallback"/> when neither name is present.
        /// </summary>
        public static string ComposeDisplayName(
            string? firstName,
            string? lastName,
            string? fallback = null)
        {
            var joined = string.Join(
                " ",
                new[] { firstName?.Trim(), lastName?.Trim() }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

            return joined.Length > 0
                ? joined
                : (fallback ?? string.Empty).Trim();
        }

        /// <summary>
        /// Best-effort split of a free-text display name into a given/family pair.
        ///
        /// Only used to pre-fill the My Profile form for accounts that predate the
        /// split-name columns. "Isagane Quarteros" becomes ("Isagane", "Quarteros");
        /// anything longer keeps the first word as the given name and treats the
        /// remainder as the family name, so a middle name is not silently dropped.
        /// </summary>
        public static (string First, string Last) SplitDisplayName(string? displayName)
        {
            var words = (displayName ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (words.Length == 0)
            {
                return (string.Empty, string.Empty);
            }

            if (words.Length == 1)
            {
                return (words[0], string.Empty);
            }

            return (words[0], string.Join(" ", words.Skip(1)));
        }

        public const string EmailClaim = "bdims:email";
        public const string FirstNameClaim = "bdims:first_name";
        public const string LastNameClaim = "bdims:last_name";
        public const string EmployeeIdClaim = "bdims:employee_id";
        public const string ContactNumberClaim = "bdims:contact_number";
    }
}