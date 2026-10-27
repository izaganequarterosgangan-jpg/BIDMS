using BDIMS.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;

namespace BDIMS.ViewComponents
{
    /// <summary>
    /// Admin sidebar badge beside the Documents link: PendingDocsBadge — how many
    /// document requests are unread AND still pending
    /// (<see cref="BDIMS.Models.DocumentRequestModel.Status"/> == "Pending" and
    /// <see cref="BDIMS.Models.DocumentRequestModel.IsRead"/> is <c>false</c>).
    ///
    /// It lives in a view component rather than a ViewData key so the badge is derived
    /// from the database on every admin page (Dashboard, Residents, Settings, ...) instead
    /// of only on the controller actions that happened to set the key - which is exactly
    /// why the counter used to vanish on pages like Settings. The number drops the
    /// moment each pending request is opened or "Mark all as read" is used.
    /// Mirrors <see cref="ResidentRequestsBadgeViewComponent"/>:
    /// it counts in the database and renders nothing at zero.
    /// </summary>
    public class SidebarBadgeViewComponent : ViewComponent
    {
        private readonly ApplicationDbContext _db;

        public SidebarBadgeViewComponent(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            // PendingDocsBadge: unread pending document requests only. A request
            // that was approved/rejected but never opened no longer counts, so
            // the Documents menu shows actionable work, not history.
            var count = await _db.DocumentRequests
                .AsNoTracking()
                .CountAsync(d => !d.IsRead && d.Status == "Pending");

            return View(count);
        }
    }
}
