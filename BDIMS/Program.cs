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
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

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