using Strata.Native;

namespace Strata.Core;

/// <summary>
/// Win+Ctrl+Alt+1…9: open or switch to the dock's first nine apps, as a click on them does. Windows keeps
/// Win+number, Win+Alt+number and Win+Ctrl+number for its own taskbar even while it is hidden, and Ctrl+Alt is
/// AltGr on many keyboards (AltGr+2 types @ on a Spanish one), so Strata takes Win+Ctrl+Alt. A combination another
/// app already holds is skipped and counted, never forced.
/// </summary>
public sealed class AppHotkeys(Func<int, uint, uint, bool> register, Action<int> unregister)
{
    public const string Gesture = "Win+Ctrl+Alt";
    public const int FirstId = 0x5401, Count = 9;
    public const uint Modifiers = Win32.MOD_WIN | Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT;

    private readonly bool[] _held = new bool[Count];

    /// <summary>How many of the nine are registered.</summary>
    public int Registered => _held.Count(h => h);

    /// <summary>"Win+Ctrl+Alt+3" for the third app; null past the ninth.</summary>
    public static string? Label(int index) => index is >= 0 and < Count ? $"{Gesture}+{index + 1}" : null;

    /// <summary>The app index a WM_HOTKEY id stands for, if it is one of these.</summary>
    public static int? IndexFor(int hotkeyId) => hotkeyId - FirstId is var i && i is >= 0 and < Count ? i : null;

    public int Register()
    {
        for (int i = 0; i < Count; i++)
        {
            if (_held[i]) continue;
            try { _held[i] = register(FirstId + i, Modifiers, (uint)('1' + i)); }
            catch (Exception ex) { Log.Write($"Registering {Label(i)} threw: {ex.Message}"); }
        }
        if (Registered < Count) Log.Write($"App shortcuts: {Registered} of {Count} {Gesture}+number combinations are free; the rest are held by other apps.");
        return Registered;
    }

    public void Unregister()
    {
        for (int i = 0; i < Count; i++)
        {
            if (!_held[i]) continue;
            try { unregister(FirstId + i); } catch (Exception) { }
            _held[i] = false;
        }
    }
}
