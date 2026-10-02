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
    }
}