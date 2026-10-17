using System;
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

        /// <summary>
        /// URL of the signed-in user's uploaded photo, or <c>null</c> when they have not
        /// uploaded one (in which case the badges keep showing
        /// <see cref="GetInitials"/>).
        ///
        /// The claim carries the stored file name; the route that serves it is applied
        /// here so the four call sites - the profile card, the sidebar badge, the
        /// dashboard header badge and the settings badge - cannot drift apart, exactly
        /// as <see cref="GetInitials"/> already guarantees for the letters.
        /// </summary>
        public static string? GetPhotoUrl(ClaimsPrincipal? principal) =>
            BuildPhotoUrl(principal?.FindFirst(UserAccount.ProfilePhotoClaim)?.Value);

        /// <summary>
        /// Route for a stored photo file name, or <c>null</c> when there is no photo.
        ///
        /// Split out from <see cref="GetPhotoUrl"/> so the My Profile page can build the
        /// same URL straight from the account row it has just loaded, instead of relying
        /// on a claim that may predate the current edit.
        /// </summary>
        public static string? BuildPhotoUrl(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            return $"/Account/ProfilePhoto?file={Uri.EscapeDataString(fileName)}";
        }
    }
}