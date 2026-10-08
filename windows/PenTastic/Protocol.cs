using System.Buffers.Binary;
using System.Text;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

enum AuthKind : byte { Loopback = 0, Token = 1, Proof = 2 }
enum RejectReason : byte { Version = 1, BadToken = 2, NotPaired = 3, Busy = 4 }

readonly record struct Sample(PenKind Kind, PenFlags Flags, ushort X, ushort Y, ushort Pressure, sbyte TiltX, sbyte TiltY, uint TimeMs);
readonly record struct MonitorInfo(int X, int Y, int Width, int Height, byte Flags);
sealed record TargetInfo(int Width, int Height, byte Selected, MonitorInfo[] Monitors);

sealed record ServerHello(ushort Version, byte[] ServerId, byte[] Nonce, string Name);
sealed record ClientHello(ushort Version, byte[] ClientId, AuthKind Auth, byte[]? Proof, string Name);
sealed record Welcome(byte[] Secret, TargetInfo Target);
sealed record Reject(RejectReason Reason, string Message);
sealed record TargetUpdate(TargetInfo Target);
sealed record Samples(Sample[] Items);
sealed record Heartbeat { public static readonly Heartbeat Instance = new(); }

enum PenMode : byte { Control = 0, Draw = 1 }
enum BrushKind : byte { Pen = 0, Pencil = 1, Marker = 2, Eraser = 3 }
enum InkCommand : byte { Undo = 1, Clear = 2, Redo = 3 }
enum KeyAction : byte { Tap = 0, Press = 1, Release = 2 }
[Flags] enum KeyMods : byte { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }

sealed record DrawState(PenMode Mode, BrushKind Brush, uint Color, ushort Width);
sealed record InkAction(InkCommand Command);

enum Paper : byte { Dark = 0, White = 1, Black = 2 }
sealed record PaperChoice(Paper Paper);
sealed record PageInfo(byte Index, byte Count);

[Flags] enum VideoFlags : byte { None = 0, Start = 1, End = 2, Key = 4, NewStream = 8 }
sealed record MirrorRequest(bool On, ushort MaxWidth, ushort MaxHeight);
sealed record VideoChunk(uint FrameId, VideoFlags Flags, ushort Width, ushort Height, byte[] Data);
sealed record VideoAck(uint FrameId);
sealed record KeyPress(KeyAction Action, KeyMods Mods, ushort Vk);
sealed record ViewRect(ushort X, ushort Y, ushort Size)
{
    public static readonly ViewRect Whole = new(0, 0, 65535);
}

static class Protocol
{
    public const ushort Version = 1;
    public const int Port = 47810;
    public const int DiscoveryPort = 47811;

    static readonly byte[] Magic = "PNTC"u8.ToArray();
    const byte TServerHello = 0x01, TClientHello = 0x02, TWelcome = 0x03, TReject = 0x04, TTarget = 0x05,
        TSamples = 0x10, TDrawState = 0x40, TInkAction = 0x41, TPaper = 0x42, TPage = 0x43, TMirror = 0x20, TVideo = 0x21, TVideoAck = 0x22, TView = 0x23, TKey = 0x50, THeartbeat = 0x7F;

