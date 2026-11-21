using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Anything.Core;

namespace Anything.Tests;

/// <summary>
/// Reading a signed-out account's classes hive for the uninstall: a hive file whose keys carry different security
/// descriptors (as every real UsrClass.dat does, which RegLoadAppKey refuses) is built with the Offline Registry Library
/// in a temporary folder and read back.
/// </summary>
public sealed partial class OfflineHiveTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("offlinehive-").FullName;
    string HivePath => Path.Combine(_dir, "UsrClass.dat");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    const string CommandKey = @"Directory\shell\RockPreview\command";
    const string Command = "\"C:\\Program Files\\Rock Preview\\RockPreview.exe\" \"%1\"";

    [Fact]
    public void AHiveWithSeveralSecurityDescriptorsIsRead()
    {
        BuildHive(HivePath);
        using var hive = OfflineHive.Open(HivePath);
        Assert.Equal(Command, hive.ReadString(CommandKey, null));
        Assert.Equal("RockPreview", hive.ReadString(@"Directory\shell", null));
        Assert.Equal("text", hive.ReadString(@"Directory\shell\RockPreview", "Named"));
    }

    [Fact]
    public void MissingKeysValuesAndNonTextAreNull()
    {
        BuildHive(HivePath);
        using var hive = OfflineHive.Open(HivePath);
        Assert.Null(hive.ReadString(@"Drive\shell\RockPreview\command", null)); // no such key
        Assert.Null(hive.ReadString(@"Directory\shell\RockPreview", null));     // key without a default value
        Assert.Null(hive.ReadString(@"Directory\shell\RockPreview", "Missing"));
        Assert.Null(hive.ReadString(@"Directory\shell\RockPreview", "Number")); // a DWORD isn't text
    }

    [Fact]
    public void AnExpandableStringIsExpanded()
    {
        BuildHive(HivePath);
        using var hive = OfflineHive.Open(HivePath);
        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\x.exe"), hive.ReadString(@"Directory\shell\RockPreview", "Expand"));
    }

    [Fact]
    public void AFileThatIsNotAHiveThrows()
    {
        File.WriteAllText(HivePath, "not a registry hive");
        Assert.Throws<Win32Exception>(() => OfflineHive.Open(HivePath).Dispose());
        Assert.Throws<Win32Exception>(() => OfflineHive.Open(Path.Combine(_dir, "missing.dat")).Dispose());
    }

    // ---- building a hive with offreg ------------------------------------------------------------

    static void BuildHive(string path)
    {
        Check(ORCreateHive(out nint root));
        try
        {
            nint shell = CreateKeys(root, @"Directory\shell");
            SetString(shell, null, "RockPreview", 1);
            nint verb = CreateKeys(shell, "RockPreview");
            SetString(verb, "Named", "text", 1);
            SetString(verb, "Expand", @"%SystemRoot%\x.exe", 2);
            Check(ORSetValue(verb, "Number", 4, BitConverter.GetBytes(7), 4));
            nint command = CreateKeys(verb, "command");
            SetString(command, null, Command, 1);
            // Different security descriptors on the keys, as a real classes hive has.
            SetSecurity(verb, "O:BAG:SYD:(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;WD)");
            SetSecurity(command, "O:SYG:SYD:(A;;KA;;;SY)(A;;KR;;;AC)");
            Check(ORCloseKey(command));
            Check(ORCloseKey(verb));
            Check(ORCloseKey(shell));
            Check(ORSaveHive(root, path, 6, 1));
        }
        finally
        {
            ORCloseHive(root);
        }
    }

    /// <summary>Creates each key of <paramref name="path"/> in turn; returns the last, open.</summary>
    static nint CreateKeys(nint parent, string path)
    {
        nint current = parent;
        foreach (var name in path.Split('\\'))
        {
            Check(ORCreateKey(current, name, null, 0, null, out nint next, out _));
            if (current != parent) ORCloseKey(current);
            current = next;
        }
        return current;
    }

    static void SetString(nint key, string? name, string text, int type)
    {
        var data = Encoding.Unicode.GetBytes(text + "\0");
        Check(ORSetValue(key, name, type, data, data.Length));
    }

    static void SetSecurity(nint key, string sddl)
    {
        var sd = new RawSecurityDescriptor(sddl);
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0); // self-relative
        Check(ORSetKeySecurity(key, 0x1 | 0x2 | 0x4 /* OWNER, GROUP, DACL */, bytes));
    }

    static void Check(int rc)
    {
        if (rc != 0) throw new Win32Exception(rc);
    }

    [LibraryImport("offreg.dll")]
    private static partial int ORCreateHive(out nint hive);

    [LibraryImport("offreg.dll")]
    private static partial int ORCloseHive(nint hive);

    [LibraryImport("offreg.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ORCreateKey(nint key, string subKey, string? keyClass, int options, byte[]? securityDescriptor, out nint result, out int disposition);

    [LibraryImport("offreg.dll")]
    private static partial int ORCloseKey(nint key);

    [LibraryImport("offreg.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ORSetValue(nint key, string? valueName, int type, byte[] data, int dataSize);

    [LibraryImport("offreg.dll")]
    private static partial int ORSetKeySecurity(nint key, int securityInformation, byte[] securityDescriptor);

    [LibraryImport("offreg.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ORSaveHive(nint hive, string path, int osMajorVersion, int osMinorVersion);
}
