using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BDIMS.Models;
using Microsoft.AspNetCore.Hosting;

namespace BDIMS.Services
{
    /// <summary>
    /// File-backed persistence for certificate template layouts.
    ///
    /// Layouts are stored as a single JSON document at
    /// &lt;ContentRoot&gt;/App_Data/certificate_templates.json so an administrator's saved
    /// layout survives an application restart without requiring a database.
    ///
    /// Reads and writes are serialised through <see cref="Gate"/>, and saves go to a
    /// temporary file that is then moved over the target. A crash or a full disk
    /// therefore leaves either the previous good file or the new one, never a
    /// half-written document.
    /// </summary>
    public static class CertificateTemplateStore
    {
        public const string AppDataFolderName = "App_Data";
        public const string FileName = "certificate_templates.json";

        private static readonly object Gate = new object();

        private static readonly JsonSerializerOptions SerializerOptions =
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                // Template HTML legitimately contains characters such as < > & and the
                // {{ Token }} braces, so keep them human-readable rather than \u-escaped.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

        private static string? _filePath;
        private static List<CertificateTemplate>? _cache;

        /// <summary>
        /// Binds the store to a content root. Called once during startup.
        /// </summary>
        public static void Configure(IWebHostEnvironment environment)
        {
            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            lock (Gate)
            {
                _filePath = Path.Combine(
                    environment.ContentRootPath,
                    AppDataFolderName,
                    FileName);

                _cache = null;
            }
        }

        /// <summary>
        /// Absolute path of the backing JSON file, or null before Configure has run.
        /// </summary>
        public static string? FilePath => _filePath;

        // ------------------------------------------------------------- Reads

        /// <summary>
        /// All saved layouts. A missing or unreadable file is replaced by the shipped
        /// default rather than throwing, so a corrupted file can never stop the
        /// certificates pages from rendering.
        /// </summary>
        public static List<CertificateTemplate> All()
        {
            lock (Gate)
            {
                return Load().Select(t => t.Clone()).ToList();
            }
        }

        /// <summary>
        /// The layout for a certificate name, or null when none is saved.
        /// </summary>
        public static CertificateTemplate? GetByName(string? certificateName)
        {
            if (string.IsNullOrWhiteSpace(certificateName))
            {
                return null;
            }

            var name = certificateName.Trim();

            lock (Gate)
            {
                return Load().FirstOrDefault(t =>
                    string.Equals(t.CertificateName, name, StringComparison.OrdinalIgnoreCase))?.Clone();
            }
        }

        public static CertificateTemplate? GetById(int id)
        {
            lock (Gate)
            {
                return Load().FirstOrDefault(t => t.Id == id)?.Clone();
            }
        }

        // ------------------------------------------------------------- Writes

        /// <summary>
        /// Inserts or updates a layout, assigning an Id and stamping UpdatedAt.
        /// Matching is by Id first, then by certificate name so a renamed row updates
        /// in place instead of creating a duplicate.
        /// </summary>
        public static CertificateTemplate Save(CertificateTemplate? template)
        {
            if (template == null)
            {
                throw new ArgumentNullException(nameof(template));
            }

            if (string.IsNullOrWhiteSpace(template.CertificateName))
            {
                throw new ArgumentException(
                    "CertificateName is required.", nameof(template));
            }

            template.CertificateName = template.CertificateName.Trim();
            template.TemplateHtml = template.TemplateHtml ?? "";
            template.UpdatedAt = DateTime.Now;

            lock (Gate)
            {
                var all = Load();

                var existing = template.Id > 0
                    ? all.FirstOrDefault(t => t.Id == template.Id)
                    : null;

                // CertificateName is compared with string.Equals rather than .Equals
                // because App_Data/certificate_templates.json is a hand-editable file and
                // a row with a null or missing name would otherwise throw here and take
                // the certificates pages, the template API and the fee sync down with it.
                existing ??= all.FirstOrDefault(t =>
                    string.Equals(t.CertificateName, template.CertificateName, StringComparison.OrdinalIgnoreCase));

                // A clone is stored rather than the caller's instance, so the cached
                // list and the file can never diverge through a later caller mutation.
                var stored = template.Clone();

                if (existing == null)
                {
                    stored.Id = all.Count == 0 ? 1 : all.Max(t => t.Id) + 1;

                    if (string.IsNullOrWhiteSpace(stored.Source))
                    {
                        stored.Source = "Template Editor";
                    }

                    all.Add(stored);
                }
                else
                {
                    stored.Id = existing.Id;
                    all[all.IndexOf(existing)] = stored;
                }

                Persist(all);
                return stored.Clone();
            }
        }

        /// <summary>
        /// Removes a layout by Id. The last remaining template is kept so the
        /// certificates pages always have a layout to fall back on.
        /// </summary>
        public static bool Delete(int id)
        {
            lock (Gate)
            {
                var all = Load();
                var removed = all.RemoveAll(t => t.Id == id);

                if (removed == 0)
                {
                    return false;
                }

                if (all.Count == 0)
                {
                    all.Add(SeedTemplate());
                }

                Persist(all);
                return true;
            }
        }

