using System.Text.RegularExpressions;

namespace Strata.Core;

/// <summary>
/// Unread counts read from window titles, the way chat and mail apps show them: "(3) WhatsApp", "Inbox (12) - Mail".
/// Windows has no public way to read another app's taskbar badge, so this is the one signal there is. Only those two
/// shapes count, so a file called "Report (2).docx" is not taken for two unread messages. Read from the titles the dock
/// already lists; never stored.
/// </summary>
public static partial class UnreadCount
{
    // "(3) Chat" / "(12) WhatsApp" / "[3] Slack": a count in brackets at the very start of the title.
    [GeneratedRegex(@"^\s*[\(\[](\d{1,4})\+?[\)\]]\s+\S")]
    private static partial Regex Leading();

    // "Inbox (3)", "Bandeja de entrada (3)", "Posteingang (3)": a mail folder with its count.
    [GeneratedRegex(@"\b(?:Inbox|Bandeja de entrada|Boîte de réception|Posteingang|Posta in arrivo|Caixa de entrada)\s*\((\d{1,5})\)", RegexOptions.IgnoreCase)]
    private static partial Regex Mailbox();

    /// <summary>The unread count in one window title, or 0 when it shows none.</summary>
    public static int FromTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return 0;
        var m = Leading().Match(title);
        if (!m.Success) m = Mailbox().Match(title);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
    }

    /// <summary>An app's count: its windows' counts added up (each window shows its own tab or folder).</summary>
    public static int ForWindows(IEnumerable<string> titles) => titles.Sum(FromTitle);

    /// <summary>The badge text: none for 0, "1".."99", then "99+".</summary>
    public static string? Badge(int unread) => unread <= 0 ? null : unread > 99 ? "99+" : unread.ToString();
}
