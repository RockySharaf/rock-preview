using Anything.Core;

namespace Anything.Tests;

/// <summary>File Explorer's "Open File Explorer to" (LaunchTo) and where new windows and tabs open for it.</summary>
public sealed class StartPlaceTests
{
    [Theory]
    [InlineData(1, StartPlace.ThisPc)]
    [InlineData(2, StartPlace.Home)]
    [InlineData(3, StartPlace.Downloads)]
    [InlineData(4, StartPlace.OneDrive)]
    public void EachOfExplorersValuesOpensItsPlace(int launchTo, StartPlace expected)
        => Assert.Equal(expected, StartPlaces.FromLaunchTo(launchTo));

    [Fact]
    public void NoValueIsHome() => Assert.Equal(StartPlace.Home, StartPlaces.FromLaunchTo(null));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void ANumberExplorerDoesntKnowIsHome(int launchTo) => Assert.Equal(StartPlace.Home, StartPlaces.FromLaunchTo(launchTo));

    [Fact]
    public void AValueOfTheWrongKindIsHome()
    {
        Assert.Equal(StartPlace.Home, StartPlaces.FromLaunchTo("1"));       // a string, not a DWORD
        Assert.Equal(StartPlace.Home, StartPlaces.FromLaunchTo(1L));        // a QWORD
        Assert.Equal(StartPlace.Home, StartPlaces.FromLaunchTo(new byte[] { 1, 0, 0, 0 }));
    }
}
