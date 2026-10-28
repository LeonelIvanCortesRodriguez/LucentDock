using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Strata.Core;

/// <summary>
/// What a release folder offers (its <c>update.json</c>, written by tools/make-release.ps1): the version, the file,
/// its SHA-256 and size, and optional notes.
/// </summary>
public sealed record UpdateManifest(string Version, string File, string Sha256, long Size, string? Notes = null);

public enum UpdateState { Off, UpToDate, Available, Invalid, Unreachable }

public sealed record UpdateCheck(UpdateState State, UpdateManifest? Manifest, string Message);

public interface IUpdateStagingIO
{
    void CreateDirectory(string path);
    void CopyFile(string source, string destination);
    Stream CreateFile(string path);
    Stream OpenRead(string path);
    void DeleteDirectory(string path);
}

public sealed class RealUpdateStagingIO : IUpdateStagingIO
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void CopyFile(string source, string destination) => File.Copy(source, destination);
    public Stream CreateFile(string path) => File.Create(path);
    public Stream OpenRead(string path) => File.OpenRead(path);
    public void DeleteDirectory(string path) => Directory.Delete(path, true);
}

/// <summary>
/// Updates from a release source the user chooses (Settings › About): a folder, or an https address such as a GitHub
/// repository ("https://github.com/owner/repo", read as its latest release's update.json). Nothing is fetched unless a
/// source is set. A newer version is copied or downloaded into a new temporary folder, its size and SHA-256 checked
/// against update.json, and, when the running Strata is signed, its signature must be valid and from the same signer.
/// Then the copy runs its own verified installer (<c>--install</c>), which replaces the running dock.
/// </summary>
public static class Updater
{
    public const string ManifestName = "update.json";
    /// <summary>update.json is a few hundred bytes; anything far larger is not one.</summary>
    public const int MaxManifestBytes = 64 * 1024;
    /// <summary>A corrupt manifest must not authorize an effectively unbounded download or disk write.</summary>
    public const long MaxReleaseBytes = 512L * 1024 * 1024;
    private const string StagePrefix = "Strata-update-";

    /// <summary>True when the source is a web address rather than a folder.</summary>
    public static bool IsWeb(string? source) =>
        source is not null && (source.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                               || source.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The update.json address for a web source, or null when it isn't one Strata accepts (only https):
    /// a GitHub repository becomes its latest release's update.json; an address ending in .json is used as it is;
    /// any other address is a folder of release files, with update.json inside.
    /// </summary>
    public static Uri? ManifestUri(string? source)
    {
        if (!IsWeb(source)) return null;
        string s = source!.Trim();
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 || (parts.Length == 3 && parts[2] == "releases"))
            {
                string repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
                return new Uri($"https://github.com/{parts[0]}/{repo}/releases/latest/download/{ManifestName}");
            }
        }
        if (uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return uri;
        return new Uri(uri, uri.AbsolutePath.EndsWith('/') ? ManifestName : uri.AbsolutePath.Split('/')[^1] + "/" + ManifestName);
    }

    /// <summary>Where a web release's file is: next to its update.json.</summary>
    public static Uri FileUri(Uri manifest, UpdateManifest m) => new(manifest, Uri.EscapeDataString(m.File));

    /// <summary>A client for update checks: https only, a short timeout, and Strata's name and version.</summary>
    public static HttpClient CreateClient(string version)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
            { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Strata/{Short(version)}");
        return http;
    }

