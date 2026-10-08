using PenTastic;

public class MirrorTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void MirrorMessagesMatchVectors()
    {
        Assert.Equal(Hex("06 00 20 01 80 07 38 04"), Protocol.Encode(new MirrorRequest(true, 1920, 1080)));
        Assert.Equal(Hex("0E 00 21 07 00 00 00 0F 80 07 38 04 00 00 00 01"),
            Protocol.Encode(new VideoChunk(7, VideoFlags.Start | VideoFlags.End | VideoFlags.Key | VideoFlags.NewStream, 1920, 1080, [0, 0, 0, 1])));
        Assert.Equal(Hex("05 00 22 07 00 00 00"), Protocol.Encode(new VideoAck(7)));
        Assert.Equal(Hex("07 00 23 00 40 00 20 00 80"), Protocol.Encode(new ViewRect(0x4000, 0x2000, 0x8000)));
        Assert.Equal(new ViewRect(0x4000, 0x2000, 0x8000), Protocol.Decode(Hex("23 00 40 00 20 00 80")));

        var chunk = Assert.IsType<VideoChunk>(Protocol.Decode(Hex("21 07 00 00 00 0F 80 07 38 04 00 00 00 01")));
        Assert.Equal((7u, (ushort)1920, (ushort)1080), (chunk.FrameId, chunk.Width, chunk.Height));
        Assert.Equal([0, 0, 0, 1], chunk.Data);
        Assert.True(Assert.IsType<MirrorRequest>(Protocol.Decode(Hex("20 01 80 07 38 04"))).On);
        Assert.Throws<InvalidDataException>(() => Protocol.Decode(Hex("20 02 80 07 38 04")));
    }

    [Fact]
    public void SplitFrameMarksStartAndEnd()
    {
        var frame = Enumerable.Range(0, 250).Select(i => (byte)i).ToArray();
        var parts = Protocol.SplitFrame(3, VideoFlags.Key, 64, 32, frame, maxChunk: 100).ToList();
        Assert.Equal(3, parts.Count);
        Assert.Equal(VideoFlags.Key | VideoFlags.Start, parts[0].Flags);
        Assert.Equal(VideoFlags.Key, parts[1].Flags);
        Assert.Equal(VideoFlags.Key | VideoFlags.End, parts[2].Flags);
        Assert.Equal(frame, parts.SelectMany(p => p.Data));
        Assert.All(parts, p => Assert.True(Protocol.Encode(p).Length <= 65537));
    }

    [Fact]
    public void SmallFrameIsOneChunk()
    {
        var p = Assert.Single(Protocol.SplitFrame(1, VideoFlags.None, 2, 2, [1, 2, 3]));
        Assert.Equal(VideoFlags.Start | VideoFlags.End, p.Flags);
    }

    [Fact]
    public void CropsTheZoomedView()
    {
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), Mirror.Crop(ViewRect.Whole, 1920, 1080));
        Assert.Equal(Rectangle.FromLTRB(480, 270, 1440, 810), Mirror.Crop(new ViewRect(16384, 16384, 32768), 1920, 1080));
        Assert.Equal(Rectangle.FromLTRB(1680, 945, 1920, 1080), Mirror.Crop(new ViewRect(57344, 57344, 8192), 1920, 1080));
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), Mirror.Crop(new ViewRect(65535, 65535, 65535), 1920, 1080));
        Assert.Equal(new Rectangle(1918, 0, 2, 2), Mirror.Crop(new ViewRect(65535, 0, 0), 1920, 1080));
    }

    [Theory]
    [InlineData(1920, 1080, 0, 0, 1920, 1080)]
    [InlineData(1920, 1080, 1906, 1072, 1920, 1080)]
    [InlineData(3840, 2160, 1906, 1072, 1906, 1072)]
    [InlineData(2560, 1080, 1280, 0, 1280, 540)]
    public void FitsInsideTheLimit(int w, int h, int maxW, int maxH, int ew, int eh)
    {
        Assert.Equal(new Size(ew, eh), Mirror.Fit(w, h, maxW, maxH));
    }
}
