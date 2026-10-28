using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Strata.Core;

/// <summary>
/// Evidence for the Phase B keyboard gate: an append-only log of the commands the dock itself carried out in
/// keyboard mode (focus, navigation, flyout open/close, focus return, exit). It records what Strata did, never
/// keystrokes: there is no keyboard hook. It is written only when STRATA_PHASEB_COMMAND_LOG=1 and storage is a
/// sandbox (STRATA_DATA_ROOT), so a normal install never writes it. Every value is normalized to a short token
/// (e.g. "2:widget:media", "app" for any pinned or running app), so the log names no app, window or user data.
/// </summary>
public sealed partial class PhaseBCommandLog
{
    public const string EnvironmentVariable = "STRATA_PHASEB_COMMAND_LOG";
    public const string FileName = "phaseb-commands.log";
    public const string Format = "strata-phaseb-commands/1";
    /// <summary>Written in place of any value that is not a plain token, so free text can never reach the log.</summary>
    public const string Invalid = "invalid";

    private static PhaseBCommandLog? _current;
    private readonly string? _path;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _sequence;

    /// <summary>The process-wide log: enabled only for a Phase B sandbox run.</summary>
    public static PhaseBCommandLog Current => _current ??= new PhaseBCommandLog(
        ShouldEnable(Environment.GetEnvironmentVariable(EnvironmentVariable), Storage.UsingDefaultRoot) ? Storage.Root : null,
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");

    /// <param name="directory">Where to write the log, or null for a disabled log that writes nothing.</param>
    public PhaseBCommandLog(string? directory, string binary)
    {
        if (directory is null) return;
        _path = Path.Combine(directory, FileName);
        Append($"# {Format} binary={Token(binary)} pid={Environment.ProcessId}");
    }

    public bool Enabled => _path is not null;

    /// <summary>Only an explicit "1" turns it on, and never against the real data folder.</summary>
    public static bool ShouldEnable(string? flag, bool usingDefaultRoot) => flag == "1" && !usingDefaultRoot;

    /// <summary>Appends "sequence elapsed-ms command key=value ...". Disabled logs ignore it.</summary>
    public void Write(string command, params (string Key, string Value)[] fields)
    {
        if (_path is null) return;
        var line = new StringBuilder();
        line.Append(++_sequence).Append(' ').Append(_clock.ElapsedMilliseconds).Append(' ').Append(Token(command));
        foreach (var (key, value) in fields) line.Append(' ').Append(Token(key)).Append('=').Append(Token(value));
        Append(line.ToString());
    }

    /// <summary>A dock item as "index:role": role is start, search, widget:&lt;id&gt;, app or other; never a name.</summary>
    public static string Item(int index, string role) => index < 0 ? "none" : $"{index}:{Token(role)}";

    /// <summary>The value itself if it is a plain token (letters, digits and : . _ + # -), otherwise <see cref="Invalid"/>.</summary>
    public static string Token(string? value) =>
        value is { Length: > 0 and <= 80 } && TokenPattern().IsMatch(value) ? value : Invalid;

    private void Append(string line)
    {
        try { File.AppendAllText(_path!, line + Environment.NewLine); }
        catch { /* evidence logging must never crash the dock; a gap in sequence numbers fails the gate */ }
    }

    [GeneratedRegex("^[A-Za-z0-9:._+#-]+$")]
    private static partial Regex TokenPattern();
}
