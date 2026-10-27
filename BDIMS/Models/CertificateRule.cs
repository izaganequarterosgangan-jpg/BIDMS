using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace BDIMS.Models
{
    public class CertificateRule
    {
        public int Id { get; set; }

        public string Type { get; set; } = "";

        /// <summary>
        /// Alias for <see cref="Type"/> so inline-rename code can read/write
        /// <c>cert.CertificateType</c>. Not mapped — both properties share one column.
        /// </summary>
        [NotMapped]
        public string CertificateType
        {
            get => Type;
            set => Type = value ?? string.Empty;
        }

        public decimal Fee { get; set; }

        public bool RequiresResidency { get; set; }

        public string Status { get; set; } = "Active";

        public string Format { get; set; } = "";

        /// <summary>
        /// Printable layout / template body for this certificate type.
        /// Kept as an empty string (never null) so inserts/updates never hit
        /// a NOT NULL database error when the row is saved without a layout.
        /// </summary>
        public string Template { get; set; } = "";

        /// <summary>
        /// True when the row was added by an administrator rather than seeded.
        /// Defaults to false so newly bound rows never post a null/missing value.
        /// </summary>
        public bool IsCustom { get; set; } = false;
    }
}