using BDIMS.Models;
using BDIMS.Services;

namespace BDIMS.Tests;

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
        var result = CertificateTemplateRenderer.Render(
            "{{ ResidentName }}|{{ ResidentNameRaw }}", Sample());

        Assert.Equal("DEN MARK B. ETORMA|Den Mark B. Etorma", result);
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

        var result = CertificateTemplateRenderer.Render("<p>{{ ResidentNameRaw }}</p>", data);

        Assert.Equal("<p>Ana &amp; Sons</p>", result);
    }

    [Fact]
    public void Render_UsesVisiblePlaceholderWhenDataIsMissing()
    {
        var result = CertificateTemplateRenderer.Render(
            "<p>{{ Province }} / {{ Purpose }}</p>", new CertificateTemplateData());

        Assert.Equal("<p>[ Province ] / [ Purpose ]</p>", result);
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
}
