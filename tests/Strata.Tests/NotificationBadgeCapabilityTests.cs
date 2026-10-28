using Strata.Core;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class NotificationBadgeCapabilityTests
{
    [Theory]
    [InlineData(false, false, NotificationBadgePermission.Unspecified, NotificationBadgeAvailability.Off)]
    [InlineData(true, false, NotificationBadgePermission.Allowed, NotificationBadgeAvailability.PackageIdentityRequired)]
    [InlineData(true, true, NotificationBadgePermission.Unspecified, NotificationBadgeAvailability.PermissionRequired)]
    [InlineData(true, true, NotificationBadgePermission.Allowed, NotificationBadgeAvailability.Active)]
    [InlineData(true, true, NotificationBadgePermission.Denied, NotificationBadgeAvailability.Denied)]
    [InlineData(true, true, NotificationBadgePermission.Revoked, NotificationBadgeAvailability.Denied)]
    public void Listener_is_off_unless_identity_capability_and_permission_can_all_be_satisfied(
        bool enabled, bool identity, NotificationBadgePermission permission, NotificationBadgeAvailability expected) =>
        Assert.Equal(expected, NotificationBadgeCapability.Evaluate(enabled, identity, permission));

    [Fact]
    public void Count_cache_is_bounded_excludable_and_contains_no_content_fields()
    {
        var cache = new NotificationBadgeCache(capacity: 2);
        var now = DateTimeOffset.UtcNow;
        cache.Replace([
            new("opaque-a", 50_000, now),
            new("excluded", 8, now.AddMinutes(-1)),
            new("opaque-b", 2, now.AddMinutes(-2)),
            new("opaque-c", 3, now.AddMinutes(-3)),
        ], new HashSet<string>(["excluded"], StringComparer.Ordinal));

        Assert.Equal(2, cache.Snapshot().Count);
        Assert.Contains(cache.Snapshot(), c => c.AppToken == "opaque-a" && c.Count == 9999);
        Assert.Contains(cache.Snapshot(), c => c.AppToken == "opaque-b");
        Assert.DoesNotContain(typeof(NotificationBadgeCount).GetProperties(), p =>
            p.Name.Contains("Title", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Body", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Sender", StringComparison.OrdinalIgnoreCase));

        cache.Disable();
        Assert.Empty(cache.Snapshot());
    }
}
