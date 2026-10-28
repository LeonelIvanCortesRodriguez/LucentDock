using Strata.Core;

namespace Strata.Tests;

/// <summary>App groups: drop one app onto another and a folder-tile is born. Pure list semantics, pinned.</summary>
[Trait("Category", "Unit")]
public class DockGroupsTests
{
    private static PinnedApp App(string name) => new() { Name = name, Path = $@"C:\apps\{name}.exe" };

    [Fact]
    public void Dropping_one_app_onto_another_makes_a_group_at_the_targets_slot()
    {
        var a = App("a"); var b = App("b"); var c = App("c");
        var pins = new List<PinnedApp> { a, b, c };
        Assert.True(DockGroups.MergeInto(pins, c, a, "Group"));
        Assert.Equal(2, pins.Count);
        Assert.True(DockGroups.IsGroup(pins[0]));
        Assert.Equal(["a", "c"], pins[0].Group!.Select(p => p.Name));
        Assert.Same(b, pins[1]);
    }

    [Fact]
    public void Dropping_onto_a_group_joins_it_and_dropping_a_group_pours_its_members()
    {
        var a = App("a"); var b = App("b"); var c = App("c"); var d = App("d");
        var pins = new List<PinnedApp> { a, b, c, d };
        Assert.True(DockGroups.MergeInto(pins, b, a, "Group"));
        var group = pins[0];
        Assert.True(DockGroups.MergeInto(pins, c, group, "Group"));
        Assert.Equal(["a", "b", "c"], group.Group!.Select(p => p.Name));
        // A second group poured into the first: members move, no nesting ever.
        var e = App("e");
        var pins2 = new List<PinnedApp> { group, d, e };
        Assert.True(DockGroups.MergeInto(pins2, e, d, "Group"));
        var second = pins2[1];
        Assert.True(DockGroups.MergeInto(pins2, second, group, "Group"));
        Assert.Equal(["a", "b", "c", "d", "e"], group.Group!.Select(p => p.Name));
        Assert.All(group.Group!, m => Assert.False(DockGroups.IsGroup(m)));
    }

    [Fact]
    public void Ungroup_pours_the_members_back_into_the_groups_slot()
    {
        var a = App("a"); var b = App("b"); var c = App("c");
        var pins = new List<PinnedApp> { a, b, c };
        DockGroups.MergeInto(pins, c, b, "Group");
        var group = pins[1];
        Assert.True(DockGroups.Ungroup(pins, group));
        Assert.Equal(["a", "b", "c"], pins.Select(p => p.Name));
        Assert.All(pins, p => Assert.False(DockGroups.IsGroup(p)));
    }

    [Fact]
    public void Dropping_an_app_onto_itself_or_a_missing_target_changes_nothing()
    {
        var a = App("a"); var b = App("b");
        var pins = new List<PinnedApp> { a, b };
        Assert.False(DockGroups.MergeInto(pins, a, a, "Group"));
        Assert.False(DockGroups.MergeInto(pins, a, App("ghost"), "Group"));
        Assert.Equal(2, pins.Count);
    }

    [Theory]
    [InlineData(100.0, 0)]   // dead centre of the first button
    [InlineData(110.0, 0)]   // still inside its central half (width 48 -> 12 either side)
    [InlineData(113.0, null)] // past the central half: between buttons, a reorder
    [InlineData(148.0, 1)]   // centre of the second
    [InlineData(999.0, null)]
    public void A_drop_counts_as_onto_only_within_the_targets_central_half(double x, int? expected)
    {
        double[] centres = [100, 148];
        double[] widths = [48, 48];
        Assert.Equal(expected, DockGroups.GroupTarget(centres, widths, x));
    }

    [Fact]
    public void A_drag_that_ever_left_its_slot_is_a_reorder_even_when_released_on_a_centre()
    {
        // The neighbours animate aside the moment the slot changes, so the static centres stop describing the
        // screen: releasing over a neighbour's OLD centre must reorder, never silently group (audit F1).
        double[] centres = [100, 148];
        double[] widths = [48, 48];
        Assert.Equal(0, DockGroups.DropOnto(centres, widths, 100, fromSlot: 2, currentSlot: 2));
        Assert.Null(DockGroups.DropOnto(centres, widths, 100, fromSlot: 2, currentSlot: 1));
        Assert.Null(DockGroups.DropOnto(centres, widths, 148, fromSlot: 0, currentSlot: 2));
    }

    [Fact]
    public void New_groups_are_named_after_their_founders_and_stay_distinguishable()
    {
        Assert.Equal("Mail & Teams", DockGroups.NameFor(App("Mail"), App("Teams")));
        var group = new PinnedApp { Name = "Work", Group = [App("a")] };
        Assert.Equal("Work", DockGroups.NameFor(group, App("b")));
        Assert.Equal("VeryLongAppNa… & b", DockGroups.NameFor(App("VeryLongAppNameHere"), App("b")));
    }

    [Fact]
    public void Sanitize_repairs_imported_files_without_inventing_data()
    {
        var pins = new List<PinnedApp>
        {
            App("plain"),
            new() { Name = "empty", Group = [] },                       // empty group: an ordinary pin either way
            new() { Name = "nulls", Group = [null!, App("kept")] },     // null member: dropped
            new() { Name = "nested", Group = [new() { Name = "inner", Group = [App("x"), App("y")] }, App("z")] },
        };
        DockGroups.Sanitize(pins);
        Assert.Equal(3, pins.Count); // the empty group pin is removed outright
        Assert.Equal(["kept"], pins[1].Group!.Select(p => p.Name));
        Assert.Equal(["x", "y", "z"], pins[2].Group!.Select(p => p.Name));
        Assert.All(pins.Where(DockGroups.IsGroup), g => Assert.All(g.Group!, m => Assert.False(DockGroups.IsGroup(m))));
    }

    [Fact]
    public void Groups_round_trip_through_the_settings_serializer()
    {
        var pins = new List<PinnedApp> { new() { Name = "Mail & Teams", Group = [App("Mail"), App("Teams")] } };
        string json = System.Text.Json.JsonSerializer.Serialize(pins, Storage.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<List<PinnedApp>>(json, Storage.Json)!;
        Assert.True(DockGroups.IsGroup(back[0]));
        Assert.Equal(["Mail", "Teams"], back[0].Group!.Select(p => p.Name));
        Assert.Equal(@"C:\apps\Mail.exe", back[0].Group![0].Path);
    }
}
