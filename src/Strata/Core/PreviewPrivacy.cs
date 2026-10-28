using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Strata.Core;

/// <summary>Scans preview artifacts for profile paths and other unmistakably personal markers.</summary>
public static class PreviewPrivacy
{
    private sealed record Marker(string Term, Regex Pattern);

    /// <summary>
    /// Paths (profile folder, data folder, the profile-root prefix) match case-insensitively anywhere.
    /// The account name matches case-sensitively as a whole word: names are often ordinary words
    /// ("Guest", "Admin"), and a case-insensitive substring match flags normal UI text such as "your user folder".
    /// </summary>
    public static IReadOnlyList<(string Term, Func<string, bool> Found)> Markers(string userName, string profile,
        IEnumerable<string>? additionalTerms = null)
    {
        static Marker Anywhere(string term) => new(term, new Regex(Regex.Escape(term), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        static Marker WholeWord(string term) => new(term, new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(term) + @"(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant));

        var markers = new List<Marker>();
        string profileRoot = Path.GetDirectoryName(profile.TrimEnd('\\')) is { Length: > 0 } r ? r.TrimEnd('\\') + "\\" : "";
        foreach (var path in new[] { profileRoot, profile, Storage.DefaultRoot })
            if (path.Length >= 3) markers.Add(Anywhere(path));
        if (userName.Length >= 3) markers.Add(WholeWord(userName));
        if (additionalTerms is not null)
            foreach (var term in additionalTerms.Where(t => !string.IsNullOrWhiteSpace(t) && t.Length >= 3)) markers.Add(Anywhere(term));
        return markers.DistinctBy(m => m.Term).Select(m => (m.Term, (Func<string, bool>)(s => m.Pattern.IsMatch(s)))).ToList();
    }

    private static IReadOnlyList<(string Term, Func<string, bool> Found)> CurrentMarkers(IEnumerable<string>? additionalTerms) =>
        Markers(Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), additionalTerms);

    public static IReadOnlyList<string> Scan(string directory, IEnumerable<string>? additionalTerms = null)
    {
        var markers = CurrentMarkers(additionalTerms);
        var hits = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            // The scan report repeats any term it found; scanning it would only re-report earlier hits.
            if (Path.GetFileName(path).Equals("privacy-scan.txt", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            string ascii = Encoding.Latin1.GetString(bytes);
            string unicode = bytes.Length % 2 == 0 ? Encoding.Unicode.GetString(bytes) : "";
            foreach (var (term, found) in markers)
                if (found(ascii) || found(unicode))
                    hits.Add($"{Path.GetFileName(path)} contains forbidden privacy marker '{term}'.");
        }
        return hits;
    }

    /// <summary>Inspect text before it is rasterized, since compressed PNG bytes are not an OCR privacy check.</summary>
    public static IReadOnlyList<string> ScanVisual(DependencyObject root, IEnumerable<string>? additionalTerms = null)
    {
        var hits = new List<string>();
        var markers = CurrentMarkers(additionalTerms);
        void Check(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (var (term, found) in markers)
                if (found(value)) hits.Add($"visual text contains forbidden privacy marker '{term}'.");
        }
        void Walk(DependencyObject node)
        {
            if (node is TextBlock text) Check(text.Text);
            if (node is TextBox box) Check(box.Text);
            if (node is ContentControl content && content.Content is string label) Check(label);
            if (node is FrameworkElement element && element.ToolTip is string tip) Check(tip);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return hits.Distinct().ToList();
    }
}
