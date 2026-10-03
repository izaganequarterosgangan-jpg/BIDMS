using System.Text;

namespace BDIMS.Services;

/// <summary>
/// Pure, side-effect-free helpers behind the Dashboard header and hero banner:
/// the time-of-day greeting, the request-count subline and the barangay location
/// string. Kept out of the Razor view so the rules are unit-testable and so the
/// markup stays free of inline branching.
/// </summary>
public static class DashboardGreeting
{
    /// <summary>Day-part boundary constants, in 24-hour local time.</summary>
    public const int MorningStartHour = 5;
    public const int AfternoonStartHour = 12;
    public const int EveningStartHour = 18;

    /// <summary>
    /// Maps an hour of the day to its greeting. The evening band deliberately
    /// wraps past midnight (18:00-23:59 and 00:00-04:59) rather than being
    /// modelled as a single range, because <c>hour &gt;= 18 || hour &lt; 5</c>
    /// is the only form that reads correctly without an off-by-one at 04:59.
    /// </summary>
    public static string ForHour(int hour)
    {
        if (hour < 0 || hour > 23)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hour), hour, "Hour must be a 24-hour value between 0 and 23.");
        }

        if (hour < MorningStartHour || hour >= EveningStartHour)
        {
            return "GOOD EVENING";
        }

        return hour < AfternoonStartHour ? "GOOD MORNING" : "GOOD AFTERNOON";
    }

    /// <summary>True for the 18:00-04:59 band, which the hero copy phrases as "tonight".</summary>
    public static bool IsEvening(int hour) => ForHour(hour) == "GOOD EVENING";

    /// <summary>
    /// Builds the hero eyebrow, e.g. <c>GOOD EVENING, ISAGANE</c>. Falls back to
    /// the bare greeting when no first name is available so the banner never
    /// renders a dangling comma. The name is uppercased to match the greeting.
    /// </summary>
    public static string Compose(int hour, string? firstName)
    {
        var greeting = ForHour(hour);
        var name = (firstName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return greeting;
        }

        // Upper-invariant so non-Latin names (e.g. "ñ") are not mangled by the
        // current culture's casing rules.
        return $"{greeting}, {name.ToUpperInvariant()}";
    }

    /// <summary>
    /// Builds the hero subline. The count and its pluralisation are unchanged from
    /// the original markup; only the trailing time word is time-aware.
    /// </summary>
    public static string ComposeSubtitle(int pendingCount, int hour)
    {
        var plural = pendingCount != 1 ? "s" : string.Empty;
        var period = IsEvening(hour) ? "tonight" : "today";

        return $"{pendingCount} document request{plural} need{(pendingCount != 1 ? "" : "s")} your attention {period}.";
    }

    /// <summary>
    /// Joins the configured locality parts, skipping blanks so an unconfigured
    /// value yields "Ubay, Bohol" instead of "Barangay , , Bohol". "Barangay" is
    /// only prepended when a barangay name actually exists to follow it.
    /// </summary>
    public static string ComposeLocation(
        string? barangayName, string? municipality, string? province)
    {
        var parts = new List<string>(3);

        var barangay = (barangayName ?? string.Empty).Trim();
        if (barangay.Length > 0)
        {
            // Already-prefixed names are left alone rather than becoming
            // "Barangay Barangay ...".
            parts.Add(barangay.StartsWith("Barangay ", StringComparison.OrdinalIgnoreCase)
                ? barangay
                : $"Barangay {barangay}");
        }

        foreach (var raw in new[] { municipality, province })
        {
            var part = (raw ?? string.Empty).Trim();
            if (part.Length > 0)
            {
                parts.Add(part);
            }
        }

        var joined = string.Join(", ", parts);
        return joined.Length > 0 ? joined : "Barangay location not configured";
    }
}