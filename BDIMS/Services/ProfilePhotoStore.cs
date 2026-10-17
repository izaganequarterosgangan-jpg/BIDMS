using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;

namespace BDIMS.Services
{
    /// <summary>
    /// File-backed storage for the profile photographs uploaded from My Profile.
    ///
    /// Images are written to &lt;ContentRoot&gt;/App_Data/profile_photos, deliberately
    /// outside <c>wwwroot</c>. These are staff photographs, and anything under
    /// <c>wwwroot</c> is served to anyone who can guess the URL, so the bytes are read
    /// back only through the authorised <c>AccountController.ProfilePhoto</c> action.
    ///
    /// Every file name is generated here from the account id and the current UTC time.
    /// The browser's <c>FileName</c> is inspected only to learn the extension and is
    /// never concatenated into the path, so neither a <c>..\..\web.config</c> name nor
    /// an absolute path can escape the photo folder.
    /// </summary>
    public static class ProfilePhotoStore
    {
        /// <summary>Folder under App_Data that holds the uploaded images.</summary>
        public const string FolderName = "profile_photos";

        /// <summary>
        /// Upper bound on an upload. Comfortably above a phone camera photo and well
        /// below the size at which a single request becomes a denial-of-service lever.
        /// </summary>
        public const long MaxBytes = 5L * 1024 * 1024;

        /// <summary>
        /// Prefix every generated file name carries. <see cref="ResolvePath"/> checks
        /// it so only files this store wrote can ever be served.
        /// </summary>
        private const string NamePrefix = "user-photo-";

        /// <summary>
        /// The accepted image formats, keyed by the extension the upload is saved
        /// under. Deliberately limited to the four raster formats named in the UI:
        /// anything able to carry script (SVG) or a document is refused outright
        /// rather than filtered.
        /// </summary>
        private static readonly (string Extension, string ContentType)[] AllowedFormats =
        {
            (".png", "image/png"),
            (".jpg", "image/jpeg"),
            (".jpeg", "image/jpeg"),
            (".webp", "image/webp")
        };

        private static readonly object Gate = new object();
        private static string? _rootDirectory;

        /// <summary>
        /// Binds the store to a content root. Called once during startup, alongside
        /// <see cref="CertificateTemplateStore.Configure"/>.
        /// </summary>
        public static void Configure(IWebHostEnvironment environment)
        {
            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            lock (Gate)
            {
                _rootDirectory = Path.Combine(
                    environment.ContentRootPath,
                    CertificateTemplateStore.AppDataFolderName,
                    FolderName);
            }
        }

        /// <summary>
        /// Absolute path of the photo folder, or <c>null</c> before
        /// <see cref="Configure"/> has run.
        /// </summary>
        public static string? RootDirectory
        {
            get
            {
                lock (Gate)
                {
                    return _rootDirectory;
                }
            }
        }

        /// <summary>
        /// Extensions offered to the file picker and echoed in the <c>accept</c>
        /// attribute, as bare extensions so the attribute stays valid HTML.
        /// </summary>
        public static string AcceptAttribute =>
            string.Join(",", AllowedFormats.Select(format => format.Extension));

        /// <summary>Media type recorded for a saved file, based on its extension.</summary>
        public static string ContentTypeFor(string fileName) =>
            AllowedFormats
                .FirstOrDefault(format => string.Equals(
                    format.Extension,
                    Path.GetExtension(fileName),
                    StringComparison.OrdinalIgnoreCase))
                .ContentType ?? "application/octet-stream";
/// <summary>
        /// Outcome of a save attempt: either the generated file name, or the reason the
        /// upload was refused. Returned rather than thrown because every refusal is a
        /// user-correctable condition the controller shows as a form message.
        /// </summary>
        public readonly struct SaveResult
        {
            private SaveResult(string? fileName, string? error)
            {
                FileName = fileName;
                Error = error;
            }

            /// <summary>Generated name of the stored file, or null on failure.</summary>
            public string? FileName { get; }

            /// <summary>Message to show the operator, or null on success.</summary>
            public string? Error { get; }

            public bool Succeeded => Error is null;

            public static SaveResult Ok(string fileName) => new SaveResult(fileName, null);

            public static SaveResult Fail(string error) => new SaveResult(null, error);
        }

