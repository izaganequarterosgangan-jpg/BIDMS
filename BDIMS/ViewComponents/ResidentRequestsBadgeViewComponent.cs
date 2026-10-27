using System.Security.Claims;
using BDIMS.Data;
using BDIMS.Models;
using BDIMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDIMS.ViewComponents
{
    /// <summary>
    /// Renders the unread-request counter for the "My Requests" sidebar link.
    ///
    /// It lives in a view component rather than a ViewData key so the badge is derived
    /// from the database on every page (Dashboard, Settings, Profile, Announcements)
    /// instead of only on the controller actions that happened to set the key - which is
    /// exactly why the counter used to vanish on pages like Settings.
    /// </summary>
    public class ResidentRequestsBadgeViewComponent : ViewComponent
    {
        private readonly ApplicationDbContext _db;

        public ResidentRequestsBadgeViewComponent(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var count = await CountUnreadRequestsAsync();
            return View(count);
        }

        private async Task<int> CountUnreadRequestsAsync()
        {
            var user = HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return 0;
            }

            var residentName = (UserClaims.GetDisplayName(user) ?? string.Empty).Trim();

            // Resolve the signed-in account the same way the controller does: by id from
            // the auth cookie, falling back to the normalised email claim.
            int? accountId = null;
            var rawId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (int.TryParse(rawId, out var id)
                && await _db.UserAccounts.AsNoTracking().AnyAsync(u => u.Id == id))
            {
                accountId = id;
            }

            if (accountId is null)
            {
                var email = UserClaims.GetEmail(user);
                if (!string.IsNullOrWhiteSpace(email))
                {
                    var normalized = UserAccount.NormalizeEmail(email);
                    accountId = (await _db.UserAccounts.AsNoTracking()
                        .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized))?.Id;
                }
            }

            // A request belongs to the resident either through the linked account id or,
            // for rows created before accounts were linked, through the resident name.
            var unread = await _db.DocumentRequests.AsNoTracking()
                .Where(d => !d.IsRead)
                .ToListAsync();

            return unread.Count(d =>
                (accountId != null && d.UserAccountId == accountId)
                || (d.UserAccountId == null
                    && residentName.Length > 0
                    && string.Equals((d.Resident ?? string.Empty).Trim(), residentName, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
