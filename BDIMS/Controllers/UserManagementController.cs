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
    /// Admin module for viewing and managing registered system accounts
    /// (<see cref="UserAccount"/>): approving or rejecting pending registrations,
    /// temporarily blocking an account for a number of hours, and deleting an
    /// account outright. Access is restricted to administrators.
    ///
    /// Every action writes its outcome to <c>TempData["SuccessMessage"]</c> or
    /// <c>TempData["ErrorMessage"]</c>, which the shared layout renders as a toast,
    /// and then redirects back to the list - the same pattern the other modules use,
    /// so results are always visible next to the row they concern.
    /// </summary>
    [Authorize(Roles = "Administrator")]
    public class UserManagementController : Controller
    {
        /// <summary>Longest block an administrator can set, in hours (7 days).</summary>
        private const int MaxBlockHours = 168;

        /// <summary>Placeholder shown when an account has no usable profile photo.</summary>
        private const string DefaultAvatarUrl = "/images/default-avatar.png";

        private readonly ApplicationDbContext _db;

        public UserManagementController(ApplicationDbContext db)
        {
            _db = db;
        }

        // GET: /UserManagement
        public async Task<IActionResult> Index()
        {
            // Only administrators may manage user accounts.
            if (!User.Identity?.IsAuthenticated == true || !User.IsInRole("Administrator"))
            {
                return Forbid();
            }

            var accounts = await _db.UserAccounts
                .OrderByDescending(u => u.CreatedUtc)
                .ToListAsync();

            // Blocks are time-boxed, so a row rendered here can be past its expiry
            // (nobody was looking when the hours ran out). Lifting them before the
            // list is returned keeps the STATUS badges and the Block/Unblock actions
            // honest without a background job watching the clock - login does the
            // same check on its side.
            var lockExpired = false;

            foreach (var account in accounts)
            {
                if (account.HasExpiredLock)
                {
                    account.ApplyStatus(UserAccountStatus.Active);
                    lockExpired = true;
                }
            }

            if (lockExpired)
            {
                await _db.SaveChangesAsync();
            }

            // Used by the view to hide the Delete button on the signed-in admin's
            // own row (the controller also rejects the action server-side), and to
            // put the live count on the "Pending Approval" filter tab.
            ViewBag.CurrentUserId = CurrentUserId;
            ViewBag.PendingCount = accounts.Count(
                account => account.Status == UserAccountStatus.Pending);

            return View(accounts);
        }

        // POST: /UserManagement/ApproveUser
        /// <summary>
        /// Approves an account awaiting registration review (or re-approves a rejected
        /// one), moving it to <see cref="UserAccountStatus.Active"/> so it can sign in.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveUser(int id)
        {
            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            if (account.Status != UserAccountStatus.Pending
                && account.Status != UserAccountStatus.Rejected)
            {
                TempData["ErrorMessage"] =
                    $"{account.DisplayName} is not awaiting approval, so there is nothing to approve.";
                return RedirectToAction(nameof(Index));
            }

            account.ApplyStatus(UserAccountStatus.Active);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] =
                $"{account.DisplayName} was approved. The account can now sign in.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /UserManagement/UpdateUserAccount
        /// <summary>
        /// Saves the display name and assigned role from the Edit Account modal. The
        /// role list is fixed server-side so the form can never grant an arbitrary
        /// claim, and <see cref="UserAccount.IsAdmin"/> is kept in step with the
        /// Administrator role because the rest of the application checks both.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateUserAccount(int id, string? displayName, string? role)
        {
            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            var newDisplayName = (displayName ?? string.Empty).Trim();
            var newRole = (role ?? string.Empty).Trim();

            if (newDisplayName.Length == 0)
            {
                TempData["ErrorMessage"] = "Enter a display name for the account.";
                return RedirectToAction(nameof(Index));
            }

            if (newDisplayName.Length > 150)
            {
                TempData["ErrorMessage"] = "The display name cannot be longer than 150 characters.";
                return RedirectToAction(nameof(Index));
            }

            if (!IsAssignableRole(newRole))
            {
                TempData["ErrorMessage"] = "Choose a valid role for the account.";
                return RedirectToAction(nameof(Index));
            }

            // "Barangay Secretary" is the administrator role the seed account uses
            // (see DbInitializer.AdminRole), while "Administrator" is the role written
            // by Add/Update; both grant administrative access, so both are preserved.
            var keepsAdministrator = string.Equals(
                    newRole,
                    "Administrator",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    newRole,
                    "Barangay Secretary",
                    StringComparison.OrdinalIgnoreCase);

            // An administrator must not remove their own administrator access: the
            // cookie would still carry the old role until the next sign-in, but the
            // account would be locked out of User Management immediately after that.
            if (id == CurrentUserId && !keepsAdministrator)
            {
                TempData["ErrorMessage"] =
                    "You cannot remove administrator access from the account you are signed in with.";
                return RedirectToAction(nameof(Index));
            }

            account.DisplayName = newDisplayName;
            account.Role = newRole;
            account.IsAdmin = keepsAdministrator;
            account.Initials = UserAccount.DeriveInitials(newDisplayName);

            await _db.SaveChangesAsync();

            // The edited user's auth cookie is not rewritten here - it lives in
            // their browser. Program.cs's cookie-validation event calls
            // UserSignIn.RefreshPrincipalAsync on every request, which re-derives
            // the principal from this row: the new role, name and initials take
            // effect on their next click, and a demoted user is refused the old
            // portal immediately instead of riding out the cookie's lifetime.

            TempData["SuccessMessage"] =
                $"{account.DisplayName}'s account details were updated.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /UserManagement/ToggleUserStatus
        /// <summary>
        /// Toggles an account between <see cref="UserAccountStatus.Active"/> and
        /// <see cref="UserAccountStatus.Deactivated"/>. Pending, rejected and blocked
        /// accounts keep their own lifecycle and are not changed here.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleUserStatus(int id)
        {
            if (id == CurrentUserId)
            {
                TempData["ErrorMessage"] = "You cannot deactivate the account you are signed in with.";
                return RedirectToAction(nameof(Index));
            }

            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            // The row hides the toggle for administrators; this guard keeps a direct
            // POST from bypassing the UI and disabling another administrator.
            if (account.IsAdmin)
            {
                TempData["ErrorMessage"] =
                    "Administrator accounts cannot be deactivated from this action.";
                return RedirectToAction(nameof(Index));
            }

            if (account.Status == UserAccountStatus.Active)
            {
                account.ApplyStatus(UserAccountStatus.Deactivated);
                await _db.SaveChangesAsync();

                TempData["SuccessMessage"] =
                    $"{account.DisplayName}'s account was deactivated. The account cannot sign in.";
                return RedirectToAction(nameof(Index));
            }

            if (account.Status == UserAccountStatus.Deactivated)
            {
                account.ApplyStatus(UserAccountStatus.Active);
                await _db.SaveChangesAsync();

                TempData["SuccessMessage"] =
                    $"{account.DisplayName}'s account was activated. The account can sign in again.";
                return RedirectToAction(nameof(Index));
            }

            TempData["ErrorMessage"] =
                $"Only active or deactivated accounts can be toggled. {account.DisplayName} is {StatusText(account.Status)}.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /UserManagement/RejectUser
        /// <summary>
        /// Rejects a pending registration: the account stays on file (nothing is
        /// deleted) but is refused at sign-in until an administrator approves it.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectUser(int id)
        {
            if (id == CurrentUserId)
            {
                TempData["ErrorMessage"] = "You cannot reject your own account.";
                return RedirectToAction(nameof(Index));
            }

            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            if (account.Status != UserAccountStatus.Pending)
            {
                TempData["ErrorMessage"] =
                    $"Only accounts awaiting approval can be rejected. {account.DisplayName} is {StatusText(account.Status).ToLowerInvariant()}.";
                return RedirectToAction(nameof(Index));
            }

            account.ApplyStatus(UserAccountStatus.Rejected);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] =
                $"{account.DisplayName}'s registration was rejected. The account cannot sign in until it is approved.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /UserManagement/Approve
        /// <summary>
        /// Approves a pending user account (alias of <see cref="ApproveUser"/> with the
        /// short action name the User Management view posts to).
        /// Finds the account by id, sets status to Active, and saves changes.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(int id)
        {
            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            if (account.Status != UserAccountStatus.Pending
                && account.Status != UserAccountStatus.Rejected)
            {
                TempData["ErrorMessage"] =
                    $"{account.DisplayName} is not awaiting approval, so there is nothing to approve.";
                return RedirectToAction(nameof(Index));
            }

            account.ApplyStatus(UserAccountStatus.Active);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] =
                $"{account.DisplayName} was approved. The account can now sign in.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /UserManagement/Reject
        /// <summary>
        /// Rejects a pending user account (alias of <see cref="RejectUser"/> with the
        /// short action name the User Management view posts to).
        /// Finds the account by id, sets status to Rejected, and saves changes.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reject(int id)
        {
            if (id == CurrentUserId)
            {
                TempData["ErrorMessage"] = "You cannot reject your own account.";
                return RedirectToAction(nameof(Index));
            }

            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            if (account.Status != UserAccountStatus.Pending)
            {
                TempData["ErrorMessage"] =
                    $"Only accounts awaiting approval can be rejected. {account.DisplayName} is {StatusText(account.Status).ToLowerInvariant()}.";
                return RedirectToAction(nameof(Index));
            }

            account.ApplyStatus(UserAccountStatus.Rejected);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] =
                $"{account.DisplayName}'s registration was rejected. The account cannot sign in until it is approved.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /UserManagement/BlockUser
        /// <summary>
        /// Temporarily blocks an active account for a fixed number of hours by stamping
        /// <see cref="UserAccount.LockoutEnd"/>. The block lifts on its own when the
        /// hours run out - login and the list both notice the elapsed expiry - and can
        /// be lifted earlier with <see cref="UnblockUser"/>.
        /// </summary>
        /// <param name="id">Account to block.</param>
        /// <param name="hours">Duration of the block, 1..<see cref="MaxBlockHours"/>.</param>
        /// <param name="reason">Optional note recorded with the block.</param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BlockUser(int id, int hours, string? reason)
        {
            if (id == CurrentUserId)
            {
                TempData["ErrorMessage"] = "You cannot block your own account.";
                return RedirectToAction(nameof(Index));
            }

            if (hours < 1 || hours > MaxBlockHours)
            {
                TempData["ErrorMessage"] =
                    $"Enter a block duration between 1 and {MaxBlockHours} hours (7 days).";
                return RedirectToAction(nameof(Index));
            }

            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            if (account.Status != UserAccountStatus.Active)
            {
                TempData["ErrorMessage"] =
                    $"Only active accounts can be blocked. {account.DisplayName} is {StatusText(account.Status).ToLowerInvariant()}.";
                return RedirectToAction(nameof(Index));
            }

            var lockoutEnd = DateTime.UtcNow.AddHours(hours);

            account.ApplyStatus(UserAccountStatus.Blocked);
            account.LockoutEnd = lockoutEnd;
            account.LockoutReason = TrimReason(reason);

            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] =
                $"{account.DisplayName} was blocked for {hours} hour{(hours == 1 ? "" : "s")}. " +
                $"Sign-in is disabled until {lockoutEnd.ToLocalTime():MMM d, yyyy h:mm tt} (local time).";
            return RedirectToAction(nameof(Index));
        }

        // POST: /UserManagement/UnblockUser
        /// <summary>
        /// Lifts a temporary block before it expires, restoring the account to
        /// <see cref="UserAccountStatus.Active"/> immediately.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UnblockUser(int id)
        {
            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            if (account.Status != UserAccountStatus.Blocked)
            {
                TempData["ErrorMessage"] = $"{account.DisplayName} is not blocked.";
                return RedirectToAction(nameof(Index));
            }

            account.ApplyStatus(UserAccountStatus.Active);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] =
                $"{account.DisplayName}'s block was lifted. The account can sign in again.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /UserManagement/DeleteUser
        /// <summary>
        /// Permanently removes an account. Guarded so an administrator can never
        /// delete the account they are signed in with, nor the last remaining
        /// administrator - either would lock the deployment out of its own modules.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(int id)
        {
            if (id == CurrentUserId)
            {
                TempData["ErrorMessage"] =
                    "You cannot delete the account you are signed in with. Ask another administrator to delete it.";
                return RedirectToAction(nameof(Index));
            }

            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            // The row itself counts, so an administrator looking at the only admin
            // account (other than themselves) still cannot remove it.
            if (account.IsAdmin
                && await _db.UserAccounts.CountAsync(u => u.IsAdmin) <= 1)
            {
                TempData["ErrorMessage"] =
                    "The last administrator account cannot be deleted.";
                return RedirectToAction(nameof(Index));
            }

            var displayName = account.DisplayName;

            _db.UserAccounts.Remove(account);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = $"{displayName}'s account was deleted.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /UserManagement/SetUserLockout
        /// <summary>
        /// Timed account lockout for the block-duration modal. Writes the same
        /// expiry an Identity <c>UserManager.SetLockoutEndDateAsync(user,
        /// DateTimeOffset.UtcNow.AddHours(durationHours))</c> call would store,
        /// directly onto <see cref="UserAccount.LockoutEnd"/> - BDIMS does not
        /// register UserManager (no Identity user store; see <see cref="UserAccount"/>),
        /// so the native column is the lockout the login path already honours.
        /// <paramref name="durationHours"/> of 0 (or less) is "Unlock / Unblock Now".
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetUserLockout(string userId, int durationHours)
        {
            if (!int.TryParse(userId, out var id))
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            if (id == CurrentUserId)
            {
                TempData["ErrorMessage"] = "You cannot lock or unlock your own account.";
                return RedirectToAction(nameof(Index));
            }

            var account = await _db.UserAccounts.FindAsync(id);

            if (account == null)
            {
                TempData["ErrorMessage"] = "That account no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            // durationHours <= 0 is the "Unlock / Unblock Now" option.
            if (durationHours <= 0)
            {
                if (account.Status != UserAccountStatus.Blocked)
                {
                    TempData["ErrorMessage"] =
                        $"{account.DisplayName} is not locked, so there is nothing to unlock.";
                    return RedirectToAction(nameof(Index));
                }

                account.ApplyStatus(UserAccountStatus.Active);
                await _db.SaveChangesAsync();

                TempData["SuccessMessage"] =
                    $"{account.DisplayName} was unlocked. The account can sign in again.";
                return RedirectToAction(nameof(Index));
            }

            if (durationHours > MaxBlockHours)
            {
                TempData["ErrorMessage"] =
                    $"Enter a lock duration between 1 and {MaxBlockHours} hours (7 days).";
                return RedirectToAction(nameof(Index));
            }

            if (account.Status != UserAccountStatus.Active
                && account.Status != UserAccountStatus.Blocked)
            {
                TempData["ErrorMessage"] =
                    $"Only active accounts can be locked. {account.DisplayName} is {StatusText(account.Status).ToLowerInvariant()}.";
                return RedirectToAction(nameof(Index));
            }

            // Same instant SetLockoutEndDateAsync would persist: now + N hours UTC.
            var lockoutEnd = DateTime.UtcNow.AddHours(durationHours);

            // ApplyStatus first (it clears LockoutEnd when leaving Blocked), then the
            // new expiry - mirroring BlockUser so the columns never disagree.
            account.ApplyStatus(UserAccountStatus.Blocked);
            account.LockoutEnd = lockoutEnd;

            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] =
                $"{account.DisplayName} was locked for {durationHours} hour{(durationHours == 1 ? "" : "s")}. " +
                $"Sign-in is disabled until {lockoutEnd.ToLocalTime():MMM d, yyyy h:mm tt} (local time).";
            return RedirectToAction(nameof(Index));
        }

        // GET: /UserManagement/GetUserProfile
        /// <summary>
        /// Full profile payload for the "View Profile Details" eye modal: every
        /// profile field except <see cref="UserAccount.PasswordHash"/> (never
        /// returned), with the photo URL resolved through the existing
        /// <see cref="BDIMS.Services.UserClaims.BuildPhotoUrl"/> helper and the
        /// dates pre-formatted for display.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUserProfile(int id)
        {
            var account = await _db.UserAccounts
                .FirstOrDefaultAsync(u => u.Id == id);

            if (account == null)
            {
                return NotFound(new { success = false, message = "That account no longer exists." });
            }

            // A timed lockout lifts itself when the clock runs out. The list and
            // the sign-in path both clear the expired block, so the profile
            // payload does the same - otherwise the modal would keep reporting
            // "Blocked" for an account that can already sign in again.
            if (account.HasExpiredLock)
            {
                account.ApplyStatus(UserAccountStatus.Active);
                await _db.SaveChangesAsync();
            }

            // Merge the account with the matching roster row: the account carries the
            // fields the resident saves from My Profile, while the Residents table holds
            // the barangay's older registry values. Account values win when both exist.
            var resident = await FindLinkedResidentAsync(account);

            var residentName = (resident?.Name ?? string.Empty).Trim();
            var residentWords = residentName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var residentFirstName = residentWords.FirstOrDefault() ?? string.Empty;
            var residentLastName = residentWords.Length > 1
                ? string.Join(" ", residentWords.Skip(1))
                : string.Empty;

            var displayName = FirstNonEmpty(account.DisplayName, residentName);
            var firstName = FirstNonEmpty(account.FirstName, residentFirstName);
            var lastName = FirstNonEmpty(account.LastName, residentLastName);
            var purok = FirstNonEmpty(account.Purok, resident?.Purok);
            var gender = FirstNonEmpty(account.Gender, resident?.Sex);
            var contactNumber = FirstNonEmpty(account.ContactNumber, resident?.Contact);
            var voterStatus = FirstNonEmpty(
                account.VoterStatus,
                resident is null ? string.Empty : (resident.IsVoter ? "Registered Voter" : "Non-Voter"));

            // The portal upload is stored on the account row as ProfilePhoto (the
            // resident's profile picture). ResolvePath verifies the file still exists, so
            // a deleted/never-uploaded photo falls back to the clean placeholder instead
            // of a broken image.
            var profilePictureUrl = BuildProfilePictureUrl(account.ProfilePhoto);

            // The profile modal shows a live age, not the number typed at registration.
            // The account's own Date of Birth is authoritative; the stored Age is only a
            // fallback for accounts that have not filled in a birth date yet.
            var dateOfBirth = account.DateOfBirth;
            var age = CalculateAge(dateOfBirth) ?? account.Age;

            return Json(new
            {
                success = true,
                id = account.Id,
                email = account.Email,
                displayName,
                firstName,
                middleName = account.MiddleName,
                lastName,
                suffix = account.Suffix,
                employeeId = account.EmployeeId,
                role = account.Role,
                isAdmin = account.IsAdmin,
                status = StatusLabel(account.Status),
                isActive = account.IsActive,
                age,
                gender,
                dateOfBirth = dateOfBirth?.ToString("MMM d, yyyy") ?? "-",
                civilStatus = account.CivilStatus,
                purok,
                voterStatus,
                contactNumber,
                profilePictureUrl,
                // Kept for the existing modal and any older callers.
                photoUrl = profilePictureUrl,
                createdUtc = account.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy h:mm tt"),
                lockoutEnd = account.LockoutEnd?.ToLocalTime().ToString("MMM d, yyyy h:mm tt") ?? string.Empty,
                lockoutReason = account.LockoutReason ?? string.Empty
            });
        }

        // GET: /UserManagement/GetUserDetails?userId=123
        /// <summary>
        /// String-id alias of <see cref="GetUserProfile"/> used by callers that address
        /// an account by userId. The payload, including the date-of-birth-derived age,
        /// is identical.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUserDetails(string userId)
        {
            if (!int.TryParse(userId, out var id))
            {
                return NotFound(new { success = false, message = "That account no longer exists." });
            }

            return await GetUserProfile(id);
        }

        // GET: /UserManagement/ProfilePhoto?file=user-photo-...
        /// <summary>
        /// Serves a resident's uploaded profile picture to an administrator. The public
        /// Account/ProfilePhoto action only serves the signed-in user's own file, which is
        /// why the admin modal previously received a 404 for every resident photo.
        /// </summary>
        [HttpGet]
        public IActionResult ProfilePhoto(string? file)
        {
            if (string.IsNullOrWhiteSpace(file)
                || !_db.UserAccounts.AsNoTracking().Any(u => u.ProfilePhoto == file))
            {
                return NotFound();
            }

            var path = BDIMS.Services.ProfilePhotoStore.ResolvePath(file);

            if (path is null)
            {
                return NotFound();
            }

            return PhysicalFile(path, BDIMS.Services.ProfilePhotoStore.ContentTypeFor(file));
        }

        /// <summary>
        /// URL the admin modal can load for an account's saved profile picture, or the
        /// default avatar when the account has no usable photo.
        /// </summary>
        private static string BuildProfilePictureUrl(string? fileName)
        {
            var name = (fileName ?? string.Empty).Trim();

            if (name.Length == 0 || BDIMS.Services.ProfilePhotoStore.ResolvePath(name) is null)
            {
                return DefaultAvatarUrl;
            }

            return "/UserManagement/ProfilePhoto?file=" + Uri.EscapeDataString(name);
        }

        /// <summary>
        /// Finds the Residents roster row that belongs to <paramref name="account"/>.
        /// The roster has no email column, so the account is resolved first (it is the
        /// row that carries the email) and the registry row is matched on contact number
        /// or name. Returns <c>null</c> when the resident is not in the registry.
        /// </summary>
        private async Task<ResidentModel?> FindLinkedResidentAsync(UserAccount account)
        {
            var contact = (account.ContactNumber ?? string.Empty).Trim();
            var displayName = (account.DisplayName ?? string.Empty).Trim();
            var fullName = string.Join(
                " ",
                new[] { account.FirstName, account.MiddleName, account.LastName, account.Suffix }
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => part!.Trim()));

            var residents = await _db.Residents.AsNoTracking().ToListAsync();

            return residents.FirstOrDefault(r =>
                (contact.Length > 0
                    && string.Equals((r.Contact ?? string.Empty).Trim(), contact, StringComparison.OrdinalIgnoreCase))
                || (displayName.Length > 0
                    && string.Equals((r.Name ?? string.Empty).Trim(), displayName, StringComparison.OrdinalIgnoreCase))
                || (fullName.Length > 0
                    && string.Equals((r.Name ?? string.Empty).Trim(), fullName, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>First non-blank value in the list, trimmed, or an empty string.</summary>
        private static string FirstNonEmpty(params string?[] values) =>
            values.Select(value => value?.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;

        /// <summary>
        /// Whole-year age from a date of birth, adjusting for whether this year's
        /// birthday has already passed. Returns <c>null</c> when no birth date is on
        /// file so callers can fall back to the age captured at registration.
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

        /// <summary>
        /// Id of the administrator making the request, read from the auth cookie.
        /// <c>null</c> when the claim is missing or malformed, in which case the
        /// self-action guards simply never match.
        /// </summary>
        private int? CurrentUserId
        {
            get
            {
                var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                return int.TryParse(raw, out var id) ? id : null;
            }
        }

        /// <summary>
        /// Roles the Edit Account modal may assign: the system's only three
        /// permissions (Administrator, Barangay Secretary, Resident). Kept in
        /// sync with the options in <c>Views/UserManagement/Index.cshtml</c>
        /// and rejected server-side so a tampered form cannot write an
        /// arbitrary role into the account row.
        /// </summary>
        private static bool IsAssignableRole(string role) =>
            string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Barangay Secretary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Resident", StringComparison.OrdinalIgnoreCase);

        /// <summary>Human-readable status used in the feedback messages.</summary>
        private static string StatusText(UserAccountStatus status) => status switch
        {
            UserAccountStatus.Pending => "pending approval",
            UserAccountStatus.Active => "active",
            UserAccountStatus.Rejected => "rejected",
            UserAccountStatus.Blocked => "blocked",
            UserAccountStatus.Deactivated => "deactivated",
            _ => status.ToString()
        };

        /// <summary>
        /// Title-cased status label for display (the table badges and the profile
        /// modal). Distinct from <see cref="StatusText"/>, which is the
        /// sentence-case wording the feedback messages embed in a sentence.
        /// </summary>
        private static string StatusLabel(UserAccountStatus status) => status switch
        {
            UserAccountStatus.Pending => "Pending",
            UserAccountStatus.Active => "Active",
            UserAccountStatus.Rejected => "Rejected",
            UserAccountStatus.Blocked => "Blocked",
            UserAccountStatus.Deactivated => "Deactivated",
            _ => status.ToString()
        };

        /// <summary>
        /// Normalises the optional block reason: blank becomes <c>null</c> (so the
        /// column stays empty rather than holding whitespace) and anything longer
        /// than the column allows is trimmed here rather than being rejected by the
        /// database.
        /// </summary>
        private static string? TrimReason(string? reason)
        {
            var trimmed = (reason ?? string.Empty).Trim();

            if (trimmed.Length == 0)
            {
                return null;
            }

            return trimmed.Length > 256 ? trimmed.Substring(0, 256) : trimmed;
        }
    }
}
