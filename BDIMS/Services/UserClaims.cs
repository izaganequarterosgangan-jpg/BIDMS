using System.Security.Claims;
using BDIMS.Models;

namespace BDIMS.Services
{
    /// <summary>
    /// Reads the signed-in user's presentation details out of the authentication
    /// principal's claims.
    ///
    /// The views need the same three values in several places (the sidebar widget, the
    /// dashboard header avatar and the settings avatar). Centralising the lookup keeps
    /// the claim names and the fallbacks identical everywhere, so the avatar can never
    /// show one person's initials next to another person's name.
    /// </summary>
    public static class UserClaims
    {
        /// <summary>Display name, falling back to <c>ClaimTypes.Name</c>.</summary>
        public static string? GetDisplayName(ClaimsPrincipal? principal) =>
            principal?.FindFirst(UserAccount.DisplayNameClaim)?.Value
            ?? principal?.FindFirst(ClaimTypes.Name)?.Value;

        /// <summary>Job title shown under the display name.</summary>
        public static string? GetRole(ClaimsPrincipal? principal) =>
            principal?.FindFirst(UserAccount.RoleClaim)?.Value;

        /// <summary>
        /// Avatar badge text. Uses the stored initials claim when present and otherwise
        /// derives them from the display name, so the badge is never blank.
        /// </summary>
        public static string GetInitials(ClaimsPrincipal? principal)
        {
            var initials = principal?.FindFirst(UserAccount.InitialsClaim)?.Value;

            if (!string.IsNullOrWhiteSpace(initials))
            {
                return initials;
            }

            return UserAccount.DeriveInitials(GetDisplayName(principal));
        }

        /// <summary>
        /// Contact email of the signed-in user. Falls back to the standard
        /// <see cref="ClaimTypes.Email"/> claim.
        /// </summary>
        public static string? GetEmail(ClaimsPrincipal? principal) =>
            principal?.FindFirst(UserAccount.EmailClaim)?.Value
            ?? principal?.FindFirst(ClaimTypes.Email)?.Value;

        /// <summary>Given name, or an empty string when the claim is absent.</summary>
        public static string GetFirstName(ClaimsPrincipal? principal) =>
            principal?.FindFirst(UserAccount.FirstNameClaim)?.Value ?? string.Empty;

        /// <summary>Family name, or an empty string when the claim is absent.</summary>
        public static string GetLastName(ClaimsPrincipal? principal) =>
            principal?.FindFirst(UserAccount.LastNameClaim)?.Value ?? string.Empty;

        /// <summary>Office identifier, or an empty string when the claim is absent.</summary>
        public static string GetEmployeeId(ClaimsPrincipal? principal) =>
            principal?.FindFirst(UserAccount.EmployeeIdClaim)?.Value ?? string.Empty;

        /// <summary>Mobile number, or an empty string when the claim is absent.</summary>
        public static string GetContactNumber(ClaimsPrincipal? principal) =>
            principal?.FindFirst(UserAccount.ContactNumberClaim)?.Value ?? string.Empty;
    }
}