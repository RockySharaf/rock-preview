namespace Anything.Core;

/// <summary>Where File Explorer opens its new windows and tabs: Folder Options' "Open File Explorer to".</summary>
public enum StartPlace { Home, ThisPc, Downloads, OneDrive }

/// <summary>Reads File Explorer's "Open File Explorer to" setting, kept apart from the registry so it can be tested.</summary>
public static class StartPlaces
{
    /// <summary>The key under HKEY_CURRENT_USER that holds <see cref="LaunchToValue"/>.</summary>
    public const string LaunchToKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>Explorer's DWORD: 1 This PC, 2 Home (once Quick access), 3 Downloads, 4 OneDrive; missing means Home.</summary>
    public const string LaunchToValue = "LaunchTo";

    /// <summary>
    /// The place <paramref name="launchTo"/> (the registry value as read, null when missing) stands for. Anything
    /// Explorer wouldn't understand (another number, another kind of value) is Home, as it is in Explorer.
    /// </summary>
    public static StartPlace FromLaunchTo(object? launchTo) => launchTo switch
    {
        1 => StartPlace.ThisPc, // a boxed int (a DWORD) only: 1L or "1" are not
        3 => StartPlace.Downloads,
        4 => StartPlace.OneDrive,
        _ => StartPlace.Home,
    };
}
