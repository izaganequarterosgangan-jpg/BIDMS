using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using BDIMS.Data;
using BDIMS.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Sign-in state is carried by an encrypted cookie. The signed-in identity (display
// name, role and avatar initials) travels in claims that AccountController issues at
// login, which is what the sidebar user widget renders. Cookie name and login path
// are set explicitly so the redirect for an unauthenticated request always lands on
// /Account/Login rather than a framework default.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "BDIMS.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Login";

        // A signed-in user who lacks the role a page requires is sent here instead of
        // the framework default (/Account/AccessDenied by convention but with no
        // matching action, which surfaced as a 404). AccountController implements it.
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

        // Session Timeout is administrator-configurable on Settings > System &
        // Security, so the idle window is enforced here against the stored value
        // rather than being frozen at startup. ExpireTimeSpan above stays as the
        // hard ceiling on the cookie itself; this check measures the time since
        // the ticket was last renewed and signs the user out once it passes the
        // configured window. ShouldRenew slides that window forward on every
        // request, which is what makes it an *idle* timeout rather than a fixed
        // lifetime. The setting is read through the provider, so a change saved in
        // Settings applies to the next request with no restart.
        options.Events.OnValidatePrincipal = async context =>
        {
            var settings =
                context.HttpContext.RequestServices
                    .GetRequiredService<BDIMS.Services.SystemSettingsProvider>();

            var issued = context.Properties.IssuedUtc ?? DateTimeOffset.UtcNow;

            if (DateTimeOffset.UtcNow - issued > settings.Current.SessionTimeoutSpan)
            {
                // Idle for longer than the configured window. RejectPrincipal ends
                // the session; the AJAX branch of OnRedirectToLogin turns the
                // resulting challenge into a 401 instead of an HTML redirect.
                context.RejectPrincipal();
                return;
            }

            // Role/claim refresh: User Management's Edit Account only writes the
            // account row, so a signed-in user's cookie would otherwise keep the
            // old role - and the old portal - until it expired. UserSignIn
            // re-derives the principal from that row: a role change lands on the
            // very next request, and a deleted, blocked or deactivated account is
            // signed out immediately instead of browsing with a stale cookie.
            var (refreshOutcome, refreshedPrincipal) = await BDIMS.Services.UserSignIn
                .RefreshPrincipalAsync(context.HttpContext, context.Principal);

            if (refreshOutcome == BDIMS.Services.UserSignIn.PrincipalRefreshOutcome.Rejected)
            {
                context.RejectPrincipal();
                return;
            }

            if (refreshOutcome == BDIMS.Services.UserSignIn.PrincipalRefreshOutcome.Refreshed
                && refreshedPrincipal != null)
            {
                // ReplacePrincipal makes the authorization middleware and the
                // layout's IsInRole checks evaluate the *new* role for this very
                // request; ShouldRenew persists it into the sliding cookie.
                context.ReplacePrincipal(refreshedPrincipal);
            }

            // "Sign out other devices" on the Resident Settings page records a
            // per-account cutoff; any cookie issued before it is treated as a revoked
            // session. The lookup is scoped to Resident accounts so administrator and
            // staff sessions pay no extra query on every request.
            if (context.Principal?.IsInRole("Resident") == true)
            {
                var userIdRaw = context.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (int.TryParse(userIdRaw, out var residentUserId))
                {
                    try
                    {
                        var db = context.HttpContext.RequestServices
                            .GetRequiredService<ApplicationDbContext>();

                        var cutoff = BDIMS.Services.ResidentSettingsStore
                            .ReadSessionsValidFrom(db, residentUserId);

                        if (cutoff.HasValue && issued < cutoff.Value)
                        {
                            context.RejectPrincipal();
                            return;
                        }
                    }
                    catch
                    {
                        // A settings read must never take a whole session down; on any
                        // failure fall through and renew the session as normal.
                    }
                }
            }

            context.ShouldRenew = true;
        };

        // A request that is already carrying a valid cookie must never be bounced back
        // to the login page: the cookie events below only react to *rejected* cookies.
        options.Events.OnRedirectToLogin = context =>
        {
            // AJAX endpoints (the modules poll /Home/GetNotifications and friends) expect
            // JSON. Returning a 302 to an HTML login page would surface as a parse error
            // in the browser console, so they get a 401 and let the page handle it.
            if (context.Request.Headers.XRequestedWith == "XMLHttpRequest")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Modules such as Blotter, Announcements, Documents, Certificates, Reports and
    // Settings are guarded by [Authorize]; a valid sign-in is what grants access.
    options.FallbackPolicy = null;
});

// Password hashing/verification for the UserAccounts store. Registering the Identity
// PasswordHasher directly keeps the default PBKDF2 (HMAC-SHA256, per-password random
// salt) parameters without pulling in the rest of the Identity framework, which BDIMS
// does not use. It is what AccountController calls at sign-in.
builder.Services.AddScoped<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();

