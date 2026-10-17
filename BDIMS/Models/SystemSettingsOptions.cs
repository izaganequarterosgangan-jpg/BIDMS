using System;

namespace BDIMS.Models
{
    /// <summary>
    /// The Settings &gt; System &amp; Security preferences.
    ///
    /// Held as its own type rather than extra properties on
    /// <see cref="SettingsViewModel"/> so the values can be read by anything that
    /// needs to honour them (document approval, notification triggers, the auth
    /// cookie) without dragging the whole page model along.
    /// </summary>
    public class SystemSettingsOptions
    {
        /// <summary>
        /// When true, an incoming non-blotter document request whose status is
        /// still Pending is approved as it is created.
        /// </summary>
        public bool AutoApproveLowRisk { get; set; } = true;

        public bool EmailNotifications { get; set; } = true;

        public bool SmsNotifications { get; set; }

        /// <summary>
        /// When true, staff sign-ins and record changes are written to the audit
        /// trail, giving the barangay a per-user activity history for
        /// administrative accountability.
        /// </summary>
        public bool AuditLogging { get; set; } = true;

        public string TwoFactorAuth { get; set; } = "Required";

        /// <summary>
        /// Idle timeout in minutes, stored as text so an unrecognised value can be
        /// rejected instead of silently becoming zero.
        /// </summary>
        public string SessionTimeout { get; set; } = "30";

        /// <summary>
        /// Password complexity preset for staff accounts. "Standard" is the
        /// everyday barangay ruleset; "Strict" enforces the hardened rules for
        /// higher-privilege accounts.
        /// </summary>
        public string PasswordPolicy { get; set; } = "Standard";

        /// <summary>
        /// The timeout as a <see cref="TimeSpan"/>, clamped to the options the
        /// dropdown actually offers so a hand-edited stored value cannot pin a
        /// session open for a day or lock a clerk out after a minute.
        /// </summary>
        public TimeSpan SessionTimeoutSpan
        {
            get
            {
                var minutes = 30;

                if (int.TryParse(SessionTimeout, out var parsed) &&
                    parsed >= MinSessionTimeoutMinutes &&
                    parsed <= MaxSessionTimeoutMinutes)
                {
                    minutes = parsed;
                }

                return TimeSpan.FromMinutes(minutes);
            }
        }

        public const int MinSessionTimeoutMinutes = 15;
        public const int MaxSessionTimeoutMinutes = 120;

        public static readonly string[] TwoFactorChoices = { "Required", "Optional", "Disabled" };

        public static bool IsKnownTwoFactor(string? value) =>
            Array.IndexOf(TwoFactorChoices, value ?? string.Empty) >= 0;

        public static readonly string[] PasswordPolicyChoices = { "Standard", "Strict" };

        public static bool IsKnownPasswordPolicy(string? value) =>
            Array.IndexOf(PasswordPolicyChoices, value ?? string.Empty) >= 0;
    }
}
