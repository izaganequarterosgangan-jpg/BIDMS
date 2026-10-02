namespace BDIMS.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Simple key/value row used by the Settings page so edits made there (barangay
/// profile, system & security options) survive restarts. The values are plain
/// strings; typed properties on the view models parse them.
/// </summary>
public class AppSetting
{
    [Key]
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
