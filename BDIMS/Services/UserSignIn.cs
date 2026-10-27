using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using BDIMS.Data;
using BDIMS.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BDIMS.Services
{
    /// <summary>
    /// Builds and issues the BDIMS sign-in cookie from a <see cref="UserAccount"/> row.
    ///
    /// Extracted from <c>AccountController</c> so any code that changes an account and
    /// has to refresh the cookie (the profile save, the photo upload, and now the
    /// Resident Settings page's "Sign out other devices") issues exactly the same
    /// principal. A second, hand-rolled copy of the claim layout could drift and make
    /// the sidebar render a different role or initials than the one the account
    /// actually holds. Also owns the portal-role mapping and the per-request
    /// refresh the cookie-validation event uses, so a role edited in User
    /// Management reaches the signed-in user's next request.
    /// </summary>
    public static class UserSignIn
    {
        /// <summary>
        /// Builds the signed-in principal. The display name, job title and avatar
        /// initials are copied into claims here, which is what lets the sidebar user
        /// widget render "IQ / Isagane Quarteros / Barangay Secretary" without querying
        /// the database on every page load.
        /// </summary>
        public static ClaimsPrincipal BuildPrincipal(UserAccount account)
        {
            var initials = account.ResolveInitials();

            var claims = new List<Claim>
            {
                // ClaimTypes.Name feeds User.Identity.Name; the widget reads the
                // dedicated claim below so it never depends on a name-format assumption.
                new(ClaimTypes.Name, account.DisplayName),

                // The authorization role drives [Authorize(Roles = ...)] and the
                // portal split (Resident portal vs administrative system). The
                // mapping lives in ResolveAuthorizationRole so the cookie, the
                // login redirects and the sidebar can never disagree about which
                // side an account belongs on.
                new(ClaimTypes.Role, ResolveAuthorizationRole(account)),

                new(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new(ClaimTypes.Email, account.Email),
                new(UserAccount.DisplayNameClaim, account.DisplayName),
                new(UserAccount.RoleClaim, account.Role ?? string.Empty),
                new(UserAccount.InitialsClaim, initials),

                // Empty when no photo has been uploaded. Carried so the badges can
                // decide between the picture and the initials without a database read
                // on every page load.
                new(
                    UserAccount.ProfilePhotoClaim,
                    account.ProfilePhoto ?? string.Empty),

                // Carried so a save can re-issue the cookie from one up-to-date row and
                // every screen reads a consistent identity.
                new(UserAccount.EmailClaim, account.Email ?? string.Empty),
                new(UserAccount.FirstNameClaim, account.FirstName ?? string.Empty),
                new(UserAccount.LastNameClaim, account.LastName ?? string.Empty),
                new(UserAccount.EmployeeIdClaim, account.EmployeeId ?? string.Empty),
                new(UserAccount.ContactNumberClaim, account.ContactNumber ?? string.Empty)
            };

            var identity = new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme);

            return new ClaimsPrincipal(identity);
        }

        /// <summary>
        /// Office-side roles that belong to the administrative system (/Home/*)
        /// rather than the Resident portal. These are the system's only
        /// assignable permissions (mirrored by UserManagement's
        /// IsAssignableRole): "Admin"/"Administrator", the seeded
        /// "Barangay Secretary" (DbInitializer.AdminRole), and "Resident" below.
        /// A row still holding a retired role string ("Secretary", "Staff") is
        /// no longer recognized here, falls through to the legacy "Staff"
        /// claim in <see cref="ResolveAuthorizationRole"/>, and is refused by
        /// both portals until an administrator re-assigns it to a valid role.
        /// </summary>
        public static bool IsAdministrativeRole(string? role) =>
            string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Barangay Secretary", StringComparison.OrdinalIgnoreCase);

        /// <summary>The Resident-portal role string.</summary>
        public static bool IsResidentRole(string? role) =>
            string.Equals(role, "Resident", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when the account belongs on the administrative side (/Home/*,
        /// "_Layout") rather than the Resident portal (/Resident/*,
        /// "_ResidentLayout"). The login redirects share this so the destination
        /// always matches the role claim <see cref="BuildPrincipal"/> issues.
        /// </summary>
        public static bool IsAdministrativeAccount(UserAccount account) =>
            account.IsAdmin || IsAdministrativeRole(account.Role);

        /// <summary>True for a plain resident account.</summary>
        public static bool IsResidentAccount(UserAccount account) =>
            !account.IsAdmin && IsResidentRole(account.Role);

        /// <summary>
        /// The claim value behind [Authorize(Roles = ...)]:
        /// "Administrator" for the administrative system's accounts, "Resident"
        /// for the Resident portal, and the legacy "Staff" claim for an
        /// unrecognized role string - which no portal route accepts, so such an
        /// account is refused everywhere instead of silently landing on one side.
        /// </summary>
        public static string ResolveAuthorizationRole(UserAccount account)
        {
            if (IsAdministrativeAccount(account))
            {
                return "Administrator";
            }

            if (IsResidentAccount(account))
            {
                return "Resident";
            }

            return "Staff";
        }

        /// <summary>
        /// Issues (or re-issues) the auth cookie from an up-to-date account row.
        ///
        /// Callers that change a claim-bearing field must invoke this in the same
        /// request: the layout and the header badges read the name, role, initials and
        /// photo from claims, so without it the change would not appear until the
        /// cookie expired.
        /// </summary>
        public static Task SignInAsync(HttpContext context, UserAccount account)
        {
            return context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                BuildPrincipal(account),
                new AuthenticationProperties
                {
                    // Preserved so "Keep me signed in" still means the same thing after
                    // the refresh; the cookie is always re-sent by the browser either way.
                    IsPersistent = context.Request.Cookies[
                        CookieAuthenticationDefaults.AuthenticationScheme] != null
                });
        }

        /// <summary>Outcome of <see cref="RefreshPrincipalAsync"/>.</summary>
        public enum PrincipalRefreshOutcome
        {
            /// <summary>The cookie's claims still match the account row.</summary>
            Unchanged,

            /// <summary>Claims changed; the returned principal must replace the cookie's.</summary>
            Refreshed,

            /// <summary>The account no longer exists or is no longer active; end the session.</summary>
            Rejected
        }

        /// <summary>
        /// Re-derives the principal from the account row behind an authenticated
        /// cookie. User Management's Edit Account writes role, name and initials
        /// to the database only - the cookie lives in the edited user's browser -
        /// so without this check they would keep the old role, and the old
        /// portal, until the 8-hour cookie expired. The cookie-validation event
        /// in Program.cs calls this on every request: a role change takes effect
        /// on the user's very next click, and a deleted, blocked or deactivated
        /// account is signed out immediately, exactly as Login would refuse it.
        /// </summary>
        public static async Task<(PrincipalRefreshOutcome Outcome, ClaimsPrincipal? Principal)> RefreshPrincipalAsync(
            HttpContext context,
            ClaimsPrincipal? current)
        {
            var rawId = current?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!int.TryParse(rawId, out var userId))
            {
                // Not one of our cookies; leave it to the authentication handler.
                return (PrincipalRefreshOutcome.Unchanged, null);
            }

            var db = context.RequestServices.GetRequiredService<ApplicationDbContext>();
            var account = await db.UserAccounts.FindAsync(userId);

            if (account == null
                || account.Status != UserAccountStatus.Active
                || !account.IsActive)
            {
                return (PrincipalRefreshOutcome.Rejected, null);
            }

            var fresh = BuildPrincipal(account);

            return SameIdentity(current!, fresh)
                ? (PrincipalRefreshOutcome.Unchanged, null)
                : (PrincipalRefreshOutcome.Refreshed, fresh);
        }

        /// <summary>
        /// Claim-by-claim comparison: two principals are the same identity when
        /// every type/value pair matches, regardless of order.
        /// </summary>
        private static bool SameIdentity(ClaimsPrincipal a, ClaimsPrincipal b)
        {
            var left = a.Claims
                .Select(c => c.Type + "=" + c.Value)
                .OrderBy(v => v, StringComparer.Ordinal);
            var right = b.Claims
                .Select(c => c.Type + "=" + c.Value)
                .OrderBy(v => v, StringComparer.Ordinal);

            return left.SequenceEqual(right, StringComparer.Ordinal);
        }
    }
}
