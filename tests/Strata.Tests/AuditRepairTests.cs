using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Strata.Core;
using Strata.Quota;
using Strata.Smart;

namespace Strata.Tests;

/// <summary>Settings restore takes effect at once; update.json byte-order marks; unsigned updates.</summary>
[Collection("Settings file")] // restoring writes the sandboxed settings.json
public class SettingsRestoreTakesEffectTests
{
    private static string FakeExe(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "strata-restore-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [0x4D, 0x5A]);   // never run: the monitor is only built, not started
        return path;
    }

    [Fact]
    public void Restored_Claude_and_Codex_settings_and_paths_take_effect_at_once()
    {
        string codex = FakeExe("codex.exe"), claude = FakeExe("claude.exe");
        var before = new AppSettings();
        before.Quota.CodexEnabled = false;
        before.Quota.ClaudeEnabled = false;
        var old = QuotaService.For(before);
        Assert.Equal(QuotaStatus.Disabled, old.Codex.Status);

        string json = SettingsBackup.Serialize(new AppSettings
        {
            Quota = new QuotaSettings { CodexEnabled = true, ClaudeEnabled = true, CodexPath = codex, ClaudePath = claude },
        }, [(Automations.MeetingPrep, new RuleState { Enabled = false })], "1.2.0", new DateTime(2026, 10, 25));
        var (file, error) = SettingsBackup.Parse(json);
        Assert.Null(error);

        var h = new SmartHarness();
        var restored = SettingsBackup.Restore(file!, h.Engine);
        try
        {
            Assert.Equal((null, null), QuotaService.Executables());         // restoring dropped the old monitor
            var now = QuotaService.For(restored);
            Assert.NotSame(old, now);                                       // the old monitor read the old settings
            Assert.NotEqual(QuotaStatus.Disabled, now.Codex.Status);        // switches from the backup
            Assert.NotEqual(QuotaStatus.Disabled, now.Claude.Status);
            Assert.Equal((codex, claude), QuotaService.Executables());       // executable paths from the backup
            Assert.False(h.Engine.Status(Automations.MeetingPrep).Enabled);  // automation choices too
            Assert.Equal(codex, AppSettings.Load().Quota.CodexPath);         // and saved
        }
        finally
        {
            QuotaService.Reset();
            File.Delete(Storage.PathFor("settings.json"));
        }
    }

    [Fact]
    public void A_different_settings_object_never_gets_the_old_monitor()
    {
        var a = new AppSettings();
        var b = new AppSettings();
        try
        {
            var first = QuotaService.For(a);
            Assert.Same(first, QuotaService.For(a));
            Assert.NotSame(first, QuotaService.For(b));
        }
        finally { QuotaService.Reset(); }
    }
}

public class UpdateManifestBomTests
{
    private static readonly byte[] Exe = [1, 2, 3, 4];
    private static readonly UTF8Encoding WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private static string ManifestJson(string version) => System.Text.Json.JsonSerializer.Serialize(new
    {
        version, file = "Strata.exe", sha256 = Convert.ToHexString(SHA256.HashData(Exe)), size = Exe.Length,
    });

    [Fact]
    public void Parse_accepts_an_optional_byte_order_mark() =>
        Assert.Equal("1.2.1", Updater.Parse("﻿" + ManifestJson("1.2.1"))?.Version);

    [Fact]
    public void A_folder_update_json_with_a_byte_order_mark_is_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "strata-bom-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Strata.exe"), Exe);
        File.WriteAllText(Path.Combine(dir, Updater.ManifestName), ManifestJson("1.2.1"), WithBom);
        Assert.Equal(UpdateState.Available, Updater.Check(dir, "1.2.0").State);
    }

    private sealed class OnePage(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
    }

    [Fact]
    public async Task A_web_update_json_with_a_byte_order_mark_is_read()
    {
        byte[] body = [.. WithBom.GetPreamble(), .. Encoding.UTF8.GetBytes(ManifestJson("1.2.1"))];
        var check = await Updater.CheckAsync("https://example.com/strata/", "1.2.0", new HttpClient(new OnePage(body)));
        Assert.Equal(UpdateState.Available, check.State);
        Assert.Equal("1.2.1", check.Manifest!.Version);
    }
}

public class UnsignedUpdateChannelTests
{
    [Fact]
    public void An_unsigned_strata_is_an_unsigned_preview_channel()
    {
        var unsigned = Path.Combine(Path.GetTempPath(), "strata-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0, 0]);
        try
        {
            Assert.True(Updater.RunningUnsigned(unsigned));
            Assert.True(Updater.RunningUnsigned(null));
        }
        finally { File.Delete(unsigned); }
        // The warning says what the hash proves and what it doesn't.
        Assert.Contains("Unsigned preview channel", Updater.UnsignedWarning);
        Assert.Contains("arrived intact, not who published it", Updater.UnsignedWarning);
    }
}
