using BDIMS.Data;
using BDIMS.Models;
using BDIMS.Services;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDIMS.Controllers
{
    public class AccountController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly IPasswordHasher<UserAccount> _passwordHasher;

        public AccountController(ApplicationDbContext db, IPasswordHasher<UserAccount> passwordHasher)
        {
            _db = db;
            _passwordHasher = passwordHasher;
        }

        // GET: /Account/Login
        [HttpGet]
        [AllowAnonymous]
        public IActionResult Login()
        {
            // A signed-in user hitting the login URL goes straight to the dashboard
            // instead of being shown a form they do not need.
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Dashboard", "Home");
            }

            return View(new LoginViewModel());
        }

        // POST: /Account/Login
        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // The lookup key is the normalized email, which makes matching independent
            // of the database collation and of the casing the user typed.
            var normalizedEmail = UserAccount.NormalizeEmail(model.Username);

            var account = await _db.UserAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail);

            // The same generic message for "no such user", "wrong password" and
            // "inactive account", so the form cannot be used to discover which
            // email addresses are registered.
            const string invalidCredentialsMessage =
                "Invalid username or password. Please use the provided credentials.";

            if (account == null || !account.IsActive)
            {
                ModelState.AddModelError(string.Empty, invalidCredentialsMessage);
                return View(model);
            }

            // The submitted password is checked against the stored PBKDF2 hash by
            // Identity's PasswordHasher, which compares in constant time. The plaintext
            // is never written to the database and never logged.
            var verification = _passwordHasher.VerifyHashedPassword(
                account, account.PasswordHash, model.Password);

            if (verification == PasswordVerificationResult.Failed)
            {
                ModelState.AddModelError(string.Empty, invalidCredentialsMessage);
                return View(model);
            }

            // SuccessRehashNeeded means the stored hash uses outdated parameters (for
            // example after a framework upgrade). Re-hashing transparently upgrades it
            // with the current settings without forcing the user to change anything.
            if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            {
                account.PasswordHash = _passwordHasher.HashPassword(account, model.Password);
                await _db.SaveChangesAsync();
            }

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                BuildPrincipal(account),
                new AuthenticationProperties
                {
                    // The "Keep me signed in" checkbox maps onto a persistent cookie;
                    // otherwise the session cookie dies with the browser.
                    IsPersistent = model.KeepSigned
                });

            return RedirectToAction("Dashboard", "Home");
        }

        // POST: /Account/Logout
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            // Without this the auth cookie survives and the next request would still be
            // authenticated, making the logout button appear to do nothing.
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }

        /// <summary>
        /// Builds the signed-in principal. The display name, job title and avatar
        /// initials are copied into claims here, which is what lets the sidebar user
        /// widget render "IQ / Isagane Quarteros / Barangay Secretary" without querying
        /// the database on every page load.
        /// </summary>
        private static ClaimsPrincipal BuildPrincipal(UserAccount account)
        {
            var initials = account.ResolveInitials();

            var claims = new List<Claim>
            {
                // ClaimTypes.Name feeds User.Identity.Name; the widget reads the
                // dedicated claim below so it never depends on a name-format assumption.
                new(ClaimTypes.Name, account.DisplayName),

                // The authorization role is deliberately separate from the displayed job
                // title: IsAdmin is what grants full access, while Role is only shown.
                new(ClaimTypes.Role, account.IsAdmin ? "Administrator" : "Staff"),

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
        /// Key of the signed-in account, or <c>null</c> when the cookie is missing or
        /// malformed.
        /// </summary>
        private int? GetCurrentUserId()
        {
            var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return int.TryParse(raw, out var id) ? id : null;
        }

        /// <summary>
        /// Redisplay the form with the derived card values recomputed from what was
        /// typed, so validation errors do not snap the avatar back to the saved name.
        /// </summary>
        private ViewResult ViewWithDerivedValues(ProfileViewModel model)
        {
            model.DisplayName = UserAccount.ComposeDisplayName(
                model.FirstName,
                model.LastName,
                model.DisplayName);

            model.Initials = UserAccount.ComposeInitials(model.FirstName, model.LastName)
                ?? UserAccount.DeriveInitials(model.DisplayName);

            // Never echo a submitted password back into the markup.
            model.CurrentPassword = string.Empty;
            model.NewPassword = string.Empty;
            model.ConfirmPassword = string.Empty;

            return View("Profile", model);
        }

        // GET: /Account/Profile
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Profile()
        {
            var id = GetCurrentUserId();

            if (id is null)
            {
                return RedirectToAction(nameof(Login));
            }

            var account = await _db.UserAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(u => u.Id == id.Value);

            if (account is null)
            {
                // The account was removed underneath the cookie. Drop the stale cookie so
                // the user is not stranded on a page that can never be saved again.
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return RedirectToAction(nameof(Login));
            }

            return View(ProfileViewModel.FromAccount(account));
        }

        // POST: /Account/SaveProfile
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> SaveProfile(ProfileViewModel model)
        {
            var id = GetCurrentUserId();

            if (id is null)
            {
                return RedirectToAction(nameof(Login));
            }

            // Tracked query, so the assignments below are persisted through this instance.
            var account = await _db.UserAccounts
                .SingleOrDefaultAsync(u => u.Id == id.Value);

            if (account is null)
            {
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return RedirectToAction(nameof(Login));
            }

            model.Id = account.Id;

            // The upload is a separate request, so the photo never comes back through
            // model binding. Carried onto the model here so a re-displayed form (after a
            // validation failure, for instance) still shows the saved photo instead of
            // silently reverting the badge to the initials.
            model.PhotoUrl =
                BDIMS.Services.UserClaims.BuildPhotoUrl(account.ProfilePhoto) ?? string.Empty;

            if (!ModelState.IsValid)
            {
                return ViewWithDerivedValues(model);
            }

            var newEmail = (model.Email ?? string.Empty).Trim();

            // Email is the sign-in key, so a collision with another account would make
            // that account unreachable. The unique index would also reject the write;
            // checking here yields a readable message instead of a database error.
            var emailTaken = await _db.UserAccounts
                .AsNoTracking()
                .AnyAsync(u => u.NormalizedEmail == UserAccount.NormalizeEmail(newEmail)
                            && u.Id != account.Id);

            if (emailTaken)
            {
                ModelState.AddModelError(
                    nameof(model.Email),
                    "That email address is already used by another account.");

                return ViewWithDerivedValues(model);
            }

            if (!ApplyPasswordChange(account, model))
            {
                return ViewWithDerivedValues(model);
            }

            // --- Profile fields -------------------------------------------------
            account.FirstName = (model.FirstName ?? string.Empty).Trim();
            account.LastName = (model.LastName ?? string.Empty).Trim();
            account.EmployeeId = (model.EmployeeId ?? string.Empty).Trim();
            account.ContactNumber = (model.ContactNumber ?? string.Empty).Trim();
            account.Role = (model.Role ?? string.Empty).Trim();
            account.Email = newEmail;
            account.NormalizedEmail = UserAccount.NormalizeEmail(newEmail);

            // Display name and initials are derived, never taken from the form, so the
            // sidebar, the header badge and this page's card cannot drift apart.
            account.DisplayName = UserAccount.ComposeDisplayName(
                account.FirstName,
                account.LastName,
                account.DisplayName);

            account.Initials = account.ResolveInitials();

            await _db.SaveChangesAsync();

            // Re-issue the authentication cookie from the updated row, so the sidebar,
            // the header badges and this page's card all read the new name, role,
            // initials and photo on the very next render.
            await RefreshSignInAsync(account);

            TempData["ProfileSuccess"] = model.IsChangingPassword
                ? "Profile and password updated successfully."
                : "Profile updated successfully.";

            return RedirectToAction(nameof(Profile));
        }

        /// <summary>
        /// Re-issues the authentication cookie from an up-to-date account row.
        ///
        /// The layout and the dashboard header render the name, role, initials and photo
        /// from claims, so anything that changes the profile has to refresh the cookie
        /// as part of the same request. Without it the badges keep showing the previous
        /// values until the cookie expires, which is the "I saved but nothing changed"
        /// behaviour this method exists to prevent.
        /// </summary>
        private async Task RefreshSignInAsync(UserAccount account)
        {
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                BuildPrincipal(account),
                new AuthenticationProperties
                {
                    // Preserved so "Keep me signed in" still means the same thing after
                    // the refresh; the cookie is always re-sent by the browser either way.
                    IsPersistent = Request.Cookies[
                        CookieAuthenticationDefaults.AuthenticationScheme] != null
                });
        }

        /// <summary>
        /// POST: /Account/ChangePhoto
        ///
        /// Stores the image picked on the My Profile page and returns JSON, because the
        /// page uploads through <c>fetch</c> and updates the badges in place rather than
        /// navigating away.
        /// </summary>
        /// <param name="photo">The uploaded image. Named to match the form field.</param>
        /// <param name="cancellationToken">Aborts the copy when the client gives up.</param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        [RequestSizeLimit(ProfilePhotoStore.MaxBytes + 65536)]
        public async Task<IActionResult> ChangePhoto(
            IFormFile? photo,
            CancellationToken cancellationToken)
        {
            var id = GetCurrentUserId();

            if (id is null)
            {
                // An expired cookie mid-upload: tell the client so it can send the
                // operator to sign in again, rather than silently doing nothing.
                return Json(new { success = false, error = "Your session has expired. Please sign in again." });
            }

            if (photo is null || photo.Length == 0)
            {
                return Json(new { success = false, error = "Please choose a photo to upload." });
            }

            if (photo.Length > ProfilePhotoStore.MaxBytes)
            {
                return Json(new
                {
                    success = false,
                    error = "That photo is larger than 5 MB. Please choose a smaller image."
                });
            }

            var account = await _db.UserAccounts
                .SingleOrDefaultAsync(u => u.Id == id.Value, cancellationToken);

            if (account is null)
            {
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Json(new { success = false, error = "Your account is no longer available." });
            }

            var previousPhoto = account.ProfilePhoto;

            var saved = await ProfilePhotoStore.SaveAsync(
                account.Id,
                photo.FileName,
                photo.ContentType,
                photo.OpenReadStream(),
                cancellationToken);

            if (!saved.Succeeded || saved.FileName is null)
            {
                return Json(new { success = false, error = saved.Error });
            }

            account.ProfilePhoto = saved.FileName;
            await _db.SaveChangesAsync(cancellationToken);

            // Only now that the new photo is safely stored is the old one removed, so a
            // failed upload can never leave the account with no photo at all.
            ProfilePhotoStore.TryDelete(previousPhoto);

            // Re-issue the cookie so the new file name reaches the sidebar and header
            // badges. Without this they would keep showing the previous photo until the
            // cookie expired.
            await RefreshSignInAsync(account);

            // Built from the account just saved, not from User. The cookie re-issued above takes
// effect on the *next* request, so reading the claim here would still return the
// previous (or absent) photo name and the client would have no URL to render.
return Json(new
            {
                success = true,
                url = UserClaims.BuildPhotoUrl(account.ProfilePhoto),
                initials = account.ResolveInitials()
            });
        }

        /// <summary>
        /// GET: /Account/ProfilePhoto?file=...
        ///
        /// Serves the signed-in user's own uploaded photo. Authorised and scoped to the
        /// caller's own file, so one signed-in staff member cannot pull another's
        /// photograph by guessing a file name - which is also why the images are stored
        /// outside <c>wwwroot</c> rather than served as static files.
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ProfilePhoto(string? file)
        {
            var id = GetCurrentUserId();

            if (id is null)
            {
                return NotFound();
            }

            // Tracked, not AsNoTracking: when the stored file turns out to be missing this
            // action repairs the row rather than only reporting the problem.
            var account = await _db.UserAccounts
                .SingleOrDefaultAsync(u => u.Id == id.Value);

            if (account is null)
            {
                return NotFound();
            }

            // Own photo only. The name is also re-validated by ResolvePath below, so
            // neither a traversal attempt nor another user's file name can resolve.
            if (string.IsNullOrWhiteSpace(file)
                || !string.Equals(file, account.ProfilePhoto, StringComparison.Ordinal))
            {
                return NotFound();
            }

            // ResolvePath returns null both for a name this store never produced and for
            // a well-formed name whose file has since disappeared; the two are handled
            // the same way below, so the distinction does not matter here.
            var path = ProfilePhotoStore.ResolvePath(file);

            if (path is null)
            {
                // The row points at an image that is no longer there - a restored backup,
                // a deployment that did not carry App_Data, or a deleted file. Left alone
                // this leaves every badge on every page rendering a broken image, because
                // the claim still carries the dead file name. Clearing it and re-issuing
                // the cookie makes the badges fall back to the initials on the next
                // render, which is the honest state: there is no photo to show.
                await ClearMissingPhotoAsync(account);

                return NotFound();
            }

            return PhysicalFile(
                path,
                ProfilePhotoStore.ContentTypeFor(file));
        }

        /// <summary>
        /// Drops a photo reference whose file no longer exists and refreshes the sign-in
        /// cookie, so the badges stop asking for an image that will never load.
        /// </summary>
        private async Task ClearMissingPhotoAsync(UserAccount account)
        {
            account.ProfilePhoto = null;

            await _db.SaveChangesAsync();
            await RefreshSignInAsync(account);
        }

        /// <summary>
        /// Validates and applies the change-password block when the user filled it in.
        /// Returns false when the block was rejected, with the reason already added to
        /// <see cref="Controller.ModelState"/>.
        /// </summary>
        private bool ApplyPasswordChange(UserAccount account, ProfileViewModel model)
        {
            // A blank block means "leave the password alone", which is the normal case
            // for a profile-only edit. Nothing below runs unless the user opted in by
            // typing into at least one of the fields.
            if (!model.IsChangingPassword)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(model.CurrentPassword)
                || string.IsNullOrWhiteSpace(model.NewPassword)
                || string.IsNullOrWhiteSpace(model.ConfirmPassword))
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Please fill in all three password fields to change your password.");

                return false;
            }

            if (!string.Equals(model.NewPassword, model.ConfirmPassword, StringComparison.Ordinal))
            {
                ModelState.AddModelError(
                    nameof(model.ConfirmPassword),
                    "The new password and its confirmation do not match.");

                return false;
            }

            // The current password is re-verified against the stored hash even though the
            // user is already authenticated. A session can outlive a password change, and
            // without this anyone who reaches an unlocked terminal could lock the owner
            // out of their own account.
            var verification = _passwordHasher.VerifyHashedPassword(
                account, account.PasswordHash, model.CurrentPassword);

            if (verification == PasswordVerificationResult.Failed)
            {
                ModelState.AddModelError(
                    nameof(model.CurrentPassword),
                    "Your current password is incorrect.");

                return false;
            }

            // Re-hashed unconditionally: HashPassword applies a fresh random salt, so
            // the same password never produces the same stored value twice.
            account.PasswordHash = _passwordHasher.HashPassword(account, model.NewPassword);

            return true;
        }
    }
}
