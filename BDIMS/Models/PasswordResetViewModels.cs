using System;
using System.ComponentModel.DataAnnotations;

namespace BDIMS.Models
{
    /// <summary>
    /// Step one of the details-based password-recovery flow
    /// (/Account/ForgotPassword): the user supplies the email registered to the
    /// account - the same lookup key the Login form accepts - to locate it.
    /// </summary>
    public class ForgotPasswordViewModel
    {
        [Required(ErrorMessage = "Please enter your email or username.")]
        [MaxLength(256)]
        [Display(Name = "EMAIL OR USERNAME")]
        public string UsernameOrEmail { get; set; } = string.Empty;
    }

    /// <summary>
    /// Step two (/Account/VerifyDetails): core personal details that must all
    /// match the registered profile before a new password can be chosen.
    /// </summary>
    public class VerifyDetailsViewModel
    {
        [Required(ErrorMessage = "Please enter your first name.")]
        [MaxLength(128)]
        [Display(Name = "FIRST NAME")]
        public string FirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your last name.")]
        [MaxLength(128)]
        [Display(Name = "LAST NAME")]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your date of birth.")]
        [Display(Name = "DATE OF BIRTH")]
        public DateTime? DateOfBirth { get; set; }

        [Required(ErrorMessage = "Please enter your contact number.")]
        [Phone(ErrorMessage = "Please enter a valid contact number.")]
        [MaxLength(32)]
        [Display(Name = "CONTACT NUMBER")]
        public string ContactNumber { get; set; } = string.Empty;
    }

    /// <summary>
    /// Step three (/Account/ResetPassword): choose the new password once the
    /// personal-details check has passed.
    /// </summary>
    public class ResetPasswordViewModel
    {
        [Required(ErrorMessage = "Please enter a new password.")]
        [DataType(DataType.Password)]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
        [Display(Name = "NEW PASSWORD")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please confirm the new password.")]
        [DataType(DataType.Password)]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        [Display(Name = "CONFIRM NEW PASSWORD")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
