using System;
using System.Collections.Generic;
using BDIMS.Models;
using BDIMS.Services;

namespace BDIMS.Tests;

/// <summary>
/// Covers the Dashboard header/hero rules in <see cref="DashboardGreeting"/>.
/// Every boundary hour is asserted because the evening band wraps past midnight;
/// an off-by-one at 04:59 or 18:00 would greet a user at the wrong time of day
/// with no error and no visible defect anywhere else.
/// </summary>
public class DashboardGreetingTests
{
    [Theory]
    [InlineData(5, "GOOD MORNING")]
    [InlineData(8, "GOOD MORNING")]
    [InlineData(11, "GOOD MORNING")]
    [InlineData(12, "GOOD AFTERNOON")]
    [InlineData(17, "GOOD AFTERNOON")]
    [InlineData(18, "GOOD EVENING")]
    [InlineData(23, "GOOD EVENING")]
    [InlineData(0, "GOOD EVENING")]
    [InlineData(4, "GOOD EVENING")]
    public void ForHour_MapsEveryBandToCorrectGreeting(int hour, string expected)
    {
        Assert.Equal(expected, DashboardGreeting.ForHour(hour));
    }

    [Fact]
    public void ForHour_RejectsOutOfRangeHour()
    {
        // Guards against a silently-wrong band from an out-of-domain value.
        Assert.Throws<ArgumentOutOfRangeException>(() => DashboardGreeting.ForHour(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DashboardGreeting.ForHour(24));
    }

    [Fact]
    public void Compose_AppendsUppercasedFirstName()
    {
        Assert.Equal("GOOD EVENING, ISAGANE",
            DashboardGreeting.Compose(20, "Isagane"));
    }

    [Fact]
    public void Compose_OmitsSeparatorWhenFirstNameMissing()
    {
        // A dangling ", " on the banner would be the visible defect here.
        Assert.Equal("GOOD MORNING", DashboardGreeting.Compose(9, null));
        Assert.Equal("GOOD MORNING", DashboardGreeting.Compose(9, ""));
        Assert.Equal("GOOD MORNING", DashboardGreeting.Compose(9, "   "));
    }

    [Fact]
    public void ComposeSubtitle_UsesTonightInEveningAndTodayOtherwise()
    {
        Assert.Equal("0 document requests need your attention tonight.",
            DashboardGreeting.ComposeSubtitle(0, 20));
        Assert.Equal("0 document requests need your attention today.",
            DashboardGreeting.ComposeSubtitle(0, 10));
    }

    [Fact]
    public void ComposeSubtitle_PluralisesCountOfOne()
    {
        Assert.Equal("1 document request needs your attention today.",
            DashboardGreeting.ComposeSubtitle(1, 13));
        Assert.Equal("2 document requests need your attention tonight.",
            DashboardGreeting.ComposeSubtitle(2, 22));
    }

    [Fact]
    public void ComposeLocation_SkipsBlankPartsInsteadOfEmittingStrayCommas()
    {
        // The reported bug: blank BarangayName/Municipality rendered
        // "Barangay , , Bohol".
        Assert.Equal("Bohol",
            DashboardGreeting.ComposeLocation("", "", "Bohol"));
        Assert.Equal("Ubay, Bohol",
            DashboardGreeting.ComposeLocation("", "Ubay", "Bohol"));
    }

    [Fact]
    public void ComposeLocation_JoinsFullyConfiguredPartsInOrder()
    {
        Assert.Equal("Barangay Governor Boyles, Ubay, Bohol",
            DashboardGreeting.ComposeLocation("Governor Boyles", "Ubay", "Bohol"));
    }

    [Fact]
    public void ComposeLocation_DoesNotDoublePrefixBarangay()
    {
        Assert.Equal("Barangay Governor Boyles, Ubay, Bohol",
            DashboardGreeting.ComposeLocation("Barangay Governor Boyles", "Ubay", "Bohol"));
    }

    [Fact]
    public void ComposeLocation_FallsBackWhenNothingConfigured()
    {
        // Blank output would collapse the header subtitle to a bare separator.
        Assert.Equal("Barangay location not configured",
            DashboardGreeting.ComposeLocation(null, null, null));
    }
}

/// <summary>
/// Covers the {{ Token }} substitution contract in <see cref="CertificateTemplateRenderer"/>.
/// These are pure string transforms with no database or file dependency, which is
/// exactly why they are worth pinning down: a silent regression here prints a
/// blank resident name on a legal certificate rather than throwing an error.
/// </summary>
public class CertificateTemplateRendererTests
{
    internal static CertificateTemplateData Sample() => new()
    {
        ResidentName = "Den Mark B. Etorma",
        ResidentAge = 17,
        Purok = "Purok 2",
        Address = "Purok 2, Governor Boyles, Ubay, Bohol",
        Purpose = "Enrollment",
        IssueDate = new DateTime(2024, 7, 23),
        BarangayCaptain = "Celes P. Pondavilla",
        SignatoryRole = "Punong Barangay",
        BarangayName = "Governor Boyles",
        Municipality = "Ubay",
        Province = "Bohol",
        OrNumber = "OR-2026-0900",
        CertificateId = "CERT-042",
        AmountPaid = 50.00m,
        CertificateType = "Barangay Indigency"
    };

