using System;
using System.Collections.Generic;
using System.Linq;
using BDIMS.Models;
using BDIMS.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BDIMS.Controllers
{
    /// <summary>
    /// JSON API backing the Certificate Template Manager under
    /// Settings &gt; Certificates &amp; Fees.
    ///
    /// Layouts live in App_Data/certificate_templates.json (see
    /// <see cref="CertificateTemplateStore"/>) rather than in a database, so no ORM or
    /// migration is involved.
    /// </summary>
    [Authorize]
    public class SettingsController : Controller
    {
        private readonly BarangayProfileProvider _barangayProfile;

        public SettingsController(BarangayProfileProvider barangayProfile)
        {
            _barangayProfile = barangayProfile;
        }

        /// <summary>
        /// Returns the saved layout for a certificate name, or the generated fallback
        /// when nothing has been saved yet, so a caller never receives a blank layout.
        /// </summary>
        [HttpGet]
        public IActionResult GetCertificateTemplate(string certificateName)
        {
            if (string.IsNullOrWhiteSpace(certificateName))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "certificateName is required."
                });
            }

            var name = certificateName.Trim();
            var saved = CertificateTemplateStore.GetByName(name);

            var template = saved ?? new CertificateTemplate
            {
                Id = 0,
                CertificateName = name,
                Fee = 0m,
                TemplateHtml = CertificateTemplateDefaults.BuildFallbackFor(name),
                Source = "Generated default (not yet saved)",
                UpdatedAt = DateTime.Now
            };

            return Json(new
            {
                success = true,
                template,
                unknownTokens = CertificateTemplateRenderer.UnknownTokens(template.TemplateHtml).ToArray(),

                // Relative only. The application has no authentication, so handing an
                // anonymous caller the absolute content-root path is free reconnaissance.
                filePath = System.IO.Path.GetFileName(CertificateTemplateStore.FilePath)
            });
        }

        /// <summary>
        /// Creates or updates a layout.
        ///
        /// The body is JSON, so the antiforgery token travels in the
        /// RequestVerificationToken header (configured in Program.cs) rather than in a
        /// form field. Without that header the state change is rejected, which keeps
        /// this endpoint protected against cross-site request forgery.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveCertificateTemplate([FromBody] CertificateTemplate dto)
        {
            if (dto == null)
            {
                return BadRequest(new { success = false, message = "A JSON body is required." });
            }

            if (string.IsNullOrWhiteSpace(dto.CertificateName))
            {
                return BadRequest(new { success = false, message = "CertificateName is required." });
            }

            if (string.IsNullOrWhiteSpace(DocumentTemplateImporter.StripTags(dto.TemplateHtml)))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "The layout is empty. Add content before saving."
                });
            }

            // Same filter applied to the editor form, so the API cannot be used to
            // bypass it.
            dto.TemplateHtml = DocumentTemplateImporter.SanitizeForStorage(dto.TemplateHtml);

            var saved = CertificateTemplateStore.Save(dto);

            return Json(new
            {
                success = true,
                template = saved,
                message = $"Saved \"{saved.CertificateName}\".",
                unknownTokens = CertificateTemplateRenderer.UnknownTokens(saved.TemplateHtml).ToArray()
            });
        }

        /// <summary>
        /// All saved layouts, for the template picker in the editor.
        /// </summary>
        [HttpGet]
        public IActionResult GetCertificateTemplates()
        {
            return Json(new
            {
                success = true,
                templates = CertificateTemplateStore.All()
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteCertificateTemplate(int id)
        {
            var removed = CertificateTemplateStore.Delete(id);

            return Json(new
            {
                success = removed,
                message = removed
                    ? "Template deleted."
                    : "That template no longer exists."
            });
        }

        /// <summary>
        /// Reports where layouts are persisted, so the Settings page can show the file
        /// an administrator would back up. Only the file name is returned, not the
        /// absolute content-root path.
        /// </summary>
        [HttpGet]
        public IActionResult GetStorageInfo()
        {
            return Json(new
            {
                success = true,
                filePath = System.IO.Path.GetFileName(CertificateTemplateStore.FilePath),
                count = CertificateTemplateStore.All().Count
            });
        }

        // -------------------------------------------------- Barangay Profile

        /// <summary>
        /// The persisted barangay profile.
        ///
        /// The Settings form posts through the antiforgery-protected action below,
        /// but a GET is exposed so the values are available to any view or script
        /// that needs them without a full page render. Reads the live provider, not
        /// the configuration snapshot, so a value saved a moment ago is returned.
        /// </summary>
        [HttpGet]
        public IActionResult GetBarangayProfile()
        {
            return Json(new
            {
                success = true,
                profile = _barangayProfile.Current
            });
        }

        /// <summary>
        /// Persists the "Barangay Profile &amp; Operations" form.
        ///
        /// The body is JSON, so the antiforgery token travels in the
        /// RequestVerificationToken header (configured in Program.cs) rather than in
        /// a form field, matching SaveCertificateTemplate above.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveBarangayProfile([FromBody] BarangayProfileForm form)
        {
            var result = _barangayProfile.Save(form);

            if (!result.Success)
            {
                // A rejected value is a client-side problem, so 400 with the
                // offending field named lets the form highlight the input instead
                // of showing a generic failure.
                return StatusCode(
                    StatusCodes.Status400BadRequest,
                    new
                    {
                        success = false,
                        message = result.Message,
                        field = result.Field
                    });
            }

            return Json(new
            {
                success = true,
                message = result.Message,
                profile = _barangayProfile.Current
            });
        }
    }
}
