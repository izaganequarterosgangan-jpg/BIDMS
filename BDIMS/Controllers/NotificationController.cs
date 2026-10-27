using BDIMS.Data;
using BDIMS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace BDIMS.Controllers
{
    /// <summary>
    /// JSON API behind the top-bar notification bell. The admin bell now talks to
    /// /Notification/GetNotifications, /Notification/MarkAsRead/{id} and
    /// /Notification/MarkAllAsRead; the resident bell keeps using /Resident/*.
    ///
    /// Every write goes through SaveChangesAsync so the read state is committed to
    /// the database before the response is returned - the client only updates the
    /// badge once that response confirms the commit, which is what stops the red
    /// counter from flickering off and then reappearing.
    /// </summary>
    [Authorize]
    public class NotificationController : Controller
    {
        private readonly ApplicationDbContext _db;

        public NotificationController(ApplicationDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> GetNotifications()
        {
            var list = await AccessibleNotifications()
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync();

            var result = list.Select(n => new
            {
                id = n.Id,
                title = n.Title,
                message = n.Message,
                type = n.Type,
                isRead = n.IsRead,
                relatedUrl = ResolveNotificationUrl(n),
                createdAt = n.CreatedAt.ToString("MMM d, yyyy h:mm tt")
            }).ToList();

            return Json(result);
        }

        [HttpGet]
        public async Task<IActionResult> GetUnreadCount()
        {
            var count = await AccessibleNotifications().CountAsync(n => !n.IsRead);
            return Json(new { count });
        }

        /// <summary>
        /// Smart badge totals for the admin shell. PendingDocsBadge = unread
        /// pending document requests (Status == "Pending" and !IsRead).
        /// TotalBellBadge = unread DocumentRequest notifications + unread
        /// Announcement notifications. Single source per surface (requests table
        /// for the sidebar, notifications table for the bell) so nothing is
        /// double-counted.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetBadgeCounts()
        {
            var pendingDocsBadge = await _db.DocumentRequests
                .AsNoTracking()
                .CountAsync(d => !d.IsRead && d.Status == "Pending");

            var accessible = AccessibleNotifications().Where(n => !n.IsRead);
            var unreadRequestNotes = await accessible.CountAsync(n =>
                n.Type == "DocumentRequest" || n.Type == "DocumentUpdate");
            var unreadAnnouncements = await accessible.CountAsync(n =>
                n.Type == "Announcement");
            var totalBellBadge = unreadRequestNotes + unreadAnnouncements;

            return Json(new { pendingDocsBadge, totalBellBadge, unreadRequestNotes, unreadAnnouncements });
        }

        [HttpGet]
        public async Task<IActionResult> GetNotification(int id)
        {
            var notification = await AccessibleNotifications().AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == id);
            if (notification is null) return NotFound(new { success = false });

            // Resolve the linked announcement so the details modal can show its fields.
            AnnouncementModel? announcement = null;
            if (!string.IsNullOrWhiteSpace(notification.RelatedId)
                && int.TryParse(notification.RelatedId, out var announcementId))
            {
                announcement = await _db.Announcements.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == announcementId);
            }

            return Json(new
            {
                success = true,
                id = notification.Id,
                title = notification.Title,
                message = notification.Message,
                type = notification.Type,
                isRead = notification.IsRead,
                createdAt = notification.CreatedAt.ToString("MMM d, yyyy h:mm tt"),
                announcementId = announcement?.Id ?? 0,
                announcementTitle = announcement?.Title ?? notification.Message,
                purposeAndDetails = announcement?.Purpose ?? notification.Message,
                targetAudience = announcement?.TargetAudience ?? "All Barangay Residents",
                date = announcement?.Date ?? notification.CreatedAt.ToString("MMM d, yyyy"),
                priority = announcement?.Priority ?? "Low",
                relatedUrl = ResolveNotificationUrl(notification)
            });
        }

        // POST /Notification/MarkAsRead/{id} - the id can arrive in the route segment
        // or the form body.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAsRead(int id)
        {
            var notification = await AccessibleNotifications().FirstOrDefaultAsync(n => n.Id == id);
            if (notification is null) return NotFound(new { success = false });

            // Marking an already-read item is a no-op rather than an error.
            if (!notification.IsRead)
            {
                notification.IsRead = true;
                await _db.SaveChangesAsync();
            }

            var unread = await AccessibleNotifications().CountAsync(n => !n.IsRead);
            return Json(new { success = true, unread, redirectUrl = ResolveNotificationUrl(notification) });
        }

        // POST /Notification/MarkAllAsRead
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllAsRead()
        {
            var unread = await AccessibleNotifications().Where(n => !n.IsRead).ToListAsync();
            foreach (var notification in unread) notification.IsRead = true;

            // "Mark all as read" on the admin bell also acknowledges every document
            // request so the sidebar counters and inline NEW badges clear in the same
            // click. Restricted to admins so a resident (whose bell shares this route
            // through the generic script) can never wipe the staff queue.
            var clearedRequests = 0;
            if (User.IsInRole("Administrator"))
            {
                var requests = await _db.DocumentRequests.Where(d => !d.IsRead).ToListAsync();
                foreach (var request in requests) request.IsRead = true;
                clearedRequests = requests.Count;
            }

            if (unread.Count > 0 || clearedRequests > 0)
            {
                await _db.SaveChangesAsync();
            }

            return Json(new { success = true, unread = 0, updated = unread.Count, requestsUpdated = clearedRequests });
        }

        // Alias kept so the shared script's generic "<prefix>/MarkAllRead" call keeps
        // working for both the admin and resident bells.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> MarkAllRead() => MarkAllAsRead();

        private IQueryable<NotificationModel> AccessibleNotifications()
        {
            var rawUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var hasUserId = int.TryParse(rawUserId, out var userId);

            // Resolve roles before the query: User.IsInRole(n.TargetRole) is not
            // translatable to SQL, and it made the JSON endpoint fail on every call.
            var roleNames = User.FindAll(ClaimTypes.Role)
                .Select(claim => claim.Value)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .ToList();

            return _db.Notifications.Where(n =>
                (n.UserId == null && (n.TargetRole == null || n.TargetRole == ""))
                || (hasUserId && n.UserId == userId)
                || (n.TargetRole != null && n.TargetRole != "" && roleNames.Contains(n.TargetRole)));
        }

        // "New Document Request" notifications carry the request id in RelatedId
        // (e.g. "REQ-20261018120000-ABCD") and should open the certificate modal;
        // announcements should land on the admin announcement manager.
        private static string? ResolveNotificationUrl(NotificationModel notification)
        {
            var relatedId = (notification.RelatedId ?? string.Empty).Trim();

            if (relatedId.StartsWith("REQ-", StringComparison.OrdinalIgnoreCase))
            {
                return "/Home/Certificates?requestId=" + Uri.EscapeDataString(relatedId);
            }

            if (string.Equals(notification.Type, "Announcement", StringComparison.OrdinalIgnoreCase))
            {
                return "/Home/Announcements";
            }

            return notification.RelatedUrl;
        }
    }
}