        /// <summary>
        /// Replaces the whole collection. Used when a fee-table save also creates
        /// certificate types, so fees and layouts stay in step.
        /// </summary>
        public static void ReplaceAll(IEnumerable<CertificateTemplate>? templates)
        {
            var incoming = templates?
                .Where(t => t != null)
                .ToList() ?? new List<CertificateTemplate>();

            lock (Gate)
            {
                var all = Load();
                var nextId = all.Count == 0 ? 1 : all.Max(t => t.Id) + 1;

                foreach (var template in incoming)
                {
                    var existing = all.FirstOrDefault(t =>
                        t.CertificateName.Equals(
                            (template.CertificateName ?? "").Trim(),
                            StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        // Never let a bulk write clobber an edited layout with a
                        // fee-only record that carries no HTML.
                        if (string.IsNullOrWhiteSpace(template.TemplateHtml)
                            && !string.IsNullOrWhiteSpace(existing.TemplateHtml))
                        {
                            continue;
                        }

                        template.Id = existing.Id;
                        all[all.IndexOf(existing)] = template;
                    }
                    else
                    {
                        template.Id = nextId++;
                        all.Add(template);
                    }
                }

                Persist(all);
            }
        }

        /// <summary>
        /// Creates a starter layout for a new certificate type, seeded with the
        /// generated skeleton for that type.
        /// </summary>
        public static CertificateTemplate CreateForType(string certificateName, decimal fee = 0m)
        {
            var name = (certificateName ?? "").Trim();

            if (name.Length == 0)
            {
                throw new ArgumentException("Certificate name is required.", nameof(certificateName));
            }

            var template = new CertificateTemplate
            {
                CertificateName = name,
                Fee = fee,
                TemplateHtml = CertificateTemplateDefaults.BuildFallbackFor(name),
                Source = "Created with certificate type"
            };

            lock (Gate)
            {
                var existing = Load().FirstOrDefault(t =>
                    t.CertificateName.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    return existing;
                }
            }

            return Save(template);
        }

        // ------------------------------------------------------------- Internals

        private static List<CertificateTemplate> Load()
        {
            if (_cache != null)
            {
                return _cache;
            }

            var path = _filePath;

            if (path == null)
            {
                // Configure has not run yet (unit test or early start-up failure).
                throw new InvalidOperationException(
                    "CertificateTemplateStore.Configure must be called during startup before the store is used.");
            }

            if (!File.Exists(path))
            {
                _cache = new List<CertificateTemplate> { SeedTemplate() };

                // Seeding is best effort. A read-only content root (the normal case in a
                // container) makes Persist throw, and the class contract is that a
                // missing or damaged file can never stop the certificates pages from
                // rendering. Serve the in-memory default instead of propagating.
                TryPersist(_cache);

                return _cache;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                _cache = JsonSerializer.Deserialize<List<CertificateTemplate>>(json, SerializerOptions)
                          ?? new List<CertificateTemplate>();

                return _cache;
            }
            catch (JsonException)
            {
                // Keep the damaged file for diagnosis instead of silently deleting it.
                TryBackupCorruptFile(path);

                _cache = new List<CertificateTemplate> { SeedTemplate() };
                TryPersist(_cache);
                return _cache;
            }
            catch (IOException)
            {
                _cache = new List<CertificateTemplate> { SeedTemplate() };
                return _cache;
            }
            catch (UnauthorizedAccessException)
            {
                // ReadAllText throws this on a locked-down ACL, and it is not an
                // IOException, so the catch above did not cover it.
                _cache = new List<CertificateTemplate> { SeedTemplate() };
                return _cache;
            }
        }

        private static void TryPersist(List<CertificateTemplate> templates)
        {
            try
            {
                Persist(templates);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Writes the collection atomically: full document to a temp file, then replace.
        /// </summary>
        private static void Persist(List<CertificateTemplate> templates)
        {
            var path = _filePath;

            if (path == null)
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(templates, SerializerOptions);
            var tempPath = path + ".tmp";

            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(path))
            {
                // File.Replace keeps a working file until the swap succeeds, but it is
                // not supported on every filesystem, so fall back to a delete + move.
                try
                {
                    File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceWithDeleteAndMove(tempPath, path);
                }
                catch (IOException)
                {
                    // File.Replace throws IOException for transient causes too - a sharing
                    // violation, an open handle, a disk hiccup - not only for "not
                    // supported here". The previous catch deleted the known-good file on
                    // any of those, and if the move then failed for the same underlying
                    // reason the data was gone: Load() silently re-seeded a default and
                    // every administrator-authored layout was lost with no error. Only
                    // fall back when the replace genuinely is not supported.
                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                        }
                        catch (PlatformNotSupportedException)
                        {
                            ReplaceWithDeleteAndMove(tempPath, path);
                        }
                    }
                    else
                    {
                        File.Move(tempPath, path);
                    }
                }
            }
            else
            {
                File.Move(tempPath, path);
            }

            _cache = templates;
        }

        private static void ReplaceWithDeleteAndMove(string tempPath, string path)
        {
            File.Delete(path);
            File.Move(tempPath, path);
        }

        private static CertificateTemplate SeedTemplate() =>
            new CertificateTemplate
            {
                Id = 1,
                CertificateName = "Barangay Indigency",
                Fee = 0m,
                TemplateHtml = CertificateTemplateDefaults.BarangayIndigencyTemplateHtml,
                Source = "Default layout",
                UpdatedAt = DateTime.Now
            };

        private static void TryBackupCorruptFile(string path)
        {
            try
            {
                var backup = Path.Combine(
                    Path.GetDirectoryName(path) ?? ".",
                    FileName + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

                File.Copy(path, backup, overwrite: false);
            }
            catch (IOException)
            {
                // A failed backup must not stop the application from starting.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Test seam: drops the in-memory cache so the next read re-hits the file.
        /// </summary>
        public static void Reload()
        {
            lock (Gate)
            {
                _cache = null;
            }
        }
    }
}
