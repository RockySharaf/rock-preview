namespace Anything.Core;

/// <summary>Which way a mouse side button or a Browser Back / Forward key asks to go.</summary>
public enum NavigationStep { None, Back, Forward }

/// <summary>
/// Reads the mouse's Back and Forward side buttons and the keyboard's (or remote's) Browser Back / Forward keys out of
/// Windows' messages, kept apart from the windows so it can be tested. They go back and forward like Alt+Left and
/// Alt+Right, as in File Explorer.
/// </summary>
public static class NavigationInput
{
    const int XBUTTON1 = 1, XBUTTON2 = 2;
    const int APPCOMMAND_BROWSER_BACKWARD = 1, APPCOMMAND_BROWSER_FORWARD = 2;
    const int VK_BROWSER_BACK = 0xA6, VK_BROWSER_FORWARD = 0xA7;

    /// <summary>
    /// The step for WM_XBUTTONDOWN / UP / DBLCLK and their non-client WM_NCXBUTTON* forms: the button is the high word
    /// of wParam (XBUTTON1 is Back, XBUTTON2 is Forward); the low word holds the keys held down and is ignored.
    /// </summary>
    public static NavigationStep FromXButton(nint wParam) => HighWord(wParam) switch
    {
        XBUTTON1 => NavigationStep.Back,
        XBUTTON2 => NavigationStep.Forward,
        _ => NavigationStep.None,
    };

    /// <summary>
    /// The step for WM_APPCOMMAND: the command is GET_APPCOMMAND_LPARAM, the high word of lParam without the device bits
    /// (mouse, key, OEM) in its top four bits; the low word holds the keys held down.
    /// </summary>
    public static NavigationStep FromAppCommand(nint lParam) => (HighWord(lParam) & 0x0FFF) switch
    {
        APPCOMMAND_BROWSER_BACKWARD => NavigationStep.Back,
        APPCOMMAND_BROWSER_FORWARD => NavigationStep.Forward,
        _ => NavigationStep.None,
    };

    /// <summary>
    /// The step for WM_KEYDOWN / UP (and the WM_SYSKEY* forms Alt gives): the keyboard's Browser Back and Browser
    /// Forward keys, whose code is wParam.
    /// </summary>
    public static NavigationStep FromKey(nint wParam) => ((int)((ulong)wParam & 0xFFFF_FFFF)) switch
    {
        VK_BROWSER_BACK => NavigationStep.Back,
        VK_BROWSER_FORWARD => NavigationStep.Forward,
        _ => NavigationStep.None,
    };

    /// <summary>
    /// Whether a message is the user doing something in a window, so an item Back or Up was still to select there is
    /// forgotten: a mouse button pressed (in the client or non-client area, a scroll bar's too), the wheel turned either
    /// way, a finger or pen put down (a tap, or the start of a pan) or its wheel turned, or a key pressed. Not a key's
    /// release, not a key held down repeating (bit 30 of <paramref name="lParam"/>, the key's previous state, is set: the
    /// key that started the Back or Up, held, repeats after the item is queued), and not Shift, Ctrl, Alt or Windows
    /// pressed on their own.
    /// </summary>
    public static bool MovesOn(int msg, nint wParam, nint lParam = 0) => msg switch
    {
        WM_LBUTTONDOWN or WM_LBUTTONDBLCLK or WM_RBUTTONDOWN or WM_RBUTTONDBLCLK or WM_MBUTTONDOWN or WM_MBUTTONDBLCLK
            or WM_XBUTTONDOWN or WM_XBUTTONDBLCLK or WM_MOUSEWHEEL or WM_MOUSEHWHEEL => true,
        WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK or WM_NCRBUTTONDOWN or WM_NCRBUTTONDBLCLK or WM_NCMBUTTONDOWN or WM_NCMBUTTONDBLCLK
            or WM_NCXBUTTONDOWN or WM_NCXBUTTONDBLCLK => true,
        WM_POINTERDOWN or WM_NCPOINTERDOWN or WM_POINTERWHEEL or WM_POINTERHWHEEL => true,
        WM_KEYDOWN or WM_SYSKEYDOWN => ((ulong)lParam & KeyWasDown) == 0 && !IsModifier((int)((ulong)wParam & 0xFFFF_FFFF)),
        _ => false,
    };

    // WinUser.h
    const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONDBLCLK = 0x0206,
              WM_MBUTTONDOWN = 0x0207, WM_MBUTTONDBLCLK = 0x0209, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B,
              WM_XBUTTONDBLCLK = 0x020D, WM_MOUSEHWHEEL = 0x020E,
              WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCRBUTTONDOWN = 0x00A4, WM_NCRBUTTONDBLCLK = 0x00A6,
              WM_NCMBUTTONDOWN = 0x00A7, WM_NCMBUTTONDBLCLK = 0x00A9, WM_NCXBUTTONDOWN = 0x00AB, WM_NCXBUTTONDBLCLK = 0x00AD,
              WM_NCPOINTERDOWN = 0x0242, WM_POINTERDOWN = 0x0246, WM_POINTERWHEEL = 0x024E, WM_POINTERHWHEEL = 0x024F,
              WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const ulong KeyWasDown = 1UL << 30; // a key message's previous key state (KF_REPEAT in the high word)
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LSHIFT = 0xA0, VK_RMENU = 0xA5;

    static bool IsModifier(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN or (>= VK_LSHIFT and <= VK_RMENU);

    /// <summary>Bits 16 to 31 of the low 32 bits: a 64-bit value that came sign-extended doesn't change it.</summary>
    static int HighWord(nint value) => (int)(((ulong)value & 0xFFFF_FFFF) >> 16) & 0xFFFF;
}