// Bind the barangay identity shown in the UI and printed on certificate
// letterheads from the BDIMS:Barangay configuration section, so it can be
// supplied through appsettings.json, user-secrets, or environment variables
// and overridden per environment without recompiling.
builder.Services.Configure<BDIMS.Models.BarangayOptions>(
    builder.Configuration.GetSection(BDIMS.Models.BarangayOptions.SectionName));

// The live, editable view of that same configuration. The Settings page writes
// through BarangayProfileProvider, which persists to the AppSettings table and
// refreshes the cached values, so the barangay name, address, hours and
// signatory appear in the header and on printed certificates on the next request
// instead of staying pinned to the values present at startup.
//
// Singleton with an internal cache: the provider deliberately does not take a
// direct ApplicationDbContext dependency, so no scoped service is captured. It
// opens a short-lived scope per unit of work instead, and guards the cache with
// a lock because a singleton is read concurrently.
builder.Services.AddSingleton<BDIMS.Services.BarangayProfileProvider>();

// Same shape for the Settings > System & Security preferences (auto-approve,
// notification channels, two-factor, session timeout). Singleton with an internal
// cache, no direct scoped dependency, so document creation and the auth cookie can
// read the live values on every request.
builder.Services.AddSingleton<BDIMS.Services.SystemSettingsProvider>();

// Per-account storage for the Resident Portal > Settings preferences (security,
// notification and appearance choices). Scoped, not singleton like the two providers
// above: it is only ever used inside a request that already has an
// ApplicationDbContext, so it takes the context directly instead of opening its own.
builder.Services.AddScoped<BDIMS.Services.ResidentSettingsStore>();

// Register ApplicationDbContext with Pomelo MySQL Provider
//
// The connection string is not stored in appsettings.json. Development picks it up
// from user-secrets; every other environment must supply it as the
// ConnectionStrings__DefaultConnection environment variable. The checked-in value is
// the CHANGE_ME placeholder, so an unconfigured deployment is rejected here with an
// actionable message instead of failing later inside ServerVersion.AutoDetect with a
// MySQL authentication error that gives no hint about the real cause.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("CHANGE_ME", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not configured. " +
        "For local development run: dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"<value>\" " +
        "(from the BDIMS project folder). For other environments set the " +
        "ConnectionStrings__DefaultConnection environment variable.");
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// The certificate template layout API takes a JSON body, which cannot carry the
// antiforgery token as a form field. Accepting the token from a request header lets
// those endpoints stay protected instead of having to opt out of validation.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
});

// Bind certificate template layouts to App_Data/certificate_templates.json.
BDIMS.Services.CertificateTemplateStore.Configure(builder.Environment);

// Point the profile-photo store at App_Data/profile_photos. Configured here for the
// same reason as the template store: both are file-backed and need the content root
// resolved once at startup rather than injected per request.
BDIMS.Services.ProfilePhotoStore.Configure(builder.Environment);

// Tell the renderer which identity literals to recognise as baked-in text. A layout
// saved before the profile became editable (e.g. imported from a .docx) hard-codes
// "Province of Bohol" instead of {{ Province }}. Passing the configured defaults lets
// CertificateTemplateRenderer promote exactly those literals to tokens at render time,
// so such a layout follows a profile edit without being rewritten on disk.
var startupBarangay = builder.Configuration
    .GetSection(BDIMS.Models.BarangayOptions.SectionName)
    .Get<BDIMS.Models.BarangayOptions>();

BDIMS.Services.CertificateTemplateRenderer.ConfigureLegacyIdentity(startupBarangay);

// The signatory is promoted the same way. A layout authored before the profile was
// editable hard-codes the official's name on the signature line, which would
// otherwise keep printing the old name after the signatory is changed in Settings.
BDIMS.Services.CertificateTemplateRenderer.ConfigureLegacySignatory(
    startupBarangay?.SignatoryName,
    startupBarangay?.SignatoryRole);

var app = builder.Build();

// Apply pending migrations and seed the reference data on startup, so a fresh
// database gets the tables plus the same records the modules previously kept in
// memory, and an existing database just gets its pending schema updates.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
    BDIMS.Services.DataSeeder.Seed(db);

    // Seeded after Migrate() so the UserAccounts table is guaranteed to exist. The
    // routine is idempotent: an already-present admin is reconciled in place and its
    // password hash is left untouched, so restarting never resets a changed password
    // and never produces a duplicate row.
    var startupLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("BDIMS.DbInitializer");

    await BDIMS.Services.DbInitializer.SeedAdminAccountAsync(db, startupLogger);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Must sit between UseRouting and UseAuthorization: it is what populates HttpContext.User
// from the auth cookie, which [Authorize] then evaluates and which the sidebar widget
// reads its display name / role / initials from.
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();

// Updated default route to start on Account/Login
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}")
    .WithStaticAssets();

app.Run();