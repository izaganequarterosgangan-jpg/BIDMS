using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BDIMS.Data;
using BDIMS.Models;
using Microsoft.EntityFrameworkCore;

namespace BDIMS.Services
{
    /// <summary>
    /// Per-account store for the Resident Portal &gt; Settings preferences.
    ///
    /// Mirrors the way <see cref="SystemSettingsProvider"/> and
    /// <see cref="BarangayProfileProvider"/> persist settings: rows live in the
    /// <c>AppSettings</c> key/value table under a namespaced key, so a resident
    /// preference needs no schema change and survives restarts. Unlike those
    /// providers this one is scoped: it is only ever used inside a request that
    /// already has an <see cref="ApplicationDbContext"/>, so it can take the context
    /// directly instead of opening its own scope.
    ///
    /// Keys take the form <c>ResidentSettings.{accountId}.{Property}</c>, which keeps
    /// one resident's choices from ever being read back for another.
    /// </summary>
    public class ResidentSettingsStore
    {
        private const string Prefix = "ResidentSettings.";

        /// <summary>
        /// Setting name (within the account namespace) holding the instant before
        /// which issued sign-in cookies are considered revoked by
        /// "Sign out other devices". Read back by the auth cookie pipeline.
        /// </summary>
        public const string SessionsValidFromProperty = "SessionsValidFromUtc";

        private readonly ApplicationDbContext _db;

        public ResidentSettingsStore(ApplicationDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Loads the stored preferences for an account, falling back to the code
        /// defaults for anything that was never saved so a brand-new resident always
        /// gets a usable page.
        /// </summary>
        public ResidentSettingsViewModel Load(int userId)
        {
            var values = ReadValues(userId);

            // Load notification preferences from UserAccount database columns
            var account = _db.UserAccounts.AsNoTracking().FirstOrDefault(a => a.Id == userId);

            return new ResidentSettingsViewModel
            {
                TwoFactorEnabled = ReadBool(values, nameof(ResidentSettingsViewModel.TwoFactorEnabled), false),
                RequestUpdates = account?.NotifyRequestUpdates ?? ReadBool(values, nameof(ResidentSettingsViewModel.RequestUpdates), true),
                BarangayAnnouncements = account?.NotifyAnnouncements ?? ReadBool(values, nameof(ResidentSettingsViewModel.BarangayAnnouncements), true),
                ServiceReminders = ReadBool(values, nameof(ResidentSettingsViewModel.ServiceReminders), false),
                ReceiveEmail = ReadBool(values, nameof(ResidentSettingsViewModel.ReceiveEmail), true),
                ReceiveSms = ReadBool(values, nameof(ResidentSettingsViewModel.ReceiveSms), false),
                DarkMode = ReadBool(values, nameof(ResidentSettingsViewModel.DarkMode), false),
                SelectedTheme = ResidentSettingsViewModel.NormalizeTheme(
                    ReadString(values, nameof(ResidentSettingsViewModel.SelectedTheme), null))
            };
        }

        /// <summary>
        /// Persists the submitted preferences for an account. The theme is normalised
        /// so an unrecognised value cannot be written through verbatim.
        /// </summary>
        public void Save(int userId, ResidentSettingsViewModel model)
        {
            Write(userId, nameof(ResidentSettingsViewModel.TwoFactorEnabled), Bool(model.TwoFactorEnabled));
            Write(userId, nameof(ResidentSettingsViewModel.RequestUpdates), Bool(model.RequestUpdates));
            Write(userId, nameof(ResidentSettingsViewModel.BarangayAnnouncements), Bool(model.BarangayAnnouncements));
            Write(userId, nameof(ResidentSettingsViewModel.ServiceReminders), Bool(model.ServiceReminders));
            Write(userId, nameof(ResidentSettingsViewModel.ReceiveEmail), Bool(model.ReceiveEmail));
            Write(userId, nameof(ResidentSettingsViewModel.ReceiveSms), Bool(model.ReceiveSms));
            Write(userId, nameof(ResidentSettingsViewModel.DarkMode), Bool(model.DarkMode));
            Write(userId, nameof(ResidentSettingsViewModel.SelectedTheme),
                ResidentSettingsViewModel.NormalizeTheme(model.SelectedTheme));

            // Also persist notification preferences to UserAccount columns
            var account = _db.UserAccounts.FirstOrDefault(a => a.Id == userId);
            if (account != null)
            {
                account.NotifyRequestUpdates = model.RequestUpdates;
                account.NotifyAnnouncements = model.BarangayAnnouncements;
            }

            _db.SaveChanges();
        }

        /// <summary>
        /// Records the moment "Sign out other devices" was requested. Every cookie
        /// issued before it is treated as a revoked session on the next request.
        /// </summary>
        public void SignOutOtherDevices(int userId)
        {
            Write(userId, SessionsValidFromProperty,
                DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            _db.SaveChanges();
        }

        /// <summary>
        /// The per-account sign-out cutoff, or <c>null</c> when the action has never
        /// been used. Static so the auth cookie pipeline can read it without taking a
        /// dependency on the scoped store.
        /// </summary>
        public static DateTimeOffset? ReadSessionsValidFrom(ApplicationDbContext db, int userId)
        {
            var key = Key(userId, SessionsValidFromProperty);

            var raw = db.AppSettings
                .AsNoTracking()
                .Where(a => a.Name == key)
                .Select(a => a.Value)
                .FirstOrDefault();

            return DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
        }

        // ------------------------------------------------------------- Internals

        private static string PrefixFor(int userId) =>
            Prefix + userId.ToString(CultureInfo.InvariantCulture) + ".";

        private static string Key(int userId, string property) =>
            PrefixFor(userId) + property;

        private static string Bool(bool value) => value ? "true" : "false";

        private Dictionary<string, string> ReadValues(int userId)
        {
            var prefix = PrefixFor(userId);

            return _db.AppSettings
                .AsNoTracking()
                .Where(a => a.Name.StartsWith(prefix))
                .ToList()
                .ToDictionary(a => a.Name, a => a.Value ?? string.Empty, StringComparer.Ordinal);
        }

        private static bool ReadBool(Dictionary<string, string> values, string property, bool fallback) =>
            values.TryGetValue(property, out var value) && value != null
                ? value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                : fallback;

        private static string? ReadString(Dictionary<string, string> values, string property, string? fallback) =>
            values.TryGetValue(property, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;

        private void Write(int userId, string property, string value)
        {
            var name = Key(userId, property);
            var existing = _db.AppSettings.FirstOrDefault(a => a.Name == name);

            if (existing is null)
            {
                _db.AppSettings.Add(new AppSetting { Name = name, Value = value });
            }
            else
            {
                existing.Value = value;
            }
        }
    }
}
