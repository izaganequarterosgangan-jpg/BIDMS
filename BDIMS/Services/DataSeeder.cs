using System;
using System.Collections.Generic;
using System.Linq;
using BDIMS.Data;
using BDIMS.Models;

namespace BDIMS.Services
{
    public static class DataSeeder
    {
        public static void Seed(ApplicationDbContext db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            SeedCertificateRules(db);
        }
        private static void SeedBlotters(ApplicationDbContext db) { _ = db; }
        private static void SeedCertificates(ApplicationDbContext db) { _ = db; }
        private static void SeedDocumentRequests(ApplicationDbContext db) { _ = db; }
        private static void SeedAnnouncements(ApplicationDbContext db) { _ = db; }
        private static void SeedReports(ApplicationDbContext db) { _ = db; }
        private static void SeedCertificateRules(ApplicationDbContext db)
        {
            if (db.CertificateRules.Any()) return;
            var rules = new List<CertificateRule>
            {
                new CertificateRule { Type = "Barangay Clearance (Employment / Personal / Business)", Fee = 50, RequiresResidency = true, Status = "Active" },
                new CertificateRule { Type = "Certificate of Indigency (Medical / Educational / Legal Aid)", Fee = 0, RequiresResidency = true, Status = "Active" },
                new CertificateRule { Type = "Certificate of Residency", Fee = 30, RequiresResidency = true, Status = "Active" },
                new CertificateRule { Type = "Certificate of Good Moral Character", Fee = 50, RequiresResidency = true, Status = "Active" },
                new CertificateRule { Type = "Business Permit Clearance", Fee = 150, RequiresResidency = false, Status = "Active" },
                new CertificateRule { Type = "First-Time Jobseeker Certificate (RA 11261)", Fee = 0, RequiresResidency = false, Status = "Active" },
                new CertificateRule { Type = "Solo Parent Certification", Fee = 30, RequiresResidency = true, Status = "Active" },
                new CertificateRule { Type = "Certificate of Low Income / Minimum Wage Earner", Fee = 30, RequiresResidency = true, Status = "Active" },
                new CertificateRule { Type = "Senior Citizen / PWD Barangay Certification", Fee = 0, RequiresResidency = true, Status = "Active" },
                new CertificateRule { Type = "Barangay ID Application", Fee = 100, RequiresResidency = true, Status = "Active" },
                new CertificateRule { Type = "BARCO Report (Barangay Road Clearing Operations)", Fee = 0, RequiresResidency = false, Status = "Active" },
                new CertificateRule { Type = "Certificate of No Objection (Events / Construction)", Fee = 100, RequiresResidency = false, Status = "Active" },
                new CertificateRule { Type = "DSWD / Hospital Endorsement Letter", Fee = 0, RequiresResidency = false, Status = "Active" },
                new CertificateRule { Type = "Certified True Copy of Blotter Entry", Fee = 50, RequiresResidency = false, Status = "Active" }
            };
            db.CertificateRules.AddRange(rules);
            db.SaveChanges();
        }
    }
}
