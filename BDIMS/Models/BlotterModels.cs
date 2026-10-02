using System.Collections.Generic;

namespace BDIMS.Models
{
    public class BlotterCaseModel
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Resident { get; set; } = "";
        public string DateFiled { get; set; } = "";
        public string Status { get; set; } = "";

        public string IncidentType { get; set; } = "";
        public string IncidentDate { get; set; } = "";
        public string Location { get; set; } = "";
        public string Priority { get; set; } = "Medium";
        public string ComplainantContact { get; set; } = "";
        public string ComplainantAddress { get; set; } = "";
        public string RespondentName { get; set; } = "";
        public string RespondentAddress { get; set; } = "";
        public string VictimName { get; set; } = "";
        public string Narrative { get; set; } = "";
        public string Officer { get; set; } = "";
        public List<string> Notes { get; set; } = new List<string>();

        public string ComplainantName => Resident;
        public string ExpectedVictim => string.IsNullOrWhiteSpace(VictimName) ? Resident : VictimName;
    }

    public class BlotterViewModel
    {
        public List<BlotterCaseModel> Cases { get; set; } =
            new List<BlotterCaseModel>();

        public int InProgressCount { get; set; }
        public int ResolvedCount { get; set; }

        public string SearchQuery { get; set; } = "";
        public string StatusFilter { get; set; } = "";
        public string CategoryFilter { get; set; } = "";
        public string PriorityFilter { get; set; } = "";
        public string DateFrom { get; set; } = "";
        public string DateTo { get; set; } = "";

        public List<string> Categories { get; set; } = new List<string>();
        public List<string> Statuses { get; set; } = new List<string>();
        public List<string> Priorities { get; set; } = new List<string>();

        // Statuses belonging to the summary cards, filled from the controller's single
        // bucket definition. The view serialises these so the clickable cards filter
        // rows by exactly the same rule the counters use, instead of keeping a second
        // hand-written copy that could drift.
        public List<string> ResolvedStatuses { get; set; } = new List<string>();
    }
}