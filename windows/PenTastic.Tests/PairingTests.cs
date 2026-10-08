using PenTastic;

public class PairingTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"pentastic-pairing-{Guid.NewGuid():N}.json");
    static byte[] Seq(int from, int n = 16) => Enumerable.Range(from, n).Select(i => (byte)i).ToArray();
    static readonly byte[] Phone = Seq(0);
    static readonly byte[] Nonce = Seq(0x20);

    public void Dispose() => File.Delete(_path);

    static ClientHello Hello(AuthKind auth, byte[]? proof = null) => new(1, Phone, auth, proof, "S23");

    [Fact]
    public void QrTokenPairsOnceThenProofReconnects()
    {
        var pairing = new Pairing(_path);
        string? paired = null;
        pairing.Paired += name => paired = name;
        var token = pairing.StartPairing();

        var secret = pairing.Authenticate(Hello(AuthKind.Token, Pairing.Proof(token, Nonce, Phone)), Nonce, loopback: false, out _);
        Assert.Equal(Pairing.Secret(token, Phone), secret);
        Assert.Equal("S23", paired);

        Assert.Null(pairing.Authenticate(Hello(AuthKind.Token, Pairing.Proof(token, Nonce, Phone)), Nonce, false, out var reason));
        Assert.Equal(RejectReason.BadToken, reason);

        var again = new Pairing(_path);
        Assert.Equal(pairing.ServerId, again.ServerId);
        Assert.Equal(secret!, again.Authenticate(Hello(AuthKind.Proof, Pairing.Proof(secret!, Nonce, Phone)), Nonce, false, out _));
    }

    [Fact]
    public void RejectsStrangers()
    {
        var pairing = new Pairing(_path);
        Assert.Null(pairing.Authenticate(Hello(AuthKind.Loopback), Nonce, loopback: false, out _));
        Assert.Equal([], pairing.Authenticate(Hello(AuthKind.Loopback), Nonce, loopback: true, out _)!);
        Assert.Null(pairing.Authenticate(Hello(AuthKind.Token, new byte[32]), Nonce, false, out var reason));
        Assert.Equal(RejectReason.BadToken, reason);
        Assert.Null(pairing.Authenticate(Hello(AuthKind.Proof, new byte[32]), Nonce, false, out reason));
        Assert.Equal(RejectReason.NotPaired, reason);

        var token = pairing.StartPairing();
        Assert.Null(pairing.Authenticate(Hello(AuthKind.Token, Pairing.Proof(new byte[16], Nonce, Phone)), Nonce, false, out _));
        pairing.StopPairing();
        Assert.Null(pairing.Authenticate(Hello(AuthKind.Token, Pairing.Proof(token, Nonce, Phone)), Nonce, false, out _));
    }

    [Fact]
    public void ReplayedProofIsRejected()
    {
        var pairing = new Pairing(_path);
        var token = pairing.StartPairing();
        var secret = pairing.Authenticate(Hello(AuthKind.Token, Pairing.Proof(token, Nonce, Phone)), Nonce, false, out _)!;
        Assert.Null(pairing.Authenticate(Hello(AuthKind.Proof, Pairing.Proof(secret, new byte[16], Phone)), Nonce, false, out _));
    }

    [Fact]
    public void CryptoMatchesVectors()
    {
        var secret = Seq(0, 32);
        Assert.Equal(Convert.FromHexString("FDB7A2DA754E32570C236091654532D0CA1C09C3D8C4C1864BA82E0BE1ADE74F"), Pairing.Proof(secret, Nonce, Phone));
        Assert.Equal(Convert.FromHexString(SecretVector), Pairing.Secret(Seq(0x30), Phone));

        using var pc = new FrameCipher(secret, Nonce, Phone, server: true);
        var sealedHeartbeat = pc.Seal(Protocol.Encode(Heartbeat.Instance));
        Assert.Equal(SealedHeartbeatVector, Convert.ToHexString(sealedHeartbeat));
        Assert.NotEqual(sealedHeartbeat, pc.Seal(Protocol.Encode(Heartbeat.Instance)));
        var fromPhone = Convert.FromHexString("1100E678B58B51A9B2CB9E5FDFBFB3A03AF57B");
        Assert.Equal([0x7F], pc.Open(fromPhone[2..]));
    }

    const string SecretVector = "B0463C1A44672CDE8074987C52268C10CAA717607C45F33FC0FB21DB356A7620";
    const string SealedHeartbeatVector = "1100788745C31C6CF33C750398A46ABC8641BD";

    [Fact]
    public void CipherRoundTripsAndRejectsTampering()
    {
        var secret = Seq(0, 32);
        using var pc = new FrameCipher(secret, Nonce, Phone, server: true);
        using var phone = new FrameCipher(secret, Nonce, Phone, server: false);
        var video = Protocol.Encode(new VideoChunk(7, VideoFlags.Start | VideoFlags.End, 2, 2, new byte[60_000]));
        for (int i = 0; i < 3; i++)
            Assert.Equal(video[2..], phone.Open(Protocol.ReadBody(new MemoryStream(pc.Seal(video)))));

        var tampered = pc.Seal(video)[2..];
        tampered[5] ^= 1;
        Assert.Throws<InvalidDataException>(() => phone.Open(tampered));

        using var other = new FrameCipher(secret, Seq(0x40), Phone, server: false);
        Assert.Throws<InvalidDataException>(() => other.Open(pc.Seal(video)[2..]));
        using var echo = new FrameCipher(secret, Nonce, Phone, server: true);
        Assert.Throws<InvalidDataException>(() => echo.Open(new FrameCipher(secret, Nonce, Phone, server: true).Seal(video)[2..]));
    }

    [Fact]
    public void LinkCarriesEverythingThePhoneNeeds() => Assert.Equal(
        "pentastic://pair?id=000102030405060708090A0B0C0D0E0F&t=202122232425262728292A2B2C2D2E2F&p=47810&h=192.168.1.5,10.0.0.2&n=My%20PC",
        Pairing.Link(Phone, Nonce, 47810, ["192.168.1.5", "10.0.0.2"], "My PC"));

    [Fact]
    public void LinkCarriesBluetoothAddress() =>
        Assert.EndsWith("&n=PC&bt=0011223344AA", Pairing.Link(Phone, Nonce, 47810, [], "PC", "0011223344AA"));

    [Fact]
    public void DiscoveryMatchesVectors()
    {
        Assert.True(Protocol.IsDiscoveryRequest(Convert.FromHexString("504E5443010030")));
        Assert.False(Protocol.IsDiscoveryRequest(Convert.FromHexString("504E5443020030")));
        Assert.Equal(Convert.FromHexString("504E5443010031" + "101112131415161718191A1B1C1D1E1F" + "C2BA" + "025043" + "80070000" + "38040000" + "01"),
            Protocol.DiscoveryReply(Seq(0x10), 47810, "PC", 1920, 1080, 1));
    }
}
