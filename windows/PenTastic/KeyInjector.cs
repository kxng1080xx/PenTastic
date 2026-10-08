using System.Runtime.InteropServices;

namespace PenTastic;

[StructLayout(LayoutKind.Sequential)]
struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Explicit, Size = 40)]
struct INPUT
{
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public KEYBDINPUT ki;
}

sealed class KeyInjector
{
    const uint InputKeyboard = 1, KeyUp = 0x2, Extended = 0x1;
    static readonly (KeyMods Mod, ushort Vk)[] Modifiers = [(KeyMods.Ctrl, 0x11), (KeyMods.Shift, 0x10), (KeyMods.Alt, 0x12)];

    readonly Action<IReadOnlyList<(ushort Vk, bool Up)>> _send;
    readonly object _gate = new();
    readonly List<(KeyMods Mods, ushort Vk)> _held = [];

    public KeyInjector(Action<IReadOnlyList<(ushort Vk, bool Up)>>? send = null) => _send = send ?? SendInputs;

    public static List<(ushort Vk, bool Up)> Sequence(KeyAction action, KeyMods mods, ushort vk)
    {
        var down = Modifiers.Where(m => mods.HasFlag(m.Mod)).Select(m => (m.Vk, false)).Append((vk, false)).ToList();
        var up = down.AsEnumerable().Reverse().Select(k => (k.Item1, true)).ToList();
        return action switch
        {
            KeyAction.Press => down,
            KeyAction.Release => up,
            _ => [.. down, .. up],
        };
    }

    public void Handle(KeyPress k)
    {
        lock (_gate)
        {
            if (k.Action == KeyAction.Press) _held.Add((k.Mods, k.Vk));
            if (k.Action == KeyAction.Release && !_held.Remove((k.Mods, k.Vk))) return;
            _send(Sequence(k.Action, k.Mods, k.Vk));
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var (mods, vk) in Enumerable.Reverse(_held)) _send(Sequence(KeyAction.Release, mods, vk));
            _held.Clear();
        }
    }

    static void SendInputs(IReadOnlyList<(ushort Vk, bool Up)> keys)
    {
        var inputs = keys.Select(k => new INPUT
        {
            type = InputKeyboard,
            ki = new KEYBDINPUT
            {
                wVk = k.Vk,
                wScan = (ushort)MapVirtualKey(k.Vk, 0),
                dwFlags = (k.Up ? KeyUp : 0) | (IsExtended(k.Vk) ? Extended : 0),
            },
        }).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Write($"key input blocked (error {Marshal.GetLastPInvokeError()}); the app in front may be running as administrator");
    }

    static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E;

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);
}