    [Fact]
    public void Render_ReplacesKnownTokensWithValues()
    {
        var result = CertificateTemplateRenderer.Render(
            "<p>{{ Province }} / {{ Municipality }} / {{ BarangayName }}</p>", Sample());

        Assert.Equal("<p>Bohol / Ubay / GOVERNOR BOYLES</p>", result);
    }

    [Fact]
    public void Render_UppercasesResidentNameAndPreservesRawVariant()
    {
        // Both variants carry the required bold+underline, so the assertion pins the
        // casing and the emphasis together.
        var result = CertificateTemplateRenderer.Render(
            "<b><u>{{ ResidentName }}</u></b>|<b><u>{{ ResidentNameRaw }}</u></b>", Sample());

        Assert.Equal(
            "<b><u>DEN MARK B. ETORMA</u></b>|<b><u>Den Mark B. Etorma</u></b>",
            result);
    }

    [Fact]
    public void Render_LeavesUnknownTokenVisibleInsteadOfBlankingIt()
    {
        // The documented contract: an unknown token stays in place. Silently
        // printing an empty string here is the worst failure mode for a legal
        // document because nothing raises an error.
        var result = CertificateTemplateRenderer.Render(
            "<p>Name: {{ ResidenName }}</p>", Sample());

        Assert.Equal("<p>Name: {{ ResidenName }}</p>", result);
    }

    [Fact]
    public void Render_ResolvesLegacySingleBraceAliases()
    {
        var result = CertificateTemplateRenderer.Render(
            "<p>{RESIDENT} / {ORNO} / {TITLE}</p>", Sample());

        Assert.Equal("<p>DEN MARK B. ETORMA / OR-2026-0900 / Barangay Indigency</p>", result);
    }

    [Fact]
    public void Render_LeavesUnmappedLegacyTokenUntouched()
    {
        var result = CertificateTemplateRenderer.Render("<p>{NOT_A_TOKEN}</p>", Sample());

        Assert.Equal("<p>{NOT_A_TOKEN}</p>", result);
    }
[Fact]
    public void Render_HtmlEncodesResolvedValues()
    {
        // Resident names are free text typed by a clerk; encoding prevents a name
        // containing markup from injecting structure into the printed HTML.
        var data = Sample();
        data.ResidentName = "Ana & Sons";

        var result = CertificateTemplateRenderer.Render("<p><b><u>{{ ResidentNameRaw }}</u></b></p>", data);

        Assert.Equal("<p><b><u>Ana &amp; Sons</u></b></p>", result);
    }

    [Fact]
    public void Render_UsesVisiblePlaceholderWhenDataIsMissing()
    {
        var result = CertificateTemplateRenderer.Render(
            "<p>{{ Province }} / <b><u>{{ Purpose }}</u></b></p>", new CertificateTemplateData());

        Assert.Equal("<p>[ Province ] / <b><u>[ Purpose ]</u></b></p>", result);
    }

