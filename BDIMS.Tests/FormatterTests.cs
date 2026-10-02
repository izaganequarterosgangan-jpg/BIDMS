using BDIMS.Services;

namespace BDIMS.Tests;

/// <summary>
/// Locks down the wording helpers that produce the fixed phrases on a printed
/// certificate (day ordinals, date phrasing, name casing).
/// </summary>
public class CertificateTemplateFormatterTests
{
    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(23, "23rd")]
    [InlineData(31, "31st")]
    public void Ordinal_ProducesCorrectDaySuffix(int day, string expected)
    {
        Assert.Equal(expected, CertificateTemplateFormatter.Ordinal(day));
    }

    [Fact]
    public void IssueDate_UsesFormalWording()
    {
        Assert.Equal(
            "23rd day of JULY, 2024",
            CertificateTemplateFormatter.IssueDate(new DateTime(2024, 7, 23)));
    }

    [Fact]
    public void IssueDateShort_UsesShortWording()
    {
        Assert.Equal(
            "23 JULY 2024",
            CertificateTemplateFormatter.IssueDateShort(new DateTime(2024, 7, 23)));
    }

    [Fact]
    public void Age_UsesSingularOnlyForOne()
    {
        Assert.Equal("1 year old", CertificateTemplateFormatter.Age(1));
        Assert.Equal("17 years old", CertificateTemplateFormatter.Age(17));
        Assert.Equal(string.Empty, CertificateTemplateFormatter.Age(null));
    }

    [Fact]
    public void ResidentName_UppercasesAndTrims()
    {
        Assert.Equal("ANA SANTOS", CertificateTemplateFormatter.ResidentName("  ana santos "));
        Assert.Equal(string.Empty, CertificateTemplateFormatter.ResidentName("   "));
    }
}