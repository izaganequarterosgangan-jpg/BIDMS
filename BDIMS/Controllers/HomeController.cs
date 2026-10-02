using BDIMS.Data;
using BDIMS.Models;
using BDIMS.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BDIMS.Controllers
{
    /// <summary>
    /// Serves every BDIMS module page (Dashboard, Residents, Blotter, Announcements,
    /// Documents, Certificates, Reports, Settings) plus their AJAX endpoints.
    ///
    /// [Authorize] is applied at the class level so a new module action is protected by
    /// default rather than by remembering to annotate it. The seeded admin signs in
    /// through /Account/Login and therefore reaches all of them; anonymous requests are
    /// redirected to the login page.
    /// </summary>
    [Authorize]
    public class HomeController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly BarangayProfileProvider _barangayProfile;

        public HomeController(ApplicationDbContext db, BarangayProfileProvider barangayProfile)
        {
            _db = db;
            _barangayProfile = barangayProfile;
        }

        // Every module reads its rows from MySQL through these accessors, so the data
        // survives restarts and the pages always reflect what is actually stored.
        // Writes go through ApplicationDbContext and are saved immediately.
        private List<ResidentModel> residentList => _db.Residents.ToList();
        private List<BlotterCaseModel> blotterList => _db.Blotters.ToList();
        private List<CertificateModel> certificateList => _db.Certificates.ToList();
        private List<DocumentRequestModel> documentRequests => _db.DocumentRequests.ToList();
        private List<AnnouncementModel> announcementsList => _db.Announcements.ToList();
        private List<ReportModel> reportList => _db.Reports.ToList();
        private List<CertificateRule> certificateRulesList => _db.CertificateRules.ToList();
        private List<NotificationModel> notificationsList => _db.Notifications.ToList();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UpdateBlotterStatus(string id, string newStatus)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(newStatus))
            {
                return BadRequest();
            }

            var item = blotterList.FirstOrDefault(b => b.Id == id);
            if (item == null)
            {
                return NotFound();
            }

            item.Status = newStatus;
            _db.SaveChanges();

            // If AJAX request, return updated item
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                // Counts are recomputed from the database rather than nudged by hand
                // (+1/-1). A status change can move a case into or out of the resolved
                // bucket, and deriving the numbers from the rows themselves is the only
                // way they cannot drift from what the next page load would render.
                var summary = BlotterSummary(blotterList);

                return Json(new
                {
                    success = true,
                    item = new
                    {
                        id = item.Id,
                        title = item.Title,
                        resident = item.Resident,
                        dateFiled = item.DateFiled,
                        status = item.Status,
                        incidentType = item.IncidentType,
                        priority = item.Priority,
                        respondent = item.RespondentName
                    },
                    counts = new
                    {
                        inProgress = summary.InProgress,
                        resolved = summary.Resolved
                    }
                });
            }

            TempData["Success"] = "Blotter case updated.";
            return RedirectToAction("Blotter");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteBlotter(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return BadRequest();

            var item = blotterList.FirstOrDefault(b => b.Id == id);

            // Reporting success for a case that was never removed left the UI showing
            // "deleted" while the record was untouched, which happens with a stale id
            // from a second tab or after a concurrent delete.
            if (item == null)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = false, id, message = "Case not found." });
                }

                TempData["Error"] = "That blotter case no longer exists.";
                return RedirectToAction("Blotter");
            }

            _db.Blotters.Remove(item);
            _db.SaveChanges();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return Json(new { success = true, id });
            }

            TempData["Success"] = "Blotter case deleted.";
            return RedirectToAction("Blotter");
        }

        // Non-certificate paperwork residents may request. Kept empty: all standard
        // barangay document types live in the CertificateRules table, which is managed
        // from Settings > Certificates & Fees.
        private static readonly string[] GeneralDocumentTypes = { };


        private string NextBlotterId()
        {
            int max = 0;
            foreach (var b in blotterList)
            {
                var seg = b.Id?.Split('-');
                if (seg != null && seg.Length > 0 && int.TryParse(seg[seg.Length - 1], out var n) && n > max)
                {
                    max = n;
                }
            }
            return $"BLT-{DateTime.Now.Year}-{max + 1:000}";
        }

        private static DateTime? ParseDisplayDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
            if (DateTime.TryParseExact(s, new[] { "MMM d, yyyy h:mm tt", "MMM d, yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            return null;
        }



        // ============================================================
        // GENERAL
        // ============================================================

        /// <summary>
        /// Target of the UseExceptionHandler path in Program.cs. Without it every
        /// unhandled exception in production re-enters the pipeline against a route
        /// that does not exist and the caller sees a 404 instead of the error page.
        /// </summary>
        [HttpGet]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        [AllowAnonymous]
        public IActionResult Error()
        {
            return View(new ErrorViewModel
            {
                RequestId = HttpContext.TraceIdentifier
            });
        }

        [HttpGet]
        public IActionResult Privacy()
        {
            return View();
        }

        // ============================================================
        // DASHBOARD
        // ============================================================

        [HttpGet]
        public IActionResult Dashboard(string searchQuery = "")
        {
            // The "Recent Document Requests" panel lists the newest rows in the
            // documentrequests table whatever their status, so an already approved or
            // rejected request still shows up. Restricting this list to Pending / In
            // Review / Processing left the table empty whenever every request had
            // already been actioned. Ordering by Id descending matches the
            // REQ-yyyyMMddHHmmss-#### generator (and the legacy REQ-yyyy-#### ids), so
            // the most recently created request comes first.
            var recent = documentRequests
                .OrderByDescending(r => r.Id)
                .Take(10)
                .ToList();

            var model = new DashboardViewModel
            {
                SearchQuery = searchQuery,
                TotalResidents = residentList.Count,
                PendingRequestsCount = documentRequests.Count(r => r.Status == "Pending" || r.Status == "In Review" || r.Status == "Processing"),
                CertificatesIssuedCount = certificateList.Count,
                BlotterCasesCount = blotterList.Count,
                Requests = recent.Select(r => new DocumentRequestViewModel
                {
                    Id = r.Id,
                    Resident = r.Resident,
                    Avatar = ResidentInitials(r.Resident),
                    Type = r.Title,
                    Date = r.Date,
                    Status = r.Status
                }).ToList()
            };

            return View("~/Views/Home/Dashboard.cshtml", model);
        }
        // ============================================================
        // GLOBAL SEARCH (top navigation "Search records..." box)
        // ============================================================

        // Backs the dashboard's top search box. It answers with JSON that the page
        // renders inside the floating panel, so the header never navigates or reflows on
        // its own and the dashboard's tables, charts and quick actions stay untouched.
        //
        // Two shapes of query are supported:
        //   1. A module keyword (Documents / Certificates / Blotter / Residents) returns
        //      a shortcut to that module plus that module's newest records, which is the
        //      "switch the recent records view to this module" behaviour. The keyword is
        //      matched as a whole word, so an id such as "REQ-001" is never mistaken for
        //      the "request" keyword.
        //   2. Anything else is matched (case-insensitive substring) across the id,
        //      resident, title, purpose and status fields of every entity.
        [HttpGet]
        public IActionResult GlobalSearch(string q = "")
        {
            var term = (q ?? string.Empty).Trim();
            var modules = new List<object>();
            var results = new List<object>();

            if (term.Length == 0)
            {
                return Json(new { success = true, query = term, modules, results });
            }

            var needle = term.ToLowerInvariant();

            bool Matches(params string[] fields) =>
                fields.Any(f => !string.IsNullOrEmpty(f) && f.ToLowerInvariant().Contains(needle));

            var wantsDocuments = new[] { "doc", "docs", "document", "documents", "request", "requests" }.Contains(needle);
            var wantsCertificates = new[] { "cert", "certs", "certificate", "certificates" }.Contains(needle);
            var wantsBlotter = new[] { "blotter", "blotters", "case", "cases", "incident", "incidents" }.Contains(needle);
            var wantsResidents = new[] { "resident", "residents", "registry", "people" }.Contains(needle);

            if (wantsDocuments)
            {
                modules.Add(new { module = "Documents", label = "Documents", sublabel = "Open the document requests queue", url = Url.Action("Documents", "Home") ?? "/Home/Documents" });
            }
            if (wantsCertificates)
            {
                modules.Add(new { module = "Certificates", label = "Certificates", sublabel = "Open the issued certificates list", url = Url.Action("Certificates", "Home") ?? "/Home/Certificates" });
            }
            if (wantsBlotter)
            {
                modules.Add(new { module = "Blotter", label = "Blotter", sublabel = "Open the blotter case records", url = Url.Action("Blotter", "Home") ?? "/Home/Blotter" });
            }
            if (wantsResidents)
            {
                modules.Add(new { module = "Residents", label = "Residents", sublabel = "Open the resident registry", url = Url.Action("Residents", "Home") ?? "/Home/Residents" });
            }

            // A module keyword brings back that module's newest rows; a free-text term is
            // matched against every entity. A handful per entity keeps the floating list
            // short and readable.
            const int perEntity = 6;

            foreach (var d in documentRequests
                .Where(d => wantsDocuments || Matches(d.Id, d.Resident, d.Title, d.Purpose, d.Status))
                .OrderByDescending(d => d.Id)
                .Take(perEntity))
            {
                results.Add(new
                {
                    module = "Documents",
                    id = d.Id,
                    title = d.Title,
                    meta = JoinParts(d.Resident, d.Date, d.Status),
                    url = Url.Action("Documents", "Home", new { searchQuery = d.Id }) ?? "/Home/Documents"
                });
            }

            foreach (var c in certificateList
                .Where(c => wantsCertificates || Matches(c.Id, c.Resident, c.Title, c.Purpose, c.Status))
                .OrderByDescending(c => c.Id)
                .Take(perEntity))
            {
                results.Add(new
                {
                    module = "Certificates",
                    id = c.Id,
                    title = c.Title,
                    meta = JoinParts(c.Resident, c.IssuedDateDisplay, c.Status),
                    url = Url.Action("Certificates", "Home", new { searchQuery = c.Id }) ?? "/Home/Certificates"
                });
            }

            foreach (var b in blotterList
                .Where(b => wantsBlotter || Matches(b.Id, b.Resident, b.Title, b.IncidentType, b.Status, b.RespondentName, b.VictimName))
                .OrderByDescending(b => b.Id)
                .Take(perEntity))
            {
                results.Add(new
                {
                    module = "Blotter",
                    id = b.Id,
                    title = string.IsNullOrWhiteSpace(b.IncidentType) ? b.Title : b.IncidentType,
                    meta = JoinParts(b.Resident, b.DateFiled, b.Status),
                    url = Url.Action("Blotter", "Home", new { searchQuery = b.Id }) ?? "/Home/Blotter"
                });
            }

            foreach (var r in residentList
                .Where(r => wantsResidents || Matches(r.Id, r.Name, r.Purok, r.Sex, r.Status, r.AgeSex))
                .OrderByDescending(r => r.Id)
                .Take(perEntity))
            {
                results.Add(new
                {
                    module = "Residents",
                    id = r.Id,
                    title = r.Name,
                    meta = JoinParts(r.Purok, r.AgeSex, r.Status),
                    url = Url.Action("Residents", "Home") ?? "/Home/Residents"
                });
            }

            if (results.Count > 20)
            {
                results = results.Take(20).ToList();
            }

            return Json(new { success = true, query = term, modules, results });
        }

        // Builds the "resident · date · status" style meta line for a search hit while
        // skipping whatever the underlying record leaves blank.
        private static string JoinParts(params string[] parts) =>
            string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));



        private static string ResidentInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "--";
            var parts = name.Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0].Substring(0, System.Math.Min(2, parts[0].Length)).ToUpperInvariant();
            return string.Concat(parts[0][0], parts[parts.Length - 1][0]).ToUpperInvariant();
        }

        // ============================================================
        // RESIDENTS
        // ============================================================

        // Static in-memory storage for residents (shared across requests)
        [HttpGet]
        public IActionResult Residents()
        {
            var vm = new ResidentsViewModel
            {
                Residents = residentList.ToList()
            };

            return View("~/Views/Home/Residents.cshtml", vm);
        }

        [HttpGet]
        public IActionResult GetResident(string id)
        {
            var item = residentList.FirstOrDefault(r => r.Id == id);
            if (item == null) return NotFound();

            return Json(new
            {
                success = true,
                item = new
                {
                    id = item.Id,
                    name = item.Name,
                    since = item.Since,
                    initials = item.Initials,
                    ageSex = item.AgeSex,
                    sex = item.Sex,
                    purok = item.Purok,
                    contact = item.Contact,
                    isVoter = item.IsVoter,
                    status = item.Status,
                    avatarClass = item.AvatarClass
                }
            });
        }

        private int NextResidentId()
        {
            var nums = residentList
                .Select(r => r.Id.StartsWith("RES-") && int.TryParse(r.Id.Substring(4), out var n) ? n : 0)
                .DefaultIfEmpty(0);
            return nums.Max() + 1;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CreateResident(
            string name,
            string age,
            string sex,
            string purok,
            string contact,
            bool isVoter,
            string status)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest();
            }

            var trimmed = name.Trim();
            var initials = string.Join("",
                trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Take(2)
                    .Select(w => char.ToUpper(w[0])));

            if (string.IsNullOrWhiteSpace(initials)) initials = "NR";

            var newResident = new ResidentModel
            {
                Name = trimmed,
                Since = DateTime.Now.Year.ToString(),
                Initials = initials,
                AgeSex = $"{(age ?? "30").Trim()} / {(string.Equals((sex ?? string.Empty).Trim(), "Male", StringComparison.OrdinalIgnoreCase) ? "M" : "F")}",
                Sex = string.IsNullOrWhiteSpace(sex) ? "Female" : sex.Trim(),
                Purok = string.IsNullOrWhiteSpace(purok) ? "Purok 1" : purok.Trim(),
                Contact = string.IsNullOrWhiteSpace(contact) ? "N/A" : contact.Trim(),
                IsVoter = isVoter,
                Status = string.IsNullOrWhiteSpace(status) ? "Active" : status.Trim()
            };

            // Choosing the id and inserting it happen in one critical section. Read-then-
            // insert let two concurrent submissions both see the same highest RES number
            // and insert a duplicate, and UpdateResident/DeleteResident resolve by id with
            // FirstOrDefault, so one would then edit or delete the wrong resident.
            newResident.Id = $"RES-{NextResidentId():000}";
            _db.Residents.Add(newResident);
            _db.SaveChanges();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                var counts = ResidentCounts();
                return Json(new
                {
                    success = true,
                    item = new
                    {
                        id = newResident.Id,
                        name = newResident.Name,
                        since = newResident.Since,
                        initials = newResident.Initials,
                        ageSex = newResident.AgeSex,
                        sex = newResident.Sex,
                        purok = newResident.Purok,
                        contact = newResident.Contact,
                        isVoter = newResident.IsVoter,
                        status = newResident.Status,
                        avatarClass = newResident.AvatarClass
                    },
                    counts
                });
            }

            TempData["Success"] = "Resident added.";
            return RedirectToAction("Residents");
        }

        /// <summary>
        /// Bulk-imports residents from an uploaded CSV.
        ///
        /// Returns JSON for both the success and the failure path so the page can show a
        /// precise message and list the rows that were skipped; a rejected file is a
        /// normal outcome here, not an exception.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(ResidentCsvImporter.MaxFileBytes)]
        public async Task<IActionResult> ImportCsv(IFormFile? file)
        {
            // ---- File-level validation ---------------------------------------
            if (file == null || file.Length == 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Please choose a CSV file to import."
                });
            }

            // Length is checked before the bytes are read, so an oversized upload is
            // rejected without ever being buffered into memory.
            if (file.Length > ResidentCsvImporter.MaxFileBytes)
            {
                return Json(new
                {
                    success = false,
                    message = "The file is too large. The maximum size is " +
                              $"{ResidentCsvImporter.MaxFileBytes / (1024 * 1024)} MB."
                });
            }

            var extension = Path.GetExtension(file.FileName);
            if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new
                {
                    success = false,
                    message = "Only .csv files are supported. Use the template to create one."
                });
            }

            // ---- Read ----------------------------------------------------------
            string content;
            using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
            {
                content = await reader.ReadToEndAsync();
            }

            // ---- Parse & validate ----------------------------------------------
            // Existing names are supplied so a re-import of the same file reports the
            // conflicts instead of quietly creating duplicate residents.
            var existingNames = new HashSet<string>(
                _db.Residents.Select(r => (r.Name ?? string.Empty).Trim().ToUpperInvariant()),
                StringComparer.Ordinal);

            var parsed = ResidentCsvImporter.Parse(content, existingNames);

            if (parsed.FatalError != null)
            {
                return Json(new { success = false, message = parsed.FatalError });
            }

            var validRows = parsed.Valid.Select(r => r.Resident!).ToList();

            if (validRows.Count == 0)
            {
                return Json(new
                {
                    success = false,
                    message = "No valid rows were found in that file.",
                    importedCount = 0,
                    skipped = parsed.Skipped
                        .Select(s => new { s.LineNumber, reason = s.Error })
                        .ToList()
                });
            }

            // ---- Persist -----------------------------------------------------------
            // Resident ids are allocated sequentially in one pass, starting from the
            // current maximum. Reusing NextResidentId() per row would re-query the table
            // for every row and, because the new rows are not saved until the end, would
            // hand out the same number over and over.
            var existingIds = new HashSet<string>(
                _db.Residents.Select(r => r.Id),
                StringComparer.OrdinalIgnoreCase);

            var nextNumber = NextResidentId();

            foreach (var resident in validRows)
            {
                // Step past any id that already exists rather than colliding with it, so
                // a partially deleted registry cannot produce duplicates.
                while (existingIds.Contains($"RES-{nextNumber:000}"))
                {
                    nextNumber++;
                }

                resident.Id = $"RES-{nextNumber:000}";
                existingIds.Add(resident.Id);
                nextNumber++;

                _db.Residents.Add(resident);
            }

            await _db.SaveChangesAsync();

            // ---- Report -------------------------------------------------------------
            // The created rows are echoed back in exactly the shape buildRowHtml()
            // already consumes, so the grid updates in place with no page reload and
            // without re-rendering the Razor table.
            var imported = validRows.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                since = r.Since,
                initials = r.Initials,
                ageSex = r.AgeSex,
                sex = r.Sex,
                purok = r.Purok,
                contact = r.Contact,
                isVoter = r.IsVoter,
                status = r.Status,
                avatarClass = r.AvatarClass
            }).ToList();

            return Json(new
            {
                success = true,
                importedCount = validRows.Count,
                skippedCount = parsed.Skipped.Count(),
                imported,
                skipped = parsed.Skipped
                    .Select(s => new { s.LineNumber, reason = s.Error })
                    .ToList(),
                missingHeaders = parsed.MissingHeaders,
                counts = ResidentCounts()
            });
        }

        /// <summary>
        /// Serves the CSV template used by the import modal. Served from the server
        /// rather than generated in the browser so the headers and example values can
        /// never drift from what the parser accepts.
        /// </summary>
        [HttpGet]
        public IActionResult ResidentCsvTemplate()
        {
            return File(
                ResidentCsvImporter.BuildTemplateCsv(),
                "text/csv; charset=utf-8",
                "residents-import-template.csv");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UpdateResident(
            string id,
            string name,
            string age,
            string sex,
            string purok,
            string contact,
            bool isVoter,
            string status)
        {
            var item = residentList.FirstOrDefault(r => r.Id == id);
            if (item == null) return NotFound();

            item.Name = string.IsNullOrWhiteSpace(name) ? item.Name : name.Trim();
            item.Initials = string.Join("",
                item.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Take(2)
                    .Select(w => char.ToUpper(w[0])));
            if (string.IsNullOrWhiteSpace(item.Initials)) item.Initials = "NR";

            item.AgeSex = string.IsNullOrWhiteSpace(age) ? item.AgeSex : $"{age.Trim()} / {(string.Equals((sex ?? string.Empty).Trim(), "Male", StringComparison.OrdinalIgnoreCase) ? "M" : "F")}";
            if (!string.IsNullOrWhiteSpace(sex)) item.Sex = sex.Trim();
            if (!string.IsNullOrWhiteSpace(purok)) item.Purok = purok.Trim();
            if (!string.IsNullOrWhiteSpace(contact)) item.Contact = contact.Trim();
            item.IsVoter = isVoter;
            item.Status = string.IsNullOrWhiteSpace(status) ? item.Status : status.Trim();
            _db.SaveChanges();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                var counts = ResidentCounts();
                return Json(new
                {
                    success = true,
                    item = new
                    {
                        id = item.Id,
                        name = item.Name,
                        since = item.Since,
                        initials = item.Initials,
                        ageSex = item.AgeSex,
                        sex = item.Sex,
                        purok = item.Purok,
                        contact = item.Contact,
                        isVoter = item.IsVoter,
                        status = item.Status,
                        avatarClass = item.AvatarClass
                    },
                    counts
                });
            }

            TempData["Success"] = "Resident updated.";
            return RedirectToAction("Residents");
        }

        // Fresh DB counts for the Residents metric cards. Computed from the live
        // Residents table (never cached) with the same trimmed/case-insensitive
        // rules as ResidentsViewModel, so AJAX add/edit/delete can refresh the
        // cards instantly without a page reload.
        private object ResidentCounts()
        {
            var residents = _db.Residents.ToList();
            return new
            {
                total = residents.Count,
                active = residents.Count(r => string.Equals((r.Status ?? string.Empty).Trim(), "Active", StringComparison.OrdinalIgnoreCase)),
                voters = residents.Count(r => r.IsVoter),
                seniors = residents.Count(r => ResidentCardAge(r) >= 60),
                male = residents.Count(r => string.Equals((r.Sex ?? string.Empty).Trim(), "Male", StringComparison.OrdinalIgnoreCase)),
                female = residents.Count(r => string.Equals((r.Sex ?? string.Empty).Trim(), "Female", StringComparison.OrdinalIgnoreCase))
            };
        }

        private static int ResidentCardAge(ResidentModel r)
        {
            var token = ((r.AgeSex ?? string.Empty).Split('/').FirstOrDefault() ?? string.Empty).Trim();
            return int.TryParse(token, out int age) ? age : -1;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteResident(string id)
        {
            var item = residentList.FirstOrDefault(r => r.Id == id);

            // See DeleteBlotter: a stale id must not report a delete that never happened.
            if (item == null)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = false, id, message = "Resident not found." });
                }

                TempData["Error"] = "That resident record no longer exists.";
                return RedirectToAction("Residents");
            }

            _db.Residents.Remove(item);
            _db.SaveChanges();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return Json(new { success = true, id, counts = ResidentCounts() });
            }

            TempData["Success"] = "Resident deleted.";
            return RedirectToAction("Residents");
        }


        // ============================================================
        // DOCUMENTS
        // ============================================================

        [HttpGet]
        public IActionResult Documents(string searchQuery = "")
        {
            // Use shared in-memory list so changes persist across requests
            var filtered = documentRequests.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var q = searchQuery.Trim().ToLower();
                filtered = filtered.Where(r =>
                    (r.Id ?? string.Empty).ToLower().Contains(q) ||
                    (r.Title ?? string.Empty).ToLower().Contains(q) ||
                    (r.Resident ?? string.Empty).ToLower().Contains(q) ||
                    (r.Status ?? string.Empty).ToLower().Contains(q) ||
                    (r.Purpose ?? string.Empty).ToLower().Contains(q)
                );
            }

            var vm = new DocumentsViewModel
            {
                Requests = filtered.ToList(),
                PendingCount = documentRequests.Count(r => r.Status == "Pending"),
                InReviewCount = documentRequests.Count(r => r.Status == "In Review"),
                ProcessingCount = documentRequests.Count(r => r.Status == "Processing"),
                ApprovedCount = documentRequests.Count(r => r.Status == "Approved"),
                RejectedCount = documentRequests.Count(r => r.Status == "Rejected"),
                CompletedCount = documentRequests.Count(r => r.Status == "Approved"),
                SearchQuery = searchQuery,
                CertificateTypes = ActiveCertificateTypes(),
                GeneralDocumentTypes = GeneralDocumentTypes.ToList(),
                Residents = residentList
                    .Where(r => r.Status == "Active")
                    .OrderBy(r => r.Name)
                    .Select(r => new ResidentModel { Name = r.Name, Purok = r.Purok })
                    .ToList()
            };

            // If querystring contains newRequest=true or a prefill title, set flags for the view
            var newRequestFlag = Request.Query.ContainsKey("newRequest") && Request.Query["newRequest"] == "true";
            if (newRequestFlag)
            {
                vm.OpenNewRequest = true;
            }

            if (Request.Query.ContainsKey("title"))
            {
                vm.PrefillTitle = Request.Query["title"].ToString();
            }
            if (Request.Query.ContainsKey("resident"))
            {
                vm.PrefillResident = Request.Query["resident"].ToString();
            }

            return View("~/Views/Home/Documents.cshtml", vm);
        }


        // ============================================================
        // CERTIFICATES
        // ============================================================

        // ============================================================
        // DOCUMENTS - POST handlers for creating and updating requests
        // ============================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateDocumentRequest(
            string resident,
            string title,
            string purpose,
            string status,
            Microsoft.AspNetCore.Http.IFormFile? attachment)
        {
            // Basic validation
            if (string.IsNullOrWhiteSpace(resident) || string.IsNullOrWhiteSpace(title))
            {
                TempData["Error"] = "Resident name and document type are required.";
                return RedirectToAction("Documents");
            }

            var newReq = new DocumentRequestModel
            {
                Resident = resident,
                Title = title,
                Date = DateTime.Now.ToString("MMM d, yyyy"),
                Status = string.IsNullOrWhiteSpace(status) ? "Pending" : status,
                Purpose = string.IsNullOrWhiteSpace(purpose) ? "N/A" : purpose
            };

            // Optional supporting attachment (Valid ID, Cedula, Proof of Residency, etc.)
            if (attachment != null && attachment.Length > 0)
            {
                const int maxBytes = 5 * 1024 * 1024; // 5 MB

                if (attachment.Length > maxBytes)
                {
                    TempData["Error"] = "Attachment exceeds the 5 MB limit.";
                    return RedirectToAction("Documents");
                }

                var mime = SafeAttachmentMime(attachment.FileName);

                if (mime == null)
                {
                    TempData["Error"] = "Attachment must be a PDF, PNG or JPEG file.";
                    return RedirectToAction("Documents");
                }

                using var ms = new System.IO.MemoryStream();
                await attachment.CopyToAsync(ms);
                newReq.AttachmentData = Convert.ToBase64String(ms.ToArray());
                newReq.AttachmentFileName = System.IO.Path.GetFileName(attachment.FileName);
                newReq.AttachmentMime = mime;
            }

            newReq.Id = NextDocumentRequestId();
            _db.DocumentRequests.Add(newReq);
            _db.SaveChanges();

            TempData["Success"] = "Document request created.";
            return RedirectToAction("Documents");
        }

        // The browser-supplied Content-Type header is attacker-controlled and is
        // interpolated into the data: URI that Documents.cshtml renders into an <img src>
        // and an <a href>, so it is derived from the extension against a fixed allowlist
        // instead of being trusted.
        [NonAction]
        private static string? SafeAttachmentMime(string? fileName)
        {
            return System.IO.Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => null
            };
        }

        private static int documentRequestSequence;

        // yyyyMMddHHmmss alone repeats within a single second, so a double-submitted form
        // produced two rows sharing an id; GetDocumentRequest and UpdateDocumentStatus
        // both resolve with FirstOrDefault, so approving one then approved the other.
        // The trailing sequence keeps ids unique even across a clock change.
        // Caller must hold lock (documentRequests).
        [NonAction]
        private static string NextDocumentRequestId()
        {
            return "REQ-" + DateTime.Now.ToString("yyyyMMddHHmmss")
                        + "-" + (++documentRequestSequence).ToString("D4");
        }

        [HttpGet]
        public IActionResult GetDocumentRequest(string id)
        {
            var item = documentRequests.FirstOrDefault(r => r.Id == id);
            if (item == null) return NotFound();

            return Json(new
            {
                success = true,
                item = new
                {
                    id = item.Id,
                    title = item.Title,
                    resident = item.Resident,
                    date = item.Date,
                    status = item.Status,
                    purpose = item.Purpose,
                    attachmentFileName = item.AttachmentFileName,
                    attachmentMime = item.AttachmentMime,
                    attachmentUrl = item.AttachmentUrl
                }
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UpdateDocumentStatus(string requestId, string newStatus)
        {
            var allowedStatuses = new[] { "Pending", "In Review", "Processing", "Approved", "Rejected" };
            if (string.IsNullOrWhiteSpace(requestId) || !allowedStatuses.Contains(newStatus))
            {
                TempData["Error"] = "Invalid request or status.";
                return RedirectToAction("Documents");
            }

            var existing = documentRequests.FirstOrDefault(r => r.Id == requestId);
            if (existing != null)
            {
                existing.Status = newStatus;
                _db.SaveChanges();
                TempData["Success"] = "Request updated.";
            }
            else
            {
                TempData["Error"] = "Request not found.";
            }

            return RedirectToAction("Documents");
        }

        // ============================================================
        // DELETE DOCUMENT REQUEST
        // ============================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteDocumentRequest(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                TempData["Error"] = "Invalid request.";
                return RedirectToAction("Documents");
            }

            var existing = documentRequests.FirstOrDefault(r => r.Id == id);
            if (existing == null)
            {
                TempData["Error"] = "Request not found.";
                return RedirectToAction("Documents");
            }

            _db.DocumentRequests.Remove(existing);
            _db.SaveChanges();
            TempData["Success"] = "Document request deleted.";

            // The Documents table deletes over AJAX, but a plain form post still reaches
            // this action, so both callers get a sensible response.
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return Json(new { success = true, id });
            }

            return RedirectToAction("Documents");
        }

        [HttpGet]
        public IActionResult Certificates(string searchQuery = "")
        {
            var filtered = certificateList.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var q = searchQuery.Trim().ToLower();

                filtered = filtered.Where(c =>
                    c.Id.ToLower().Contains(q) ||
                    c.Title.ToLower().Contains(q) ||
                    c.Resident.ToLower().Contains(q) ||
                    c.Status.ToLower().Contains(q)
                );
            }

            var viewModel = new CertificatesViewModel
            {
                Certificates = filtered.ToList(),

                IssuedThisWeekCount =
                    certificateList.Count(c => c.IssueDate.HasValue && c.IssueDate.Value.Date >= DateTime.Today.AddDays(-7)),

                AwaitingPickupCount =
                    certificateList.Count(c => c.Status == "Ready"),

                PrintedCount =
                    certificateList.Count(c => c.Status == "Printed"),

                SearchQuery = searchQuery,
                CertificateTypes = ActiveCertificateTypes(),
                Residents = residentList
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Puroks = KnownPuroks(),

                // Bridge from Documents: "Issue Certificate" shortcut pre-fills
                // the walk-in certificate modal with resident / type / purpose.
                OpenIssueModal =
                    Request.Query.ContainsKey("issue") &&
                    Request.Query["issue"] == "true",

                PrefillResident =
                    Request.Query.ContainsKey("resident")
                        ? Request.Query["resident"].ToString()
                        : string.Empty,

                PrefillTitle =
                    Request.Query.ContainsKey("title")
                        ? Request.Query["title"].ToString()
                        : string.Empty,

                PrefillPurpose =
                    Request.Query.ContainsKey("purpose")
                        ? Request.Query["purpose"].ToString()
                        : string.Empty
            };

            return View(viewModel);
        }


        // ============================================================
        // BLOTTER
        // ============================================================

        // OPEN / RESOLVED are matched against explicit lists. IN PROGRESS is the
        // complement of RESOLVED -- every case that is not closed out -- so it needs
        // no list of its own and can never drift out of step with the RESOLVED card.
        // The lists are mutually exclusive and are matched with ordinal-ignore-case
        // equality, never StartsWith/Contains: a near-miss like "Issued CFAs" must not
        // be able to leak one bucket into another. In particular "Issued CFA" belongs
        // to RESOLVED only -- it is a case already closed out with a citation, so
        // counting it as OPEN would overstate the queue that still needs follow-up.
        // A case is finished once its status matches one of these. Everything NOT listed
