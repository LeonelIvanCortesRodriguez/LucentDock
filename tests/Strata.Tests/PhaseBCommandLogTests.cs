using Strata.Core;

namespace Strata.Tests;

/// <summary>
/// The Phase B command log is the keyboard gate's evidence. It must stay off outside a Phase B sandbox and must
/// never carry free text (app names, track titles, paths), only normalized tokens.
/// </summary>
[Trait("Category", "Unit")]
public class PhaseBCommandLogTests
{
    [Theory]
    [InlineData("1", false, true)]
    [InlineData("1", true, false)]      // never against the real %APPDATA%\Strata
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("true", false, false)]  // only an explicit "1"
    [InlineData("0", false, false)]
    public void Enabled_only_by_the_flag_in_a_sandbox(string? flag, bool usingDefaultRoot, bool expected) =>
        Assert.Equal(expected, PhaseBCommandLog.ShouldEnable(flag, usingDefaultRoot));

    [Fact]
    public void Disabled_log_writes_nothing()
    {
        var log = new PhaseBCommandLog(null, "1.1.0+abc");
        log.Write("nav", ("dir", "next"));
        Assert.False(log.Enabled);
    }

    [Fact]
    public void Writes_a_header_then_numbered_commands()
    {
        string dir = NewDir();
        var log = new PhaseBCommandLog(dir, "1.1.0+25fcc4bc38fe");
        log.Write("dock.focus", ("source", "hotkey"), ("item", PhaseBCommandLog.Item(0, "start")));
        log.Write("nav", ("dir", "next"), ("from", "0:start"), ("to", PhaseBCommandLog.Item(1, "search")));
        var lines = File.ReadAllLines(Path.Combine(dir, PhaseBCommandLog.FileName));
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^# strata-phaseb-commands/1 binary=1\.1\.0\+25fcc4bc38fe pid=\d+$", lines[0]);
        Assert.Matches(@"^1 \d+ dock\.focus source=hotkey item=0:start$", lines[1]);
        Assert.Matches(@"^2 \d+ nav dir=next from=0:start to=1:search$", lines[2]);
    }

    [Theory]
    [InlineData("Spotify - My Song")]
    [InlineData(@"C:\Users\someone\Music")]
    [InlineData("name=value")]
    [InlineData("")]
    [InlineData(null)]
    public void Free_text_never_reaches_the_log(string? value)
    {
        string dir = NewDir();
        var log = new PhaseBCommandLog(dir, "1.1.0");
        log.Write("flyout.open", ("item", value!));
        string written = File.ReadAllLines(Path.Combine(dir, PhaseBCommandLog.FileName))[1];
        Assert.EndsWith(" flyout.open item=invalid", written);
        if (!string.IsNullOrEmpty(value)) Assert.DoesNotContain(value, written);
    }

    [Fact]
    public void Items_are_position_and_role_only()
    {
        Assert.Equal("2:widget:media", PhaseBCommandLog.Item(2, "widget:media"));
        Assert.Equal("5:app", PhaseBCommandLog.Item(5, "app"));
        Assert.Equal("none", PhaseBCommandLog.Item(-1, "start"));
        Assert.Equal("3:invalid", PhaseBCommandLog.Item(3, "My Private App"));
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "strata-cmdlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
