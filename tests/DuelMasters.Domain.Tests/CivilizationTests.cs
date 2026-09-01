using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

public class CivilizationTests
{
    [Fact]
    public void Domain_Loads_And_Enumerates_All_Five_Civilizations()
    {
        var civs = System.Enum.GetValues<Civilization>();

        Assert.Equal(6, civs.Length);
        Assert.Contains(Civilization.Light, civs);
        Assert.Contains(Civilization.Water, civs);
        Assert.Contains(Civilization.Darkness, civs);
        Assert.Contains(Civilization.Fire, civs);
        Assert.Contains(Civilization.Nature, civs);
    }
}