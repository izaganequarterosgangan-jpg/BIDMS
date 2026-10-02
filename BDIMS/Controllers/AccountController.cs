using BDIMS.Data;
using BDIMS.Models;
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
            // Initials are derived from the display name when the stored value is blank,
            // so the badge always matches the name beside it even if an older build left
            // the column stale.
            var initials = string.IsNullOrWhiteSpace(account.Initials)
                ? UserAccount.DeriveInitials(account.DisplayName)
                : account.Initials;

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
                new(UserAccount.RoleClaim, account.Role),
                new(UserAccount.InitialsClaim, initials)
            };

            var identity = new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme);

            return new ClaimsPrincipal(identity);
        }
    }
}
