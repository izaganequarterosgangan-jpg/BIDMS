using System;

namespace BDIMS.Models
{
    public class CertificateRule
    {
        public int Id { get; set; }

        public string Type { get; set; } = "";

        public decimal Fee { get; set; }

        public bool RequiresResidency { get; set; }

        public string Status { get; set; } = "Active";

        public string Format { get; set; } = "";
    }
}