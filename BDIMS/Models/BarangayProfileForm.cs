namespace BDIMS.Models;

/// <summary>
/// Request payload for the "Barangay Profile &amp; Operations" form.
/// A dedicated DTO rather than the bound <see cref="BarangayOptions"/> so the
/// endpoint only accepts the eight profile fields a form legitimately posts.
/// Binding the configuration model directly would let a crafted request set
/// unrelated options through the same action.
/// </summary>
public class BarangayProfileForm
{
    public string? BarangayName { get; set; }
    public string? Municipality { get; set; }
    public string? Province { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? OfficeHours { get; set; }
    public string? SignatoryName { get; set; }
    public string? SignatoryRole { get; set; }
}