        /// <summary>
        /// Validates and stores an uploaded image for <paramref name="userId"/>. The
        /// caller deletes the previous photo once this has succeeded.
        ///
        /// Validation is content-based as well as extension-based: the leading bytes
        /// have to match the format the extension claims. The extension alone would let
        /// a file named <c>me.png</c> actually be an HTML page, which the browser would
        /// then render for anyone who requested it.
        /// </summary>
        /// <param name="userId">Account the photo belongs to.</param>
        /// <param name="originalFileName">
        /// Browser-supplied name. Read only for its extension; never used as a path.
        /// </param>
        /// <param name="declaredContentType">Browser-supplied media type, or null.</param>
        /// <param name="stream">Uploaded bytes. Read once, from position 0.</param>
        /// <param name="cancellationToken">Cancels a long upload mid-copy.</param>
        public static async Task<SaveResult> SaveAsync(
            int userId,
            string? originalFileName,
            string? declaredContentType,
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            var root = RootDirectory;

            if (root is null)
            {
                // Reaching this means Configure was not called at startup, which is a
                // programming error rather than anything the operator can fix.
                return SaveResult.Fail(
                    "Photo storage is not available. Please try again later.");
            }

            if (stream is null)
            {
                return SaveResult.Fail("Please choose a photo to upload.");
            }

            var extension = Path.GetExtension(originalFileName ?? string.Empty);

            var format = AllowedFormats.FirstOrDefault(candidate => string.Equals(
                candidate.Extension,
                extension,
                StringComparison.OrdinalIgnoreCase));

            if (format.Extension is null)
            {
                return SaveResult.Fail(
                    "Please choose a .png, .jpg, .jpeg or .webp image file.");
            }

            // The declared media type is a hint from the browser and is trivially
            // spoofed, so it is only used to catch an obviously wrong pick early; the
            // signature check below is what actually decides. Some systems report a
            // generic type for .webp, so a mismatch is not fatal on its own.
            if (!string.IsNullOrWhiteSpace(declaredContentType)
                && declaredContentType.IndexOf("image", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return SaveResult.Fail(
                    "That file is not an image. Please choose an image file.");
            }

            if (!HasExpectedSignature(stream, format.Extension))
            {
                return SaveResult.Fail(
                    "That file does not appear to be a valid "
                        + format.Extension.TrimStart('.').ToUpperInvariant()
                        + " image.");
            }
Directory.CreateDirectory(root);

            // Generated name only. The timestamp gives each re-upload a new URL, so a
            // browser that cached the previous photo cannot keep showing the old face
            // after the operator changes it.
            var fileName = string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1}-{2}{3}",
                NamePrefix,
                userId,
                DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
                format.Extension);

            var fullPath = Path.Combine(root, fileName);

            await using (var target = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                stream.Position = 0;
                await stream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            return SaveResult.Ok(fileName);
        }

        /// <summary>
        /// Absolute path of a stored photo, or <c>null</c> when the name is not one this
        /// store produced or the file is no longer there.
        ///
        /// The name is reduced to its bare file name and matched against the prefix
        /// written by <see cref="SaveAsync"/> before being combined with the known
        /// folder, so a value that somehow reached the database still cannot traverse
        /// out of the photo directory. Existence is checked here too: the photo lives
        /// outside the database, so the two can drift apart (a restored backup, a
        /// deployment that did not carry App_Data, a manually deleted file). Reporting
        /// "missing" through the same <c>null</c> keeps that one check in one place
        /// instead of duplicated - and eventually forgotten - at each call site.
        /// </summary>
        public static string? ResolvePath(string? fileName)
        {
            var root = RootDirectory;

            if (root is null || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var name = Path.GetFileName(fileName);

            if (!string.Equals(name, fileName, StringComparison.Ordinal)
                || !name.StartsWith(NamePrefix, StringComparison.Ordinal)
                || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, name));
            var rootWithSeparator = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                return null;
            }

            return File.Exists(fullPath) ? fullPath : null;
        }

        /// <summary>
        /// Best-effort removal of a previously stored photo. A failure here (the file is
        /// locked, the disk is read-only) must not fail the new upload, so the error is
        /// swallowed: the worst outcome is one orphaned image on disk.
        /// </summary>
        public static void TryDelete(string? fileName)
        {
            var path = ResolvePath(fileName);

            if (path is null)
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Left behind on purpose; the new photo is already saved.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Checks the file's leading bytes against the format its extension claims, then
        /// rewinds so the caller can copy the whole stream.
        /// </summary>
        private static bool HasExpectedSignature(Stream stream, string extension)
        {
            // Longest signature checked is RIFF/WEBP, which needs twelve bytes.
            Span<byte> header = stackalloc byte[12];
            var read = 0;

            stream.Position = 0;

            while (read < header.Length)
            {
                var chunk = stream.Read(header[read..]);

                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            stream.Position = 0;

            if (read < 4)
            {
                return false;
            }

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                return read >= 8
                    && header[0] == 0x89 && header[1] == 0x50
                    && header[2] == 0x4E && header[3] == 0x47
                    && header[4] == 0x0D && header[5] == 0x0A
                    && header[6] == 0x1A && header[7] == 0x0A;
            }

            // JPEG: FF D8 FF
            if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
            }

            // WebP: "RIFF" <4-byte length> "WEBP"
            if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
            {
                return read >= 12
                    && header[0] == (byte)'R' && header[1] == (byte)'I'
                    && header[2] == (byte)'F' && header[3] == (byte)'F'
                    && header[8] == (byte)'W' && header[9] == (byte)'E'
                    && header[10] == (byte)'B' && header[11] == (byte)'P';
            }

            return false;
        }
    }
}