using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Strata.Core;

/// <summary>
/// All data lives under one root: %APPDATA%\Strata by default, or the folder in the STRATA_DATA_ROOT
/// environment variable (used by tests and audits). Preview mode always uses a private sandbox and
/// <see cref="PrivacyGuard"/> refuses any access to the real root while it runs.
/// </summary>
public static class Storage
{
    public static readonly string DefaultRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Strata");

    public static string Root { get; private set; } = ResolveInitialRoot();

    private static string ResolveInitialRoot()
    {
        string? env = Environment.GetEnvironmentVariable("STRATA_DATA_ROOT");
        return string.IsNullOrWhiteSpace(env) ? DefaultRoot : Path.GetFullPath(env);
    }

    /// <summary>Point storage at another folder (preview sandbox). Must happen before anything is loaded.</summary>
    public static void Configure(string root) => Root = Path.GetFullPath(root);

    public static bool UsingDefaultRoot =>
        string.Equals(Path.GetFullPath(Root).TrimEnd('\\'), Path.GetFullPath(DefaultRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string name)
    {
        PrivacyGuard.ThrowIfPreviewTouches(Root);
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, name);
    }

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            string path = PathFor(name);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
        }
        catch (PrivacyViolationException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"Could not read {name}: {ex.Message}");
        }
        return new T();
    }

    /// <summary>Write via a temp file so a crash mid-write never corrupts existing data.</summary>
    public static void Save<T>(string name, T value) => TrySave(name, value);

    /// <summary>Atomic save with an explicit durability result for safety-critical callers.</summary>
    public static bool TrySave<T>(string name, T value)
    {
        try
        {
            string path = PathFor(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            DurableFile.WriteAllTextAtomic(path, JsonSerializer.Serialize(value, Json));
            return true;
        }
        catch (PrivacyViolationException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"Could not save {name}: {ex.Message}");
            return false;
        }
    }
}

public interface IDurableFileIO
{
    Stream CreateWriteThrough(string path);
    void FlushToDisk(Stream stream);
    void ReplaceWriteThrough(string source, string destination);
    bool FileExists(string path);
    void DeleteFile(string path);
}

public sealed class WindowsDurableFileIO : IDurableFileIO
{
    private const uint MoveFileReplaceExisting = 0x1, MoveFileWriteThrough = 0x8;

    public Stream CreateWriteThrough(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 4096, FileOptions.WriteThrough);
    public void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);
    public void ReplaceWriteThrough(string source, string destination)
    {
        if (!MoveFileEx(source, destination, MoveFileReplaceExisting | MoveFileWriteThrough))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The durable file replacement failed.");
    }
    public bool FileExists(string path) => File.Exists(path);
    public void DeleteFile(string path) => File.Delete(path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}

/// <summary>Flushes file contents and the replacing rename before reporting a safety-critical save as durable.</summary>
public static class DurableFile
{
    public static void WriteAllTextAtomic(string path, string contents, IDurableFileIO? fileIO = null)
    {
        fileIO ??= new WindowsDurableFileIO();

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (Stream stream = fileIO.CreateWriteThrough(temporary))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true))
            {
                writer.Write(contents);
                writer.Flush();
                fileIO.FlushToDisk(stream);
            }
            fileIO.ReplaceWriteThrough(temporary, path);
        }
        finally
        {
            try { if (fileIO.FileExists(temporary)) fileIO.DeleteFile(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string path = Storage.PathFor("strata.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the dock */ }
    }
}

public sealed class PrivacyViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// While previews render, nothing may read live, personal state: the user's data folder, open windows,
/// screen time, audio devices, media sessions or the network. Code that reaches those calls
/// <see cref="ThrowIfPreview"/>, so a mistake fails loudly (and fails the preview run) instead of leaking.
/// </summary>
public static class PrivacyGuard
{
    public static bool PreviewActive { get; private set; }

    public static void EnterPreview() => PreviewActive = true;

    public static void ThrowIfPreview(string what)
    {
        if (PreviewActive) throw new PrivacyViolationException($"Preview mode must not access {what}.");
    }

    public static void ThrowIfPreviewTouches(string root)
    {
        if (PreviewActive && string.Equals(Path.GetFullPath(root).TrimEnd('\\'), Path.GetFullPath(Storage.DefaultRoot).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            throw new PrivacyViolationException("Preview mode must not use the real Strata data folder.");
    }
}
