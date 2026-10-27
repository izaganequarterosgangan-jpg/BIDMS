using System.ComponentModel.DataAnnotations;

namespace BDIMS.Models
{
    /// <summary>
    /// Form model for the "Create Resident Account" modal on the Login page.
    ///
    /// Submissions land as <see cref="UserAccount"/> rows with role "Resident" and
    /// status <see cref="UserAccountStatus.Pending"/>, so an administrator has to
    /// approve the account from the User Management page before it can sign in.
    /// </summary>
    public class RegisterViewModel
    {
        [Required(ErrorMessage = "Please enter your first name.")]
        [MaxLength(128)]
        [Display(Name = "FIRST NAME")]
        public string FirstName { get; set; } = string.Empty;

        /// <summary>Optional - many residents do not supply a middle name.</summary>
        [MaxLength(128)]
        [Display(Name = "MIDDLE NAME")]
        public string? MiddleName { get; set; }

        [Required(ErrorMessage = "Please enter your last name.")]
        [MaxLength(128)]
        [Display(Name = "LAST NAME")]
        public string LastName { get; set; } = string.Empty;

        /// <summary>Optional - generational suffix such as "Jr." or "III".</summary>
        [MaxLength(16)]
        [Display(Name = "SUFFIX")]
        public string? Suffix { get; set; }

        /// <summary>
        /// Birth date entered on the form. The age shown on the account is derived
        /// from this value (server-side) rather than trusting a posted number, so
        /// the two can never disagree.
        /// </summary>
        [Required(ErrorMessage = "Please enter your date of birth.")]
        [Display(Name = "DATE OF BIRTH")]
        public DateTime? DateOfBirth { get; set; }

        /// <summary>
        /// Auto-calculated from <see cref="DateOfBirth"/> by the form (read-only
        /// input) and recomputed authoritatively in AccountController.Register.
        /// </summary>
        [Range(1, 150, ErrorMessage = "Age must be between 1 and 150.")]
        [Display(Name = "AGE")]
        public int? Age { get; set; }

        /// <summary>Optional - marital status of the registrant.</summary>
        [MaxLength(32)]
        [Display(Name = "CIVIL STATUS")]
        public string? CivilStatus { get; set; }

        [Required(ErrorMessage = "Please select your sex.")]
        [Display(Name = "SEX")]
        public string Gender { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please select your purok.")]
        [MaxLength(64)]
        [Display(Name = "PUROK / ZONE")]
        public string Purok { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please select your voter status.")]
        [MaxLength(32)]
        [Display(Name = "VOTER STATUS")]
        public string VoterStatus { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your email address.")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        [MaxLength(256)]
        [Display(Name = "EMAIL ADDRESS")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please confirm your email address.")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        [MaxLength(256)]
        [Display(Name = "CONFIRM EMAIL")]
        public string ConfirmEmail { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your contact number.")]
        [Phone(ErrorMessage = "Please enter a valid contact number.")]
        [MaxLength(32)]
        [Display(Name = "CONTACT NUMBER")]
        public string ContactNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter a password.")]
        [DataType(DataType.Password)]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
        [Display(Name = "PASSWORD")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please confirm your password.")]
        [DataType(DataType.Password)]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
        [Display(Name = "CONFIRM PASSWORD")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
