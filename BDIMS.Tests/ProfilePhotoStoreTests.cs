using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BDIMS.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace BDIMS.Tests;

/// <summary>
/// Covers the profile-photo upload rules: which formats are accepted, what the store
/// writes, and - most importantly - that a hostile or malformed upload cannot escape
/// the photo folder or overwrite an unrelated file. These are the paths no UI click
/// reaches, so nothing else exercises them.
/// </summary>
public class ProfilePhotoStoreTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _contentRoot;

    public ProfilePhotoStoreTests()
    {
        // A throwaway sandbox per test, so the real App_Data is never touched and one
        // test's uploads cannot be observed by another.
        //
        // The sandbox has two levels: _contentRoot is handed to the store, and _sandbox
        // is the directory immediately above it. "Escaping the photo folder" means
        // writing into _sandbox, which this test exclusively owns - unlike %TEMP%
        // itself, which is shared with every other process on the machine and would
        // make the assertion depend on unrelated files happening to exist.
        _sandbox = Path.Combine(
            Path.GetTempPath(),
            "bdims-photo-tests-" + Guid.NewGuid().ToString("N"));

        _contentRoot = Path.Combine(_sandbox, "content");

        Directory.CreateDirectory(_contentRoot);

        ProfilePhotoStore.Configure(new StubEnvironment { ContentRootPath = _contentRoot });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
            {
                Directory.Delete(_sandbox, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail an otherwise-passing run.
        }
    }

    private static Stream PngBytes() => new MemoryStream(new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05
    });

    private static Stream JpegBytes() =>
        new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 });

    private static Stream WebPBytes()
    {
        var bytes = new byte[16];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);

        // Bytes 4-7 are the RIFF chunk length; deliberately left as zero.
        Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);

        return new MemoryStream(bytes);
    }

    [Fact]
    public async Task SaveAsync_StoresEachSupportedFormatUnderThePhotoFolder()
    {
        var cases = new (string FileName, Stream Bytes)[]
        {
            ("me.png", PngBytes()),
            ("me.jpg", JpegBytes()),
            ("me.jpeg", JpegBytes()),
            ("me.webp", WebPBytes())
        };

        foreach (var (fileName, bytes) in cases)
        {
            // Captured before saving: SaveAsync rewinds the stream, so the length has to
            // be read up front to prove nothing was lost or truncated by the header read
            // that inspects the signature.
            var expectedLength = bytes.Length;

            var result = await ProfilePhotoStore.SaveAsync(1, fileName, null, bytes);

            Assert.True(result.Succeeded, $"expected {fileName} to be accepted: {result.Error}");
            Assert.NotNull(result.FileName);

            // The saved name must carry the account id so the serving route can prove
            // ownership, and must sit inside the configured folder.
            Assert.StartsWith("user-photo-1-", result.FileName);

            var path = ProfilePhotoStore.ResolvePath(result.FileName);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));

            // The whole stream must survive the copy, not just the sniffed header.
            Assert.Equal(expectedLength, new FileInfo(path).Length);
        }
    }

    [Theory]
    [InlineData("me.gif")]
    [InlineData("me.bmp")]
    [InlineData("me.svg")]
    [InlineData("me.exe")]
    [InlineData("me")]
    public async Task SaveAsync_RefusesAnExtensionThatIsNotAnAllowedImageFormat(string fileName)
    {
        var result = await ProfilePhotoStore.SaveAsync(1, fileName, null, PngBytes());

        Assert.False(result.Succeeded);
        Assert.Null(result.FileName);
    }
