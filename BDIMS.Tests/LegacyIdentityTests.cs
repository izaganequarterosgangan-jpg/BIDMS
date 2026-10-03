using BDIMS.Models;
using BDIMS.Services;

namespace BDIMS.Tests;

/// <summary>
/// Pins the badge-initial rules the My Profile page depends on.
///
/// The badge is derived rather than typed, so the name a user saves can silently
/// change it. These cases lock down the two behaviours the UI relies on: the
/// structured first/last names win when present, and the display name is the
/// fallback for accounts created before those columns existed.
/// </summary>
public class ProfileInitialsTests
{
    [Theory]
    // First letter of first name + first letter of last name.
    [InlineData("Isagane", "Quarteros", "IQ")]
    [InlineData("Quarteros", "Isagane", "QI")]
    [InlineData("maria", "santos", "MS")]
    [InlineData("Ana", "Dela Cruz", "AD")]
    public void ComposeInitials_UsesFirstLetterOfEachName(string first, string last, string expected)
    {
        Assert.Equal(expected, UserAccount.ComposeInitials(first, last));
    }

    [Fact]
    public void ComposeInitials_ReturnsNullWhenANameHalfIsMissing()
    {
        // Null, not a one-letter badge: the caller falls back to DeriveInitials so
        // the badge tracks the display name instead of collapsing to "I".
        Assert.Null(UserAccount.ComposeInitials("Isagane", ""));
        Assert.Null(UserAccount.ComposeInitials("", "Quarteros"));
        Assert.Null(UserAccount.ComposeInitials(null, null));
    }

    [Theory]
    [InlineData("Isagane Quarteros", "IQ")]
    [InlineData("Quarteros Isagane", "QI")]
    [InlineData("  Isagane   Quarteros  ", "IQ")]
    [InlineData("Isagane", "I")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void DeriveInitials_FallsBackToDisplayName(string? displayName, string expected)
    {
        Assert.Equal(expected, UserAccount.DeriveInitials(displayName));
    }

    [Fact]
    public void ResolveInitials_PrefersStructuredNamesOverDisplayName()
    {
        var account = new UserAccount
        {
            DisplayName = "Isagane Quarteros",
            FirstName = "Quarteros",
            LastName = "Isagane"
        };

        // The stored halves carry the true order, so the badge must not be derived
        // from the display name here - that would render "IQ" instead of "QI".
        Assert.Equal("QI", account.ResolveInitials());
    }

    [Fact]
    public void ResolveInitials_UsesDisplayNameWhenHalvesAreBlank()
    {
        // Accounts predating the split-name columns have both halves empty; their
        // badges must keep rendering from the display name, not collapse to "?".
        var account = new UserAccount { DisplayName = "Isagane Quarteros" };

        Assert.Equal("IQ", account.ResolveInitials());
    }

    [Fact]
    public void ComposeDisplayName_JoinsHalvesAndOmitsMissingSeparator()
    {
        Assert.Equal("Isagane Quarteros", UserAccount.ComposeDisplayName("Isagane", "Quarteros"));
        Assert.Equal("Isagane", UserAccount.ComposeDisplayName("Isagane", "   "));
        Assert.Equal("Quarteros", UserAccount.ComposeDisplayName(null, "Quarteros"));

        // Falls back to the stored value when neither half is filled in, so the
        // sidebar never renders an empty name.
        Assert.Equal(
            "Existing Name",
            UserAccount.ComposeDisplayName("", "", "Existing Name"));
    }

    [Fact]
    public void SplitDisplayName_RecoversHalvesForOlderAccounts()
    {
        Assert.Equal(("Isagane", "Quarteros"), UserAccount.SplitDisplayName("Isagane Quarteros"));
        Assert.Equal(("Isagane", ""), UserAccount.SplitDisplayName("Isagane"));
        Assert.Equal((string.Empty, string.Empty), UserAccount.SplitDisplayName("   "));
    }

    [Fact]
    public void ProfileViewModel_ProjectsAccountAndDerivesBadge()
    {
        var account = new UserAccount
        {
            Id = 7,
            DisplayName = "Isagane Quarteros",
            FirstName = "Isagane",
            LastName = "Quarteros",
            EmployeeId = "EMP-0015",
            ContactNumber = "09171234567",
            Email = "isagane@example.com",
            Role = "Barangay Secretary"
        };

        var model = ProfileViewModel.FromAccount(account);

        Assert.Equal(7, model.Id);
        Assert.Equal("EMP-0015", model.EmployeeId);
        Assert.Equal("09171234567", model.ContactNumber);
        Assert.Equal("Isagane Quarteros", model.DisplayName);
        Assert.Equal("IQ", model.Initials);
    }

    [Fact]
    public void ProfileViewModel_BackfillsHalvesForAccountWithoutStructuredNames()
    {
        // An account saved before this feature has blank halves. The form must open
        // with usable text boxes rather than two empty ones.
        var account = new UserAccount { DisplayName = "Isagane Quarteros" };

        var model = ProfileViewModel.FromAccount(account);

        Assert.Equal("Isagane", model.FirstName);
        Assert.Equal("Quarteros", model.LastName);
        Assert.Equal("IQ", model.Initials);
    }

    [Fact]
    public void IsChangingPassword_OnlyWhenAPasswordFieldIsFilledIn()
    {
        var model = new ProfileViewModel();

        // Blank means "leave the password alone" - the normal profile-only edit.
        Assert.False(model.IsChangingPassword);

        model.NewPassword = "Something@123";
        Assert.True(model.IsChangingPassword);
    }
}

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