using Anything.Core;

namespace Anything.Tests;

/// <summary>The mouse's Back and Forward side buttons and the Browser Back / Forward keys, read out of Windows' messages.</summary>
public sealed class NavigationInputTests
{
    const int MK_LBUTTON = 0x1, MK_SHIFT = 0x4, MK_CONTROL = 0x8, MK_XBUTTON1 = 0x20, MK_XBUTTON2 = 0x40;

    static nint XButton(int button, int keys = 0) => (nint)((button << 16) | keys);

    [Fact]
    public void TheFirstSideButtonGoesBack() => Assert.Equal(NavigationStep.Back, NavigationInput.FromXButton(XButton(1)));

    [Fact]
    public void TheSecondSideButtonGoesForward() => Assert.Equal(NavigationStep.Forward, NavigationInput.FromXButton(XButton(2)));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(0xFFFF)]
    public void OtherButtonsGoNowhere(int button) => Assert.Equal(NavigationStep.None, NavigationInput.FromXButton(XButton(button)));

    [Fact]
    public void KeysHeldDownDontChangeTheButton() // the low word: MK_* flags, the side button's own among them
    {
        Assert.Equal(NavigationStep.Back, NavigationInput.FromXButton(XButton(1, MK_XBUTTON1 | MK_SHIFT | MK_CONTROL | MK_LBUTTON)));
        Assert.Equal(NavigationStep.Forward, NavigationInput.FromXButton(XButton(2, MK_XBUTTON2 | MK_SHIFT)));
        Assert.Equal(NavigationStep.None, NavigationInput.FromXButton(XButton(0, MK_XBUTTON1)));
    }

    [Fact]
    public void TheNonClientFormReadsTheSame() // WM_NCXBUTTON*: hit-test code in the low word, button in the high word
    {
        const int HTCAPTION = 2, HTCLIENT = 1;
        Assert.Equal(NavigationStep.Back, NavigationInput.FromXButton(XButton(1, HTCAPTION)));
        Assert.Equal(NavigationStep.Forward, NavigationInput.FromXButton(XButton(2, HTCLIENT)));
    }

    [Fact]
    public void ASignExtendedValueReadsItsLow32Bits()
    {
        // Bits above 32 (sign extension of a negative 32-bit value) must not leak into the high word.
        Assert.Equal(NavigationStep.Back, NavigationInput.FromXButton(unchecked((nint)(long)0xFFFF_FFFF_0001_0020)));
        Assert.Equal(NavigationStep.Forward, NavigationInput.FromXButton(unchecked((nint)(long)0x7FFF_0000_0002_0000)));
        Assert.Equal(NavigationStep.None, NavigationInput.FromXButton(-1)); // high word 0xFFFF
        Assert.Equal(NavigationStep.None, NavigationInput.FromXButton(unchecked((nint)(long)0xFFFF_FFFF_0000_0001)));
    }

    // WM_APPCOMMAND's lParam: high word = command | device (FAPPCOMMAND_MOUSE 0x8000, KEY 0, OEM 0x1000), low word = keys.
    const int FAPPCOMMAND_MOUSE = 0x8000, FAPPCOMMAND_OEM = 0x1000;
    const int APPCOMMAND_BROWSER_BACKWARD = 1, APPCOMMAND_BROWSER_FORWARD = 2, APPCOMMAND_BROWSER_REFRESH = 3, APPCOMMAND_VOLUME_UP = 10;

    static nint AppCommand(int command, int device = 0, int keys = 0) => unchecked((nint)(int)(((command | device) << 16) | keys));

    [Fact]
    public void BrowserBackAndForwardKeys()
    {
        Assert.Equal(NavigationStep.Back, NavigationInput.FromAppCommand(AppCommand(APPCOMMAND_BROWSER_BACKWARD)));
        Assert.Equal(NavigationStep.Forward, NavigationInput.FromAppCommand(AppCommand(APPCOMMAND_BROWSER_FORWARD)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(APPCOMMAND_BROWSER_REFRESH)]
    [InlineData(APPCOMMAND_VOLUME_UP)]
    [InlineData(0x0FFF)]
    public void OtherCommandsGoNowhere(int command)
        => Assert.Equal(NavigationStep.None, NavigationInput.FromAppCommand(AppCommand(command)));

    [Fact]
    public void TheDeviceAndKeyBitsDontChangeTheCommand()
    {
        // From the mouse the device bit is the top bit of the 32: the int is negative, sign-extended on 64-bit.
        nint mouseBack = AppCommand(APPCOMMAND_BROWSER_BACKWARD, FAPPCOMMAND_MOUSE, MK_XBUTTON1);
        Assert.True(mouseBack < 0);
        Assert.Equal(NavigationStep.Back, NavigationInput.FromAppCommand(mouseBack));
        Assert.Equal(NavigationStep.Forward, NavigationInput.FromAppCommand(AppCommand(APPCOMMAND_BROWSER_FORWARD, FAPPCOMMAND_MOUSE, MK_XBUTTON2 | MK_SHIFT)));
        Assert.Equal(NavigationStep.Back, NavigationInput.FromAppCommand(AppCommand(APPCOMMAND_BROWSER_BACKWARD, FAPPCOMMAND_OEM, MK_CONTROL)));
        Assert.Equal(NavigationStep.None, NavigationInput.FromAppCommand(AppCommand(APPCOMMAND_VOLUME_UP, FAPPCOMMAND_MOUSE, 0xFFFF)));
    }

    [Fact]
    public void NegativeAppCommandValuesReadTheirLow32Bits()
    {
        Assert.Equal(NavigationStep.None, NavigationInput.FromAppCommand(-1)); // command 0xFFF
        Assert.Equal(NavigationStep.Forward, NavigationInput.FromAppCommand(unchecked((nint)(long)0x1234_5678_0002_0000)));
    }

    [Fact]
    public void BrowserBackAndForwardKeyPresses() // WM_KEYDOWN / WM_KEYUP: the virtual-key code is wParam
    {
        const int VK_BROWSER_BACK = 0xA6, VK_BROWSER_FORWARD = 0xA7, VK_BROWSER_REFRESH = 0xA8, VK_LEFT = 0x25, VK_BACK = 0x08;
        Assert.Equal(NavigationStep.Back, NavigationInput.FromKey(VK_BROWSER_BACK));
        Assert.Equal(NavigationStep.Forward, NavigationInput.FromKey(VK_BROWSER_FORWARD));
        Assert.Equal(NavigationStep.None, NavigationInput.FromKey(VK_BROWSER_REFRESH));
        Assert.Equal(NavigationStep.None, NavigationInput.FromKey(VK_LEFT));
        Assert.Equal(NavigationStep.None, NavigationInput.FromKey(VK_BACK)); // Backspace is not Browser Back
        Assert.Equal(NavigationStep.None, NavigationInput.FromKey(0));
        Assert.Equal(NavigationStep.None, NavigationInput.FromKey(-1));
        Assert.Equal(NavigationStep.Back, NavigationInput.FromKey(unchecked((nint)(long)0x7FFF_0000_0000_00A6))); // only the low 32 bits count
        Assert.Equal(NavigationStep.None, NavigationInput.FromKey(0x1_00A6)); // a different 32-bit value is not the key
    }

    // ---- the user moving on in the folder view (a queued Back or Up select is then dropped) ----

    [Theory]
    [InlineData(0x0201)] // WM_LBUTTONDOWN
    [InlineData(0x0203)] // WM_LBUTTONDBLCLK
    [InlineData(0x0204)] // WM_RBUTTONDOWN
    [InlineData(0x0207)] // WM_MBUTTONDOWN
    [InlineData(0x020B)] // WM_XBUTTONDOWN
    [InlineData(0x020A)] // WM_MOUSEWHEEL
    [InlineData(0x020E)] // WM_MOUSEHWHEEL
    [InlineData(0x00A1)] // WM_NCLBUTTONDOWN: a scroll bar
    [InlineData(0x00A4)] // WM_NCRBUTTONDOWN
    [InlineData(0x0246)] // WM_POINTERDOWN: a finger or pen tap, or the start of a pan
    [InlineData(0x0242)] // WM_NCPOINTERDOWN
    [InlineData(0x024E)] // WM_POINTERWHEEL
    [InlineData(0x024F)] // WM_POINTERHWHEEL
    public void A_mouse_press_or_the_wheel_moves_on(int msg) => Assert.True(NavigationInput.MovesOn(msg, 0));

    [Theory]
    [InlineData(0x0202)] // WM_LBUTTONUP
    [InlineData(0x0205)] // WM_RBUTTONUP
    [InlineData(0x020C)] // WM_XBUTTONUP
    [InlineData(0x0200)] // WM_MOUSEMOVE
    [InlineData(0x0245)] // WM_POINTERUPDATE: a finger or pen moving or hovering
    [InlineData(0x0247)] // WM_POINTERUP
    [InlineData(0x00A0)] // WM_NCMOUSEMOVE
    [InlineData(0x0101)] // WM_KEYUP
    [InlineData(0x0105)] // WM_SYSKEYUP
    [InlineData(0x0102)] // WM_CHAR
    [InlineData(0x000F)] // WM_PAINT
    [InlineData(0x0113)] // WM_TIMER
    public void Releases_moves_and_other_messages_dont(int msg) => Assert.False(NavigationInput.MovesOn(msg, 0x41));

    [Theory]
    [InlineData(0x41)] // A
    [InlineData(0x28)] // Down
    [InlineData(0x0D)] // Enter
    [InlineData(0x20)] // Space
    [InlineData(0x71)] // F2
    [InlineData(0x2E)] // Delete
    public void A_key_press_moves_on(int vk)
    {
        Assert.True(NavigationInput.MovesOn(0x0100, vk));  // WM_KEYDOWN
        Assert.True(NavigationInput.MovesOn(0x0104, vk));  // WM_SYSKEYDOWN, with Alt
    }

    [Theory]
    [InlineData(0x10)] // VK_SHIFT
    [InlineData(0x11)] // VK_CONTROL
    [InlineData(0x12)] // VK_MENU
    [InlineData(0x5B)] // VK_LWIN
    [InlineData(0x5C)] // VK_RWIN
    [InlineData(0xA0)] // VK_LSHIFT
    [InlineData(0xA3)] // VK_RCONTROL
    [InlineData(0xA5)] // VK_RMENU
    public void A_modifier_on_its_own_doesnt(int vk)
    {
        Assert.False(NavigationInput.MovesOn(0x0100, vk));
        Assert.False(NavigationInput.MovesOn(0x0104, vk));
    }

    [Theory]
    [InlineData(0x25)] // Left, held with Alt after Back
    [InlineData(0x26)] // Up, held with Alt after Up
    [InlineData(0x08)] // Backspace
    [InlineData(0xA6)] // VK_BROWSER_BACK
    public void A_key_held_down_repeating_doesnt(int vk)
    {
        nint first = 0x0001_0001, repeat = 0x4001_0001; // repeat count 1, scan code 1; bit 30: the key was down already
        Assert.True(NavigationInput.MovesOn(0x0104, vk, first));   // WM_SYSKEYDOWN, first press
        Assert.False(NavigationInput.MovesOn(0x0104, vk, repeat)); // its auto-repeat
        Assert.True(NavigationInput.MovesOn(0x0100, vk, first));   // WM_KEYDOWN
        Assert.False(NavigationInput.MovesOn(0x0100, vk, repeat));
        Assert.False(NavigationInput.MovesOn(0x0100, vk, unchecked((nint)0xFFFF_FFFF_4001_0001))); // sign-extended on x64
    }
}
