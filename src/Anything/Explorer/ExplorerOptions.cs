using System.Runtime.InteropServices;

namespace Anything.Explorer;

/// <summary>
/// File Explorer's own view options, shared with it: what View › Show › Hidden items in either app changes for both.
/// Read and set through SHGetSetSettings, which also tells every open folder view (File Explorer's too) to follow.
/// </summary>
internal static class ExplorerOptions
{
    const uint SSF_SHOWALLOBJECTS = 0x1;

    /// <summary>SHELLSTATE's first bits: fShowAllObjects is bit 0. The rest of the structure isn't touched (the mask says so).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct SHELLSTATE
    {
        public uint Flags1;
        public uint Flags2;
        public uint dwWin95Unused;
        public uint uWin95Unused;
        public int lParamSort;
        public int iSortDirection;
        public uint version;
        public uint uNotUsed;
        public uint Flags3;
    }

    [DllImport("shell32.dll")] static extern void SHGetSetSettings(ref SHELLSTATE state, uint mask, bool set);

    /// <summary>Hidden files and folders show in folder views.</summary>
    public static bool ShowHidden
    {
        get
        {
            var state = new SHELLSTATE();
            try { SHGetSetSettings(ref state, SSF_SHOWALLOBJECTS, false); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
            return (state.Flags1 & 1) != 0;
        }
        set
        {
            var state = new SHELLSTATE { Flags1 = value ? 1u : 0u };
            try { SHGetSetSettings(ref state, SSF_SHOWALLOBJECTS, true); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }
    }
}