    [Fact]
    public void Render_ReturnsEmptyStringForEmptyTemplate()
    {
        Assert.Equal(string.Empty, CertificateTemplateRenderer.Render("", Sample()));
        Assert.Equal(string.Empty, CertificateTemplateRenderer.Render(null, Sample()));
    }

    [Fact]
    public void Render_FallsBackToPurokWhenAddressIsBlank()
    {
        var data = Sample();
        data.Address = "   ";

        var result = CertificateTemplateRenderer.Render("{{ Address }}", data);

        Assert.Equal("Purok 2", result);
    }

    [Fact]
    public void Render_FormatsAmountWithTwoDecimalsAndInvariantCulture()
    {
        var data = Sample();
        data.AmountPaid = 1234.5m;

        var result = CertificateTemplateRenderer.Render("{{ AmountPaid }}", data);

        // Invariant culture keeps the decimal separator a '.' regardless of the
        // server locale, so an amount never prints as "1234,50" on a certificate.
        // The "N" format keeps the thousands group separator, which is the normal
        // way to write a peso amount on an official document.
        Assert.Equal("1,234.50", result);
    }

    [Fact]
    public void ExtractTokens_ReturnsEachTokenOnce()
    {
        var tokens = CertificateTemplateRenderer
            .ExtractTokens("{{ Province }} {{ Province }} {RESIDENT}")
            .ToList();

        Assert.Equal(new[] { "Province", "ResidentName" }, tokens);
    }

    [Fact]
    public void UnknownTokens_ReportsOnlyNamesMissingFromTheTokenTable()
    {
        var unknown = CertificateTemplateRenderer
            .UnknownTokens("{{ Province }} {{ ResidenName }} {{ NotAField }}")
            .ToList();

        Assert.Equal(new[] { "ResidenName", "NotAField" }, unknown);
    }

    // ------------------------------------------------------------------
    // Required inline emphasis on a printed certificate.
    // ------------------------------------------------------------------

    [Fact]
    public void Render_BoldsAndUnderlinesResidentNamePurposeAndDateParts()
    {
        var result = CertificateTemplateRenderer.Render(
            "<b><u>{{ ResidentName }}</u></b> and <b><u>{{ Purpose }}</u></b> and "
            + "<b><u>{{ DayOrdinal }}</u></b> and <b><u>{{ MonthYear }}</u></b>",
            Sample());

        Assert.Equal(
            "<b><u>DEN MARK B. ETORMA</u></b> and <b><u>Enrollment</u></b> and "
            + "<b><u>23rd</u></b> and <b><u>JULY, 2024</u></b>",
            result);
    }

    [Fact]
    public void Render_BoldsAgeButLeavesYearsOldPlain()
    {
        // The required output: the number is emphasised, the words are not.
        var result = CertificateTemplateRenderer.Render(
            "<b>{{ ResidentAge }}</b> years old", Sample());

        Assert.Equal("<b>17</b> years old", result);
    }

    [Fact]
    public void Render_LeavesPurokUnemphasised()
    {
        // "a resident of Purok 2" must read as ordinary prose.
        var result = CertificateTemplateRenderer.Render(
            "a resident of {{ Purok }}", Sample());

        Assert.Equal("a resident of Purok 2", result);
    }

    [Fact]
    public void Render_DoesNotDoubleWrapWhenMarkupAlreadyCarriesEmphasis()
    {
        // The shipped default has the tags in the markup. Wrapping again would emit
        // <b><u><b><u>..</u></b></u></b> and bloat every printed certificate.
        var result = CertificateTemplateRenderer.Render(
            "<b><u>{{ ResidentName }}</u></b>", Sample());

        Assert.Equal("<b><u>DEN MARK B. ETORMA</u></b>", result);
        Assert.DoesNotContain("<b><u><b>", result);
    }

