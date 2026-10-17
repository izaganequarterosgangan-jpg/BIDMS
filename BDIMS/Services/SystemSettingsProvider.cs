using BDIMS.Data;
using BDIMS.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BDIMS.Services
{
    /// <summary>
    /// Authoritative source of the Settings &gt; System &amp; Security preferences.
    ///
    /// Mirrors <see cref="BarangayProfileProvider"/> deliberately: values live in
    /// the AppSettings key/value table under a "System." prefix, are seeded from
    /// the code defaults on first read, and are cached in memory and refreshed on
    /// every save. That gives a restart-safe, editable store with no migration,
    /// and it means a change saved on the Settings page is honoured by the very
    /// next request.
    ///
    /// Anything that has to consult a preference - document approval, a
    /// notification trigger, the auth cookie - reads it from here rather than from
    /// configuration, which is a one-time startup snapshot.
    /// </summary>
    public class SystemSettingsProvider
    {
        private const string KeyPrefix = "System.";

        private const string KeyAutoApprove = KeyPrefix + nameof(SystemSettingsOptions.AutoApproveLowRisk);
        private const string KeyEmail = KeyPrefix + nameof(SystemSettingsOptions.EmailNotifications);
        private const string KeySms = KeyPrefix + nameof(SystemSettingsOptions.SmsNotifications);
        private const string KeyTwoFactor = KeyPrefix + nameof(SystemSettingsOptions.TwoFactorAuth);
        private const string KeySessionTimeout = KeyPrefix + nameof(SystemSettingsOptions.SessionTimeout);
        private const string KeyAuditLogging = KeyPrefix + nameof(SystemSettingsOptions.AuditLogging);
        private const string KeyPasswordPolicy = KeyPrefix + nameof(SystemSettingsOptions.PasswordPolicy);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SystemSettingsProvider> _logger;
        private readonly object _gate = new object();

        private SystemSettingsOptions? _cache;

        // Singleton so the cache is shared across requests; the scoped
        // ApplicationDbContext is therefore resolved per unit of work.
        public SystemSettingsProvider(
            IServiceScopeFactory scopeFactory,
            ILogger<SystemSettingsProvider> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>
        /// The current preferences. Always usable: an unreachable database falls
        /// back to the code defaults rather than failing document creation or the
        /// Settings page.
        /// </summary>
        public SystemSettingsOptions Current
        {
            get
            {
                lock (_gate)
                {
                    return _cache ??= Load();
                }
            }
        }

        /// <summary>Convenience flag for the document-approval path.</summary>
        public bool AutoApproveLowRisk => Current.AutoApproveLowRisk;

        /// <summary>Checked before any email alert is sent.</summary>
        public bool EmailNotificationsEnabled => Current.EmailNotifications;

        /// <summary>Checked before any SMS alert is sent.</summary>
        public bool SmsNotificationsEnabled => Current.SmsNotifications;

        /// <summary>
        /// Persists submitted preferences, normalising anything outside the offered
        /// options so a tampered post cannot store an unusable value.
        /// </summary>
        public (bool Success, string Message) Save(SystemSettingsOptions? submitted)
        {
            if (submitted == null)
            {
                return (false, "No system settings were submitted.");
            }

            var candidate = new SystemSettingsOptions
            {
                AutoApproveLowRisk = submitted.AutoApproveLowRisk,
                EmailNotifications = submitted.EmailNotifications,
                SmsNotifications = submitted.SmsNotifications,

                // An unrecognised two-factor value falls back to the current
                // setting instead of being written through verbatim.
                TwoFactorAuth = SystemSettingsOptions.IsKnownTwoFactor(submitted.TwoFactorAuth)
                    ? submitted.TwoFactorAuth
                    : Current.TwoFactorAuth,

                AuditLogging = submitted.AuditLogging,

                // An unrecognised password policy falls back to the current setting.
                PasswordPolicy = SystemSettingsOptions.IsKnownPasswordPolicy(submitted.PasswordPolicy)
                    ? submitted.PasswordPolicy
                    : Current.PasswordPolicy,

                SessionTimeout = submitted.SessionTimeout ?? string.Empty
            };

            lock (_gate)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    WriteSetting(db, KeyAutoApprove, candidate.AutoApproveLowRisk ? "true" : "false");
                    WriteSetting(db, KeyEmail, candidate.EmailNotifications ? "true" : "false");
                    WriteSetting(db, KeySms, candidate.SmsNotifications ? "true" : "false");
                    WriteSetting(db, KeyTwoFactor, candidate.TwoFactorAuth);
                    WriteSetting(db, KeyAuditLogging, candidate.AuditLogging ? "true" : "false");
                    WriteSetting(db, KeyPasswordPolicy, candidate.PasswordPolicy);
                    WriteSetting(db, KeySessionTimeout, candidate.SessionTimeout);

                    db.SaveChanges();

                    // Refresh only after the write succeeded, so a failed save
                    // leaves the cache matching the database.
                    _cache = candidate;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist the system settings.");

                    _cache = null;

                    return (false, "The system settings could not be saved. Please try again.");
                }
            }

            return (true, "System settings updated successfully!");
        }

        // ------------------------------------------------------------- Internals

        /// <summary>
        /// Resolves each preference from the database, falling back to the code
        /// default so a fresh installation shows a working set of values.
        /// </summary>
        private SystemSettingsOptions Load()
        {
            var defaults = new SystemSettingsOptions();

            var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                foreach (var row in db.AppSettings
                             .Where(a => a.Name.StartsWith(KeyPrefix))
                             .ToList())
                {
                    stored[row.Name] = row.Value ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                // An unreadable table must not stop a certificate being issued or
                // the Settings page from rendering; the defaults are usable.
                _logger.LogWarning(
                    ex,
                    "System settings could not be read from the database; using the default values.");
            }

            return new SystemSettingsOptions
            {
                AutoApproveLowRisk = ReadBool(stored, KeyAutoApprove, defaults.AutoApproveLowRisk),
                EmailNotifications = ReadBool(stored, KeyEmail, defaults.EmailNotifications),
                SmsNotifications = ReadBool(stored, KeySms, defaults.SmsNotifications),
                TwoFactorAuth = ReadString(stored, KeyTwoFactor, defaults.TwoFactorAuth),
                AuditLogging = ReadBool(stored, KeyAuditLogging, defaults.AuditLogging),
                PasswordPolicy = ReadString(stored, KeyPasswordPolicy, defaults.PasswordPolicy),
                SessionTimeout = ReadString(stored, KeySessionTimeout, defaults.SessionTimeout)
            };
        }

        private static bool ReadBool(Dictionary<string, string> stored, string key, bool fallback)
        {
            // A value that was never stored keeps the default; "true" is the only
            // spelling that means on, so a typo cannot silently enable a control.
            return stored.TryGetValue(key, out var value) && value != null
                ? value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                : fallback;
        }

        private static string ReadString(Dictionary<string, string> stored, string key, string fallback)
        {
            return stored.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;
        }

        private static void WriteSetting(ApplicationDbContext db, string name, string value)
        {
            var existing = db.AppSettings.FirstOrDefault(a => a.Name == name);

            if (existing == null)
            {
                db.AppSettings.Add(new AppSetting { Name = name, Value = value });
            }
            else
            {
                existing.Value = value;
            }
        }
    }
}