[Fact]
    public async Task SaveAsync_RefusesAFileWhoseBytesContradictItsExtension()
    {
        // The classic attack: an HTML/SVG payload named .png. Extension-based checking
        // alone would store it and later serve it back as something a browser renders.
        var html = new MemoryStream(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"));

        var result = await ProfilePhotoStore.SaveAsync(1, "harmless.png", "image/png", html);

        Assert.False(result.Succeeded);
        Assert.Null(result.FileName);
    }

    [Fact]
    public async Task SaveAsync_RefusesANonImageContentType()
    {
        var result = await ProfilePhotoStore.SaveAsync(1, "me.png", "text/html", PngBytes());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SaveAsync_RefusesATruncatedFileThatCannotCarryASignature()
    {
        var tooShort = new MemoryStream(new byte[] { 0x89, 0x50 });

        var result = await ProfilePhotoStore.SaveAsync(1, "me.png", "image/png", tooShort);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SaveAsync_IgnoresTheSuppliedPathAndNeverWritesOutsideTheFolder()
    {
        // A traversal name must not produce a file above the photo directory. Only the
        // extension is read, so the traversal segments are discarded entirely.
        var result = await ProfilePhotoStore.SaveAsync(
            7,
            @"..\..\..\wwwroot\evil.png",
            "image/png",
            PngBytes());

        Assert.True(result.Succeeded, result.Error);

        var path = ProfilePhotoStore.ResolvePath(result.FileName);
        Assert.NotNull(path);

        var root = Path.GetFullPath(ProfilePhotoStore.RootDirectory!)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        Assert.StartsWith(root, Path.GetFullPath(path), StringComparison.Ordinal);

        // Nothing may have been created anywhere above the photo folder: not in the
        // content root, and not in the sandbox directory immediately above it (which
        // this test owns outright, so the check cannot be confused by stray files
        // left in the shared %TEMP%).
        Assert.False(File.Exists(Path.Combine(_contentRoot, "evil.png")));
        Assert.False(File.Exists(Path.Combine(_sandbox, "evil.png")));

        // Nor a wwwroot folder recreated from the traversal segments.
        Assert.False(Directory.Exists(Path.Combine(_sandbox, "wwwroot")));
    }

    [Theory]
    [InlineData("..\\..\\web.config")]
    [InlineData("/etc/passwd")]
    [InlineData("user-photo.png")]
    [InlineData("secrets.png")]
    [InlineData("user-photo-1-20260101120000000.exe")]
    public void ResolvePath_RefusesNamesTheStoreNeverProduced(string candidate)
    {
        Assert.Null(ProfilePhotoStore.ResolvePath(candidate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolvePath_RefusesEmptyNames(string candidate)
    {
        Assert.Null(ProfilePhotoStore.ResolvePath(candidate));
    }

    [Fact]
    public async Task TryDelete_RemovesAPreviousPhotoAndIgnoresForeignNames()
    {
        var first = await ProfilePhotoStore.SaveAsync(1, "a.png", null, PngBytes());
        Assert.NotNull(first.FileName);

        ProfilePhotoStore.TryDelete(first.FileName);
        Assert.False(File.Exists(ProfilePhotoStore.ResolvePath(first.FileName)!));

        // A name that is not ours must be a silent no-op rather than an attempt to
        // delete something in the application directory. Reaching the end of this test
        // without an exception is the assertion.
        ProfilePhotoStore.TryDelete("..\\..\\BDIMS.csproj");
    }

    [Fact]
    public async Task Reupload_ProducesADifferentFileNameSoCachedPhotosAreInvalidated()
    {
        // The file name feeds the badge's <img src>. Reusing it would let a browser keep
        // showing the old image from cache after the operator changed it.
        var first = await ProfilePhotoStore.SaveAsync(1, "a.png", null, PngBytes());

        // The store stamps names to the millisecond, so a same-millisecond collision is
        // possible; loop until a genuinely later stamp is produced.
        ProfilePhotoStore.SaveResult second = first;
        for (var attempt = 0; attempt < 50 && second.FileName == first.FileName; attempt++)
        {
            await Task.Delay(2);
            second = await ProfilePhotoStore.SaveAsync(1, "a.png", null, PngBytes());
        }

        Assert.NotEqual(first.FileName, second.FileName);
    }

    [Fact]
    public async Task ResolvePath_ReturnsNullOnceTheStoredFileHasBeenRemoved()
    {
        // Regression: the photo lives outside the database, so the two can drift apart -
        // a restored backup, a deployment that did not carry App_Data, or a deleted
        // file all leave UserAccounts.ProfilePhoto naming an image that no longer
        // exists. ResolvePath must then report "missing" so the serving action can
        // clear the stale reference and let the badges fall back to the initials,
        // instead of leaving a broken image on every page.
        var saved = await ProfilePhotoStore.SaveAsync(1, "a.png", null, PngBytes());
        Assert.NotNull(saved.FileName);

        Assert.NotNull(ProfilePhotoStore.ResolvePath(saved.FileName));

        File.Delete(ProfilePhotoStore.ResolvePath(saved.FileName)!);

        // Still a well-formed name - ResolvePath must not claim this is a traversal
        // attempt, it is simply a file that is gone.
        Assert.Null(ProfilePhotoStore.ResolvePath(saved.FileName));
    }

    [Theory]
    [InlineData("me.png", "image/png")]
    [InlineData("me.PNG", "image/png")]
    [InlineData("me.jpg", "image/jpeg")]
    [InlineData("me.jpeg", "image/jpeg")]
    [InlineData("me.webp", "image/webp")]
    public void ContentTypeFor_MapsEachStoredExtensionToItsMediaType(string name, string expected)
    {
        Assert.Equal(expected, ProfilePhotoStore.ContentTypeFor(name));
    }

    [Fact]
    public void AcceptAttribute_ListsExactlyTheFourSupportedExtensions()
    {
        // Drives both the picker filter and the client-side pre-check, so it must stay
        // in step with the formats SaveAsync actually accepts.
        Assert.Equal(".png,.jpg,.jpeg,.webp", ProfilePhotoStore.AcceptAttribute);
    }

    /// <summary>Minimal host used only to supply a content root to the store.</summary>
    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Test";
        public string WebServerUrl { get; set; } = string.Empty;
    }
}