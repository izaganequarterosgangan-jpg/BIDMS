using BDIMS.Models;
using BDIMS.Services;

namespace BDIMS.Tests;

/// <summary>
/// Regression tests for the letterhead-literal promotion added with the editable
/// barangay profile.
///
/// A template imported from a .docx arrives with the place names typed in by hand
/// ("Province of Bohol", "Barangay Governor Boyles, Ubay, Bohol"). If those
/// literals are not promoted to tokens before substitution, a later profile
/// change is silently ignored and the certificate keeps printing the old place -
/// with no error anywhere. These tests pin the promotion and, just as
/// importantly, pin the rule that unrelated text is left alone.
///
/// The renderer holds the recognised identity in private static state, which is
/// why the assembly disables test parallelisation in AssemblyInfo.cs.
/// </summary>
public class LegacyIdentityNormalizationTests
{
    private static void Configure() => CertificateTemplateRenderer.ConfigureLegacyIdentity(
        new BarangayOptions
        {
            BarangayName = "Governor Boyles",
            Municipality = "Ubay",
            Province = "Bohol"
        });

    [Fact]
    public void Normalize_PromotesStandaloneProvinceLine()
    {
        Configure();

        var result = CertificateTemplateRenderer.NormalizeLegacyIdentity("<p>Province of Bohol</p>");

        Assert.Equal("<p>Province of {{ Province }}</p>", result);
    }

    [Fact]
    public void Normalize_PromotesStandaloneMunicipalityLine()
    {
        Configure();

        var result = CertificateTemplateRenderer.NormalizeLegacyIdentity("<p>Municipality of Ubay</p>");

        Assert.Equal("<p>Municipality of {{ Municipality }}</p>", result);
    }

    [Fact]
    public void Normalize_PromotesBarangayLineAndKeepsTrailingComma()
    {
        Configure();

        var result = CertificateTemplateRenderer.NormalizeLegacyIdentity(
            "<p>Barangay Governor Boyles,</p>");

        Assert.Equal("<p>Barangay {{ BarangayName }},</p>", result);
    }

    [Fact]
    public void Normalize_StripsClosingSpanTagsBeforeMatching()
    {
        // A .docx import wraps nearly every run in its own <span>. An earlier
        // version of this normaliser left "</p></span>" in place, compared it
        // against "Bohol" and matched nothing, so every letterhead line kept its
        // stale literal. This case is the regression guard for that bug.
        //
        // The tags themselves are deliberately preserved - only the identity text
        // is swapped, so the styling a clerk applied survives the promotion.
        Configure();

        var result = CertificateTemplateRenderer.NormalizeLegacyIdentity(
            "<p><span>Province of Bohol</span></p>");

        Assert.Equal("<p><span>Province of {{ Province }}</span></p>", result);
    }

    [Fact]
    public void Normalize_PromotesIdentityInsideAnAddressSentence()
    {
        Configure();

        var result = CertificateTemplateRenderer.NormalizeLegacyIdentity(
            "<p>issued at Barangay Governor Boyles, Ubay, Bohol, Philippines</p>");

        Assert.Equal(
            "<p>issued at Barangay {{ BarangayName }}, {{ Municipality }}, {{ Province }}, Philippines</p>",
            result);
    }

    [Fact]
    public void Normalize_PromotesIdentityEndingInPunctuation()
    {
        // A letterhead line normally ends in a comma. The captured text is then
        // "Governor Boyles," rather than the configured identity, so a comparison
        // that does not trim trailing punctuation fails to match and the stale
        // place survives onto the printed certificate.
        Configure();

        var result = CertificateTemplateRenderer.NormalizeLegacyIdentity(
            "<p>Barangay Governor Boyles,</p>");

        Assert.Equal("<p>Barangay {{ BarangayName }},</p>", result);
    }

    [Fact]
    public void Normalize_PromotesIdentityEndingInPunctuationInsideSpans()
    {
        // The combination that the imported .docx templates actually produce:
        // strong and span wrappers plus a trailing comma.
        Configure();

        var result = CertificateTemplateRenderer.NormalizeLegacyIdentity(
            "<p><span><strong>Barangay Governor Boyles,</strong></span></p>");

        Assert.Equal(
            "<p><span><strong>Barangay {{ BarangayName }},</strong></span></p>", result);
    }

    [Fact]
    public void Normalize_LeavesUnrelatedPlaceNamesAlone()
    {
        // Only values that actually match the configured identity are promoted, so
        // a layout that legitimately names a different place is not corrupted.
        Configure();

        const string html = "<p>Municipality of Talibon</p><p>Province of Leyte</p>";

        var result = CertificateTemplateRenderer.NormalizeLegacyIdentity(html);

        Assert.Equal(html, result);
    }

    [Fact]
    public void Normalize_IsANoOpWhenNoIdentityIsConfigured()
    {
        CertificateTemplateRenderer.ConfigureLegacyIdentity(null);

        const string html = "<p>Province of Bohol</p>";

        Assert.Equal(html, CertificateTemplateRenderer.NormalizeLegacyIdentity(html));
    }

    [Fact]
    public void Render_UsesPromotedTokensSoAProfileChangeReachesTheLetterhead()
    {
        Configure();

        // The template on disk still says "Bohol"; the data says otherwise. The
        // rendered output must follow the data.
        var html = "<p>Province of Bohol</p><p>Municipality of Ubay</p>";
        var data = CertificateTemplateRendererTests.Sample();
        data.Province = "Cebu";
        data.Municipality = "Mandaue";

        var result = CertificateTemplateRenderer.Render(html, data);

        Assert.Equal("<p>Province of Cebu</p><p>Municipality of Mandaue</p>", result);
    }
}