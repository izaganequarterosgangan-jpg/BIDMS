using BDIMS.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BDIMS.Services
{
    /// <summary>
    /// Authoritative source of the barangay profile shown on the Settings page,
    /// printed on certificate letterheads and in the system header.
    ///
    /// Reads are served from the AppSettings key/value table, which is seeded
    /// from the BDIMS:Barangay configuration section (appsettings.json, user
    /// secrets or environment variables) the first time a value is missing. That
    /// gives an editable, restart-safe store while a deployment that only
    /// configures appsettings.json keeps working unchanged.
    ///
    /// The resolved values are cached in memory and refreshed on every save, so a
    /// change made on the Settings page is visible to the very next request
    /// without a restart. Callers therefore read through this provider instead of
    /// <see cref="IOptions{TOptions}"/>, which is a one-time startup snapshot and
    /// would keep serving the old values for the lifetime of the process.
    /// </summary>
    public class BarangayProfileProvider
    {
        // AppSettings row names. Prefixed so these keys cannot collide with other
        // settings that may share the table.
        private const string KeyPrefix = "Barangay.";

        private const string KeyBarangayName = KeyPrefix + nameof(BarangayOptions.BarangayName);
        private const string KeyMunicipality = KeyPrefix + nameof(BarangayOptions.Municipality);
        private const string KeyProvince = KeyPrefix + nameof(BarangayOptions.Province);
        private const string KeyContactEmail = KeyPrefix + nameof(BarangayOptions.ContactEmail);
        private const string KeyContactPhone = KeyPrefix + nameof(BarangayOptions.ContactPhone);
        private const string KeyOfficeHours = KeyPrefix + nameof(BarangayOptions.OfficeHours);
        private const string KeySignatoryName = KeyPrefix + nameof(BarangayOptions.SignatoryName);
        private const string KeySignatoryRole = KeyPrefix + nameof(BarangayOptions.SignatoryRole);

        // Upper bound keeps an accidentally pasted essay from pushing a printed
        // letterhead onto a second page. Generous enough for real values (office
        // hours, a full name with titles) while still bounded.
        public const int MaxLength = 200;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BarangayProfileProvider> _logger;
        private readonly IOptions<BarangayOptions> _configured;
        private readonly object _gate = new object();

        private BarangayOptions? _cache;

        // Registered as a singleton so the cache is shared across requests, which
        // means the scoped ApplicationDbContext cannot be injected directly and is
        // resolved through a scope factory per unit of work instead.
        public BarangayProfileProvider(
            IServiceScopeFactory scopeFactory,
            IOptions<BarangayOptions> configured,
            ILogger<BarangayProfileProvider> logger)
        {
            _scopeFactory = scopeFactory;
            _configured = configured;
            _logger = logger;
        }

        /// <summary>
        /// The current profile. Always returns a usable object: a database that
        /// cannot be reached falls back to the configured values rather than
        /// failing the page, because an unreadable profile must never stop the
        /// certificates or the Settings page from rendering.
        /// </summary>
        public BarangayOptions Current
        {
            get
            {
                lock (_gate)
                {
                    return _cache ??= Load();
                }
            }
        }

        /// <summary>
        /// Persists a submitted profile. Values are trimmed and length-checked; a
        /// rejected field is reported back to the caller so the form can flag
        /// which input needs attention instead of silently discarding the save.
        /// </summary>
        public (bool Success, string Message, string? Field) Save(BarangayProfileForm? form)
        {
            if (form == null)
            {
                return (false, "No profile values were submitted.", null);
            }

            var candidate = new BarangayOptions
            {
                BarangayName = Clean(form.BarangayName),
                Municipality = Clean(form.Municipality),
                Province = Clean(form.Province),
                ContactEmail = Clean(form.ContactEmail),
                ContactPhone = Clean(form.ContactPhone),
                OfficeHours = Clean(form.OfficeHours),
                SignatoryName = Clean(form.SignatoryName),
                SignatoryRole = Clean(form.SignatoryRole)
            };

            if (candidate.BarangayName.Length == 0)
            {
                return (false, "Barangay Name is required.", nameof(BarangayOptions.BarangayName));
            }

            if (candidate.ContactEmail.Length > 0 && !LooksLikeEmail(candidate.ContactEmail))
            {
                return (false, "Official Email is not a valid address.", nameof(BarangayOptions.ContactEmail));
            }

            lock (_gate)
            {
                try
                {
                    // One scope per save: the context is disposed at the end of the
                    // block, so a pooled connection is never held open by the cache.
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<BDIMS.Data.ApplicationDbContext>();

                    WriteSetting(db, KeyBarangayName, candidate.BarangayName);
                    WriteSetting(db, KeyMunicipality, candidate.Municipality);
                    WriteSetting(db, KeyProvince, candidate.Province);
                    WriteSetting(db, KeyContactEmail, candidate.ContactEmail);
                    WriteSetting(db, KeyContactPhone, candidate.ContactPhone);
                    WriteSetting(db, KeyOfficeHours, candidate.OfficeHours);
                    WriteSetting(db, KeySignatoryName, candidate.SignatoryName);
                    WriteSetting(db, KeySignatoryRole, candidate.SignatoryRole);

                    db.SaveChanges();

                    // Refresh the cache only after the write succeeded, so a failed
                    // save leaves the in-memory values matching the database
                    // instead of advertising a profile that was never stored.
                    _cache = candidate;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist the barangay profile.");

                    // Drop the cache so the next read rebuilds from the database.
                    // A failed SaveChanges can leave entities tracked but unsaved,
                    // and re-reading is the safe recovery.
                    _cache = null;

                    return (false, "The profile could not be saved. Please try again.", null);
                }
            }

            return (true, "Barangay Profile updated successfully!", null);
        }


        // ------------------------------------------------------------- Internals

        /// <summary>
        /// Resolves each field from the database, falling back to the configured
        /// default so a fresh installation shows the values from appsettings.json
        /// rather than blanks.
        /// </summary>
        private BarangayOptions Load()
        {
            var configured = _configured.Value;

            var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BDIMS.Data.ApplicationDbContext>();

                foreach (var row in db.AppSettings
                             .Where(a => a.Name.StartsWith(KeyPrefix))
                             .ToList())
                {
                    stored[row.Name] = row.Value ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                // A missing or unreachable table must not break the Settings page,
                // the header or certificate printing; the configured values are a
                // complete, usable profile on their own.
                _logger.LogWarning(
                    ex,
                    "Barangay profile could not be read from the database; using the configured values.");
            }

            return new BarangayOptions
            {
                BarangayName = Pick(stored, KeyBarangayName, configured.BarangayName),
                Municipality = Pick(stored, KeyMunicipality, configured.Municipality),
                Province = Pick(stored, KeyProvince, configured.Province),
                ContactEmail = Pick(stored, KeyContactEmail, configured.ContactEmail),
                ContactPhone = Pick(stored, KeyContactPhone, configured.ContactPhone),
                OfficeHours = Pick(stored, KeyOfficeHours, configured.OfficeHours),
                SignatoryName = Pick(stored, KeySignatoryName, configured.SignatoryName),
                SignatoryRole = Pick(stored, KeySignatoryRole, configured.SignatoryRole)
            };
        }

        private static string Pick(Dictionary<string, string> stored, string key, string fallback)
        {
            // A stored blank is treated as "not configured", so a deployment that
            // configures the signatory in appsettings.json does not end up with a
            // blank signature block on its certificates just because the table
            // holds an empty string.
            return stored.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        private static void WriteSetting(
            BDIMS.Data.ApplicationDbContext db,
            string name,
            string value)
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

        /// <summary>
        /// Trims, folds line breaks and strips control characters. A pasted value
        /// can carry a newline that would break a printed letterhead; line breaks
        /// collapse to a single space rather than being dropped so
        /// "8:00 AM - 5:00 PM" entered across two lines is preserved as typed.
        /// </summary>
        private static string Clean(string? value)
        {
            var text = (value ?? string.Empty)
                .Replace("\r\n", " ")
                .Replace('\n', ' ')
                .Replace('\r', ' ')
                .Trim();

            text = new string(text.Where(c => !char.IsControl(c)).ToArray());

            return text.Length > MaxLength ? text[..MaxLength] : text;
        }

        private static bool LooksLikeEmail(string value)
        {
            var at = value.IndexOf('@');

            // Exactly one '@', characters either side of it, and a dot in the
            // domain. Deliberately permissive: the goal is to catch a typo such as
            // a missing '@', not to re-implement RFC 5322.
            return at > 0
                   && at == value.LastIndexOf('@')
                   && at < value.Length - 1
                   && value.IndexOf('.', at) > at + 1
                   && !value.Contains(' ');
        }
    }
}