    [Theory]
    [InlineData("<strong><u>{{ ResidentName }}</u></strong>")]
    [InlineData("<u><b>{{ ResidentName }}</b></u>")]
    [InlineData("<b><u> {{ ResidentName }} </u></b>")]
    public void Render_AcceptsAnyExistingEmphasisTagOrder(string markup)
    {
        // A .docx import emits <strong>/<u> in either order; re-wrapping would
        // duplicate it, so all of these must be left alone.
        var result = CertificateTemplateRenderer.Render(markup, Sample());

        Assert.Equal(1, CountOccurrences(result, "<u>"));
        Assert.Equal(
            1,
            CountOccurrences(result, "<b>") + CountOccurrences(result, "<strong>"));
        Assert.DoesNotContain("<b><u><b>", result);
        Assert.DoesNotContain("<u><b><u>", result);
    }

    [Fact]
    public void Render_WrapsBareTokenForOlderTemplates()
    {
        // A template saved before the emphasis rule has no tags at all. It must
        // acquire them rather than print unformatted on a legal document.
        var result = CertificateTemplateRenderer.Render(
            "<p>{{ ResidentName }}, {{ Purpose }}</p>", Sample());

        Assert.Equal(
            "<p><b><u>DEN MARK B. ETORMA</u></b>, <b><u>Enrollment</u></b></p>",
            result);
    }

    [Fact]
    public void Render_LeavesBarePurokAndOtherTokensAlone()
    {
        var result = CertificateTemplateRenderer.Render(
            "{{ Purok }} {{ Municipality }} {{ Province }}", Sample());

        Assert.Equal("Purok 2 Ubay Bohol", result);
    }

    [Fact]
    public void Render_ResidentAgeFullKeepsWholePhraseForLegacyTemplates()
    {
        // Templates saved before the age split have no literal "years old" next to
        // the token, so they use this token instead of losing the words.
        var result = CertificateTemplateRenderer.Render(
            "{{ ResidentAgeFull }}, a resident of", Sample());

        Assert.Equal("17 years old, a resident of", result);
    }

    [Fact]
    public void Render_ResidentAgeFullStaysPlainBecauseItIsNotInTheEmphasisTable()
    {
        var result = CertificateTemplateRenderer.Render("{{ ResidentAgeFull }}", Sample());

        Assert.Equal("17 years old", result);
    }

    [Fact]
    public void ShippedDefaultTemplate_MatchesTheRequiredLayout()
    {
        // The default layout is what a clerk restores, so its emphasis has to match
        // the rule exactly - checked here against the shipped constant.
        var rendered = CertificateTemplateRenderer.Render(
            CertificateTemplateDefaults.BarangayIndigencyTemplateHtml, Sample());

        Assert.Contains(
            "<b><u>DEN MARK B. ETORMA</u></b>, <b>17</b> years old, a resident of Purok 2,",
            rendered);
        Assert.Contains(
            "<b><u>Enrollment</u></b>",
            rendered);
        Assert.Contains(
            "<b><u>23rd</u></b> day of <b><u>JULY, 2024</u></b>",
            rendered);
    }

    [Fact]
    public void Unsaved_Certificate_Template_Renders_Age_Bold_Only_And_Day_Split_Out()
    {
        // "17" alone must be bold; the trailing words must not inherit bold or underline,
        // and the day must come through as a separately styleable token.
        var r = CertificateTemplateRenderer.Render(
            "<b>{{ResidentAge}}</b> years old, given this <b><u>{{DayOrdinal}}</u></b> day of <b><u>{{MonthYear}}</u></b>",
            Sample());

        Assert.Contains("<b>17</b> years old", r);
        Assert.DoesNotContain("<b>17 years old</b>", r);
        Assert.Contains("<b><u>23rd</u></b> day of", r);
        Assert.Contains("<b><u>JULY, 2024</u></b>", r);
    }

    [Fact]
    public void Unsaved_Certificate_Template_Survives_Double_Wrapped_Token()
    {
        // A template saved before this rule wrapped the token itself. Applying the rule
        // on an already-wrapped token would nest <b><u><b> and print stray markup.
        var r = CertificateTemplateRenderer.Render(
            "<strong><u>{{ResidentName}}</u></strong>",
            Sample());

        Assert.Equal("<strong><u>DEN MARK B. ETORMA</u></strong>", r);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
