using System;
using System.Collections.Generic;
using System.Linq;

namespace BDIMS.Models
{
    /// <summary>
    /// Backing model for the Resident Portal &gt; Settings page.
    ///
    /// Everything on the page that a resident can change is represented here as a
    /// plain, bindable property: the account-security flags, the notification
    /// preferences (what to send and through which channel) and the appearance
    /// options. The values are persisted per account by
    /// <see cref="BDIMS.Services.ResidentSettingsStore"/> in the AppSettings
    /// key/value table, so they survive restarts without a schema migration.
    /// </summary>
    public class ResidentSettingsViewModel
    {
        // Theme identifiers. Centralised (rather than literals sprinkled through the
        // controller and the view) so the picker, the store and any future skin share
        // exactly the same strings.
        public const string ThemeBlueHarbor = "blue-harbor";
        public const string ThemeGreenCoast = "green-coast";
        public const string ThemeSunsetMarket = "sunset-market";

        /// <summary>Themes the picker offers, in display order.</summary>
        public static IReadOnlyList<string> AvailableThemes { get; } =
            new[] { ThemeBlueHarbor, ThemeGreenCoast, ThemeSunsetMarket };

        /// <summary>Ask for a one-time code when signing in from a new device.</summary>
        public bool TwoFactorEnabled { get; set; }

        /// <summary>Status changes on the resident's document requests.</summary>
        public bool RequestUpdates { get; set; }

        /// <summary>Community news, activities and advisories.</summary>
        public bool BarangayAnnouncements { get; set; }

        /// <summary>Reminders about upcoming barangay services.</summary>
        public bool ServiceReminders { get; set; }

        /// <summary>Notifications may be delivered by email.</summary>
        public bool ReceiveEmail { get; set; }

        /// <summary>Notifications may be delivered by SMS.</summary>
        public bool ReceiveSms { get; set; }

        /// <summary>Prefer the darker palette across the resident portal.</summary>
        public bool DarkMode { get; set; }

        /// <summary>
        /// Colour scheme key. Always one of <see cref="AvailableThemes"/>; a value
        /// outside that set is normalised to <see cref="ThemeBlueHarbor"/> on save so a
        /// tampered post cannot store an unusable theme.
        /// </summary>
        public string SelectedTheme { get; set; } = ThemeBlueHarbor;

        /// <summary>True when <paramref name="theme"/> is one of the offered themes.</summary>
        public static bool IsKnownTheme(string? theme) =>
            !string.IsNullOrWhiteSpace(theme)
            && AvailableThemes.Contains(theme.Trim().ToLowerInvariant());

        /// <summary>Returns the stored form of a valid theme, or the default fallback.</summary>
        public static string NormalizeTheme(string? theme) =>
            IsKnownTheme(theme) ? theme!.Trim().ToLowerInvariant() : ThemeBlueHarbor;
    }
}