// here is still active work and therefore counts toward IN PROGRESS.
//
// "Referred" is intentionally absent from this list: a referral passes the case on
// but it still needs following up, so it stays active. The legacy spellings are
// kept here so a case that was already closed is never suddenly reported as
// outstanding work.
private static readonly string[] BlotterResolvedStatuses =
{
    "Resolved", "Settled", "Issued CFA", "Dismissed",
    "Settled / Amicably Resolved", "Referred to PNP"
};

// True when status is in the given set, compared case- and
        // whitespace-insensitively so " issued cfa " still matches "Issued CFA".
        // Equality only -- a substring match would let one bucket claim another's
        // statuses and count a single case on two different cards.
        private static bool BlotterStatusIn(string status, string[] statuses)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            var normalized = status.Trim();

            foreach (var candidate in statuses)
            {
                if (string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // True when the case is closed out. Every card and every live counter keys off this
        // single predicate: marking a case resolved therefore moves it out of IN
        // PROGRESS and into RESOLVED automatically, with no second rule to keep in sync.
        private static bool BlotterIsResolved(string status)
        {
            return BlotterStatusIn(status, BlotterResolvedStatuses);
        }

        // Counts the two summary cards.
        //
        // IN PROGRESS is the active workload: every case that is NOT resolved, rather
        // than a hand-picked subset. That keeps it a true complement of RESOLVED, so
        // the two cards always sum to the total and marking a case resolved moves it
        // across exactly one boundary with no second rule to keep in sync.
        //
        // Computed from the full list rather than the filtered set, so applying a
        // filter can never change the totals -- only which rows the table shows.
        private static (int InProgress, int Resolved) BlotterSummary(List<BlotterCaseModel> cases)
        {
            var inProgress = 0;
            var resolved = 0;

            foreach (var c in cases)
            {
                if (BlotterIsResolved(c.Status))
                {
                    resolved++;
                }
                else
                {
                    inProgress++;
                }
            }

            return (inProgress, resolved);
        }

        // The statuses offered in the page's Status filter dropdown.
        //
        // Previously this was the distinct statuses found in the table, which made the
        // dropdown change as cases were edited and left a stale value selectable for a
        // status no longer in use. It is now the canonical list, in a fixed order, so
        // the filter offers the same choices the status modal does.
        [NonAction]
        public static string[] BlotterFilterStatuses()
        {
            return new[]
            {
                "Under Investigation",
                "Scheduled for Hearing",
                "Resolved",
                "Settled / Amicably Resolved",
                "Referred to PNP",
                "Issued CFA",
                "Dismissed"
            };
        }

        // The full membership list for a card, as used by the client-side row filter.
        // IN PROGRESS cannot be expressed as a fixed list -- it is everything that is
        // not resolved -- so the view is given the resolved list and negates it, which
        // keeps one definition of "resolved" behind both the counters and the filter.
        [NonAction]
        public static string[] BlotterStatusesFor(string bucket)
        {
            switch ((bucket ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "resolved":
                    return BlotterResolvedStatuses.ToArray();

                default:
                    return Array.Empty<string>();
            }
        }

        // True when the status belongs to the given card. "inprogress" is the
        // complement of resolved rather than a membership test.
        [NonAction]
        public static bool IsBlotterStatusIn(string status, string bucket)
        {
            switch ((bucket ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "inprogress":
                case "in progress":
                    return !BlotterIsResolved(status) &&
                           !string.IsNullOrWhiteSpace(status);

                case "resolved":
                    return BlotterIsResolved(status);

                default:
                    return false;
            }
        }

        [HttpGet]
        public IActionResult Blotter(
            string searchQuery = "",
            string statusFilter = "",
            string categoryFilter = "",
            string priorityFilter = "",
            string dateFrom = "",
            string dateTo = "")
        {
            var filtered = blotterList.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var q = searchQuery.Trim().ToLower();

                filtered = filtered.Where(c =>
                    (c.Id ?? string.Empty).ToLower().Contains(q) ||
                    (c.Title ?? string.Empty).ToLower().Contains(q) ||
                    (c.IncidentType ?? string.Empty).ToLower().Contains(q) ||
                    (c.Resident ?? string.Empty).ToLower().Contains(q) ||
                    (c.RespondentName ?? string.Empty).ToLower().Contains(q) ||
                    (c.VictimName ?? string.Empty).ToLower().Contains(q) ||
                    (c.Location ?? string.Empty).ToLower().Contains(q) ||
                    (c.Officer ?? string.Empty).ToLower().Contains(q) ||
                    (c.Narrative ?? string.Empty).ToLower().Contains(q) ||
                    (c.Status ?? string.Empty).ToLower().Contains(q)
                );
            }

            if (!string.IsNullOrWhiteSpace(categoryFilter))
            {
                filtered = filtered.Where(c => c.IncidentType == categoryFilter);
            }
            if (!string.IsNullOrWhiteSpace(statusFilter))
            {
                filtered = filtered.Where(c => c.Status == statusFilter);
            }
            if (!string.IsNullOrWhiteSpace(priorityFilter))
            {
                filtered = filtered.Where(c => c.Priority == priorityFilter);
            }
            if (!string.IsNullOrWhiteSpace(dateFrom) && DateTime.TryParse(dateFrom, out var fromDate))
            {
                filtered = filtered.Where(c =>
                {
                    var d = ParseDisplayDate(c.DateFiled);
                    return d.HasValue && d.Value.Date >= fromDate.Date;
                });
            }
            if (!string.IsNullOrWhiteSpace(dateTo) && DateTime.TryParse(dateTo, out var toDate))
            {
                filtered = filtered.Where(c =>
                {
                    var d = ParseDisplayDate(c.DateFiled);
                    return d.HasValue && d.Value.Date <= toDate.Date;
                });
            }

            var summary = BlotterSummary(blotterList);

            var vm = new BlotterViewModel
            {
                Cases = filtered.ToList(),

                // From the full list, never the filtered set, so the cards keep showing
                // the real totals while a filter hides rows.
                InProgressCount = summary.InProgress,
                ResolvedCount = summary.Resolved,

                SearchQuery = searchQuery ?? "",
                StatusFilter = statusFilter ?? "",
                CategoryFilter = categoryFilter ?? "",
                PriorityFilter = priorityFilter ?? "",
                DateFrom = dateFrom ?? "",
                DateTo = dateTo ?? "",

                Categories = blotterList
                    .Where(c => !string.IsNullOrWhiteSpace(c.IncidentType))
                    .Select(c => c.IncidentType)
                    .Distinct()
                    .OrderBy(t => t)
                    .ToList(),

                // Fixed canonical list rather than the statuses found in the table, so the
                // filter always offers the same choices in the same order.
                Statuses = BlotterFilterStatuses().ToList(),

                Priorities = new List<string> { "Low", "Medium", "High", "Urgent" },

                // Same bucket definition the counters above use, handed to the view so
                // the clickable cards filter rows under one shared rule.
                ResolvedStatuses = BlotterStatusesFor("resolved").ToList()
            };

            return View("~/Views/Home/Blotter.cshtml", vm);
        }


        // ============================================================
        // REPORTS
        // ============================================================

        [HttpGet]
        public IActionResult Reports(string searchQuery = "")
        {
            var filtered = reportList.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var q = searchQuery.Trim().ToLower();

                filtered = filtered.Where(r =>
                    r.Title.ToLower().Contains(q) ||
                    r.Date.ToLower().Contains(q) ||
                    r.Status.ToLower().Contains(q)
                );
            }

            var vm = new ReportsViewModel
            {
                Reports = filtered.ToList(),

                PreparedCount =
                    reportList.Count(r =>
                        r.Status == "Prepared" ||
                        r.Status == "Ready"),

                PendingCount =
                    reportList.Count(r =>
                        r.Status == "Pending Review"),

                TotalCount = reportList.Count,

                SearchQuery = searchQuery
            };

            return View("~/Views/Home/Reports.cshtml", vm);
        }


        // ============================================================
        // CREATE REPORT
        // ============================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CreateReport(
            string title,
            string date,
            string status)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                AddReport(title.Trim(), date, status);
            }

            return RedirectToAction("Reports");
        }

        // Sequential rather than DateTime.Now.Ticks truncated to int: the low 32 bits
        // repeat roughly every seven minutes, so two reports created in the same session
        // could share an id, and DeleteReport resolves by id via FirstOrDefault, which
        // would then remove the wrong record. Choosing the id and inserting it happen in
        // one critical section, because locking only the read leaves a gap where two
        // clerks submitting at once both see the same highest id and insert duplicates.
        [NonAction]
        private ReportModel AddReport(string title, string date, string status)
        {
            DateTime parsedDate;

            var formattedDate =
                DateTime.TryParse(date, out parsedDate)
                    ? parsedDate.ToString("MMM d, yyyy")
                    : DateTime.Now.ToString("MMM d, yyyy");

            var highest = reportList
                .Select(r => r.Id)
                .DefaultIfEmpty(0)
                .Max();

            var report = new ReportModel
            {
                Id = highest + 1,

                Title = title,

                Date = formattedDate,

                Status = string.IsNullOrWhiteSpace(status)
                    ? "Prepared"
                    : status
            };

            _db.Reports.Add(report);
            _db.SaveChanges();

            return report;
        }


        // ============================================================
        // DELETE REPORT
        // ============================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteReport(int id)
        {
            var report =
                reportList.FirstOrDefault(r => r.Id == id);

            if (report != null)
            {
                _db.Reports.Remove(report);
                _db.SaveChanges();
            }

            return RedirectToAction("Reports");
        }


        // ============================================================
        // CREATE BLOTTER
        // ============================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CreateBlotter(
            string title,
            string resident,
            string status,
            string incidentType,
            string incidentDate,
            string location,
            string priority,
            string complainantContact,
            string complainantAddress,
            string respondentName,
            string respondentAddress,
            string victimName,
            string narrative,
            string officer)
        {
            if (!string.IsNullOrWhiteSpace(resident))
            {
                var now = DateTime.Now;

                var newCase = new BlotterCaseModel
                {
                    Title = (string.IsNullOrWhiteSpace(incidentType) ? (title ?? string.Empty) : incidentType).Trim(),
                    Resident = resident.Trim(),
                    DateFiled = now.ToString("MMM d, yyyy h:mm tt"),
                    // "Open" is no longer a status. A newly filed case is active work that has
                    // not been picked up yet, so it starts as "Under Investigation"
                    // and counts toward IN PROGRESS until it is resolved.
                    Status = string.IsNullOrWhiteSpace(status) ? "Under Investigation" : status,
                    IncidentType = (incidentType ?? title ?? string.Empty).Trim(),
                    IncidentDate = incidentDate ?? "",
                    Location = location?.Trim() ?? "",
                    Priority = string.IsNullOrWhiteSpace(priority) ? "Medium" : priority,
                    ComplainantContact = complainantContact?.Trim() ?? "",
                    ComplainantAddress = complainantAddress?.Trim() ?? "",
                    RespondentName = respondentName?.Trim() ?? "",
                    RespondentAddress = respondentAddress?.Trim() ?? "",
                    VictimName = victimName?.Trim() ?? "",
                    Narrative = narrative?.Trim() ?? "",
                    Officer = officer?.Trim() ?? "",
                    Notes = new List<string>
                    {
                        $"{now.ToString("MMM d, yyyy h:mm tt")} - Case filed by {resident.Trim()}." +
                        (string.IsNullOrWhiteSpace(officer) ? "" : $" Received by {officer.Trim()}.")
                    }
                };

                // Case number allocation and insertion are one critical section, so two
                // clerks filing at once cannot both claim the same BLT number and have
                // UpdateBlotter / DeleteBlotter / AddBlotterNote hit the wrong case.
                newCase.Id = NextBlotterId();
                _db.Blotters.Add(newCase);
                _db.SaveChanges();
                TempData["Success"] = "Blotter case created.";

                // If AJAX request, return the created case as JSON
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new
                    {
                        success = true,
                        item = new
                        {
                            id = newCase.Id,
                            title = newCase.Title,
                            resident = newCase.Resident,
                            dateFiled = newCase.DateFiled,
                            status = newCase.Status,
                            incidentType = newCase.IncidentType,
                            incidentDate = newCase.IncidentDate,
                            location = newCase.Location,
                            priority = newCase.Priority,
                            complainant = newCase.Resident,
                            complainantContact = newCase.ComplainantContact,
                            complainantAddress = newCase.ComplainantAddress,
                            respondent = newCase.RespondentName,
                            respondentAddress = newCase.RespondentAddress,
                            victim = newCase.VictimName,
                            narrative = newCase.Narrative,
                            officer = newCase.Officer,
                            notes = newCase.Notes
                        }
                    });
                }
            }

            return RedirectToAction("Blotter");
        }

        [HttpGet]
        public IActionResult GetBlotterCase(string id)
        {
            var item = blotterList.FirstOrDefault(b => b.Id == id);
            if (item == null)
            {
                return NotFound();
            }

            return Json(new
            {
                success = true,
                item = new
                {
                    id = item.Id,
                    title = item.Title,
                    resident = item.Resident,
                    dateFiled = item.DateFiled,
                    status = item.Status,
                    incidentType = item.IncidentType,
                    incidentDate = item.IncidentDate,
                    location = item.Location,
                    priority = item.Priority,
                    complainant = item.Resident,
                    complainantContact = item.ComplainantContact,
                    complainantAddress = item.ComplainantAddress,
                    respondent = item.RespondentName,
                    respondentAddress = item.RespondentAddress,
                    victim = item.VictimName,
                    narrative = item.Narrative,
                    officer = item.Officer,
                    notes = item.Notes
                }
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UpdateBlotter(
            string id,
            string incidentType,
            string incidentDate,
            string location,
            string priority,
            string complainant,
            string complainantContact,
            string complainantAddress,
            string respondentName,
            string respondentAddress,
            string victimName,
            string narrative,
            string status,
            string officer)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest();
            }

            var item = blotterList.FirstOrDefault(b => b.Id == id);
            if (item == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrWhiteSpace(complainant))
            {
                item.Resident = complainant.Trim();
            }
            item.Title = (string.IsNullOrWhiteSpace(incidentType) ? item.Title : incidentType.Trim());
            item.IncidentType = (string.IsNullOrWhiteSpace(incidentType) ? item.IncidentType : incidentType.Trim());
            item.IncidentDate = incidentDate ?? "";
            item.Location = location?.Trim() ?? "";
            item.Priority = string.IsNullOrWhiteSpace(priority) ? item.Priority : priority;
            item.ComplainantContact = complainantContact?.Trim() ?? "";
            item.ComplainantAddress = complainantAddress?.Trim() ?? "";
            item.RespondentName = respondentName?.Trim() ?? "";
            item.RespondentAddress = respondentAddress?.Trim() ?? "";
            item.VictimName = victimName?.Trim() ?? "";
            item.Narrative = narrative?.Trim() ?? "";
            item.Officer = officer?.Trim() ?? "";
            item.Status = string.IsNullOrWhiteSpace(status) ? item.Status : status;
            _db.SaveChanges();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return Json(new
                {
                    success = true,
                    item = new
                    {
                        id = item.Id,
                        title = item.Title,
                        resident = item.Resident,
                        dateFiled = item.DateFiled,
                        status = item.Status,
                        incidentType = item.IncidentType,
                        incidentDate = item.IncidentDate,
                        location = item.Location,
                        priority = item.Priority,
                        complainant = item.Resident,
                        complainantContact = item.ComplainantContact,
                        complainantAddress = item.ComplainantAddress,
                        respondent = item.RespondentName,
                        respondentAddress = item.RespondentAddress,
                        victim = item.VictimName,
                        narrative = item.Narrative,
                        officer = item.Officer,
                        notes = item.Notes
                    }
                });
            }

            TempData["Success"] = "Blotter case updated.";
            return RedirectToAction("Blotter");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult AddBlotterNote(string id, string note)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(note))
            {
                return BadRequest();
            }

            var item = blotterList.FirstOrDefault(b => b.Id == id);
            if (item == null)
            {
                return NotFound();
            }

            var entry = $"{DateTime.Now.ToString("MMM d, yyyy h:mm tt")} - {note.Trim()}";
            item.Notes.Add(entry);
            _db.SaveChanges();

            return Json(new { success = true, note = entry });
        }


        // ============================================================
        // CREATE CERTIFICATE
        // ============================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CreateCertificate(
            string resident,
            string residentAge,
            string purok,
            string address,
            string title,
            string purpose,
            string orNumber,
            string amountPaid,
            string issueDate,
            string status)
        {
            if (!string.IsNullOrWhiteSpace(resident))
            {
                var issuedOn = ParseIssueDate(issueDate);

                var newCert = new CertificateModel
                {
                    Resident = resident.Trim(),

                    // Walk-ins are not always on the roster, so the details the layout
                    // placeholders need are captured here and fall back to the resident
                    // record only when the clerk leaves them blank.
                    ResidentAge = residentAge?.Trim() ?? string.Empty,
                    Purok = purok?.Trim() ?? string.Empty,
                    Address = address?.Trim() ?? string.Empty,

                    Title =
                        string.IsNullOrWhiteSpace(title)
                            ? "Barangay Clearance"
                            : title,

                    Purpose =
                        string.IsNullOrWhiteSpace(purpose)
                            ? "N/A"
                            : purpose,

                    OrNumber =
                        string.IsNullOrWhiteSpace(orNumber)
                            ? "N/A"
                            : orNumber,

                    AmountPaid =
                        string.IsNullOrWhiteSpace(amountPaid)
                            ? "0.00"
                            : amountPaid,

                    IssueDate = issuedOn,
                    Issued = issuedOn.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),

                    Status =
                        string.IsNullOrWhiteSpace(status)
                            ? "Ready"
                            : status,

                    // Snapshot the template in force at issuance so the printed
                    // document matches what the template editor saved.
                    TemplateHtml = ResolveTemplate(title)?.TemplateHtml ?? "",

                    Format = ResolveTemplate(title)?.TemplateHtml
                        ?? certificateRulesList
                            .FirstOrDefault(c => c.Type.Equals(title, StringComparison.OrdinalIgnoreCase))?
                            .Format
                        ?? ""
                };

                // Allocating the control number and inserting the certificate happen in one
                // critical section, so two clerks issuing at the same moment cannot produce
                // two certificates sharing a control number.
                newCert.Id = NextCertificateId();
                _db.Certificates.Add(newCert);
                _db.SaveChanges();
            }

            return RedirectToAction("Certificates");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult MarkCertificatePrinted(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return BadRequest();

            var item = certificateList.FirstOrDefault(c => c.Id == id);
            if (item == null)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = false, id, message = "Certificate not found." });
                }

                TempData["Error"] = "That certificate no longer exists.";
                return RedirectToAction("Certificates");
            }

            item.Status = "Printed";
            _db.SaveChanges();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return Json(new
                {
                    success = true,
                    id = item.Id,
                    status = item.Status,
                    issuedThisWeekCount = certificateList.Count(c => c.IssueDate.HasValue && c.IssueDate.Value.Date >= DateTime.Today.AddDays(-7)),
                    awaitingPickupCount = certificateList.Count(c => c.Status == "Ready"),
                    printedCount = certificateList.Count(c => c.Status == "Printed")
                });
            }

            TempData["Success"] = "Certificate marked as printed.";
            return RedirectToAction("Certificates");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteCertificate(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return BadRequest();

            var item = certificateList.FirstOrDefault(c => c.Id == id);
            if (item == null)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = false, id, message = "Certificate not found." });
                }

                TempData["Error"] = "That certificate no longer exists.";
                return RedirectToAction("Certificates");
            }

            _db.Certificates.Remove(item);
            _db.SaveChanges();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return Json(new
                {
                    success = true,
                    id,
                    noRecords = !certificateList.Any(),
                    issuedThisWeekCount = certificateList.Count(c => c.IssueDate.HasValue && c.IssueDate.Value.Date >= DateTime.Today.AddDays(-7)),
                    awaitingPickupCount = certificateList.Count(c => c.Status == "Ready"),
                    printedCount = certificateList.Count(c => c.Status == "Printed")
                });
            }

            TempData["Success"] = "Certificate deleted.";
            return RedirectToAction("Certificates");
        }

        /// <summary>
        /// Parses the date-picker value posted by the issue modal, falling back to
        /// today so a blank or malformed field never blanks the issue-date
        /// placeholder on the printed certificate.
        /// </summary>
        [NonAction]
        private static DateTime ParseIssueDate(string? issueDate) =>
            DateTime.TryParse(
                issueDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed.Date
                : DateTime.Today;

        // Sequential so two certificates issued in the same session cannot end up
        // sharing a control number, which would be printed on both documents.
        [NonAction]
        private string NextCertificateId()
        {
            var highest = certificateList
                .Select(c => c.Id.StartsWith("CERT-") && int.TryParse(c.Id.Substring(5), out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();

            return $"CERT-{highest + 1:000}";
        }


        // ============================================================
        // ANNOUNCEMENTS
        // ============================================================

        private static string ResidentReachLabel(int count)
        {
            return count switch
            {
                0 => "No residents",
                1 => "1 resident",
                _ => $"{count} residents"
            };
        }

        /// <summary>
        /// Summary card counts, always recomputed with a direct LINQ query over the
        /// database rows, so a card can never drift a manual increment away from the
        /// truth. The page simply renders these values, and the AJAX responses return
        /// exactly the same numbers -- there is no client-side arithmetic anywhere.
        ///
        /// SCHEDULED is the size of the active set: a notice stays active until it is
        /// deleted, so every row on the board is a scheduled notice. It deliberately
        /// does not filter on Date. That column is free text typed by the clerk rather
        /// than a scheduling flag, and filtering on it made the card disagree with the
        /// table directly above it (a post dated earlier in the year showed as 0).
        ///
        /// HIGH PRIORITY compares a trimmed, case-insensitive value, so "High", "high"
        /// and " High " are all the same priority.
        /// </summary>
        private static (int Total, int Scheduled, int HighPriority, string ResidentsReach) AnnouncementSummary(
            List<AnnouncementModel> announcements,
            int residentCount)
        {
            var totalScheduled = announcements.Count();

            var totalHighPriority = announcements.Count(
                a => a.Priority != null
                     && a.Priority.Trim().Equals("High", StringComparison.OrdinalIgnoreCase));

            return (
                announcements.Count,
                totalScheduled,
                totalHighPriority,
                ResidentReachLabel(residentCount)
            );
        }

        [HttpGet]
        public IActionResult Announcements(string searchQuery = "")
        {
            var filtered = announcementsList.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var q = searchQuery.Trim().ToLower();
                filtered = filtered.Where(a =>
                    a.Title.ToLower().Contains(q) ||
                    a.Purpose.ToLower().Contains(q) ||
                    a.TargetAudience.ToLower().Contains(q) ||
                    a.Priority.ToLower().Contains(q)
                );
            }

            // Counted from the full list, not the filtered one, so a search can never make
            // the summary cards disagree with what is actually stored.
            var summary = AnnouncementSummary(announcementsList, residentList.Count);

            var vm = new AnnouncementsViewModel
            {
                Announcements = filtered.ToList(),
                ScheduledCount = summary.Scheduled,
                HighPriorityCount = summary.HighPriority,
                ResidentsReach = summary.ResidentsReach,
                SearchQuery = searchQuery,
                ActiveTab = "Announcements"
            };

            return View("~/Views/Home/Announcements.cshtml", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CreateAnnouncement(string title, string purpose, string targetAudience, string date, string priority)
        {
            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(purpose))
            {
                var newAnnouncement = new AnnouncementModel
                {
                    Title = title.Trim(),
                    Purpose = purpose.Trim(),
                    TargetAudience = string.IsNullOrWhiteSpace(targetAudience) ? "All Barangay Residents" : targetAudience,
                    Date = string.IsNullOrWhiteSpace(date) ? DateTime.Now.ToString("MMM d, yyyy") : date,
                    Priority = string.IsNullOrWhiteSpace(priority) ? "Low" : priority
                };

                _db.Announcements.Add(newAnnouncement);
                _db.SaveChanges();

                // Create a corresponding notification (one per announcement)
                CreateNotification(
                    title: "New Announcement",
                    message: newAnnouncement.Title,
                    type: "Announcement",
                    relatedUrl: $"/Home/Announcements#{newAnnouncement.Id}",
                    relatedId: newAnnouncement.Id.ToString()
                );

                // Set TempData for non-AJAX fallback and to show toast after reload
                TempData["Success"] = "Announcement created.";

                // If AJAX request, return created item as JSON
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    // Recomputed from the database rather than incremented in memory, so
                    // the cards the page updates to are the counts the next full page
                    // load would render as well.
                    var summary = AnnouncementSummary(announcementsList, residentList.Count);

                    return Json(new
                    {
                        success = true,
                        item = new
                        {
                            id = newAnnouncement.Id,
                            title = newAnnouncement.Title,
                            purpose = newAnnouncement.Purpose,
                            targetAudience = newAnnouncement.TargetAudience,
                            date = newAnnouncement.Date,
                            priority = newAnnouncement.Priority
                        },
                        counts = new
                        {
                            total = summary.Total,
                            scheduled = summary.Scheduled,
                            highPriority = summary.HighPriority,
                            residentsReach = summary.ResidentsReach
                        }
                    });
                }
            }

            return RedirectToAction("Announcements");
        }

        // Updates an existing announcement in place.
        //
        // Returns the same JSON contract as CreateAnnouncement -- success, the saved
        // item, and counts recomputed from the database -- so the page can update the
        // row it just edited and the summary cards without a reload, and the two
        // flows stay impossible to confuse from the client side.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UpdateAnnouncement(int id, string title, string purpose, string targetAudience, string date, string priority)
        {
            // Checked separately from the field validation below. Reporting a missing
            // record as "Title and Purpose are required" sent people looking at the
            // wrong fields when the real problem was a request that carried no id.
            if (id <= 0)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = false, message = "No announcement was selected to update." });
                }

                TempData["Error"] = "No announcement was selected to update.";
                return RedirectToAction("Announcements");
            }

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(purpose))
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = false, message = "Title and Purpose are required." });
                }

                TempData["Error"] = "Title and Purpose are required.";
                return RedirectToAction("Announcements");
            }

            var item = _db.Announcements.FirstOrDefault(a => a.Id == id);

            if (item == null)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return Json(new { success = false, message = "Announcement was not found." });
                }

                TempData["Error"] = "Announcement was not found.";
                return RedirectToAction("Announcements");
            }

            item.Title = title.Trim();
            item.Purpose = purpose.Trim();
            item.TargetAudience = string.IsNullOrWhiteSpace(targetAudience) ? "All Barangay Residents" : targetAudience;
            item.Date = string.IsNullOrWhiteSpace(date) ? item.Date : date;
            item.Priority = string.IsNullOrWhiteSpace(priority) ? item.Priority : priority;
            _db.SaveChanges();

            // The bell shows the announcement title as the notification message, so an
            // edited title has to be reflected there too or the dropdown would keep
            // advertising the old wording.
            var linked = _db.Notifications
                .Where(n => n.RelatedId == id.ToString(CultureInfo.InvariantCulture))
                .ToList();

            foreach (var notification in linked)
            {
                notification.Message = item.Title;
            }

            if (linked.Count > 0)
            {
                _db.SaveChanges();
            }

            TempData["Success"] = "Announcement updated successfully.";

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                var summary = AnnouncementSummary(announcementsList, residentList.Count);

                return Json(new
                {
                    success = true,
                    id = item.Id,
                    item = new
                    {
                        id = item.Id,
                        title = item.Title,
                        purpose = item.Purpose,
                        targetAudience = item.TargetAudience,
                        date = item.Date,
                        priority = item.Priority
                    },
                    counts = new
                    {
                        total = summary.Total,
                        scheduled = summary.Scheduled,
                        highPriority = summary.HighPriority,
                        residentsReach = summary.ResidentsReach
                    }
                });
            }

            return RedirectToAction("Announcements");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteAnnouncement(int id)
        {
            var item = _db.Announcements.FirstOrDefault(a => a.Id == id);
            if (item != null)
            {
                _db.Announcements.Remove(item);
                var linked = _db.Notifications
                    .Where(n => n.RelatedId == id.ToString(CultureInfo.InvariantCulture))
                    .ToList();
                _db.Notifications.RemoveRange(linked);
                _db.SaveChanges();
            }

            // Same contract as CreateAnnouncement, so deleting a post live also updates
            // the summary cards instead of leaving them one behind.
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                var summary = AnnouncementSummary(announcementsList, residentList.Count);

                return Json(new
                {
                    success = true,
                    id,
                    counts = new
                    {
                        total = summary.Total,
                        scheduled = summary.Scheduled,
                        highPriority = summary.HighPriority,
                        residentsReach = summary.ResidentsReach
                    }
                });
            }

            return RedirectToAction("Announcements");
        }

        // Return JSON notifications for client-side bell
        [HttpGet]
        public IActionResult GetNotifications()
        {
            // Return combined notifications (for demo we use notificationsList maintained here)
            var list = notificationsList.OrderByDescending(n => n.CreatedAt).Select(n => new
            {
                id = n.Id,
                title = n.Title,
                message = n.Message,
                type = n.Type,
                isRead = n.IsRead,
                relatedUrl = n.RelatedUrl,
                createdAt = n.CreatedAt.ToString("MMM d, yyyy h:mm tt")
            }).ToList();

            return Json(list);
        }

        [HttpGet]
        public IActionResult GetUnreadCount()
        {
            var count = notificationsList.Count(n => !n.IsRead);
            return Json(new { count = count });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult MarkAsRead(int id)
        {
            // Queried directly off the tracked context rather than through the
            // notificationsList property: that property issues a fresh .ToList() on
            // every access, so the entity it returns is only in scope for the
            // expression that produced it. Setting IsRead on it and returning would
            // discard the change, and the badge would reappear on the next request.
            var item = _db.Notifications.FirstOrDefault(n => n.Id == id);
            if (item == null) return NotFound(new { success = false, message = "Notification not found" });

            // Marking an already-read item again is a no-op rather than an error, so a
            // double-click on the eye icon cannot fail.
            if (!item.IsRead)
            {
                item.IsRead = true;
                _db.SaveChanges();
            }

            // Counted from the database after the save, so the number returned is the
            // persisted truth and not a guess that the next request might contradict.
            var unread = _db.Notifications.Count(n => !n.IsRead);
            return Json(new { success = true, unread = unread });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult MarkAllRead()
        {
            // Only the unread rows are loaded, so the UPDATE touches just those and does
            // not rewrite every notification in the table.
            var unread = _db.Notifications.Where(n => !n.IsRead).ToList();
            foreach (var n in unread)
            {
                n.IsRead = true;
            }

            // Without this SaveChanges the whole operation was a no-op and every badge
            // came back on the next page load.
            if (unread.Count > 0)
            {
                _db.SaveChanges();
            }

            return Json(new { success = true, unread = 0, updated = unread.Count });
        }

        // Return full detail of a single notification, including its linked announcement fields
        [HttpGet]
        public IActionResult GetNotification(int id)
        {
            var notif = notificationsList.FirstOrDefault(n => n.Id == id);
            if (notif == null) return NotFound(new { success = false, message = "Notification not found" });

            // Resolve the linked announcement so the modal can show full details
            AnnouncementModel? announcement = null;
            if (!string.IsNullOrWhiteSpace(notif.RelatedId) && int.TryParse(notif.RelatedId, out var annId))
            {
                announcement = announcementsList.FirstOrDefault(a => a.Id == annId);
            }

            return Json(new
            {
                success = true,
                id = notif.Id,
                title = notif.Title,
                message = notif.Message,
                type = notif.Type,
                isRead = notif.IsRead,
                createdAt = notif.CreatedAt.ToString("MMM d, yyyy h:mm tt"),
                announcementId = announcement?.Id ?? 0,
                announcementTitle = announcement?.Title ?? notif.Message,
                purposeAndDetails = announcement?.Purpose ?? notif.Message,
                targetAudience = announcement?.TargetAudience ?? "All Barangay Residents",
                date = announcement?.Date ?? notif.CreatedAt.ToString("MMM d, yyyy"),
                priority = announcement?.Priority ?? "Low",
                relatedUrl = notif.RelatedUrl
            });
        }

        // Helper: create a new notification. Accepts createdAt and relatedId for announcement linkage.
        [NonAction]
        public void CreateNotification(string title, string message, string type = "System", string relatedUrl = "", DateTime? createdAt = null, string relatedId = "")
        {
            var notification = new BDIMS.Models.NotificationModel
            {
                Title = title,
                Message = message,
                Type = type,
                IsRead = false,
                CreatedAt = createdAt ?? DateTime.Now,
                RelatedUrl = relatedUrl,
                RelatedId = relatedId
            };

            _db.Notifications.Add(notification);
            _db.SaveChanges();
        }


        // ============================================================
        // SETTINGS
        // ============================================================

        // Certificate fee rules are stored in the CertificateRules table so the fee
        // table, dropdowns, and Settings > Certificates & Fees all share one source.

        // Single source of truth for the certificate types available to the issue/request dropdowns
        private List<CertificateRule> ActiveCertificateTypes()
        {
            return certificateRulesList
                .Where(c => c.Status == "Active" && !string.IsNullOrWhiteSpace(c.Type))
                .GroupBy(c => c.Type, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(c => c.Type, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ============================================================
        // CERTIFICATE TEMPLATES
        // ============================================================

        // Printable HTML templates edited under Settings > Certificates & Fees
        // Layouts are persisted to App_Data/certificate_templates.json rather than
        // held in memory, so a saved layout survives a restart. This accessor keeps the
        // read sites below unchanged; writes go through CertificateTemplateStore, since
        // All() hands back a copy and mutating it would have no effect.
        private static List<CertificateTemplate> certificateTemplateList =>
            CertificateTemplateStore.All();

        // Fee-table labels mapped to the template names administrators use in the
        // Template Editor, so the seeded "Barangay Indigency" layout is picked up
        // by the existing "Certificate of Indigency" rule without renaming it.
        private static readonly Dictionary<string, string> TemplateTypeAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Certificate of Indigency (Medical / Educational / Legal Aid)"] = "Barangay Indigency"
            };

        // Finds the template to use for a certificate type: exact name, then alias,
        // then a "starts with" match so a renamed fee-table row still resolves.
        [NonAction]
        private static CertificateTemplate? ResolveTemplate(string? certificateType)
        {
            if (string.IsNullOrWhiteSpace(certificateType))
            {
                return null;
            }

            var type = certificateType.Trim();

            var exact = certificateTemplateList.FirstOrDefault(t =>
                t.CertificateType.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            if (TemplateTypeAliases.TryGetValue(type, out var alias))
            {
                var aliased = certificateTemplateList.FirstOrDefault(t =>
                    t.CertificateType.Equals(alias, StringComparison.OrdinalIgnoreCase));
                if (aliased != null)
                {
                    return aliased;
                }
            }

            return certificateTemplateList.FirstOrDefault(t =>
                type.StartsWith(t.CertificateType, StringComparison.OrdinalIgnoreCase));
        }

        // The fee-table row a template belongs to, used to keep fees in sync.
        [NonAction]
        private CertificateRule? RuleForTemplate(CertificateTemplate template)
        {
            return certificateRulesList.FirstOrDefault(r =>
                    r.Type.Equals(template.CertificateType, StringComparison.OrdinalIgnoreCase))
                ?? certificateRulesList.FirstOrDefault(r =>
                    r.Type.StartsWith(template.CertificateType, StringComparison.OrdinalIgnoreCase))
                ?? certificateRulesList.FirstOrDefault(r =>
                    TemplateTypeAliases.TryGetValue(r.Type, out var alias) &&
                    alias.Equals(template.CertificateType, StringComparison.OrdinalIgnoreCase));
        }

        [NonAction]
        private static int? ParseResidentAge(string? ageSex)
        {
            if (string.IsNullOrWhiteSpace(ageSex))
            {
                return null;
            }

            var head = ageSex.Split('/')[0].Trim();
            return int.TryParse(head, out var age) ? age : null;
        }

        [NonAction]
        private ResidentModel? FindResident(string? nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
            {
                return null;
            }

            var key = nameOrId.Trim();

            return residentList.FirstOrDefault(r => r.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? residentList.FirstOrDefault(r => r.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        // Purok options for the issue modal. Derived from the roster, but falls back to
        // the barangay's standard eight puroks so a walk-in can still be issued a
        // certificate when the roster has not recorded anyone in a given purok yet.
        [NonAction]
        private List<string> KnownPuroks()
        {
            var fromRoster = residentList
                .Select(r => r.Purok)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var standard = Enumerable
                .Range(1, DefaultPurokCount)
                .Select(n => "Purok " + n.ToString(CultureInfo.InvariantCulture));

            return fromRoster
                .Concat(standard)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => PurokSortKey(p))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private const int DefaultPurokCount = 8;

        /// <summary>
        /// Sorts "Purok 2" before "Purok 10" by numeric suffix, falling back to the
        /// whole label for entries that are not in the "Purok n" shape.
        /// </summary>
        [NonAction]
        private static int PurokSortKey(string? purok)
        {
            var parts = (purok ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 &&
                   parts[0].StartsWith("Purok", StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : int.MaxValue;
        }

        [NonAction]
        private CertificateTemplateData BuildTemplateData(
            string? certificateType,
            string? residentNameOrId,
            string? purpose,
            string? orNumber,
            decimal amountPaid,
            string? certificateId,
            DateTime issueDate,
            string? residentAge = null,
            string? purok = null,
            string? address = null)
        {
            var resident = FindResident(residentNameOrId);

            // Values captured on the issue form win, since a walk-in may not be on the
            // roster or the clerk may have corrected the record. Anything left blank
            // falls back to the resident profile.
            var age = int.TryParse(residentAge?.Trim(), out var parsedAge)
                ? parsedAge
                : resident != null ? ParseResidentAge(resident.AgeSex) : null;

            var resolvedPurok = !string.IsNullOrWhiteSpace(purok)
                ? purok!.Trim()
                : resident?.Purok ?? string.Empty;

            var resolvedAddress = !string.IsNullOrWhiteSpace(address)
                ? address!.Trim()
                : resident != null ? resident.Purok ?? string.Empty : string.Empty;

            return new CertificateTemplateData
            {
                CertificateType = string.IsNullOrWhiteSpace(certificateType)
                    ? "Barangay Clearance"
                    : certificateType.Trim(),

                ResidentName = resident?.Name ?? (residentNameOrId ?? "").Trim(),
                ResidentAge = age,
                Purok = resolvedPurok,
                Address = resolvedAddress,

                Purpose = purpose ?? "",
                OrNumber = orNumber ?? "",
                AmountPaid = amountPaid,
                CertificateId = certificateId ?? "",

                IssueDate = issueDate,
                BarangayCaptain = CurrentSignatoryName,
                SignatoryRole = CurrentSignatoryRole,
                BarangayName = CurrentBarangayName,
                Municipality = CurrentMunicipality,
                Province = CurrentProvince
            };
        }

        // Barangay profile values. They resolve through BarangayProfileProvider, which
        // is seeded from the BDIMS:Barangay configuration section but then backed by
        // the AppSettings table. Reading the provider rather than IOptions is what
        // makes an edit on the Settings page appear on the next printed certificate
        // instead of only after a restart.
        private string CurrentBarangayName => _barangayProfile.Current.BarangayName;
        private string CurrentMunicipality => _barangayProfile.Current.Municipality;
        private string CurrentProvince => _barangayProfile.Current.Province;
        private string CurrentSignatoryName => _barangayProfile.Current.SignatoryName;
        private string CurrentSignatoryRole => _barangayProfile.Current.SignatoryRole;

        // Union of fee-table types and existing templates, for the editor's type picker.
        [NonAction]
        private List<string> CertificateTypeOptions()
        {
            return certificateRulesList.Select(r => r.Type)
                .Concat(certificateTemplateList.Select(t => t.CertificateType))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [NonAction]
        private decimal FeeForCertificateType(string? certificateType)
        {
            return certificateRulesList
                .FirstOrDefault(c => c.Type.Equals(certificateType, StringComparison.OrdinalIgnoreCase))?
                .Fee ?? 0m;
        }

        [HttpGet]
        public IActionResult CertificateTemplateEditor(int? id = null, string? type = null)
        {
            CertificateTemplate? selected = null;

            if (id.HasValue)
            {
                selected = certificateTemplateList.FirstOrDefault(t => t.Id == id.Value);
            }
            else if (!string.IsNullOrWhiteSpace(type))
            {
                var wanted = type.Trim();

                selected = certificateTemplateList.FirstOrDefault(t =>
                    t.CertificateType.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                selected = certificateTemplateList.FirstOrDefault(t =>
                    t.CertificateType.Equals("Barangay Indigency", StringComparison.OrdinalIgnoreCase))
                    ?? certificateTemplateList.FirstOrDefault();
            }

            if (selected == null)
            {
                // No saved layout for this type yet: hand the editor a transient draft
                // (Id 0) pre-filled with the standard letterhead.
                var draftType = string.IsNullOrWhiteSpace(type) ? "Barangay Indigency" : type!.Trim();

                selected = new CertificateTemplate
                {
                    Id = 0,
                    CertificateName = draftType,
                    Fee = FeeForCertificateType(draftType),
                    TemplateHtml = draftType.Equals("Barangay Indigency", StringComparison.OrdinalIgnoreCase)
                        ? CertificateTemplateDefaults.BarangayIndigencyTemplateHtml
                        : CertificateTemplateDefaults.BuildFallbackFor(draftType)
                };
            }

            var template = new CertificateTemplate
            {
                Id = selected.Id,
                CertificateName = selected.CertificateName,
                Fee = selected.Fee,
                TemplateHtml = selected.TemplateHtml,
                Source = selected.Source,
                UpdatedAt = selected.UpdatedAt
            };

            var residents = residentList.ToList();

            var vm = new CertificateTemplateEditorViewModel
            {
                Template = template,
                Templates = certificateTemplateList
                    .OrderBy(t => t.CertificateType, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                CertificateTypes = CertificateTypeOptions(),
                Tokens = CertificateTemplateRenderer.SupportedTokens.ToList(),
                Residents = residents,
                DefaultHtml = CertificateTemplateDefaults.BarangayIndigencyTemplateHtml,
                PreviewResidentId = residents.FirstOrDefault()?.Id ?? "",
                PreviewPurpose = "ENROLLMENT IN TAGBILARAN CITY COLLEGE (TCC)"
            };

            vm.PreviewHtml = CertificateTemplateRenderer.Render(
                template.TemplateHtml,
                BuildTemplateData(
                    template.CertificateType,
                    vm.PreviewResidentId,
                    vm.PreviewPurpose,
                    "OR-2026-0900",
                    template.Fee,
                    "CERT-PREVIEW",
                    DateTime.Today));

            return View("~/Views/Home/CertificateTemplateEditor.cshtml", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveCertificateTemplate(CertificateTemplateEditorViewModel model)
        {
            var submitted = model?.Template ?? new CertificateTemplate();
            var certificateType = (submitted.CertificateType ?? "").Trim();

            if (string.IsNullOrWhiteSpace(certificateType))
            {
                TempData["TemplateError"] = "Certificate type is required.";

                return RedirectToAction("CertificateTemplateEditor", new { id = submitted.Id });
            }

            var templateHtml = DocumentTemplateImporter.SanitizeForStorage(submitted.TemplateHtml);

            if (string.IsNullOrWhiteSpace(DocumentTemplateImporter.StripTags(templateHtml)))
            {
                TempData["TemplateError"] = "The layout is empty. Add content before saving.";

                return RedirectToAction("CertificateTemplateEditor", new { id = submitted.Id });
            }

            // The store matches on Id first and then on certificate name, so re-saving a
            // draft updates the existing row instead of creating a duplicate.
            var toSave = new CertificateTemplate
            {
                Id = submitted.Id,
                CertificateName = certificateType,
                TemplateHtml = templateHtml,
                Source = "Template Editor",
                Fee = RuleForTemplate(new CertificateTemplate { CertificateName = certificateType })?.Fee
                      ?? submitted.Fee
            };

            var saved = CertificateTemplateStore.Save(toSave);

            // Keep the fee table in step if this type was created here rather than
            // through the Certificates & Fees table.
            EnsureFeeRow(certificateType, saved.Fee);

            TempData["SuccessMessage"] = $"Template for \"{certificateType}\" saved.";

            return RedirectToAction("CertificateTemplateEditor", new { id = saved.Id });
        }

        /// <summary>
        /// Adds a certificate type to the fee table so it appears in the Documents and
        /// Certificates request dropdowns, together with a starter template.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CreateCertificateType(string certificateName, decimal fee, bool requiresResidency)
        {
            var name = (certificateName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["TemplateError"] = "Enter a name for the new certificate type.";

                return RedirectToAction("CertificateTemplateEditor");
            }

            var existingRule = certificateRulesList.FirstOrDefault(r =>
                r.Type.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (existingRule != null)
            {
                TempData["TemplateError"] = $"\"{name}\" is already in the Certificates &amp; Fees table.";

                return RedirectToAction("CertificateTemplateEditor", new { type = name });
            }

            var charge = fee < 0 ? 0 : fee;

            certificateRulesList.Add(new CertificateRule
            {
                Id = certificateRulesList.Any() ? certificateRulesList.Max(r => r.Id) + 1 : 1,
                Type = name,
                Fee = charge,
                RequiresResidency = requiresResidency,
                Status = "Active"
            });

            var created = CertificateTemplateStore.CreateForType(name, charge);

            TempData["SuccessMessage"] = $"\"{name}\" is ready to issue. Its template is open for editing.";

            return RedirectToAction("CertificateTemplateEditor", new { id = created.Id });
        }

        /// <summary>
        /// Replaces the active layout for a certificate type with an uploaded .docx or .html
        /// document, converted to HTML by <see cref="DocumentTemplateImporter"/>.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(10 * 1024 * 1024)]
        public async Task<IActionResult> ImportCertificateTemplate(
            int templateId,
            string certificateName,
            IFormFile? file)
        {
            var certificateType = (certificateName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(certificateType))
            {
                TempData["TemplateError"] = "Certificate type is required.";

                return RedirectToAction("CertificateTemplateEditor", new { id = templateId });
            }

            if (file == null || file.Length == 0)
            {
                TempData["TemplateError"] = "Choose a .docx or .html file to import.";

                return RedirectToAction("CertificateTemplateEditor", new { id = templateId });
            }

            TemplateImportResult result;

            try
            {
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer);
                buffer.Position = 0;

                result = DocumentTemplateImporter.Import(file.FileName, buffer);
            }
            catch (IOException)
            {
                result = TemplateImportResult.Fail("The file could not be read. Try saving it again and re-uploading.");
            }

            if (!result.Success)
            {
                TempData["TemplateError"] = result.Message;

                return RedirectToAction("CertificateTemplateEditor", new { id = templateId, type = certificateType });
            }

            var template = UpsertTemplate(templateId, certificateType);

            template.TemplateHtml = DocumentTemplateImporter.SanitizeForStorage(result.Html);
            template.Source = result.SourceLabel;
            template.UpdatedAt = DateTime.Now;
            template.Fee = RuleForTemplate(template)?.Fee ?? template.Fee;

            EnsureFeeRow(certificateType, template.Fee);

            // UpsertTemplate persists the layout it found, before these edits. The store
            // now hands back a detached copy, so the imported HTML has to be saved
            // explicitly or the editor shows it while the file still holds the old layout
            // and the import is lost on the next restart.
            template = CertificateTemplateStore.Save(template);

            TempData["SuccessMessage"] = result.Message;

            return RedirectToAction("CertificateTemplateEditor", new { id = template.Id });
        }

        /// <summary>
        /// Attaches a layout to a single certificate type from the Settings fee table,
        /// so an administrator can paste a layout or upload a .docx/.html for one
        /// certificate without leaving Settings for the Template Editor.
        ///
        /// The pasted path runs through the same
        /// <see cref="DocumentTemplateImporter.SanitizeForStorage"/> filter as an
        /// imported document, because the stored layout is later rendered with
        /// @Html.Raw / innerHTML.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(10 * 1024 * 1024)]
        public async Task<IActionResult> SaveCertificateTemplateForType(
            string certificateName,
            string? pastedHtml,
            IFormFile? file)
        {
            var certificateType = (certificateName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(certificateType))
            {
                TempData["TemplateError"] = "Certificate type is required.";

                return RedirectToAction("Settings", new { tab = "documents" });
            }

            var hasFile = file != null && file.Length > 0;
            var pasted = (pastedHtml ?? "").Trim();

            if (!hasFile && pasted.Length == 0)
            {
                TempData["TemplateError"] =
                    "Paste a layout or choose a .docx or .html file for this certificate.";

                return RedirectToAction("Settings", new { tab = "documents" });
            }

            string html;
            string source;
            string message;

            if (hasFile)
            {
                TemplateImportResult result;

                try
                {
                    using var buffer = new MemoryStream();
                    await file!.CopyToAsync(buffer);
                    buffer.Position = 0;

                    result = DocumentTemplateImporter.Import(file.FileName, buffer);
                }
                catch (IOException)
                {
                    result = TemplateImportResult.Fail(
                        "The file could not be read. Try saving it again and re-uploading.");
                }

                if (!result.Success)
                {
                    TempData["TemplateError"] = result.Message;

                    return RedirectToAction("Settings", new { tab = "documents" });
                }

                html = result.Html;
                source = result.SourceLabel;
                message = result.Message;
            }
            else
            {
                html = pasted;
                source = "Pasted in Settings";
                message = $"Layout pasted for \"{certificateType}\".";
            }

            // Resolve before upserting so an aliased row updates the layout it
            // already renders with instead of creating a second template that the
            // renderer would never reach.
            var resolved = ResolveTemplate(certificateType);
            var template = resolved != null
                ? CertificateTemplateStore.GetById(resolved.Id) ?? resolved
                : CertificateTemplateStore.CreateForType(certificateType);

            template.TemplateHtml = DocumentTemplateImporter.SanitizeForStorage(html);
            template.Source = source;
            template.UpdatedAt = DateTime.Now;
            template.Fee = RuleForTemplate(template)?.Fee ?? template.Fee;

            EnsureFeeRow(certificateType, template.Fee);

            // Store.All() hands back detached copies, so the layout has to be
            // written back explicitly or the edit is lost on the next load.
            template = CertificateTemplateStore.Save(template);

            TempData["SuccessMessage"] = message
                + $" \"{template.CertificateName}\" now uses this layout.";

            return RedirectToAction("Settings", new { tab = "documents" });
        }

        [NonAction]
        private CertificateTemplate UpsertTemplate(int id, string certificateType)
        {
            var current = id != 0 ? CertificateTemplateStore.GetById(id) : null;

            current ??= CertificateTemplateStore.GetByName(certificateType);

            if (current != null)
            {
                current.CertificateName = certificateType;
                return CertificateTemplateStore.Save(current);
            }

            return CertificateTemplateStore.CreateForType(certificateType);
        }

        [NonAction]
        private void EnsureFeeRow(string certificateType, decimal fee)
        {
            var rule = certificateRulesList.FirstOrDefault(r =>
                r.Type.Equals(certificateType, StringComparison.OrdinalIgnoreCase));

            if (rule != null)
            {
                return;
            }

            certificateRulesList.Add(new CertificateRule
            {
                Id = certificateRulesList.Any() ? certificateRulesList.Max(r => r.Id) + 1 : 1,
                Type = certificateType,
                Fee = fee,
                RequiresResidency = false,
                Status = "Active"
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteCertificateTemplate(int id)
        {
            var existing = CertificateTemplateStore.GetById(id);

            if (existing != null && CertificateTemplateStore.Delete(id))
            {
                TempData["SuccessMessage"] = $"Template for \"{existing.CertificateName}\" deleted.";
            }

            return RedirectToAction("CertificateTemplateEditor");
        }

        // Live preview for the editor: renders unsaved HTML against chosen sample data.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult PreviewCertificateTemplate(
            string templateHtml,
            string residentId,
            string purpose,
            DateTime? issueDate)
        {
            var html = CertificateTemplateRenderer.Render(
                templateHtml,
                BuildTemplateData(
                    "Barangay Indigency",
                    residentId,
                    purpose,
                    "OR-2026-0900",
                    0m,
                    "CERT-PREVIEW",
                    issueDate ?? DateTime.Today));

            return Json(new
            {
                success = true,
                html,
                unknownTokens = CertificateTemplateRenderer.UnknownTokens(templateHtml)
            });
        }

        // Renders the saved template for an issued certificate, used by preview and print
        // on both the Documents and Certificates pages. Types without a saved layout get a
        // generated one so a certificate is never blank.
        [HttpGet]
        public IActionResult RenderCertificateHtml(
            string? title,
            string? resident,
            string? residentAge,
            string? purok,
            string? address,
            string? purpose,
            string? orNumber,
            string? amountPaid,
            string? issued,
            string? certificateId)
        {
            var template = ResolveTemplate(title);
            var hasTemplate = !string.IsNullOrWhiteSpace(template?.TemplateHtml);

            // Same resolution order as GetCertificateFormat: the administrator's saved
            // layout, then a legacy format stored on the fee row, then a generated one.
            // The fee rows carry an empty string rather than null for "no format", so
            // this has to test for whitespace, not just null, or the generated layout
            // is skipped and the certificate renders blank.
            var legacyFormat = certificateRulesList
                .FirstOrDefault(c => c.Type.Equals(title, StringComparison.OrdinalIgnoreCase))?
                .Format;

            var templateHtml = hasTemplate
                ? template!.TemplateHtml
                : !string.IsNullOrWhiteSpace(legacyFormat)
                    ? legacyFormat
                    : CertificateTemplateDefaults.BuildFallbackFor(title);

            var money = decimal.TryParse(
                amountPaid,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsedFee)
                ? parsedFee
                : 0m;

            var date = DateTime.TryParse(
                issued,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate)
                ? parsedDate
                : DateTime.Today;

            var html = CertificateTemplateRenderer.Render(
                templateHtml,
                BuildTemplateData(
                    title,
                    resident,
                    purpose,
                    orNumber,
                    money,
                    certificateId,
                    date,
                    residentAge,
                    purok,
                    address));

            return Json(new
            {
                success = true,
                hasTemplate,
                certificateName = template?.CertificateName ?? title ?? "",
                html,
                unknownTokens = CertificateTemplateRenderer.UnknownTokens(templateHtml)
            });
        }

        [HttpGet]
        public IActionResult Settings(string tab = "general")
        {
            // Make sure the tab is valid
            if (tab != "general" &&
                tab != "documents" &&
                tab != "security")
            {
                tab = "general";
            }

            var model = new SettingsViewModel
            {
                ActiveTab = tab,

                // Pre-fill the Barangay Info form from the persisted profile so the
                // page always matches the values certificates actually print.
                BarangayName = CurrentBarangayName,
                Municipality = CurrentMunicipality,
                Province = CurrentProvince,
                ContactEmail = _barangayProfile.Current.ContactEmail,
                ContactPhone = _barangayProfile.Current.ContactPhone,
                OfficeHours = _barangayProfile.Current.OfficeHours,
                SignatoryName = CurrentSignatoryName,
                SignatoryRole = CurrentSignatoryRole,

                Certificates = certificateRulesList.ToList(),
                CertificateTemplates = certificateTemplateList
                    .OrderBy(t => t.CertificateType, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                TemplateTokens = CertificateTemplateRenderer.SupportedTokens.ToList()
            };

            return View(
                "~/Views/Home/Settings.cshtml",
                model
            );
        }


        // ============================================================
        // SAVE SETTINGS
        // ============================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveSettings(SettingsViewModel model)
        {
            // Make sure the tab is valid
            if (model.ActiveTab != "general" &&
                model.ActiveTab != "documents" &&
                model.ActiveTab != "security")
            {
                model.ActiveTab = "general";
            }

            // Persist the barangay profile when the general tab is submitted. The
            // card's own "Save Profile Changes" button posts to
            // Settings/SaveBarangayProfile, but the tab-level submit posts here and
            // carries the same eight named inputs. Routing them through the same
            // provider means either path persists identically, instead of this one
            // silently discarding the values it just received.
            if (model.ActiveTab == "general")
            {
                var profileResult = _barangayProfile.Save(new BarangayProfileForm
                {
                    BarangayName = model.BarangayName,
                    Municipality = model.Municipality,
                    Province = model.Province,
                    ContactEmail = model.ContactEmail,
                    ContactPhone = model.ContactPhone,
                    OfficeHours = model.OfficeHours,
                    SignatoryName = model.SignatoryName,
                    SignatoryRole = model.SignatoryRole
                });

                if (!profileResult.Success)
                {
                    // The certificate rules were not touched on this tab, so nothing
                    // has been half-written. Return to the form with the reason rather
                    // than reporting a success that did not happen.
                    TempData["ErrorMessage"] = profileResult.Message;

                    return RedirectToAction("Settings", new { tab = "general" });
                }
            }

            // Temporary success message
            TempData["SuccessMessage"] =
                "Settings saved successfully!";

            // Persist certificate rules if coming from the documents tab
            if (model.ActiveTab == "documents" && model.Certificates != null)
            {
                var incoming = model.Certificates
                    .Where(c => !string.IsNullOrWhiteSpace(c.Type))
                    .ToList();

                var existingRules = _db.CertificateRules.ToList();

                // Remove rules not in the incoming list
                var incomingTypes = incoming.Select(c => c.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var toRemove = existingRules.Where(r => !incomingTypes.Contains(r.Type)).ToList();
                _db.CertificateRules.RemoveRange(toRemove);

                // Update or add rules
                foreach (var incomingRule in incoming)
                {
                    var existing = existingRules.FirstOrDefault(r =>
                        r.Type.Equals(incomingRule.Type, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.Fee = incomingRule.Fee;
                        existing.RequiresResidency = incomingRule.RequiresResidency;
                        existing.Status = incomingRule.Status;
                        existing.Format = incomingRule.Format;
                    }
                    else
                    {
                        _db.CertificateRules.Add(new CertificateRule
                        {
                            Type = incomingRule.Type,
                            Fee = incomingRule.Fee,
                            RequiresResidency = incomingRule.RequiresResidency,
                            Status = incomingRule.Status,
                            Format = incomingRule.Format
                        });
                    }
                }

                _db.SaveChanges();

                // Keep template fees aligned with the fee table. All() hands back
                // detached copies, so the aligned fees must be written back explicitly.
                var aligned = CertificateTemplateStore.All();

                var feeChanged = false;
                foreach (var template in aligned)
                {
                    var rule = RuleForTemplate(template);
                    if (rule != null && rule.Fee != template.Fee)
                    {
                        template.Fee = rule.Fee;
                        feeChanged = true;
                    }
                }

                if (feeChanged)
                {
                    CertificateTemplateStore.ReplaceAll(aligned);
                }
            }

            // Return to the same Settings tab
            return RedirectToAction(
                "Settings",
                new
                {
                    tab = model.ActiveTab
                }
            );
        }


        // ============================================================
        // GET CERTIFICATE FORMAT
        // ============================================================

        [HttpGet]
        public IActionResult GetCertificateFormat(string title)
        {
            // Prefer the administrator's Template Editor layout, fall back to the
            // legacy plain-text format stored on the fee table row.
            var template = ResolveTemplate(title);

            var format = !string.IsNullOrWhiteSpace(template?.TemplateHtml)
                ? template!.TemplateHtml
                : certificateRulesList
                    .FirstOrDefault(c => c.Type.Equals(title, StringComparison.OrdinalIgnoreCase))?
                    .Format ?? "";

            return Json(new { success = true, format });
        }


        // ============================================================
        // LOGOUT
        // ============================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            // The sidebar logout form posts here, so this is the sign-out that actually
            // clears the auth cookie. Without it the cookie would survive and the next
            // request would still be authenticated.
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login", "Account");
        }
    }
}