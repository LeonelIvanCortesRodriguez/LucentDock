using System.Runtime.InteropServices;

namespace Strata.Core;

public enum NotificationBadgePermission { Unspecified, Allowed, Denied, Revoked }
public enum NotificationBadgeAvailability { Off, PackageIdentityRequired, PermissionRequired, Active, Denied }

/// <summary>
/// Fail-closed gate for the optional Windows notification-listener source. The listener requires package identity,
/// a package-manifest capability and explicit user permission. The unpackaged 1.3 build therefore remains off and
/// continues to use title-derived counts. No notification content is represented by this API.
/// </summary>
public static class NotificationBadgeCapability
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static NotificationBadgeAvailability Evaluate(bool enabled, bool hasPackageIdentity, NotificationBadgePermission permission)
    {
        if (!enabled) return NotificationBadgeAvailability.Off;
        if (!hasPackageIdentity) return NotificationBadgeAvailability.PackageIdentityRequired;
        return permission switch
        {
            NotificationBadgePermission.Allowed => NotificationBadgeAvailability.Active,
            NotificationBadgePermission.Denied or NotificationBadgePermission.Revoked => NotificationBadgeAvailability.Denied,
            _ => NotificationBadgeAvailability.PermissionRequired,
        };
    }

    /// <summary>Documented package-identity probe. Unexpected errors fail closed.</summary>
    public static bool CurrentProcessHasPackageIdentity()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        return result is 0 or ErrorInsufficientBuffer && result != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength,
        [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder? packageFullName);
}

/// <summary>Minimal in-memory shape: an opaque app token, count and observation time; never title, sender or body.</summary>
public sealed record NotificationBadgeCount(string AppToken, int Count, DateTimeOffset ObservedUtc);

public sealed class NotificationBadgeCache(int capacity = 256)
{
    private readonly int _capacity = Math.Clamp(capacity, 1, 4096);
    private readonly Dictionary<string, NotificationBadgeCount> _counts = new(StringComparer.Ordinal);

    public IReadOnlyCollection<NotificationBadgeCount> Snapshot() => _counts.Values.ToArray();

    public void Replace(IEnumerable<NotificationBadgeCount> counts, ISet<string>? excludedAppTokens = null)
    {
        _counts.Clear();
        foreach (var item in counts.Where(c => c.Count > 0 && !string.IsNullOrWhiteSpace(c.AppToken))
                     .OrderByDescending(c => c.ObservedUtc))
        {
            if (_counts.Count >= _capacity) break;
            if (excludedAppTokens?.Contains(item.AppToken) == true) continue;
            _counts[item.AppToken] = item with { Count = Math.Min(item.Count, 9999) };
        }
    }

    public void Disable() => _counts.Clear();
}
