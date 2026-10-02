namespace BDIMS.Models;

/// <summary>
/// Bindable settings describing the barangay identity shown across the UI and
/// printed on certificate letterheads. Bound from the &quot;BDIMS:Barangay&quot;
/// configuration section, so values can come from appsettings.json, developer
/// user-secrets, or BDIMS__Barangay__* environment variables.
/// </summary>
public class BarangayOptions
{
    public const string SectionName = "BDIMS:Barangay";

    public string BarangayName { get; set; } = "Governor Boyles";
    public string Municipality { get; set; } = "Ubay";
    public string Province { get; set; } = "Bohol";
    public string ContactEmail { get; set; } = "contact@govboyles-ubay.gov.ph";
    public string ContactPhone { get; set; } = "+63 912 345 6789";
    public string OfficeHours { get; set; } = "8:00 AM - 5:00 PM (Mon-Fri)";
    public string SignatoryName { get; set; } = "Hon. Celes P. Pondavilla";
    public string SignatoryRole { get; set; } = "Punong Barangay";
}
