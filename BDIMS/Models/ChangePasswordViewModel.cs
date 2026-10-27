using System.ComponentModel.DataAnnotations;

namespace BDIMS.Models
{
    /// <summary>
    /// Backing model for the "Change password" modal on the Resident Settings page.
    ///
    /// Kept separate from <see cref="ResidentSettingsViewModel"/> because the modal is
    /// its own form posting to its own action; folding the password fields into the
    /// settings model would make the main "Save changes" post carry (and risk
    /// persisting) a partially filled password block.
    ///
    /// The rules mirror the change-password block on the My Profile page: the current
    /// password is re-verified against the stored hash even though the user is already
    /// signed in, because a session can outlive a password change.
    /// </summary>
    public class ChangePasswordViewModel
    {
        [Required(ErrorMessage = "Please enter your current password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter a new password.")]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "The new password and its confirmation do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