    /// <summary>Check a folder or a web source. A web check downloads update.json only.</summary>
    public static async Task<UpdateCheck> CheckAsync(string? source, string currentVersion, HttpClient http, CancellationToken cancel = default)
    {
        if (!IsWeb(source)) return Check(source, currentVersion);
        if (ManifestUri(source) is not { } uri) return new(UpdateState.Invalid, null, "Use an https:// address for updates.");
        string json;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.RequestMessage?.RequestUri?.Scheme is { } scheme && scheme != Uri.UriSchemeHttps)
                return new(UpdateState.Invalid, null, "The release address redirected away from https.");
            if (!response.IsSuccessStatusCode)
                return new(UpdateState.Unreachable, null, response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? $"No {ManifestName} at the release address (is there a published release?)."
                    : $"The release address answered {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaxManifestBytes) return new(UpdateState.Invalid, null, $"{ManifestName} is too large to be one.");
            var bytes = await ReadAtMost(response, MaxManifestBytes, timeout.Token);
            if (bytes is null) return new(UpdateState.Invalid, null, $"{ManifestName} is too large to be one.");
            json = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(UpdateState.Unreachable, null, "The release address couldn't be reached. Strata will try again later.");
        }
        var m = Parse(json);
        if (m is null) return new(UpdateState.Invalid, null, $"{ManifestName} is not a valid release description.");
        return Compare(m.Version, currentVersion) > 0
            ? new(UpdateState.Available, m, $"Strata {Short(m.Version)} is ready to install.")
            : new(UpdateState.UpToDate, m, $"Up to date ({Short(currentVersion)}).");
    }

    private static async Task<byte[]?> ReadAtMost(HttpResponseMessage response, int max, CancellationToken cancel)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Compare two versions by their "major.minor.patch" core; build metadata after '+' is ignored.</summary>
    public static int Compare(string a, string b)
    {
        static int[] Core(string v)
        {
            var core = v.Split('+')[0].Split('-')[0].Split('.');
            return [.. Enumerable.Range(0, 3).Select(i => i < core.Length && int.TryParse(core[i], out var n) ? n : 0)];
        }
        var (x, y) = (Core(a), Core(b));
        for (int i = 0; i < 3; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    public static UpdateManifest? Parse(string json)
    {
        // An optional UTF-8 byte-order mark: Windows PowerShell writes one, and a web download keeps it (a folder read
        // already drops it).
        json = json.TrimStart('\uFEFF');
        try
        {
            var m = JsonSerializer.Deserialize<UpdateManifest>(json, Storage.Json);
            return m is { Version.Length: > 0, File.Length: > 0, Sha256.Length: 64, Size: > 0 }
                && m.Size <= MaxReleaseBytes && m.Sha256.All(Uri.IsHexDigit)
                && !m.File.Contains('\\') && !m.File.Contains('/') && !m.File.Contains("..") ? m : null;
        }
        catch (JsonException) { return null; }
    }

    public static UpdateCheck Check(string? folder, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new(UpdateState.Off, null, "Updates are off: no release folder is set.");
        string path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path)) return new(UpdateState.Unreachable, null, $"No {ManifestName} in the release folder.");
        UpdateManifest? m;
        try
        {
            string? json = ReadLocalTextAtMost(path, MaxManifestBytes);
            if (json is null) return new(UpdateState.Invalid, null, $"{ManifestName} is too large to be one.");
            m = Parse(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(UpdateState.Unreachable, null, $"The release folder couldn't be read: {ex.Message}");
        }
        if (m is null) return new(UpdateState.Invalid, null, $"{ManifestName} is not a valid release description.");
        var file = new FileInfo(Path.Combine(folder, m.File));
        if (!file.Exists || file.Length != m.Size) return new(UpdateState.Invalid, m, $"{m.File} is missing or not the size {ManifestName} gives.");
        return Compare(m.Version, currentVersion) > 0
            ? new(UpdateState.Available, m, $"Strata {Short(m.Version)} is ready to install.")
            : new(UpdateState.UpToDate, m, $"Up to date ({Short(currentVersion)}).");
    }

    /// <summary>
    /// Shown while the running Strata is unsigned: updates are then an unsigned preview channel. The SHA-256 comes
    /// from the same update.json as the file, so it proves the download arrived intact, not who published it.
    /// </summary>
    public const string UnsignedWarning = "Unsigned preview channel: Strata checks each download against the SHA-256 in the same update.json. "
        + "That proves the file arrived intact, not who published it. Install only from a source you trust.";

    /// <summary>True when this Strata is unsigned, so the same-signer check can't apply to updates.</summary>
    public static bool RunningUnsigned(string? runningExe) => runningExe is null || Signer(runningExe) is null;

    /// <summary>"1.2.1" from "1.2.1+commit".</summary>
    public static string Short(string version) => version.Split('+')[0];

    /// <summary>
    /// Copy the release into a new temporary folder and check it there (so the folder can't change it afterwards).
    /// Returns the checked copy, or null and why not.
    /// </summary>
    public static (string? Path, string Message) Stage(string folder, UpdateManifest m, string? runningExe, string tempRoot,
        IUpdateStagingIO? stagingIO = null)
    {
        stagingIO ??= new RealUpdateStagingIO();
        if (m.Size <= 0 || m.Size > MaxReleaseBytes)
            return (null, "The release size is outside Strata's bounded staging limit; nothing was installed.");
        string dir = Path.Combine(tempRoot, StagePrefix + Guid.NewGuid().ToString("N"));
        try
        {
            stagingIO.CreateDirectory(dir);
            string staged = Path.Combine(dir, "Strata.exe");
            if (!CopyExactSize(Path.Combine(folder, m.File), staged, m.Size, stagingIO))
            {
                TryDelete(dir, stagingIO);
                return (null, "The release file isn't the size update.json gives; nothing was installed.");
            }
            return Verify(dir, staged, m, runningExe, stagingIO);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(dir, stagingIO);
            return (null, "The release couldn't be staged on this disk; nothing was installed.");
        }
    }

    /// <summary>
    /// Download a web release into a new temporary folder and check it there. The download stops as soon as it is
    /// larger than update.json says, and the copy must then match its size, SHA-256 and signer exactly.
    /// </summary>
    public static async Task<(string? Path, string Message)> StageAsync(string source, UpdateManifest m, string? runningExe, string tempRoot,
        HttpClient http, CancellationToken cancel = default, IUpdateStagingIO? stagingIO = null)
    {
        stagingIO ??= new RealUpdateStagingIO();
        if (m.Size <= 0 || m.Size > MaxReleaseBytes)
            return (null, "The release size is outside Strata's bounded staging limit; nothing was installed.");
        if (!IsWeb(source)) return Stage(source, m, runningExe, tempRoot, stagingIO);
        if (ManifestUri(source) is not { } manifest) return (null, "Use an https:// address for updates.");
        string dir = Path.Combine(tempRoot, StagePrefix + Guid.NewGuid().ToString("N"));
        string staged = Path.Combine(dir, "Strata.exe");
        try
        {
            stagingIO.CreateDirectory(dir);
            using var response = await http.GetAsync(FileUri(manifest, m), HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.RequestMessage?.RequestUri?.Scheme is { } scheme && scheme != Uri.UriSchemeHttps)
            {
                TryDelete(dir, stagingIO);
                return (null, "The download redirected away from https; nothing was installed.");
            }
            if (!response.IsSuccessStatusCode)
            {
                TryDelete(dir, stagingIO);
                return (null, $"The release file couldn't be downloaded ({(int)response.StatusCode}); nothing was installed.");
            }
            await using (var from = await response.Content.ReadAsStreamAsync(cancel))
            await using (var to = stagingIO.CreateFile(staged))
            {
                var chunk = new byte[81920];
                long total = 0;
                int read;
                while ((read = await from.ReadAsync(chunk, cancel)) > 0)
                {
                    total += read;
                    if (total > m.Size) break;
                    await to.WriteAsync(chunk.AsMemory(0, read), cancel);
                }
                if (total != m.Size)
                {
                    to.Close();
                    TryDelete(dir, stagingIO);
                    return (null, "The downloaded file isn't the size update.json gives; nothing was installed.");
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            TryDelete(dir, stagingIO);
            return (null, "The release couldn't be downloaded; nothing was installed. Try again later.");
        }
        return Verify(dir, staged, m, runningExe, stagingIO);
    }

    /// <summary>The staged copy must match update.json's SHA-256 and, for a signed Strata, its signer.</summary>
    private static (string? Path, string Message) Verify(string dir, string staged, UpdateManifest m, string? runningExe,
        IUpdateStagingIO stagingIO)
    {
        string? hash;
        try { using var s = stagingIO.OpenRead(staged); hash = HashExactSize(s, m.Size); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(dir, stagingIO);
            return (null, "The staged release couldn't be read back safely; nothing was installed.");
        }
        if (hash is null)
        {
            TryDelete(dir, stagingIO);
            return (null, "The staged release isn't the size update.json gives; nothing was installed.");
        }
        if (!string.Equals(hash, m.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(dir, stagingIO);
            return (null, "The release doesn't match its SHA-256 in update.json; nothing was installed.");
        }
        // Signed builds only update to builds signed by the same signer, with a valid signature.
        if (runningExe is not null && Signer(runningExe) is { } current)
        {
            var next = Signer(staged);
            if (!SignerAccepted(current, next, next is not null && SignatureValid(staged)))
            {
                TryDelete(dir, stagingIO);
                return (null, "The release isn't signed by the same publisher as this Strata; nothing was installed.");
            }
        }
        return (staged, "Checked.");
    }

    private static bool CopyExactSize(string source, string destination, long expected, IUpdateStagingIO stagingIO)
    {
        using Stream from = stagingIO.OpenRead(source);
        using Stream to = stagingIO.CreateFile(destination);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int request = (int)Math.Min(buffer.Length, expected - total + 1);
            int read = from.Read(buffer, 0, request);
            if (read == 0) break;
            total += read;
            if (total > expected) return false;
            to.Write(buffer, 0, read);
        }
        return total == expected;
    }

    private static string? HashExactSize(Stream stream, long expected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int request = (int)Math.Min(buffer.Length, expected - total + 1);
            int read = stream.Read(buffer, 0, request);
            if (read == 0) break;
            total += read;
            if (total > expected) return null;
            hash.AppendData(buffer, 0, read);
        }
        return total == expected ? Convert.ToHexString(hash.GetHashAndReset()) : null;
    }

    /// <summary>Run the checked copy's transactional installer, retaining a verified last-known-good build.</summary>
    public static bool Install(string staged)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(staged, "--apply-update") { UseShellExecute = false });
            if (process is not null) return true;
        }
        catch (Exception ex) { Log.Write($"Starting the update installer failed: {ex.Message}"); }
        string? directory = Path.GetDirectoryName(staged);
        if (directory is not null) CleanupOwnedStage(directory, Path.GetTempPath());
        return false;
    }

    /// <summary>Pure same-publisher rule for deterministic unsigned, wrong-signer and invalid-signature tests.</summary>
    public static bool SignerAccepted(string? currentSigner, string? candidateSigner, bool candidateSignatureValid) =>
        currentSigner is null || (candidateSignatureValid && candidateSigner is not null
            && string.Equals(candidateSigner, currentSigner, StringComparison.OrdinalIgnoreCase));

    private static void TryDelete(string dir, IUpdateStagingIO stagingIO) { try { stagingIO.DeleteDirectory(dir); } catch (Exception) { } }

    /// <summary>Only a direct temp-root child with Strata's exact GUID stage name is ever eligible for recursive cleanup.</summary>
    public static bool IsOwnedStageDirectory(string? directory, string tempRoot)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            string root = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileName(full);
            string suffix = name.StartsWith(StagePrefix, StringComparison.Ordinal) ? name[StagePrefix.Length..] : "";
            return suffix.Length == 32 && suffix.All(Uri.IsHexDigit);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool CleanupOwnedStage(string directory, string tempRoot)
    {
        if (!IsOwnedStageDirectory(directory, tempRoot)) return false;
        try { Directory.Delete(directory, recursive: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return false; }
    }

    /// <summary>Bounded next-start recovery for a cleanup helper that was interrupted.</summary>
    public static int ScavengeStaleStages(string tempRoot, DateTime utcNow, int maximum = 4)
    {
        int removed = 0;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(tempRoot, StagePrefix + "*").Take(64))
            {
                if (removed >= Math.Clamp(maximum, 0, 16)) break;
                if (!IsOwnedStageDirectory(directory, tempRoot)) continue;
                if (utcNow - Directory.GetCreationTimeUtc(directory) < TimeSpan.FromDays(1)) continue;
                if (CleanupOwnedStage(directory, tempRoot)) removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        return removed;
    }

    public static bool RunStageCleanup(int parentProcessId, string directory)
    {
        if (parentProcessId <= 0 || !IsOwnedStageDirectory(directory, Path.GetTempPath())) return false;
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            if (!parent.WaitForExit(15000)) return false;
        }
        catch (ArgumentException) { }
        return CleanupOwnedStage(directory, Path.GetTempPath());
    }

    private static string? ReadLocalTextAtMost(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes) return null;
        using var buffer = new MemoryStream((int)Math.Min(stream.Length, maxBytes));
        var chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The thumbprint of the certificate that signed <paramref name="file"/>, or null if unsigned.</summary>
    public static string? Signer(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057 // reads the Authenticode signer; there is no loader equivalent for signed files
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.Thumbprint;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- WinVerifyTrust: is the Authenticode signature valid and trusted on this PC? ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr File, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool SignatureValid(string file)
    {
        var info = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = file };
        IntPtr infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,          // WTD_UI_NONE
                RevocationChecks = 0,  // WTD_REVOKE_NONE
                UnionChoice = 1,       // WTD_CHOICE_FILE
                FileInfo = infoPtr,
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(infoPtr);
            Marshal.FreeHGlobal(infoPtr);
        }
    }
}
