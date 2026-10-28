using Windows.Devices.Radios;

namespace Strata.Core;

public enum QuickRadio { WiFi, Bluetooth }

/// <summary>Wi-Fi and Bluetooth on and off, for the quick settings in the volume flyout.</summary>
public interface IRadioControl
{
    /// <summary>On, off, or null when this PC has no such radio (or its state isn't known yet).</summary>
    bool? IsOn(QuickRadio radio);
    /// <summary>Turn a radio on or off. False when Windows refused (apps may not control radios) or it failed.</summary>
    Task<bool> SetAsync(QuickRadio radio, bool on);
}

/// <summary>The radios through Windows.Devices.Radios. Reads and switches only when the user opens or taps a tile.</summary>
public sealed class WindowsRadios : IRadioControl
{
    private IReadOnlyList<Radio> _radios = [];
    private RadioAccessStatus _access = RadioAccessStatus.Unspecified;
    private readonly Task _loading;

    public WindowsRadios() => _loading = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            _access = await Radio.RequestAccessAsync();
            _radios = await Radio.GetRadiosAsync();
        }
        catch (Exception ex) { Log.Write($"Radios unavailable: {ex.Message}"); }
    }

    /// <summary>Done once the radios are listed (the first flyout may open before that).</summary>
    public Task Ready => _loading;

    private Radio? Find(QuickRadio radio) =>
        _radios.FirstOrDefault(r => r.Kind == (radio == QuickRadio.WiFi ? RadioKind.WiFi : RadioKind.Bluetooth));

    public bool? IsOn(QuickRadio radio) => Find(radio)?.State switch
    {
        RadioState.On => true,
        RadioState.Off or RadioState.Disabled => false,
        _ => null,
    };

    public async Task<bool> SetAsync(QuickRadio radio, bool on)
    {
        await _loading;
        if (_access != RadioAccessStatus.Allowed || Find(radio) is not { } r) return false;
        try { return await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed; }
        catch (Exception ex) { Log.Write($"Could not switch {radio}: {ex.Message}"); return false; }
    }
}

/// <summary>Fixed radios for previews and tests: nothing is read from or switched on this PC.</summary>
public sealed class FixtureRadios(bool? wifi, bool? bluetooth) : IRadioControl
{
    private readonly Dictionary<QuickRadio, bool?> _state = new() { [QuickRadio.WiFi] = wifi, [QuickRadio.Bluetooth] = bluetooth };
    public bool? IsOn(QuickRadio radio) => _state[radio];
    public Task<bool> SetAsync(QuickRadio radio, bool on)
    {
        if (_state[radio] is null) return Task.FromResult(false);
        _state[radio] = on;
        return Task.FromResult(true);
    }
}

public static class RadioSource
{
    private static IRadioControl? _current;
    public static IRadioControl Current
    {
        get
        {
            if (_current is null)
            {
                PrivacyGuard.ThrowIfPreview("the radios");
                _current = new WindowsRadios();
            }
            return _current;
        }
        set => _current = value;
    }
}
