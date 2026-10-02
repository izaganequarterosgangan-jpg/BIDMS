using System.ComponentModel.DataAnnotations;

namespace BDIMS.Models
{
    public class LoginViewModel
    {
        [Required(ErrorMessage = "Please enter your email or username.")]
        [Display(Name = "EMAIL / USERNAME")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your password.")]
        [DataType(DataType.Password)]
        [Display(Name = "PASSWORD")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Keep me signed in")]
        public bool KeepSigned { get; set; }
    }
}