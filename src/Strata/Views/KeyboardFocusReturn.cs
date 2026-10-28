namespace Strata.Views;

/// <summary>Why a flyout or menu closed.</summary>
public enum FlyoutCloseReason
{
    /// <summary>Closed by code: a menu action ran, or another flyout replaced it.</summary>
    Programmatic,
    /// <summary>The user pressed Escape in it.</summary>
    Escape,
    /// <summary>It lost activation: the user clicked or switched somewhere else.</summary>
    LostFocus,
}

/// <summary>
/// Where keyboard focus goes when a flyout or menu that was opened in dock keyboard mode closes. Escape always
/// returns to the dock item that opened it; a menu action returns unless it moved the foreground elsewhere
/// (another app, or a Strata window such as Settings); losing activation never pulls focus back from where the
/// user went. If the opener is gone (e.g. an app was just unpinned), focus goes to the first dock item.
/// </summary>
public static class KeyboardFocusReturn
{
    public static T? Target<T>(bool openedInKeyboardMode, FlyoutCloseReason reason, bool foregroundMovedElsewhere,
        T? opener, IReadOnlyList<T> available) where T : class
    {
        if (!openedInKeyboardMode || available.Count == 0) return null;
        bool returns = reason switch
        {
            FlyoutCloseReason.Escape => true,
            FlyoutCloseReason.Programmatic => !foregroundMovedElsewhere,
            _ => false,
        };
        if (!returns) return null;
        return opener is not null && available.Contains(opener) ? opener : available[0];
    }
}