    public static byte[] Encode(object message)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        switch (message)
        {
            case ServerHello m:
                w.Write(TServerHello); w.Write(Magic); w.Write(m.Version);
                WriteFixed(w, m.ServerId, 16); WriteFixed(w, m.Nonce, 16); WriteStr(w, m.Name);
                break;
            case ClientHello m:
                w.Write(TClientHello); w.Write(Magic); w.Write(m.Version);
                WriteFixed(w, m.ClientId, 16); w.Write((byte)m.Auth);
                if (m.Auth != AuthKind.Loopback) WriteFixed(w, m.Proof!, 32);
                WriteStr(w, m.Name);
                break;
            case Welcome m:
                w.Write(TWelcome); WriteFixed(w, m.Secret, 32); WriteTarget(w, m.Target);
                break;
            case Reject m:
                w.Write(TReject); w.Write((byte)m.Reason); WriteStr(w, m.Message);
                break;
            case TargetUpdate m:
                w.Write(TTarget); WriteTarget(w, m.Target);
                break;
            case Samples m:
                w.Write(TSamples); w.Write(checked((byte)m.Items.Length));
                foreach (var s in m.Items)
                {
                    w.Write((byte)s.Kind); w.Write((byte)s.Flags); w.Write(s.X); w.Write(s.Y); w.Write(s.Pressure);
                    w.Write(s.TiltX); w.Write(s.TiltY); w.Write(s.TimeMs);
                }
                break;
            case DrawState m:
                w.Write(TDrawState); w.Write((byte)m.Mode); w.Write((byte)m.Brush); w.Write(m.Color); w.Write(m.Width);
                break;
            case MirrorRequest m:
                w.Write(TMirror); w.Write(m.On); w.Write(m.MaxWidth); w.Write(m.MaxHeight);
                break;
            case VideoChunk m:
                w.Write(TVideo); w.Write(m.FrameId); w.Write((byte)m.Flags); w.Write(m.Width); w.Write(m.Height); w.Write(m.Data);
                break;
            case KeyPress m:
                w.Write(TKey); w.Write((byte)m.Action); w.Write((byte)m.Mods); w.Write(m.Vk);
                break;
            case VideoAck m:
                w.Write(TVideoAck); w.Write(m.FrameId);
                break;
            case ViewRect m:
                w.Write(TView); w.Write(m.X); w.Write(m.Y); w.Write(m.Size);
                break;
            case PaperChoice m:
                w.Write(TPaper); w.Write((byte)m.Paper);
                break;
            case PageInfo m:
                w.Write(TPage); w.Write(m.Index); w.Write(m.Count);
                break;
            case InkAction m:
                w.Write(TInkAction); w.Write((byte)m.Command);
                break;
            case Heartbeat:
                w.Write(THeartbeat);
                break;
            default:
                throw new ArgumentException($"not a protocol message: {message}");
        }
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)(bytes.Length - 2)));
        return bytes;
    }

    public static bool IsDiscoveryRequest(ReadOnlySpan<byte> d) =>
        d.Length == 7 && d[..4].SequenceEqual(Magic) && BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) == Version && d[6] == 0x30;

    public static byte[] DiscoveryReply(byte[] serverId, ushort port, string name, int targetW, int targetH, byte monitors)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Magic); w.Write(Version); w.Write((byte)0x31);
        WriteFixed(w, serverId, 16); w.Write(port); WriteStr(w, name);
        w.Write(targetW); w.Write(targetH); w.Write(monitors);
        return ms.ToArray();
    }

    public static IEnumerable<VideoChunk> SplitFrame(uint id, VideoFlags flags, ushort width, ushort height, byte[] frame, int maxChunk = 60_000)
    {
        for (int at = 0; at == 0 || at < frame.Length; at += maxChunk)
        {
            int n = Math.Min(maxChunk, frame.Length - at);
            var f = flags | (at == 0 ? VideoFlags.Start : 0) | (at + n >= frame.Length ? VideoFlags.End : 0);
            yield return new VideoChunk(id, f, width, height, frame[at..(at + n)]);
        }
    }

    public static object? Read(Stream s) => Decode(ReadBody(s));

    public static byte[] ReadBody(Stream s)
    {
        Span<byte> len = stackalloc byte[2];
        s.ReadExactly(len);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(len);
        if (n == 0) throw new InvalidDataException("empty frame");
        var body = new byte[n];
        s.ReadExactly(body);
        return body;
    }

    public static object? Decode(byte[] body)
    {
        var r = new BinaryReader(new MemoryStream(body));
        object? m = r.ReadByte() switch
        {
            TServerHello => new ServerHello(ReadMagicVersion(r), ReadFixed(r, 16), ReadFixed(r, 16), ReadStr(r)),
            TClientHello => ReadClientHello(r),
            TWelcome => new Welcome(ReadFixed(r, 32), ReadTarget(r)),
            TReject => new Reject((RejectReason)r.ReadByte(), ReadStr(r)),
            TTarget => new TargetUpdate(ReadTarget(r)),
            TSamples => new Samples(ReadSamples(r)),
            TDrawState => new DrawState(
                Enum<PenMode>(r.ReadByte()), Enum<BrushKind>(r.ReadByte()), r.ReadUInt32(), r.ReadUInt16()),
            TInkAction => new InkAction(Enum<InkCommand>(r.ReadByte())),
            TPaper => new PaperChoice(Enum<Paper>(r.ReadByte())),
            TPage => new PageInfo(r.ReadByte(), r.ReadByte()),
            TMirror => new MirrorRequest(r.ReadByte() switch { 0 => false, 1 => true, var b => throw new InvalidDataException($"bad bool {b}") }, r.ReadUInt16(), r.ReadUInt16()),
            TVideo => new VideoChunk(r.ReadUInt32(), (VideoFlags)r.ReadByte(), r.ReadUInt16(), r.ReadUInt16(), r.ReadBytes(body.Length - 10)),
            TVideoAck => new VideoAck(r.ReadUInt32()),
            TKey => new KeyPress(Enum<KeyAction>(r.ReadByte()), (KeyMods)(r.ReadByte() & 7), r.ReadUInt16()),
            TView => new ViewRect(r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16()),
            THeartbeat => Heartbeat.Instance,
            _ => null,
        };
        if (m != null && r.BaseStream.Position != body.Length) throw new InvalidDataException("trailing bytes");
        return m;
    }

    static T Enum<T>(byte b) where T : struct, Enum =>
        System.Enum.IsDefined(typeof(T), b) ? (T)(object)b : throw new InvalidDataException($"bad {typeof(T).Name} {b}");

    static ClientHello ReadClientHello(BinaryReader r)
    {
        var version = ReadMagicVersion(r);
        var id = ReadFixed(r, 16);
        var auth = (AuthKind)r.ReadByte();
        byte[]? proof = null;
        switch (auth)
        {
            case AuthKind.Loopback: break;
            case AuthKind.Token or AuthKind.Proof: proof = ReadFixed(r, 32); break;
            default: throw new InvalidDataException($"bad auth kind {auth}");
        }
        return new ClientHello(version, id, auth, proof, ReadStr(r));
    }

    static Sample[] ReadSamples(BinaryReader r)
    {
        var items = new Sample[r.ReadByte()];
        for (int i = 0; i < items.Length; i++)
        {
            var kind = r.ReadByte();
            if (kind > (byte)PenKind.Cancel) throw new InvalidDataException($"bad sample kind {kind}");
            items[i] = new Sample((PenKind)kind, (PenFlags)r.ReadByte(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16(),
                r.ReadSByte(), r.ReadSByte(), r.ReadUInt32());
        }
        return items;
    }

    static void WriteTarget(BinaryWriter w, TargetInfo t)
    {
        w.Write(t.Width); w.Write(t.Height); w.Write(t.Selected); w.Write(checked((byte)t.Monitors.Length));
        foreach (var m in t.Monitors)
        {
            w.Write(m.X); w.Write(m.Y); w.Write(m.Width); w.Write(m.Height); w.Write(m.Flags);
        }
    }

    static TargetInfo ReadTarget(BinaryReader r)
    {
        int width = r.ReadInt32(), height = r.ReadInt32();
        byte selected = r.ReadByte();
        var monitors = new MonitorInfo[r.ReadByte()];
        for (int i = 0; i < monitors.Length; i++)
            monitors[i] = new MonitorInfo(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadByte());
        return new TargetInfo(width, height, selected, monitors);
    }

    static ushort ReadMagicVersion(BinaryReader r)
    {
        if (!ReadFixed(r, 4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("bad magic");
        return r.ReadUInt16();
    }

    static void WriteFixed(BinaryWriter w, byte[] b, int n)
    {
        if (b.Length != n) throw new ArgumentException($"expected {n} bytes, got {b.Length}");
        w.Write(b);
    }

    static byte[] ReadFixed(BinaryReader r, int n)
    {
        var b = r.ReadBytes(n);
        return b.Length == n ? b : throw new EndOfStreamException();
    }

    static void WriteStr(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length > 255) b = b[..255];
        w.Write((byte)b.Length);
        w.Write(b);
    }

    static string ReadStr(BinaryReader r) => Encoding.UTF8.GetString(ReadFixed(r, r.ReadByte()));
}
