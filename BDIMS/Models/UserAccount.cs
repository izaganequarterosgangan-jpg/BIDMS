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

        /// <summary>Job title shown under the display name (e.g. "Barangay Secretary").</summary>
        [Required]
        [MaxLength(128)]
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// Avatar badge text (e.g. "IQ"). Persisted rather than recomputed per request so
        /// the badge a user sees is stable and auditable, but it is always derived from
        /// <see cref="DisplayName"/> by <see cref="DeriveInitials"/> when written.
        /// </summary>
        [MaxLength(8)]
        public string Initials { get; set; } = string.Empty;

        /// <summary>
        /// Grants the full administrative policy (every module: Blotter, Announcements,
        /// Documents, Certificates, Reports, Settings).
        /// </summary>
        public bool IsAdmin { get; set; }

        /// <summary>Inactive accounts are refused at sign-in without being deleted.</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Creation timestamp (UTC), for audit purposes.</summary>
        public DateTime CreatedUtc { get; set; }

        // ---- Claim type names shared by the sign-in code and the views ------------

        public const string DisplayNameClaim = "bdims:display_name";
        public const string RoleClaim = "bdims:role";
        public const string InitialsClaim = "bdims:initials";

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
    }
}