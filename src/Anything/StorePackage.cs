using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace Anything;

/// <summary>
/// Rock Preview from the Microsoft Store: the same program, run inside the Store's package (build/Package-Store.ps1). The
/// Store installs, updates and removes it, so Setup, the update check and the service stay out of it, and it changes
/// nothing in Windows that would outlive the package (File Explorer stays as it is). Windows starts it with Windows
/// through the package's startup task, and its restarts go through its app alias, which starts it inside its package.
/// </summary>
internal static class StorePackage
{
    /// <summary>The package's family name (Partner Center's Package Family Name), or null outside the Store's package.</summary>
    public static string? FamilyName { get; } = ReadFamilyName();

    /// <summary>Running from the Store's package.</summary>
    public static bool IsInstalled => FamilyName is not null;

    /// <summary>The startup task's ID in the package's manifest (build/Store/AppxManifest.xml).</summary>
    public const string StartupTaskId = "RockPreviewStartup";

    /// <summary>
    /// The app alias the package declares (build/Store/AppxManifest.xml): %LOCALAPPDATA%\Microsoft\WindowsApps\RockPreview.exe.
    /// Started through it, Rock Preview runs inside its package, elevated or not; its program file in the package's
    /// folder changes with each version.
    /// </summary>
    public static string AliasPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", "RockPreview.exe");

    /// <summary>The program that starts Rock Preview again: the app alias in the Store's package, else this program file.</summary>
    public static string LaunchPath => IsInstalled && File.Exists(AliasPath) ? AliasPath : Environment.ProcessPath!;

    /// <summary>Rock Preview's page in the Microsoft Store app, which shows a new version and installs it.</summary>
    public static void OpenStorePage() => AnythingApp.OpenWebPage("ms-windows-store://pdp/?PFN=" + FamilyName);

    /// <summary>Settings › Apps › Installed apps, where a Store app is uninstalled.</summary>
    public static void OpenInstalledApps() => AnythingApp.OpenWebPage("ms-settings:appsfeatures");

    /// <summary>Windows started it with Windows (the startup task), what -startup says outside the package.</summary>
    public static bool StartedWithWindows()
    {
        if (!IsInstalled) return false;
        try { return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask; }
        catch (Exception) { return false; }
    }

    /// <summary>Whether Windows starts Rock Preview with Windows: the startup task, which Task Manager › Startup apps also turns off.</summary>
    public static bool StartsWithWindows()
    {
        try { return IsOn(Wait(async () => (await StartupTask.GetAsync(StartupTaskId)).State)); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Turns the startup task on or off and returns whether it is on. Turned off in Task Manager, it stays off: Windows lets
    /// only the user turn it on again there.
    /// </summary>
    public static bool SetStartsWithWindows(bool on)
    {
        try
        {
            return Wait(async () =>
            {
                var task = await StartupTask.GetAsync(StartupTaskId);
                if (on) return IsOn(await task.RequestEnableAsync());
                task.Disable();
                return false;
            });
        }
        catch (Exception) { return StartsWithWindows(); }
    }

    static bool IsOn(StartupTaskState state) => state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    /// <summary>Waits for Windows' answer on a pool thread (it never needs the UI thread, and the setting is applied at once).</summary>
    static T Wait<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    static string? ReadFamilyName()
    {
        try
        {
            int length = 0;
            // APPMODEL_ERROR_NO_PACKAGE (15700) outside a package; with a package, the length it needs.
            if (GetCurrentPackageFamilyName(ref length, null) != ERROR_INSUFFICIENT_BUFFER) return null;
            var name = new StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
        }
        catch (Exception) { return null; }
    }

    const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, StringBuilder? packageFamilyName);
}
