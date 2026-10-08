using PenTastic;

public class ProtocolTests
{
    static byte[] Hex(string s) => Convert.FromHexString(string.Concat(s.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)));
    static byte[] Seq(int from) => Enumerable.Range(from, 16).Select(i => (byte)i).ToArray();

    const string HeartbeatHex = "01 00 7F";
    const string SamplesHex = "1E 00 10 02  01 01 00 80 00 40 FF FF F6 14 78 56 34 12  02 06 FF FF 00 00 00 04 00 A6 01 00 00 00";
    const string ClientHelloHex = "1C 00 02 50 4E 54 43 01 00 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 00 03 53 32 33";
    const string ClientHelloTokenHex = "3C 00 02 50 4E 54 43 01 00 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 01 30 31 32 33 34 35 36 37 38 39 3A 3B 3C 3D 3E 3F 40 41 42 43 44 45 46 47 48 49 4A 4B 4C 4D 4E 4F 03 53 32 33";
    const string ServerHelloHex = "2A 00 01 50 4E 54 43 01 00 10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F 20 21 22 23 24 25 26 27 28 29 2A 2B 2C 2D 2E 2F 02 50 43";
    const string WelcomeHex = "3C 00 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 "
        + "80 07 00 00 38 04 00 00 00 01  00 00 00 00 00 00 00 00 80 07 00 00 38 04 00 00 01";
    const string TargetHex = "2D 00 05 00 0F 00 00 38 04 00 00 FF 02  00 00 00 00 00 00 00 00 80 07 00 00 38 04 00 00 01  "
        + "80 F8 FF FF 00 00 00 00 80 07 00 00 38 04 00 00 00";
    const string RejectHex = "04 00 04 01 01 76";

    static readonly MonitorInfo Primary = new(0, 0, 1920, 1080, 1);

    public static TheoryData<string, object> Vectors => new()
    {
        { HeartbeatHex, Heartbeat.Instance },
        { SamplesHex, new Samples([
            new(PenKind.Down, PenFlags.Barrel, 0x8000, 0x4000, 0xFFFF, -10, 20, 0x12345678),
            new(PenKind.Move, PenFlags.Eraser | PenFlags.TiltValid, 0xFFFF, 0, 0x0400, 0, -90, 1)]) },
        { ClientHelloHex, new ClientHello(1, Seq(0), AuthKind.Loopback, null, "S23") },
        { ClientHelloTokenHex, new ClientHello(1, Seq(0), AuthKind.Token, [.. Seq(0x30), .. Seq(0x40)], "S23") },
        { ServerHelloHex, new ServerHello(1, Seq(0x10), Seq(0x20), "PC") },
        { WelcomeHex, new Welcome(new byte[32], new TargetInfo(1920, 1080, 0, [Primary])) },
        { TargetHex, new TargetUpdate(new TargetInfo(3840, 1080, 0xFF, [Primary, new(-1920, 0, 1920, 1080, 0)])) },
        { RejectHex, new Reject(RejectReason.Version, "v") },
        { DrawStateHex, new DrawState(PenMode.Draw, BrushKind.Pen, 0xFFFFD166, 1083) },
        { InkUndoHex, new InkAction(InkCommand.Undo) },
        { PaperWhiteHex, new PaperChoice(Paper.White) },
    };

    const string DrawStateHex = "09 00 40 01 00 66 D1 FF FF 3B 04";
    const string InkUndoHex = "02 00 41 01";
    const string PaperWhiteHex = "02 00 42 01";

    [Fact]
    public void BadEnumValuesThrow()
    {
        Assert.Throws<InvalidDataException>(() => Protocol.Decode(Hex("40 02 00 66 D1 FF FF 3B 04")));
        Assert.Throws<InvalidDataException>(() => Protocol.Decode(Hex("40 01 04 66 D1 FF FF 3B 04")));
        Assert.Throws<InvalidDataException>(() => Protocol.Decode(Hex("41 04")));
    }

    [Theory, MemberData(nameof(Vectors))]
    public void EncodesVector(string hex, object message) => Assert.Equal(Hex(hex), Protocol.Encode(message));

    [Theory, MemberData(nameof(Vectors))]
    public void DecodesVector(string hex, object message)
    {
        var decoded = Protocol.Read(new MemoryStream(Hex(hex)));
        Assert.Equal(message.GetType(), decoded!.GetType());
        Assert.Equal(Hex(hex), Protocol.Encode(decoded));
    }

    [Fact]
    public void DecodedSamplesHaveFieldValues()
    {
        var s = Assert.IsType<Samples>(Protocol.Read(new MemoryStream(Hex(SamplesHex)))).Items;
        Assert.Equal(new Sample(PenKind.Down, PenFlags.Barrel, 0x8000, 0x4000, 0xFFFF, -10, 20, 0x12345678), s[0]);
        Assert.Equal(-90, s[1].TiltY);
    }

    [Fact]
    public void ReadsBackToBackFrames()
    {
        var stream = new MemoryStream(Hex(HeartbeatHex + " " + SamplesHex));
        Assert.IsType<Heartbeat>(Protocol.Read(stream));
        Assert.IsType<Samples>(Protocol.Read(stream));
    }

    [Fact]
    public void UnknownTypeIsIgnored() => Assert.Null(Protocol.Decode([0x6E, 1, 2, 3]));

    [Fact]
    public void TruncatedSampleThrows() =>
        Assert.Throws<EndOfStreamException>(() => Protocol.Decode(Hex(SamplesHex)[2..^1]));

    [Fact]
    public void TrailingBytesThrow() => Assert.Throws<InvalidDataException>(() => Protocol.Decode([0x7F, 0]));

    [Fact]
    public void BadMagicThrows()
    {
        var body = Hex(ClientHelloHex)[2..];
        body[1] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => Protocol.Decode(body));
    }

    [Fact]
    public void BadSampleKindThrows()
    {
        var body = Hex(SamplesHex)[2..];
        body[2] = 9;
        Assert.Throws<InvalidDataException>(() => Protocol.Decode(body));
    }

    [Fact]
    public void TruncatedFrameOnStreamThrows() =>
        Assert.ThrowsAny<EndOfStreamException>(() => Protocol.Read(new MemoryStream(Hex(SamplesHex)[..10])));
}
