using System;
using System.Collections.Generic;
using System.Linq;

namespace BDIMS.Models
{
    public class ResidentModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Since { get; set; } = string.Empty;
        public string Initials { get; set; } = string.Empty;
        public string AgeSex { get; set; } = string.Empty;
        public string Purok { get; set; } = string.Empty;
        public string Contact { get; set; } = string.Empty;
        public bool IsVoter { get; set; }
        public string Status { get; set; } = "Active";
        public string AvatarClass { get; set; } = "bg-amber-avatar";
        public string Sex { get; set; } = string.Empty;
    }

    public class ResidentsViewModel
    {
        public List<ResidentModel> Residents { get; set; } = new();

        // All counters below are computed from the real database rows loaded into
        // Residents. Comparisons are trimmed + case-insensitive so a stored value
        // like "active", "ACTIVE" or " Active " still counts correctly.
        public int TotalResidents => Residents.Count;

        public int ActiveResidents => Residents.Count(r => IsActive(r.Status));

        public int RegisteredVoters => Residents.Count(r => r.IsVoter);

        public int SeniorCitizens => Residents.Count(r => ResidentAge(r) >= 60);

        public int MaleCount => Residents.Count(r => IsSex(r.Sex, "Male"));

        public int FemaleCount => Residents.Count(r => IsSex(r.Sex, "Female"));

        private static bool IsActive(string? status) =>
            string.Equals((status ?? string.Empty).Trim(), "Active", StringComparison.OrdinalIgnoreCase);

        private static bool IsSex(string? sex, string expected) =>
            string.Equals((sex ?? string.Empty).Trim(), expected, StringComparison.OrdinalIgnoreCase);

        private static int ResidentAge(ResidentModel r)
        {
            var token = ((r.AgeSex ?? string.Empty).Split('/').FirstOrDefault() ?? string.Empty).Trim();
            return int.TryParse(token, out int age) ? age : -1;
        }
    }
}