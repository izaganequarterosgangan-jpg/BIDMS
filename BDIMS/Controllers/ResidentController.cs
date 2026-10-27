using BDIMS.Data;
using BDIMS.Models;
using BDIMS.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace BDIMS.Controllers
{
    // The Resident portal is resident-only: locking the whole controller to the
    // "Resident" role keeps Secretaries, Staff and Administrators out of it, so
    // their only home is the administrative system (/Home/*). The role claim is
    // re-derived from the account row on every request (Program.cs cookie
    // validation via UserSignIn.RefreshPrincipalAsync), so a role changed in
    // User Management applies to a signed-in user's very next click.
    [Authorize(Roles = "Resident")]
    public class ResidentController : Controller
    {
        private const string PaymentMethodNameKey = "Payment.PaymentMethodName";
        private const string PaymentGcashNameKey = "Payment.GCashAccountName";
        private const string PaymentGcashNumberKey = "Payment.GCashAccountNumber";

        private readonly ApplicationDbContext _db;
        private readonly IPasswordHasher<UserAccount> _passwordHasher;
        private readonly ResidentSettingsStore _settingsStore;
        private readonly BarangayProfileProvider _barangayProfile;
        private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;

        public ResidentController(
            ApplicationDbContext db,
            IPasswordHasher<UserAccount> passwordHasher,
            ResidentSettingsStore settingsStore,
            BarangayProfileProvider barangayProfile,
            Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
        {
            _db = db;
            _passwordHasher = passwordHasher;
            _settingsStore = settingsStore;
            _barangayProfile = barangayProfile;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> GetNotifications()
        {
            var account = await GetCurrentUserAccountAsync();
            var query = _db.Notifications.AsNoTracking()
                .Where(n => (account != null && n.UserId == account.Id)
                    || (n.UserId == null && string.IsNullOrWhiteSpace(n.TargetRole)));

            var unreadCount = await query.CountAsync(n => !n.IsRead);

            // Newest five keep the dropdown short; the badge count above is the
            // authoritative unread total, so the panel never has to be complete.
            var rows = await query
                .OrderByDescending(n => n.CreatedAt)
                .Take(5)
                .ToListAsync();

            // Projected in memory rather than in the query so the legacy-URL rewrite
            // below can run; EF cannot translate the helper into SQL.
            var notifications = rows.Select(n => new
            {
                id = n.Id,
                title = n.Title,
                message = n.Message,
                type = n.Type,
                isRead = n.IsRead,
                targetUrl = NormalizeAnnouncementUrl(n.RelatedUrl),
                createdAt = n.CreatedAt.ToString("MMM d, yyyy h:mm tt")
            }).ToList();

            return Json(new { unreadCount, notifications });
        }

        [HttpGet]
        public async Task<IActionResult> GetUnreadCount()
        {
            var account = await GetCurrentUserAccountAsync();
            var count = await _db.Notifications.CountAsync(n => !n.IsRead
                && ((account != null && n.UserId == account.Id)
                    || (n.UserId == null && string.IsNullOrWhiteSpace(n.TargetRole))));
            return Json(new { count });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAsRead(int id)
        {
            var account = await GetCurrentUserAccountAsync();
            var notification = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id
                && ((account != null && n.UserId == account.Id)
                    || (n.UserId == null && string.IsNullOrWhiteSpace(n.TargetRole))));
            if (notification is null) return NotFound(new { success = false });

            notification.IsRead = true;
            await _db.SaveChangesAsync();
            var unread = await _db.Notifications.CountAsync(n => !n.IsRead
                && ((account != null && n.UserId == account.Id)
                    || (n.UserId == null && string.IsNullOrWhiteSpace(n.TargetRole))));
            return Json(new { success = true, unread });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllRead()
        {
            var account = await GetCurrentUserAccountAsync();
            var notifications = await _db.Notifications.Where(n => !n.IsRead
                && ((account != null && n.UserId == account.Id)
                    || (n.UserId == null && string.IsNullOrWhiteSpace(n.TargetRole)))).ToListAsync();
            foreach (var notification in notifications) notification.IsRead = true;
            await _db.SaveChangesAsync();
            return Json(new { success = true, unread = 0, updated = notifications.Count });
        }

        [HttpGet]
        public async Task<IActionResult> GetNotification(int id)
        {
            var account = await GetCurrentUserAccountAsync();
            var notification = await _db.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id
                && ((account != null && n.UserId == account.Id)
                    || (n.UserId == null && string.IsNullOrWhiteSpace(n.TargetRole))));
            if (notification is null) return NotFound(new { success = false });

            return Json(new
            {
                success = true,
                id = notification.Id,
                title = notification.Title,
                message = notification.Message,
                type = notification.Type,
                isRead = notification.IsRead,
                createdAt = notification.CreatedAt.ToString("MMM d, yyyy h:mm tt"),
                relatedUrl = NormalizeAnnouncementUrl(notification.RelatedUrl)
            });
        }

        /// <summary>
        /// Rewrites a notification's stored target when it points at the administrator
        /// announcements route. Older builds wrote <c>/Home/Announcements</c> for the
        /// "New Announcement" notification, but that controller is gated by
        /// <c>[Authorize(Roles = "Administrator")]</c>, so a resident who clicked the
        /// item was bounced to AccessDenied. Mapping the prefix onto the Resident
        /// controller keeps those historic rows usable without a data migration; the
        /// fragment (e.g. <c>#41</c>) is carried across so the page still scrolls to
        /// the matching card.
        /// </summary>
        private static string NormalizeAnnouncementUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            const string adminPrefix = "/Home/Announcements";

            if (!url.StartsWith(adminPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            return "/Resident/Announcements" + url.Substring(adminPrefix.Length);
        }

        // GET: /Resident/Dashboard
        public async Task<IActionResult> Dashboard()
        {
            var displayName = UserClaims.GetDisplayName(User) ?? string.Empty;
            var email = UserClaims.GetEmail(User) ?? string.Empty;
            var userIdRaw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            UserAccount? account = null;
            if (int.TryParse(userIdRaw, out var userId))
            {
                account = await _db.UserAccounts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == userId);
            }

            account ??= await _db.UserAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.NormalizedEmail == UserAccount.NormalizeEmail(email));

            var residentName = account?.DisplayName ?? displayName;
            if (string.IsNullOrWhiteSpace(residentName))
            {
                residentName = User.Identity?.Name ?? "Resident";
            }

            var allResidents = await _db.Residents.AsNoTracking().ToListAsync();
            var resident = allResidents.FirstOrDefault(r =>
                string.Equals((r.Name ?? string.Empty).Trim(),
                    residentName.Trim(),
                    StringComparison.OrdinalIgnoreCase));

            var allRequests = await _db.DocumentRequests
                .AsNoTracking()
                .ToListAsync();

            var myRequests = allRequests
                .Where(d => (account != null && d.UserAccountId == account.Id)
                    || (d.UserAccountId == null && string.Equals((d.Resident ?? string.Empty).Trim(),
                        residentName.Trim(),
                        StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(d => d.Date)
                .ToList();

            var pendingCount = myRequests.Count(d => IsPending(d.Status));
            var readyCount = myRequests.Count(d => IsReadyForPickup(d.Status));
            var receivedCount = myRequests.Count(d => IsIssued(d.Status));

            var recentRequests = myRequests
                .Take(5)
                .Select(ToResidentRequestItem)
                .ToList();

            var announcements = await _db.Announcements
                .AsNoTracking()
                .OrderByDescending(a => a.Id)
                .Take(5)
                .ToListAsync();

            var announcementItems = announcements
                .Select(a => new ResidentAnnouncementItem
                {
                    Id = a.Id,
                    Title = a.Title,
                    Description = a.Purpose,
                    Category = a.Category,
                    DateString = ToDateBadge(a.Date),
                    FullDate = a.Date,
                    ScheduleTime = a.ScheduleTime
                })
                .ToList();

            var emergencyHotlines = await _db.EmergencyHotlines
                .AsNoTracking()
                .OrderBy(h => h.DisplayOrder)
                .ToListAsync();

            var readyForPickup = myRequests.FirstOrDefault(d => IsReadyForPickup(d.Status));

            var viewModel = new ResidentDashboardViewModel
            {
                ResidentName = residentName,
                ResidentId = resident?.Id ?? string.Empty,
                Address = resident?.Purok ?? string.Empty,
                VerificationStatus = resident != null ? "Verified resident" : "Unverified",
                FirstName = account?.FirstName ?? string.Empty,
                MiddleName = account?.MiddleName ?? string.Empty,
                LastName = account?.LastName ?? string.Empty,
                Suffix = account?.Suffix ?? string.Empty,
                Age = account?.Age,
                Gender = account?.Gender ?? resident?.Sex ?? string.Empty,
                Purok = account?.Purok ?? resident?.Purok ?? string.Empty,
                VoterStatus = account?.VoterStatus ?? (resident?.IsVoter == true ? "Registered Voter" : string.Empty),
                Email = account?.Email ?? email,
                ContactNumber = account?.ContactNumber ?? string.Empty,
                AccountCreatedUtc = account?.CreatedUtc,
                ProfilePhotoUrl = UserClaims.BuildPhotoUrl(account?.ProfilePhoto) ?? string.Empty,
                DateOfBirth = account?.DateOfBirth,
                CivilStatus = account?.CivilStatus ?? string.Empty,
                RegisteredAddress = ComposeRegisteredAddress(account?.Purok ?? resident?.Purok),
                HasActiveAlert = readyForPickup != null,
                AlertMessage = readyForPickup != null
                    ? $"Your {readyForPickup.Title} is ready for pickup. Bring a valid ID and your request number to the barangay hall during office hours."
                    : string.Empty,
                PendingRequestsCount = pendingCount,
                ReadyForPickupCount = readyCount,
                DocumentsReceivedCount = receivedCount,
                RecentRequests = recentRequests,
                Announcements = announcementItems,
                EmergencyHotlines = emergencyHotlines
            };

            return View(viewModel);
        }

        // GET: /Resident/Profile
        public async Task<IActionResult> Profile()
        {
            var vm = await BuildDashboardAsync();
            vm.AvailableCertificateTypes = await GetActiveCertificateTypesAsync();
            return View("Profile", vm);
        }

        // GET: /Resident/Requests
        public async Task<IActionResult> Requests(string? searchQuery = null)
        {
            // Search box in the resident header posts here; filter this resident's own
            // rows only (never another resident's) and keep the full dynamic model.
            var vm = await BuildDashboardAsync();
            vm.AvailableCertificateTypes = await GetActiveCertificateTypesAsync();

            // ViewBag.Certificates mirrors the model list so the Document Type
            // dropdown (and its data-fee attributes) bind straight to the active
            // rows from the Settings > Certificates & Fees table.
            ViewBag.Certificates = vm.AvailableCertificateTypes;

            // Payment details configured by the administrator under Settings >
            // Online Payment Methods. The QR image has a fixed upload path, and
            // the cache-busting query keeps a replaced image from showing stale.
            var gcashQrPath = System.IO.Path.Combine(
                _env.WebRootPath ?? System.IO.Path.Combine(_env.ContentRootPath, "wwwroot"),
                "uploads",
                "gcash_qr.png");

            ViewBag.GCashQrUrl = System.IO.File.Exists(gcashQrPath)
                ? Url.Content("~/uploads/gcash_qr.png") + "?v=" + DateTime.UtcNow.Ticks
                : string.Empty;
            var paymentMethodName = ReadPaymentSetting(PaymentMethodNameKey);
            ViewBag.PaymentMethodName = string.IsNullOrWhiteSpace(paymentMethodName)
                ? "GCash"
                : paymentMethodName;
            ViewBag.GCashAccountName = ReadPaymentSetting(PaymentGcashNameKey);
            ViewBag.GCashAccountNumber = ReadPaymentSetting(PaymentGcashNumberKey);

            // Profile snapshot used to pre-fill - and, via "Autofill from profile", to
            // re-fill - the four identity fields on the new-request form. Age is derived
            // from the date of birth saved on My Profile when present, falling back to the
            // age captured at registration. The document type, purpose category and
            // remarks are deliberately not passed: the resident must choose those fresh
            // for every request.
            var account = await GetCurrentUserAccountAsync();
            var profileAge = CalculateAge(account?.DateOfBirth) ?? account?.Age;
            vm.Age = profileAge;
            ViewData["ProfileFullName"] = vm.ResidentName;
            ViewData["ProfileAge"] = profileAge?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            ViewData["ProfilePurok"] = vm.Purok;
            ViewData["ProfileAddress"] = vm.RegisteredAddress;

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var q = searchQuery.Trim();
                vm.RecentRequests = vm.RecentRequests
                    .Where(r => (r.DocumentName ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (r.RequestNo ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (r.Purpose ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (r.Status ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Opening "My Requests" clears the sidebar badge by flagging this resident's
            // unseen rows as read before the layout renders the counter.
            await MarkResidentRequestsReadAsync();
            return View("Requests", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitRequest(
            string documentType,
            string purposeCategory,
            string specificRemarks,
            int? age,
            string? purok,
            string? address,
            string? contactNumber = null,
            string? fulfillmentMode = null)
        {
            var vm = await BuildDashboardAsync();
            vm.AvailableCertificateTypes = await GetActiveCertificateTypesAsync();

            var account = await GetCurrentUserAccountAsync();
            if (account is null)
            {
                return RedirectToAction("Login", "Account");
            }

            var submittedDocumentType = (documentType ?? string.Empty).Trim();
            var selectedCertificate = vm.AvailableCertificateTypes.FirstOrDefault(c =>
                string.Equals(c.Type.Trim(), submittedDocumentType, StringComparison.OrdinalIgnoreCase)
                || (submittedDocumentType.Equals("First Time Job Seeker Certificate", StringComparison.OrdinalIgnoreCase)
                    && c.Type.Contains("Jobseeker", StringComparison.OrdinalIgnoreCase)));

            if (selectedCertificate is null)
            {
                TempData["Error"] = "Please choose an available document type.";
                return RedirectToAction(nameof(Requests));
            }

            if (string.IsNullOrWhiteSpace(purposeCategory))
            {
                TempData["Error"] = "Please enter the purpose of your request.";
                return RedirectToAction(nameof(Requests));
            }

            var newRequestId = "REQ-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            _db.DocumentRequests.Add(new DocumentRequestModel
            {
                Id = newRequestId,
                UserAccountId = account.Id,
                ResidentId = vm.ResidentId,
                FullName = vm.ResidentName,
                Age = age ?? vm.Age,
                Purok = string.IsNullOrWhiteSpace(purok) ? vm.Purok : purok.Trim(),
                Address = string.IsNullOrWhiteSpace(address) ? vm.Address : address.Trim(),
                Title = selectedCertificate.Type,
                Resident = vm.ResidentName,
                Date = DateTime.Now.ToString("MMM d, yyyy"),
                CreatedAt = DateTime.Now,
                Status = "Pending",
                FulfillmentMode = NormalizeFulfillmentMode(fulfillmentMode),
                Purpose = string.IsNullOrWhiteSpace(specificRemarks)
                    ? purposeCategory.Trim()
                    : $"{purposeCategory.Trim()}: {specificRemarks.Trim()}"
            });

            _db.Notifications.Add(new NotificationModel
            {
                TargetRole = "Administrator",
                Title = "New Document Request",
                Message = $"{vm.ResidentName} requested a {selectedCertificate.Type}.",
                // Smart-badge contract: unread DocumentRequest notifications drive
                // PendingDocsBadge (via the linked Pending request) and
                // TotalBellBadge. The linked DocumentRequest row carries
                // Status = "Pending" (set above) since Notifications has no
                // Status column. "DocumentUpdate" is kept as a legacy alias in
                // badge queries so older rows still count.
                Type = "DocumentRequest",
                IsRead = false,
                CreatedAt = DateTime.Now,
                RelatedId = newRequestId,
                // Deep-links the admin straight into the certificate issue modal for this
                // request (view/print), instead of dumping them on the documents list.
                RelatedUrl = $"/Home/Certificates?requestId={Uri.EscapeDataString(newRequestId)}"
            });

            await _db.SaveChangesAsync();
            TempData["Success"] = "Document request submitted successfully and is pending barangay review.";
            return RedirectToAction(nameof(Requests));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateRequest(
            string requestId,
            string? purposeCategory,
            string? specificRemarks,
            string? claimFormat)
        {
            var account = await GetCurrentUserAccountAsync();
            if (account is null)
            {
                return RedirectToAction("Login", "Account");
            }

            var cleanRequestId = (requestId ?? string.Empty).Trim();
            var submittedCategory = (purposeCategory ?? string.Empty).Trim();

            if (cleanRequestId.Length == 0)
            {
                TempData["Error"] = "The request could not be identified. Please try again.";
                return RedirectToAction(nameof(Requests));
            }

            var allowedCategories = new[]
            {
                "Employment",
                "Scholarship / School Requirement",
                "Medical Assistance",
                "Financial Assistance",
                "Business / Legal"
            };

            var category = allowedCategories.FirstOrDefault(option =>
                string.Equals(option, submittedCategory, StringComparison.OrdinalIgnoreCase));

            if (category is null)
            {
                TempData["Error"] = "Choose a valid purpose category.";
                return RedirectToAction(nameof(Requests));
            }

            var request = await _db.DocumentRequests
                .FirstOrDefaultAsync(item => item.Id == cleanRequestId);

            var residentName = (User.Identity?.Name ?? string.Empty).Trim();

            var ownsRequest = request is not null
                && (request.UserAccountId == account.Id
                    || (request.UserAccountId == null
                        && string.Equals(
                            (request.Resident ?? string.Empty).Trim(),
                            residentName,
                            StringComparison.OrdinalIgnoreCase)));

            if (!ownsRequest)
            {
                TempData["Error"] = "That request was not found on your account.";
                return RedirectToAction(nameof(Requests));
            }

            if (!IsPending(request!.Status))
            {
                TempData["Error"] = "Only pending requests can be edited.";
                return RedirectToAction(nameof(Requests));
            }

            var remarks = (specificRemarks ?? string.Empty).Trim();

            request.Purpose = remarks.Length == 0
                ? category
                : $"{category}: {remarks}";
            request.FulfillmentMode = NormalizeFulfillmentMode(claimFormat);

            await _db.SaveChangesAsync();

            TempData["Success"] = "Your pending request was updated.";
            return RedirectToAction(nameof(Requests));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitPaymentProof(
            string requestId,
            string? referenceNumber,
            string? paymentNote,
            Microsoft.AspNetCore.Http.IFormFile? receiptImage)
        {
            var account = await GetCurrentUserAccountAsync();
            if (account is null)
            {
                return RedirectToAction("Login", "Account");
            }

            var cleanRequestId = (requestId ?? string.Empty).Trim();
            var reference = (referenceNumber ?? string.Empty).Trim();
            var note = (paymentNote ?? string.Empty).Trim();

            if (note.Length > 120)
            {
                note = note[..120];
            }

            if (cleanRequestId.Length == 0)
            {
                TempData["Error"] = "The request could not be identified. Please try again.";
                return RedirectToAction(nameof(Requests));
            }

            if (reference.Length == 0)
            {
                TempData["Error"] = "Enter the GCash reference number.";
                return RedirectToAction(nameof(Requests));
            }

            if (receiptImage is null || receiptImage.Length == 0)
            {
                TempData["Error"] = "Attach a screenshot or photo of your GCash receipt.";
                return RedirectToAction(nameof(Requests));
            }

            if (receiptImage.Length > 5L * 1024 * 1024)
            {
                TempData["Error"] = "The receipt image cannot be larger than 5 MB.";
                return RedirectToAction(nameof(Requests));
            }

            var extension = System.IO.Path.GetExtension(receiptImage.FileName).ToLowerInvariant();
            if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
            {
                TempData["Error"] = "Upload the receipt as a PNG, JPG, JPEG or WEBP image.";
                return RedirectToAction(nameof(Requests));
            }

            if (!HasValidImageSignature(receiptImage, extension))
            {
                TempData["Error"] = "That file does not appear to be a valid image.";
                return RedirectToAction(nameof(Requests));
            }

            var request = await _db.DocumentRequests
                .FirstOrDefaultAsync(item => item.Id == cleanRequestId);

            var residentName = (User.Identity?.Name ?? string.Empty).Trim();

            var ownsRequest = request is not null
                && (request.UserAccountId == account.Id
                    || (request.UserAccountId == null
                        && string.Equals(
                            (request.Resident ?? string.Empty).Trim(),
                            residentName,
                            StringComparison.OrdinalIgnoreCase)));

            if (!ownsRequest)
            {
                TempData["Error"] = "That request was not found on your account.";
                return RedirectToAction(nameof(Requests));
            }

            var receiptsDirectory = System.IO.Path.Combine(
                _env.WebRootPath ?? System.IO.Path.Combine(_env.ContentRootPath, "wwwroot"),
                "uploads",
                "receipts");

            System.IO.Directory.CreateDirectory(receiptsDirectory);

            var storedFileName =
                "receipt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" +
                Guid.NewGuid().ToString("N") + extension;
            var storedPath = System.IO.Path.Combine(receiptsDirectory, storedFileName);

            await using (var target = new System.IO.FileStream(
                storedPath,
                System.IO.FileMode.CreateNew,
                System.IO.FileAccess.Write,
                System.IO.FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await receiptImage.CopyToAsync(target);
            }

            request!.ReferenceNumber = reference;
            request.PaymentNote = note;
            request.ReceiptImageUrl = Url.Content("~/uploads/receipts/") + storedFileName;

            try
            {
                await _db.SaveChangesAsync();
            }
            catch
            {
                // Do not leave a receipt behind when the request row could not be
                // updated, otherwise the admin queue and the upload folder disagree.
                try
                {
                    System.IO.File.Delete(storedPath);
                }
                catch (System.IO.IOException)
                {
                }

                TempData["Error"] = "The payment proof could not be saved. Please try again.";
                return RedirectToAction(nameof(Requests));
            }

            TempData["Success"] = "Payment proof submitted. The barangay will verify your receipt.";
            return RedirectToAction(nameof(Requests));
        }

        private async Task<UserAccount?> GetCurrentUserAccountAsync()
        {
            var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (int.TryParse(raw, out var id))
            {
                var accountById = await _db.UserAccounts.FirstOrDefaultAsync(u => u.Id == id);
                if (accountById is not null)
                {
                    return accountById;
                }
            }

            var email = UserClaims.GetEmail(User);
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            return await _db.UserAccounts.FirstOrDefaultAsync(
                u => u.NormalizedEmail == UserAccount.NormalizeEmail(email));
        }

        /// <summary>
        /// Projects a stored request into the resident-facing row, splitting the
        /// combined Purpose text back into the category and remarks the edit modal
        /// needs while preserving the original display value.
        /// </summary>
        private static ResidentRequestItem ToResidentRequestItem(DocumentRequestModel request)
        {
            var purpose = (request.Purpose ?? string.Empty).Trim();
            var separator = purpose.IndexOf(": ", StringComparison.Ordinal);

            return new ResidentRequestItem
            {
                RequestNo = request.Id,
                DocumentName = request.Title,
                Purpose = purpose,
                PurposeCategory = separator > 0 ? purpose[..separator] : purpose,
                SpecificRemarks = separator > 0 ? purpose[(separator + 2)..].Trim() : string.Empty,
                ClaimFormat = string.IsNullOrWhiteSpace(request.FulfillmentMode)
                    ? "Printed Hard Copy"
                    : request.FulfillmentMode,
                SubmittedDate = request.Date,
                Status = request.Status,
                AdminReply = request.AdminReply,
                HasUnreadReply = request.HasUnreadReply,
                DocumentPdfUrl = request.DocumentPdfUrl,
                CertificateNote = request.CertificateNote
            };
        }

        /// <summary>
        /// Reads a value saved by the administrator from the AppSettings key/value
        /// table (the storage behind Settings > Online Payment Methods).
        /// </summary>
        private string ReadPaymentSetting(string key) =>
            _db.AppSettings
                .AsNoTracking()
                .Where(setting => setting.Name == key)
                .Select(setting => setting.Value)
                .FirstOrDefault()
                ?.Trim()
            ?? string.Empty;

        /// <summary>
        /// Confirms the uploaded receipt's leading bytes match the extension it is
        /// stored under. Receipts are served from wwwroot, so an HTML or script file
        /// renamed as .png must be refused rather than published as an image.
        /// </summary>
        private static bool HasValidImageSignature(
            Microsoft.AspNetCore.Http.IFormFile file,
            string extension)
        {
            using var stream = file.OpenReadStream();
            Span<byte> header = stackalloc byte[12];
            var read = 0;

            while (read < header.Length)
            {
                var chunk = stream.Read(header[read..]);

                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            if (read < 4)
            {
                return false;
            }

            if (extension == ".png")
            {
                return read >= 8
                    && header[0] == 0x89 && header[1] == 0x50
                    && header[2] == 0x4E && header[3] == 0x47
                    && header[4] == 0x0D && header[5] == 0x0A
                    && header[6] == 0x1A && header[7] == 0x0A;
            }

            if (extension is ".jpg" or ".jpeg")
            {
                return header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
            }

            if (extension == ".webp")
            {
                return read >= 12
                    && header[0] == (byte)'R' && header[1] == (byte)'I'
                    && header[2] == (byte)'F' && header[3] == (byte)'F'
                    && header[8] == (byte)'W' && header[9] == (byte)'E'
                    && header[10] == (byte)'B' && header[11] == (byte)'P';
            }

            return false;
        }

        /// <summary>
        /// Marks every unseen request belonging to the signed-in resident as read.
        ///
        /// Called when they open "My Requests" so the sidebar badge drops back to zero.
        /// Ownership mirrors <see cref="BuildDashboardAsync"/>: rows linked to the account
        /// by id, plus legacy rows (no account id) carrying the resident's display name.
        /// </summary>
        private async Task MarkResidentRequestsReadAsync()
        {
            var account = await GetCurrentUserAccountAsync();
            var residentName = (UserClaims.GetDisplayName(User) ?? string.Empty).Trim();

            var unread = await _db.DocumentRequests
                .Where(d => !d.IsRead)
                .ToListAsync();

            var mine = unread.Where(d =>
                (account != null && d.UserAccountId == account.Id)
                || (d.UserAccountId == null
                    && residentName.Length > 0
                    && string.Equals((d.Resident ?? string.Empty).Trim(), residentName, StringComparison.OrdinalIgnoreCase)));

            var changed = false;
            foreach (var request in mine)
            {
                request.IsRead = true;
                changed = true;
            }

            if (changed)
            {
                await _db.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Clears the "unread reply" flag on a request once the resident opens the
        /// Barangay Feedback modal, which removes the red dot from the feedback icon.
        /// Ownership mirrors <see cref="BuildDashboardAsync"/> so a resident can never
        /// clear the badge on somebody else's request.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkReplyAsRead(string requestId)
        {
            var cleanRequestId = (requestId ?? string.Empty).Trim();
            if (cleanRequestId.Length == 0)
            {
                return BadRequest(new { success = false });
            }

            var request = await _db.DocumentRequests
                .FirstOrDefaultAsync(d => d.Id == cleanRequestId);

            if (request == null)
            {
                return NotFound(new { success = false });
            }

            var account = await GetCurrentUserAccountAsync();
            var residentName = (UserClaims.GetDisplayName(User) ?? string.Empty).Trim();

            var owned = (account != null && request.UserAccountId == account.Id)
                || (request.UserAccountId == null
                    && residentName.Length > 0
                    && string.Equals((request.Resident ?? string.Empty).Trim(), residentName, StringComparison.OrdinalIgnoreCase));

            if (!owned)
            {
                return NotFound(new { success = false });
            }

            if (request.HasUnreadReply)
            {
                request.HasUnreadReply = false;
                await _db.SaveChangesAsync();
            }

            return Json(new { success = true });
        }

        // POST: /Resident/CancelRequest
        /// <summary>
        /// Lets a resident withdraw a document request that is still awaiting review
        /// (Status == "Pending"). The row moves to "Cancelled", so it drops out of
        /// the barangay's pending queue and the resident's pending count. Requests
        /// that already left Pending cannot be cancelled here - the secretary has
        /// started working on them - and the status is re-checked server-side so a
        /// stale browser tab can never cancel a processed request.
        /// Answered with JSON because the "Track your requests" page calls it
        /// through fetch and reloads the list on success.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelRequest(string requestId)
        {
            var cleanRequestId = (requestId ?? string.Empty).Trim();
            if (cleanRequestId.Length == 0)
            {
                return BadRequest(new { success = false, message = "The request could not be identified." });
            }

            var request = await _db.DocumentRequests
                .FirstOrDefaultAsync(d => d.Id == cleanRequestId);

            var account = await GetCurrentUserAccountAsync();
            var residentName = (UserClaims.GetDisplayName(User) ?? string.Empty).Trim();

            var owned = request is not null
                && ((account != null && request.UserAccountId == account.Id)
                    || (request.UserAccountId == null
                        && residentName.Length > 0
                        && string.Equals(
                            (request.Resident ?? string.Empty).Trim(),
                            residentName,
                            StringComparison.OrdinalIgnoreCase)));

            if (!owned)
            {
                return NotFound(new { success = false, message = "That request was not found on your account." });
            }

            if (!IsPending(request!.Status))
            {
                return BadRequest(new { success = false, message = "Only pending requests can be cancelled." });
            }

            request.Status = "Cancelled";
            await _db.SaveChangesAsync();

            return Json(new { success = true, message = "Your request has been cancelled." });
        }

        // POST: /Resident/DeleteRequest
        /// <summary>
        /// Removes a cancelled document request from the resident's list for good.
        /// Only rows whose Status is "Cancelled" qualify: a live request belongs to
        /// the barangay's workflow and has to be approved, rejected or cancelled
        /// first - the status is re-checked server-side so a stale browser tab
        /// cannot delete an active request.
        /// Answered with JSON because the "Track your requests" page calls it
        /// through fetch and reloads the list on success.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRequest(string requestId)
        {
            var cleanRequestId = (requestId ?? string.Empty).Trim();
            if (cleanRequestId.Length == 0)
            {
                return BadRequest(new { success = false, message = "The request could not be identified." });
            }

            var request = await _db.DocumentRequests
                .FirstOrDefaultAsync(d => d.Id == cleanRequestId);

            var account = await GetCurrentUserAccountAsync();
            var residentName = (UserClaims.GetDisplayName(User) ?? string.Empty).Trim();

            var owned = request is not null
                && ((account != null && request.UserAccountId == account.Id)
                    || (request.UserAccountId == null
                        && residentName.Length > 0
                        && string.Equals(
                            (request.Resident ?? string.Empty).Trim(),
                            residentName,
                            StringComparison.OrdinalIgnoreCase)));

            if (!owned)
            {
                return NotFound(new { success = false, message = "That request was not found on your account." });
            }

            if (!IsCancelled(request!.Status))
            {
                return BadRequest(new { success = false, message = "Only cancelled requests can be deleted." });
            }

            _db.DocumentRequests.Remove(request);
            await _db.SaveChangesAsync();

            return Json(new { success = true, message = "The cancelled request was deleted." });
        }

        // GET: /Resident/RequestDocument
        // Entry point for "+ New request" / "Request a Document". Reuses the existing
        // dynamic dashboard data so the page can list this resident's history while the
        // create flow stays on one screen; no duplicate routes or static placeholders.
        public async Task<IActionResult> RequestDocument()
        {
            return RedirectToAction(nameof(Requests));
        }

        // POST: /Resident/RequestDocument
        // Creates a document request for the signed-in resident only. The resident name
        // is taken from the authenticated account (not the form) so one resident can
        // never file under another resident's name.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestDocument(string documentType, string purpose, string? contactNumber = null)
        {
            return await SubmitRequest(documentType, purpose, purpose, null, null, null, contactNumber);
        }

        private async Task<List<CertificateRule>> GetActiveCertificateTypesAsync()
        {
            return await _db.CertificateRules
                .AsNoTracking()
                .Where(c => c.Status == "Active")
                .OrderBy(c => c.Type)
                .ToListAsync();
        }

        /// <summary>
        /// Only the two claim formats the form offers are stored; anything else
        /// (blank, legacy, or tampered post) falls back to the printed hard copy
        /// so the database never holds an unknown mode.
        /// </summary>
        private static string NormalizeFulfillmentMode(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Equals("Digital Soft Copy", StringComparison.OrdinalIgnoreCase)
                ? "Digital Soft Copy"
                : "Printed Hard Copy";
        }

        // GET: /Resident/Announcements
        // Read-only list of the same dynamic announcements shown on the dashboard.
        // Explicitly scoped to the Resident role: without it a signed-in account whose
        // cookie predates the role claim, or a future staff-only login, could reach the
        // portal page it has no dashboard for.
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Announcements()
        {
            var vm = await BuildDashboardAsync();
            return View("Announcements", vm);
        }

        // GET: /Resident/Settings
        // Account, notification and appearance preferences for the signed-in resident.
        // Scoped to the Resident role like the rest of the portal.
        [HttpGet]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Settings()
        {
            var account = await GetCurrentUserAccountAsync();
            if (account is null)
            {
                return RedirectToAction("Login", "Account");
            }

            return View(_settingsStore.Load(account.Id));
        }

        // POST: /Resident/Settings
        // Persists the security, notification and appearance preferences for the
        // signed-in resident. The account id comes from the authenticated claim, never
        // from the form, so one resident cannot write another resident's preferences.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> SaveSettings(ResidentSettingsViewModel model)
        {
            var account = await GetCurrentUserAccountAsync();
            if (account is null)
            {
                return RedirectToAction("Login", "Account");
            }

            if (!ModelState.IsValid)
            {
                TempData["SettingsErrorMessage"] =
                    "Some preferences could not be read. Please review your choices and try again.";

                return RedirectToAction(nameof(Settings));
            }

            _settingsStore.Save(account.Id, model);
            TempData["SettingsSuccessMessage"] = "Your preferences have been saved.";
            return RedirectToAction(nameof(Settings));
        }

        // POST: /Resident/UpdateNotificationPreference
        // AJAX endpoint for toggling a single notification preference.
        // Persists directly to the UserAccount row so the setting is immediately
        // effective for notification creation logic without requiring a full page save.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> UpdateNotificationPreference(string preference, bool enabled)
        {
            var account = await GetCurrentUserAccountAsync();
            if (account is null)
            {
                return Json(new { success = false, message = "Not authenticated" });
            }

            switch (preference)
            {
                case "RequestUpdates":
                    account.NotifyRequestUpdates = enabled;
                    break;
                case "Announcements":
                    account.NotifyAnnouncements = enabled;
                    break;
                default:
                    return Json(new { success = false, message = "Invalid preference key" });
            }

            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        // POST: /Resident/SavePersonalInfo
        // Persists the resident-editable profile fields (date of birth, civil status and
        // sex). Written to the signed-in account, never to an id from the form, so a
        // resident can only ever edit their own record. Values are constrained to a
        // fixed vocabulary here rather than trusted from the select, because a hand-made
        // POST could otherwise store arbitrary text in a column the UI treats as an enum.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> SavePersonalInfo(
            DateTime? dateOfBirth,
            string? civilStatus,
            string? sex,
            string? purok,
            string? suffix)
        {
            var account = await GetCurrentUserAccountAsync();
            if (account is null)
            {
                return RedirectToAction("Login", "Account");
            }

            var civilStatuses = new[] { "Single", "Married", "Widowed", "Separated", "Divorced", "Annulled" };
            var sexes = new[] { "Male", "Female" };

            var normalizedCivilStatus = NormalizeChoice(civilStatus, civilStatuses);
            if (normalizedCivilStatus is null)
            {
                TempData["ProfileErrorMessage"] = "Please choose a valid civil status.";
                return RedirectToAction(nameof(Profile));
            }

            var normalizedSex = NormalizeChoice(sex, sexes);
            if (normalizedSex is null)
            {
                TempData["ProfileErrorMessage"] = "Please choose a valid sex.";
                return RedirectToAction(nameof(Profile));
            }

            if (dateOfBirth.HasValue && dateOfBirth.Value.Date > DateTime.Today)
            {
                TempData["ProfileErrorMessage"] = "Date of birth cannot be in the future.";
                return RedirectToAction(nameof(Profile));
            }

            // Purok is stored on the account row, not on the barangay Residents record:
            // the account is the resident's own, so this is the only row they are allowed
            // to write, and BuildDashboardAsync already prefers account.Purok over the
            // linked resident's when the two are shown side by side.
            var normalizedPurok = (purok ?? string.Empty).Trim();
            if (normalizedPurok.Length > 64)
            {
                TempData["ProfileErrorMessage"] = "Purok must be 64 characters or fewer.";
                return RedirectToAction(nameof(Profile));
            }

            // Suffix is optional, max 16 characters (e.g., Jr., Sr., III)
            var normalizedSuffix = (suffix ?? string.Empty).Trim();
            if (normalizedSuffix.Length > 16)
            {
                TempData["ProfileErrorMessage"] = "Suffix must be 16 characters or fewer.";
                return RedirectToAction(nameof(Profile));
            }

            account.DateOfBirth = dateOfBirth?.Date;
            account.CivilStatus = normalizedCivilStatus;
            account.Gender = normalizedSex;
            account.Purok = normalizedPurok;
            account.Suffix = normalizedSuffix;

            await _db.SaveChangesAsync();
            await RefreshSignInAsync(account);

            TempData["ProfileSuccessMessage"] = "Your personal information has been saved.";
            return RedirectToAction(nameof(Profile));
        }

        // Matches a posted value against the allowed list (case-insensitively) and returns
        // the canonical spelling. Blank is valid and clears the field; anything that is
        // neither blank nor a listed option returns null so the caller can reject it.
        private static string? NormalizeChoice(string? value, string[] allowed)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            return allowed.FirstOrDefault(option =>
                string.Equals(option, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        // POST: /Resident/ChangePassword
        // Handler for the "Change password" modal. The current password is re-verified
        // against the stored hash even though the resident is already signed in, because
        // a session can outlive a password change. Re-hashing is unconditional:
        // HashPassword applies a fresh random salt, so the same password never produces
        // the same stored value twice (and this also upgrades a stale hash).
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            var account = await GetCurrentUserAccountAsync();
            if (account is null)
            {
                return RedirectToAction("Login", "Account");
            }

            if (!ModelState.IsValid)
            {
                TempData["SettingsErrorMessage"] = FirstModelError(
                    "Please fill in all three password fields to change your password.");

                return RedirectToAction(nameof(Settings));
            }

            var verification = _passwordHasher.VerifyHashedPassword(
                account, account.PasswordHash, model.CurrentPassword);

            if (verification == PasswordVerificationResult.Failed)
            {
                TempData["SettingsErrorMessage"] = "Your current password is incorrect.";
                return RedirectToAction(nameof(Settings));
            }

            account.PasswordHash = _passwordHasher.HashPassword(account, model.NewPassword);
            await _db.SaveChangesAsync();

            TempData["SettingsSuccessMessage"] = "Your password was updated successfully.";
            return RedirectToAction(nameof(Settings));
        }

        // POST: /Resident/SignOutOtherDevices
        // Records a per-account cutoff (see ResidentSettingsStore) that the auth cookie
        // pipeline treats as "sign out every session issued before now", then re-issues
        // this device's cookie so the resident who clicked the button stays signed in.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> SignOutOtherDevices()
        {
            var account = await GetCurrentUserAccountAsync();
            if (account is null)
            {
                return RedirectToAction("Login", "Account");
            }

            // The cutoff is written first, so the cookie re-issued below is guaranteed to
            // be newer than it and this device is not caught by its own revocation.
            _settingsStore.SignOutOtherDevices(account.Id);
            await RefreshSignInAsync(account);

            TempData["SettingsSuccessMessage"] = "You have been signed out of all other devices.";
            return RedirectToAction(nameof(Settings));
        }

        /// <summary>
        /// The first validation message collected for this post, or
        /// <paramref name="fallback"/> when validation produced none. The modal is a
        /// full-page submit with no field-level redisplay, so the reason is surfaced
        /// through the same TempData banner the rest of the page uses.
        /// </summary>
        private string FirstModelError(string fallback)
        {
            var first = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

            return string.IsNullOrWhiteSpace(first) ? fallback : first;
        }

        /// <summary>
        /// Re-issues the resident's auth cookie from the current account row. Used after
        /// "Sign out other devices" so this device's freshly issued ticket is newer than
        /// the cutoff that revokes the others.
        /// </summary>
        private async Task RefreshSignInAsync(UserAccount account)
        {
            await UserSignIn.SignInAsync(HttpContext, account);
        }

        private async Task<ResidentDashboardViewModel> BuildDashboardAsync()
        {
            var displayName = UserClaims.GetDisplayName(User) ?? string.Empty;
            var email = UserClaims.GetEmail(User) ?? string.Empty;
            var account = await GetCurrentUserAccountAsync();

            var residentName = account?.DisplayName ?? displayName;
            if (string.IsNullOrWhiteSpace(residentName))
            {
                residentName = User.Identity?.Name ?? "Resident";
            }

            var allResidents = await _db.Residents.AsNoTracking().ToListAsync();
            var resident = allResidents.FirstOrDefault(r =>
                string.Equals((r.Name ?? string.Empty).Trim(),
                    residentName.Trim(),
                    StringComparison.OrdinalIgnoreCase));

            var myRequests = (await _db.DocumentRequests.AsNoTracking().ToListAsync())
                .Where(d => (account != null && d.UserAccountId == account.Id)
                    || (d.UserAccountId == null && string.Equals((d.Resident ?? string.Empty).Trim(),
                        residentName.Trim(),
                        StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(d => ParseRequestDate(d.Date))
                .ThenByDescending(d => d.Id)
                .ToList();

            var announcements = await _db.Announcements
                .AsNoTracking()
                .OrderByDescending(a => a.Id)
                .Take(5)
                .ToListAsync();

            return new ResidentDashboardViewModel
            {
                ResidentName = residentName,
                ResidentId = resident?.Id ?? string.Empty,
                Address = resident?.Purok ?? string.Empty,
                VerificationStatus = resident != null ? "Verified resident" : "Unverified",
                FirstName = account?.FirstName ?? string.Empty,
                MiddleName = account?.MiddleName ?? string.Empty,
                LastName = account?.LastName ?? string.Empty,
                Suffix = account?.Suffix ?? string.Empty,
                Age = account?.Age,
                Gender = account?.Gender ?? resident?.Sex ?? string.Empty,
                Purok = account?.Purok ?? resident?.Purok ?? string.Empty,
                VoterStatus = account?.VoterStatus ?? (resident?.IsVoter == true ? "Registered Voter" : string.Empty),
                Email = account?.Email ?? email,
                ContactNumber = account?.ContactNumber ?? string.Empty,
                AccountCreatedUtc = account?.CreatedUtc,
                ProfilePhotoUrl = UserClaims.BuildPhotoUrl(account?.ProfilePhoto) ?? string.Empty,
                DateOfBirth = account?.DateOfBirth,
                CivilStatus = account?.CivilStatus ?? string.Empty,
                RegisteredAddress = ComposeRegisteredAddress(account?.Purok ?? resident?.Purok),
                PendingRequestsCount = myRequests.Count(d => IsPending(d.Status)),
                ReadyForPickupCount = myRequests.Count(d => IsReadyForPickup(d.Status)),
                DocumentsReceivedCount = myRequests.Count(d => IsIssued(d.Status)),
                RecentRequests = myRequests
                    .Take(20)
                    .Select(ToResidentRequestItem)
                    .ToList(),
                Announcements = announcements
                    .Select(a => new ResidentAnnouncementItem
                    {
                        Id = a.Id,
                        Title = a.Title,
                        Description = a.Purpose,
                        Category = a.TargetAudience,
                        DateString = ToDateBadge(a.Date),
                        FullDate = a.Date,
                        ScheduleTime = a.ScheduleTime
                    })
                    .ToList()
            };
        }

        /// <summary>
        /// Builds the resident's registered address from the purok on their record plus
        /// the admin-configured barangay profile (Barangay / Municipality / Province).
        /// The barangay profile has no street field of its own, so this is the most
        /// specific address the portal can show; composing it here means the profile
        /// card never falls back to "No address on file" once a barangay is configured.
        /// </summary>
        private string ComposeRegisteredAddress(string? purok)
        {
            var profile = _barangayProfile.Current;

            var locality = string.Join(
                ", ",
                new[] { profile.BarangayName, profile.Municipality, profile.Province }
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => part.Trim()));

            var trimmedPurok = (purok ?? string.Empty).Trim();

            if (trimmedPurok.Length == 0)
            {
                return locality;
            }

            return locality.Length == 0
                ? trimmedPurok
                : $"{trimmedPurok}, {locality}";
        }

        /// <summary>
        /// Whole-year age from a date of birth, or <c>null</c> when no birth date is on
        /// file (so the caller can fall back to the age captured at registration).
        /// Clamped at zero so a future-dated value cannot produce a negative age that the
        /// number input would then refuse to show.
        /// </summary>
        private static int? CalculateAge(DateTime? dateOfBirth)
        {
            if (!dateOfBirth.HasValue)
            {
                return null;
            }

            var today = DateTime.Today;
            var dob = dateOfBirth.Value.Date;
            var age = today.Year - dob.Year;

            if (dob > today.AddYears(-age))
            {
                age--;
            }

            return Math.Max(0, age);
        }

        private static bool IsPending(string? status) =>
            string.Equals((status ?? string.Empty).Trim(), "Pending", StringComparison.OrdinalIgnoreCase);

        private static bool IsReadyForPickup(string? status) =>
            string.Equals((status ?? string.Empty).Trim(), "Ready for pickup", StringComparison.OrdinalIgnoreCase)
            || string.Equals((status ?? string.Empty).Trim(), "Approved", StringComparison.OrdinalIgnoreCase);

        private static bool IsIssued(string? status) =>
            string.Equals((status ?? string.Empty).Trim(), "Issued", StringComparison.OrdinalIgnoreCase)
            || string.Equals((status ?? string.Empty).Trim(), "Released", StringComparison.OrdinalIgnoreCase)
            || string.Equals((status ?? string.Empty).Trim(), "Completed", StringComparison.OrdinalIgnoreCase);

        private static bool IsCancelled(string? status) =>
            string.Equals((status ?? string.Empty).Trim(), "Cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals((status ?? string.Empty).Trim(), "Canceled", StringComparison.OrdinalIgnoreCase);

        private static DateTime ParseRequestDate(string? value) =>
            DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;

        private static string ToDateBadge(string? date)
        {
            if (DateTime.TryParse(date, out var parsed))
            {
                return parsed.ToString("MMM d").ToUpperInvariant();
            }
            return (date ?? string.Empty).Trim().ToUpperInvariant();
        }
    }
}
