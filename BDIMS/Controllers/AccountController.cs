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
            // A signed-in user hitting the login URL goes straight to their
            // dashboard instead of being shown a form they do not need. The
            // portal follows the authorization role in the cookie - "Resident"
            // on the Resident portal, the administrative side otherwise - so a
            // Secretary or Staff member is never bounced into the Resident portal.
            if (User.Identity?.IsAuthenticated == true)
            {
                if (User.IsInRole("Resident"))
                {
                    return RedirectToAction("Dashboard", "Resident");
                }

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

            // Tracked, not AsNoTracking: the two writes below (an elapsed temporary
            // lock being lifted, and a rehash of an outdated password hash) go through
            // this instance, so the context has to be tracking it for the save to
            // actually persist.
            var account = await _db.UserAccounts
                .SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail);

            // The same generic message for "no such user" and "wrong password", so the
            // form cannot be used to discover which email addresses are registered.
            // The status-specific messages are only revealed further down, after the
            // password has been verified against the stored account: someone probing
            // addresses learns nothing, while the legitimate owner of a locked account
            // is told exactly why they cannot get in.
            const string invalidCredentialsMessage =
                "Invalid username or password. Please use the provided credentials.";

            if (account == null)
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

            // A temporary block whose hours have run out is lifted here, at the first
            // sign-in attempt after the expiry: no background job watches the clock,
            // and this is the moment the elapsed time actually matters. (The user
            // list does the same, so an administrator's view is never stale.)
            if (account.HasExpiredLock)
            {
                account.ApplyStatus(UserAccountStatus.Active);
                await _db.SaveChangesAsync();
            }

            // From here on the account exists and the password was correct, so the
            // refusal can safely say *why* - without handing a stranger a reason to
            // try again with someone else's address.
            if (account.Status == UserAccountStatus.Blocked)
            {
                ModelState.AddModelError(
                    string.Empty,
                    account.LockoutEnd.HasValue
                        ? "This account has been temporarily blocked by an administrator. " +
                          $"Sign-in is disabled until {account.LockoutEnd.Value.ToLocalTime():MMM d, yyyy h:mm tt} " +
                          "(local time). Contact the administrator if you need access sooner."
                        : "This account has been blocked by an administrator. " +
                          "Contact the administrator to have it unblocked.");

                return View(model);
            }

            if (account.Status == UserAccountStatus.Pending)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Your account is pending admin approval. Please wait for the barangay office to verify and approve your registration.");

                return View(model);
            }

            if (account.Status == UserAccountStatus.Rejected)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "This account's registration was rejected by an administrator. " +
                    "Contact the administrator if you believe this is a mistake.");

                return View(model);
            }

            if (account.Status == UserAccountStatus.Deactivated)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "This account has been deactivated by an administrator. " +
                    "Contact the administrator if you need it reactivated.");

                return View(model);
            }

            // Status is Active. The legacy flag is still enforced as a second line of
            // defence for rows written before the status column existed.
            if (!account.IsActive)
            {
                ModelState.AddModelError(string.Empty, invalidCredentialsMessage);
                return View(model);
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

            // The portal follows the account's role, not a hard-coded admin
            // check: Administrator and Barangay Secretary accounts belong to the
            // administrative system, while a Resident goes to the Resident
            // portal. UserSignIn owns the mapping, so the redirect always
            // matches the role claim that [Authorize(Roles = ...)] and the
            // sidebar evaluate.
            return UserSignIn.IsResidentAccount(account)
                ? RedirectToAction("Dashboard", "Resident")
                : RedirectToAction("Dashboard", "Home");
        }

        /// <summary>
        /// TempData key carrying the account id across the three recovery
        /// steps: located in step one, proven in step two, consumed in step
        /// three. Steps peek at it (instead of reading) so it survives each
        /// redirect until the reset completes or the flow is abandoned.
        /// </summary>
        private const string ForgotPasswordUserIdKey = "ForgotPasswordUserId";

        // GET: /Account/ForgotPassword
        /// <summary>
        /// Password recovery, step one: locate the account by the email
        /// registered to it - the same lookup key the Login form accepts.
        /// </summary>
        [HttpGet]
        [AllowAnonymous]
        public IActionResult ForgotPassword()
        {
            return View(new ForgotPasswordViewModel());
        }

        // POST: /Account/ForgotPassword
        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Same normalized-email key as Login, so any identifier that signs
            // in also locates the account here.
            var normalized = UserAccount.NormalizeEmail(model.UsernameOrEmail.Trim());
            var account = await _db.UserAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized);

            if (account == null
                || account.Status != UserAccountStatus.Active
                || !account.IsActive)
            {
                // One message for unknown, pending, rejected and blocked
                // accounts: the lookup step tells the visitor nothing about
                // which identifiers are registered beyond "not usable here".
                ModelState.AddModelError(
                    string.Empty,
                    "Account not found. Please check the email or username you entered.");
                return View(model);
            }

            TempData[ForgotPasswordUserIdKey] = account.Id;
            return RedirectToAction(nameof(VerifyDetails));
        }

        // GET: /Account/VerifyDetails
        /// <summary>
        /// Step two: the personal-details form. Without an account located in
        /// step one there is nothing to verify, so the flow restarts.
        /// </summary>
        [HttpGet]
        [AllowAnonymous]
        public IActionResult VerifyDetails()
        {
            if (TempData.Peek(ForgotPasswordUserIdKey) is not int)
            {
                return RedirectToAction(nameof(ForgotPassword));
            }

            return View(new VerifyDetailsViewModel());
        }

        // POST: /Account/VerifyDetails
        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        public async Task<IActionResult> VerifyDetails(VerifyDetailsViewModel model)
        {
            if (TempData.Peek(ForgotPasswordUserIdKey) is not int userId)
            {
                return RedirectToAction(nameof(ForgotPassword));
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var account = await _db.UserAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(u => u.Id == userId);

            if (account == null
                || account.Status != UserAccountStatus.Active
                || !account.IsActive)
            {
                TempData.Remove(ForgotPasswordUserIdKey);
                return RedirectToAction(nameof(ForgotPassword));
            }

            // Every field must match the registered profile. Names compare
            // case-insensitively, the birth date by calendar day, and the
            // contact number by its digits alone, so formatting differences
            // (casing, "0917 123 4567" vs "09171234567") do not fail a match.
            var matches =
                string.Equals(
                    model.FirstName.Trim(), account.FirstName?.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    model.LastName.Trim(), account.LastName?.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                && model.DateOfBirth?.Date == account.DateOfBirth?.Date
                && NormalizeContact(model.ContactNumber)
                    == NormalizeContact(account.ContactNumber);

            if (!matches)
            {
                // The account id is only peeked, never consumed, so the form
                // can be retried without restarting the whole flow.
                ModelState.AddModelError(
                    string.Empty,
                    "Provided details do not match our records. Please check and try again.");
                return View(model);
            }

            return RedirectToAction(nameof(ResetPassword));
        }

        // GET: /Account/ResetPassword
        /// <summary>
        /// Step three: choose the new password. Reached only after the
        /// personal-details check in step two has passed.
        /// </summary>
        [HttpGet]
        [AllowAnonymous]
        public IActionResult ResetPassword()
        {
            if (TempData.Peek(ForgotPasswordUserIdKey) is not int)
            {
                return RedirectToAction(nameof(ForgotPassword));
            }

            return View(new ResetPasswordViewModel());
        }

        // POST: /Account/ResetPassword
        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (TempData.Peek(ForgotPasswordUserIdKey) is not int userId)
            {
                return RedirectToAction(nameof(ForgotPassword));
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var account = await _db.UserAccounts
                .SingleOrDefaultAsync(u => u.Id == userId);

            if (account == null
                || account.Status != UserAccountStatus.Active
                || !account.IsActive)
            {
                TempData.Remove(ForgotPasswordUserIdKey);
                return RedirectToAction(nameof(ForgotPassword));
            }

            // The same PBKDF2 hasher Login verifies against, so the new
            // password works on the very next sign-in attempt.
            account.PasswordHash = _passwordHasher.HashPassword(account, model.Password);
            await _db.SaveChangesAsync();

            // The temporary account id dies with the flow; the Login page
            // renders the success banner from this key.
            TempData.Remove(ForgotPasswordUserIdKey);
            TempData["ResetPasswordSuccess"] =
                "Password successfully reset! You can now log in.";
            return RedirectToAction(nameof(Login));
        }

        /// <summary>
        /// Digits-only form of a contact number, so "0917 123 4567" and
        /// "09171234567" compare equal during identity verification.
        /// </summary>
        private static string NormalizeContact(string? contact)
        {
            if (string.IsNullOrEmpty(contact))
            {
                return string.Empty;
            }

            var digits = new char[contact.Length];
            var count = 0;

            foreach (var c in contact)
            {
                if (char.IsDigit(c))
                {
                    digits[count++] = c;
                }
            }

            return new string(digits, 0, count);
        }

        // POST: /Account/Register
        /// <summary>
        /// Creates a resident account from the "Create Resident Account" modal on the
        /// Login page. The account is stored with role "Resident" and status
        /// <see cref="UserAccountStatus.Pending"/>, so it cannot sign in until an
        /// administrator approves it from the User Management page.
        ///
        /// The modal submits through <c>fetch</c>, so this action answers with JSON:
        /// field-level errors keep the modal open, and success closes it and shows the
        /// confirmation banner on the Login page.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            try
            {
                if (!string.Equals(
                        model.Email.Trim(),
                        model.ConfirmEmail.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError(
                        nameof(model.ConfirmEmail),
                        "The email addresses do not match.");
                }

                if (!string.Equals(model.Password, model.ConfirmPassword, StringComparison.Ordinal))
                {
                    ModelState.AddModelError(
                        nameof(model.ConfirmPassword),
                        "The passwords do not match.");
                }

                // The normalized email is the sign-in key and carries a unique index, so a
                // duplicate is checked here to produce a readable message instead of a
                // database error. Trimmed and normalized exactly like the Login lookup.
                var normalizedEmail = UserAccount.NormalizeEmail(model.Email);

                var emailTaken = await _db.UserAccounts
                    .AsNoTracking()
                    .AnyAsync(u => u.NormalizedEmail == normalizedEmail);

                if (emailTaken)
                {
                    ModelState.AddModelError(
                        nameof(model.Email),
                        "That email address is already registered.");
                }

                // Check for duplicate contact number
                var contactTaken = await _db.UserAccounts
                    .AsNoTracking()
                    .AnyAsync(u => u.ContactNumber == model.ContactNumber.Trim());

                if (contactTaken)
                {
                    ModelState.AddModelError(
                        nameof(model.ContactNumber),
                        "This contact number is already registered.");
                }

                // The form caps the date pickers, but the server re-checks the
                // range so a crafted POST cannot register an impossible age.
                if (model.DateOfBirth is { } dateOfBirth)
                {
                    if (dateOfBirth > DateTime.Today)
                    {
                        ModelState.AddModelError(
                            nameof(model.DateOfBirth),
                            "Date of birth cannot be in the future.");
                    }
                    else if (dateOfBirth < DateTime.Today.AddYears(-100))
                    {
                        ModelState.AddModelError(
                            nameof(model.DateOfBirth),
                            "Date of birth must be within the last 100 years.");
                    }
                }

                // Civil status is optional, but a supplied value has to come from
                // the list the rest of the system (profile editor, certificates)
                // already understands.
                var allowedCivilStatuses = new[] { "Single", "Married", "Widowed", "Separated", "Divorced" };
                var submittedCivilStatus = (model.CivilStatus ?? string.Empty).Trim();
                string? civilStatus = null;

                if (submittedCivilStatus.Length > 0)
                {
                    civilStatus = allowedCivilStatuses.FirstOrDefault(option =>
                        string.Equals(option, submittedCivilStatus, StringComparison.OrdinalIgnoreCase));

                    if (civilStatus is null)
                    {
                        ModelState.AddModelError(
                            nameof(model.CivilStatus),
                            "Choose a valid civil status.");
                    }
                }

                if (!ModelState.IsValid)
                {
                    var errors = ModelState
                        .Where(kvp => kvp.Value?.Errors.Count > 0)
                        .ToDictionary(
                            kvp => kvp.Key,
                            kvp => kvp.Value!.Errors
                                .Select(e => e.ErrorMessage)
                                .ToArray());

                    // Also include a flat list of all error messages for easier debugging
                    var allMessages = ModelState
                        .Where(kvp => kvp.Value?.Errors.Count > 0)
                        .SelectMany(kvp => kvp.Value!.Errors.Select(e => e.ErrorMessage))
                        .ToArray();

                    return Json(new
                    {
                        success = false,
                        errors,
                        messages = allMessages
                    });
                }

                // A registrant who already appears in the barangay Residents registry is
                // known to the office and is activated straight away. Anyone else - a
                // newcomer, a name spelled differently, someone outside the registry -
                // has to be vetted by the secretary first, so the account lands as
                // Pending and cannot sign in until it is approved from User Management.
                var registrantName = string.Join(
                    " ",
                    new[]
                    {
                        (model.FirstName ?? string.Empty).Trim(),
                        (model.MiddleName ?? string.Empty).Trim(),
                        (model.LastName ?? string.Empty).Trim(),
                        (model.Suffix ?? string.Empty).Trim()
                    }
                        .Where(part => !string.IsNullOrWhiteSpace(part)));

                // Normalize optional fields: convert empty strings to null for database storage
                var middleName = string.IsNullOrWhiteSpace(model.MiddleName) ? null : model.MiddleName.Trim();
                var suffix = string.IsNullOrWhiteSpace(model.Suffix) ? null : model.Suffix.Trim();

                var registrantContact = (model.ContactNumber ?? string.Empty).Trim();

                // Match against the official masterlist (Residents table) on full name AND purok.
                // A resident who registered with the same full name AND purok is considered
                // a verified match. Contact number is also checked as a secondary identifier.
                var registrantPurok = (model.Purok ?? string.Empty).Trim();

                // Pomelo (MySQL) cannot translate string.Equals(..., StringComparison)
                // to SQL, so the case-insensitive comparison is done with LOWER() on
                // both sides instead: search terms are lowercased in memory here, the
                // columns are lowercased by the translated SQL.
                var registrantNameLower = registrantName.ToLowerInvariant();
                var registrantPurokLower = registrantPurok.ToLowerInvariant();
                var registrantContactLower = registrantContact.ToLowerInvariant();

                var isKnownResident = await _db.Residents
                    .AsNoTracking()
                    .AnyAsync(r =>
                        ((r.Name ?? string.Empty).Trim().ToLower() == registrantNameLower
                            && (r.Purok ?? string.Empty).Trim().ToLower() == registrantPurokLower)
                        || (registrantContact.Length > 0
                            && (r.Contact ?? string.Empty).Trim().ToLower() == registrantContactLower));

                var status = isKnownResident
                    ? UserAccountStatus.Active
                    : UserAccountStatus.Pending;

                // Age is derived from the date of birth here instead of trusting
                // the read-only number the form posts: the birth date is the
                // source of truth and the posted value is only a display aid.
                var age = model.DateOfBirth is { } birthDate
                    ? DateTime.Today.Year - birthDate.Year
                        - (DateTime.Today.DayOfYear < birthDate.DayOfYear ? 1 : 0)
                    : model.Age;

                var account = new UserAccount
                {
                    Email = model.Email.Trim(),
                    NormalizedEmail = normalizedEmail,

                    // Same Identity PasswordHasher the seeded accounts use: PBKDF2 with a
                    // per-password random salt; the plaintext is never stored.
                    PasswordHash = _passwordHasher.HashPassword(null!, model.Password),

                    FirstName = model.FirstName.Trim(),
                    MiddleName = middleName,
                    LastName = model.LastName.Trim(),
                    Suffix = suffix,
                    Age = age,
                    DateOfBirth = model.DateOfBirth,
                    CivilStatus = civilStatus ?? string.Empty,
                    Gender = (model.Gender ?? string.Empty).Trim(),
                    Purok = (model.Purok ?? string.Empty).Trim(),
                    VoterStatus = (model.VoterStatus ?? string.Empty).Trim(),
                    ContactNumber = (model.ContactNumber ?? string.Empty).Trim(),

                    // Resident self-registration never grants administrative rights. The
                    // role is always "Resident"; only an administrator's Approve action
                    // can move a Pending account to Active, which is the only status
                    // allowed to sign in.
                    Role = "Resident",
                    IsAdmin = false,
                    CreatedUtc = DateTime.UtcNow
                };

                // Writes Status, the legacy IsActive mirror and the temporary-lock
                // columns together, so the two cannot drift apart.
                account.ApplyStatus(status);

                // Full name shown across the UI, including the optional middle name and
                // suffix the resident supplied ("Juan Santos Dela Cruz Jr."). Initials
                // still come from the first/last pair via ResolveInitials().
                account.DisplayName = string.Join(
                    " ",
                    new[] { account.FirstName, account.MiddleName, account.LastName, account.Suffix }
                        .Where(part => !string.IsNullOrWhiteSpace(part)));

                account.Initials = account.ResolveInitials();

                _db.UserAccounts.Add(account);

                await _db.SaveChangesAsync();

                var successMessage = status == UserAccountStatus.Active
                    ? "Registration successful! You can now log in with your account."
                    : "Registration submitted! Please wait for Admin approval before logging in.";

                // Carried on TempData as well so the banner survives a full-page post
                // (e.g. when the browser resubmits the form after a refresh).
                TempData["RegisterSuccess"] = successMessage;

                return Json(new { success = true, message = successMessage });
            }
            catch (Exception ex)
            {
                // Catch ALL exceptions (antiforgery, model binding, DB, etc.) and return JSON
                // so the frontend fetch() can parse the response without throwing "Unexpected token"
                var errorMsg = ex.InnerException?.Message ?? ex.Message;
                return Json(new { success = false, message = $"Registration failed: {errorMsg}" });
            }
        }

        // GET: /Account/AccessDenied
        /// <summary>
        /// Landing page for a signed-in user who is authenticated but not authorised for
        /// the page they asked for (for example a Resident opening an administrator-only
        /// module). Anonymous so the response can always be rendered, even if the auth
        /// cookie expired on the way here.
        /// </summary>
        [HttpGet]
        [AllowAnonymous]
        public IActionResult AccessDenied()
        {
            return View();
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
        /// Builds the signed-in principal via <see cref="UserSignIn"/>, which owns the
        /// claim layout shared by sign-in and every cookie refresh (including the
        /// Resident Settings page's "Sign out other devices").
        /// </summary>
        private static ClaimsPrincipal BuildPrincipal(UserAccount account) =>
            UserSignIn.BuildPrincipal(account);

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

            // Role is the security column that decides which portal the account
            // belongs to; it is managed exclusively from User Management's Edit
            // Account. The profile form shows it read-only and the posted value
            // is discarded here, so a tampered form cannot promote the signed-in
            // account (e.g. a resident writing "Barangay Secretary" into their own row).
            model.Role = account.Role;

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
            await UserSignIn.SignInAsync(HttpContext, account);
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
