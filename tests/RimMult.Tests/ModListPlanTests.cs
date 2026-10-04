using RimMult.Shared.Mods;

namespace RimMult.Tests;

public class ModListPlanTests
{
    private static List<ModEntry> Server(params string[] ids) => ids.Select(id => new ModEntry(id, id, "1", id.StartsWith("ws.") ? 100UL : 0)).ToList();

    private static Func<string, string?> Installed(params string[] ids) =>
        id => ids.FirstOrDefault(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TakesTheServersOrderAndSet()
    {
        var plan = ModListPlan.LikeServer(
            Server("ludeon.rimworld", "brrainz.harmony", "a", "b", "c"),
            ["ludeon.rimworld", "brrainz.harmony", "c", "a", "extra", "b"],
            Installed("ludeon.rimworld", "brrainz.harmony", "a", "b", "c", "extra"),
            keepLocal: _ => false);

        Assert.Equal(["ludeon.rimworld", "brrainz.harmony", "a", "b", "c"], plan.Active);
        Assert.Equal(["extra"], plan.Disabled);
        Assert.Empty(plan.Enabled);
        Assert.True(plan.OrderChanged);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void EnablesInstalledModsAndListsMissingOnes()
    {
        var plan = ModListPlan.LikeServer(
            Server("core", "a", "ws.b", "c"),
            ["core", "c"],
            Installed("core", "a", "c"),
            keepLocal: _ => false);

        Assert.Equal(["core", "a", "c"], plan.Active);
        Assert.Equal(["a"], plan.Enabled);
        Assert.Equal("ws.b", Assert.Single(plan.Missing).PackageId);
        Assert.False(plan.OrderChanged); // core before c, as it was
    }

    [Fact]
    public void KeepsOwnModsRightAfterTheModTheyFollowed()
    {
        var plan = ModListPlan.LikeServer(
            Server("core", "a", "b"),
            ["core", "ui.camera", "b", "b.ru", "a"],
            Installed("core", "a", "b", "ui.camera", "b.ru"),
            keepLocal: id => id is "ui.camera" or "b.ru");

        Assert.Equal(["core", "ui.camera", "a", "b", "b.ru"], plan.Active);
        Assert.Empty(plan.Disabled);
        Assert.True(plan.OrderChanged);
    }

    [Fact]
    public void UsesTheIdTheModHasHereAndComparesIgnoringCase()
    {
        var plan = ModListPlan.LikeServer(
            Server("Ludeon.RimWorld", "Some.Mod"),
            ["ludeon.rimworld", "some.mod_steam"],
            id => id.ToLowerInvariant() switch { "ludeon.rimworld" => "ludeon.rimworld", "some.mod" => "some.mod_steam", _ => null },
            keepLocal: _ => false);

        Assert.Equal(["ludeon.rimworld", "some.mod_steam"], plan.Active);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void JustSubscribedModsTakeTheirPlaceInAdvance()
    {
        var plan = ModListPlan.LikeServer(
            Server("core", "ws.new", "a"),
            ["core", "a"],
            Installed("core", "a"),
            keepLocal: _ => false,
            expectInstalled: m => m.WorkshopId != 0);

        Assert.Equal(["core", "ws.new", "a"], plan.Active);
        Assert.Equal("ws.new", Assert.Single(plan.Missing).PackageId);
        Assert.Equal(["ws.new"], plan.Enabled);
    }
}
