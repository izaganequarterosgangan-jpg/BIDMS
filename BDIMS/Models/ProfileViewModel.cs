using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BDIMS.Models
{
    /// <summary>
    /// Backing model for the My Profile page.
    ///
    /// It carries both the information fields and the change-password block, and is
    /// posted back as a single unit so the password section can be left completely
    /// blank when only the profile is being updated.
    /// </summary>
    public class ProfileViewModel
    {
        /// <summary>
        /// Database key of the account. Bound from the signed-in claim rather than from
        /// the form, so a tampered field cannot redirect the update at another user.
        /// </summary>
        [BindNever]
        public int Id { get; set; }

        [Display(Name = "FIRST NAME")]
        [StringLength(128, ErrorMessage = "First name cannot exceed 128 characters.")]
        [Required(ErrorMessage = "Please enter your first name.")]
        public string FirstName { get; set; } = string.Empty;

        [Display(Name = "LAST NAME")]
        [StringLength(128, ErrorMessage = "Last name cannot exceed 128 characters.")]
        [Required(ErrorMessage = "Please enter your last name.")]
        public string LastName { get; set; } = string.Empty;

        [Display(Name = "EMPLOYEE ID")]
        [StringLength(64, ErrorMessage = "Employee ID cannot exceed 64 characters.")]
        // Nullable, not just un-annotated: the project has <Nullable>enable</Nullable>,
        // and MVC supplies an implicit [Required] for every non-nullable reference-type
        // property. That implicit attribute rejects "" as well as null, so an officer
        // who has not been issued an ID yet could not save their profile at all - the
        // save failed on a field they are legitimately allowed to leave blank.
        public string? EmployeeId { get; set; }

        [Display(Name = "CONTACT NUMBER")]
        [StringLength(32, ErrorMessage = "Contact number cannot exceed 32 characters.")]
        // Nullable for the same reason as EmployeeId above.
        public string? ContactNumber { get; set; }

        [Display(Name = "EMAIL ADDRESS")]
        [StringLength(256, ErrorMessage = "Email address cannot exceed 256 characters.")]
        [Required(ErrorMessage = "Please enter your email address.")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "ROLE / POSITION")]
        [StringLength(128, ErrorMessage = "Role cannot exceed 128 characters.")]
        // Required explicitly so the operator gets a message that says what to do,
        // instead of the generic implicit one. The role is rendered under the name in
        // the sidebar, so leaving it blank would show an empty line there.
        [Required(ErrorMessage = "Please enter your role or position.")]
        public string Role { get; set; } = string.Empty;

        /// <summary>Badge text, recomputed on save. Never posted back by the form.</summary>
        [BindNever]
        public string Initials { get; set; } = string.Empty;

        /// <summary>Name shown on the profile card, recomputed on save.</summary>
        [BindNever]
        public string DisplayName { get; set; } = string.Empty;

        // --- Change password -------------------------------------------------
        // None of these are [Required]: an empty password block means "leave the
        // password alone", which is the normal case for a profile-only edit. The
        // controller enforces the rule that they must all be supplied together.

        [Display(Name = "CURRENT PASSWORD")]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Display(Name = "NEW PASSWORD")]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = string.Empty;

        [Display(Name = "CONFIRM NEW PASSWORD")]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "The new password and its confirmation do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;

        /// <summary>
        /// True when the user filled in any part of the password block. The password
        /// rules are only enforced when this is set, so a blank block is never read as
        /// "change my password to nothing".
        /// </summary>
        public bool IsChangingPassword =>
            !string.IsNullOrWhiteSpace(CurrentPassword)
            || !string.IsNullOrWhiteSpace(NewPassword)
            || !string.IsNullOrWhiteSpace(ConfirmPassword);

        /// <summary>Projects a stored account into the form, deriving the badge initials.</summary>
        public static ProfileViewModel FromAccount(UserAccount account)
        {
            // Accounts created before the split-name columns existed still have a usable
            // display name, so the halves are recovered from it rather than showing two
            // blank boxes the operator has to guess at.
            var first = string.IsNullOrWhiteSpace(account.FirstName)
                ? UserAccount.SplitDisplayName(account.DisplayName).First
                : account.FirstName;

            var last = string.IsNullOrWhiteSpace(account.LastName)
                ? UserAccount.SplitDisplayName(account.DisplayName).Last
                : account.LastName;

            return new ProfileViewModel
            {
                Id = account.Id,
                FirstName = first,
                LastName = last,
                EmployeeId = account.EmployeeId ?? string.Empty,
                ContactNumber = account.ContactNumber ?? string.Empty,
                Email = account.Email ?? string.Empty,
                Role = account.Role ?? string.Empty,
                DisplayName = UserAccount.ComposeDisplayName(first, last, account.DisplayName),
                Initials = account.ResolveInitials()
            };
        }
    }
}