using System.Runtime.InteropServices;
using PenTastic;

public class KeyTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void InputStructMatchesWindowsOnX64() => Assert.Equal(40, Marshal.SizeOf<INPUT>());

    [Fact]
    public void TapPressesModifiersFirstAndReleasesThemLast() =>
        Assert.Equal([(0x11, false), (0x5A, false), (0x5A, true), (0x11, true)],
            KeyInjector.Sequence(KeyAction.Tap, KeyMods.Ctrl, 0x5A));

    [Fact]
    public void PressAndReleaseSplitTheTap()
    {
        Assert.Equal([(0x11, false), (0x10, false), (0x5A, false)], KeyInjector.Sequence(KeyAction.Press, KeyMods.Ctrl | KeyMods.Shift, 0x5A));
        Assert.Equal([(0x5A, true), (0x10, true), (0x11, true)], KeyInjector.Sequence(KeyAction.Release, KeyMods.Ctrl | KeyMods.Shift, 0x5A));
    }

    [Fact]
    public void HeldKeysAreReleasedWhenThePhoneGoes()
    {
        var sent = new List<(ushort Vk, bool Up)>();
        var keys = new KeyInjector(s => sent.AddRange(s));
        keys.Handle(new KeyPress(KeyAction.Press, KeyMods.None, 0x20));
        keys.Handle(new KeyPress(KeyAction.Tap, KeyMods.None, 0x42));
        sent.Clear();
        keys.ReleaseAll();
        Assert.Equal([(0x20, true)], sent);
        sent.Clear();
        keys.ReleaseAll();
        Assert.Empty(sent);
    }

    [Fact]
    public void ReleaseOfAKeyNotHeldDoesNothing()
    {
        var sent = new List<(ushort Vk, bool Up)>();
        new KeyInjector(s => sent.AddRange(s)).Handle(new KeyPress(KeyAction.Release, KeyMods.None, 0x20));
        Assert.Empty(sent);
    }

    [Fact]
    public void KeyMessageMatchesVector()
    {
        Assert.Equal(Hex("05 00 50 00 01 5A 00"), Protocol.Encode(new KeyPress(KeyAction.Tap, KeyMods.Ctrl, 0x5A)));
        Assert.Equal(new KeyPress(KeyAction.Press, KeyMods.None, 0x20), Protocol.Decode(Hex("50 01 00 20 00")));
        Assert.Throws<InvalidDataException>(() => Protocol.Decode(Hex("50 03 00 20 00")));
    }

    [Fact]
    public void PageMessageMatchesVector()
    {
        Assert.Equal(Hex("03 00 43 02 05"), Protocol.Encode(new PageInfo(2, 5)));
        Assert.Equal(new PageInfo(1, 1), Protocol.Decode(Hex("43 01 01")));
    }
}
