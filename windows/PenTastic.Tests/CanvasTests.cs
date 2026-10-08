using System.Windows.Media;
using PenTastic;
using Color = System.Windows.Media.Color;

public class CanvasTests
{
    static readonly DrawState Pen = new(PenMode.Draw, BrushKind.Pen, 0xFF000000, 2000);
    static readonly DrawState Eraser = Pen with { Brush = BrushKind.Eraser };

    static Sample[] Line(double x0, double x1, PenFlags flags = PenFlags.None) =>
        Enumerable.Range(0, 21).Select(i =>
        {
            var kind = i == 0 ? PenKind.Down : i == 20 ? PenKind.Up : PenKind.Move;
            return new Sample(kind, flags, (ushort)((x0 + (x1 - x0) * i / 20) * 65535), 32768, 50000, 0, 0, 0);
        }).ToArray();

    static CanvasDocument Doc() => new(400, 200);

    [Fact]
    public void EachPenDownToUpIsOneStroke()
    {
        var d = Doc();
        d.Apply(Line(0.1, 0.4), Pen);
        d.Apply(Line(0.6, 0.9), Pen);
        Assert.Equal(2, d.Strokes.Count);
        Assert.Equal(20, d.Strokes[0].StylusPoints.Count);
    }

    [Fact]
    public void UndoAndRedoWholeStrokes()
    {
        var d = Doc();
        d.Apply(Line(0.1, 0.4), Pen);
        d.Apply(Line(0.6, 0.9), Pen);
        d.Undo();
        Assert.Single(d.Strokes);
        d.Redo();
        Assert.Equal(2, d.Strokes.Count);
        d.Undo();
        d.Apply(Line(0.5, 0.6), Pen);
        Assert.False(d.CanRedo);
    }

    [Fact]
    public void EraserCutsAStrokeInTwoAndUndoRestoresIt()
    {
        var d = Doc();
        d.Apply(Line(0.1, 0.9), Pen);
        var original = d.Strokes[0];
        d.Apply([new Sample(PenKind.Down, PenFlags.None, 32768, 26000, 0, 0, 0, 0),
                 new Sample(PenKind.Move, PenFlags.None, 32768, 39000, 0, 0, 0, 0),
                 new Sample(PenKind.Up, PenFlags.None, 32768, 39000, 0, 0, 0, 0)], Eraser);
        Assert.Equal(2, d.Strokes.Count);
        Assert.DoesNotContain(original, d.Strokes);
        d.Undo();
        Assert.Equal([original], d.Strokes);
    }

    [Fact]
    public void PenEraserFlagErasesWhateverTheBrush()
    {
        var d = Doc();
        d.Apply(Line(0.1, 0.9), Pen);
        d.Apply(Line(0.0, 1.0, PenFlags.Eraser), Pen);
        Assert.Empty(d.Strokes);
    }

    [Fact]
    public void ClearIsOneUndoStep()
    {
        var d = Doc();
        d.Apply(Line(0.1, 0.4), Pen);
        d.Apply(Line(0.6, 0.9), Pen);
        d.Clear();
        Assert.Empty(d.Strokes);
        d.Undo();
        Assert.Equal(2, d.Strokes.Count);
    }

    [Fact]
    public void MarkerIsAHighlighterAndPencilIsSofter()
    {
        var d = Doc();
        d.Apply(Line(0.1, 0.4), Pen with { Brush = BrushKind.Marker });
        d.Apply(Line(0.1, 0.4), Pen with { Brush = BrushKind.Pencil });
        Assert.True(d.Strokes[0].DrawingAttributes.IsHighlighter);
        Assert.True(d.Strokes[1].DrawingAttributes.Color.A < 255);
    }

    [Fact]
    public void RendersOnPaperOrTransparent()
    {
        RunSta(() =>
        {
            var d = Doc();
            d.Apply(Line(0.1, 0.9), Pen);
            var onPaper = Pixels(d.Render(Colors.White));
            var bare = Pixels(d.Render(null));
            Assert.Equal(0xFF, onPaper[0].A);
            Assert.Equal(0, bare[0].A);
            Assert.Equal(0xFF, bare[100 * 400 + 200].A);
        });
    }

    [Fact]
    public void PagesSurviveARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pentastic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pages = new CanvasPages(dir, 400, 200);
            int changes = 0;
            pages.Changed += () => changes++;
            pages.CurrentPage.Doc.Apply(Line(0.1, 0.4), Pen);
            pages.Add();
            pages.SetPaper(Paper.Black);
            pages.CurrentPage.Doc.Apply(Line(0.1, 0.4), Pen);
            pages.CurrentPage.Doc.Apply(Line(0.5, 0.9), Pen);
            pages.Add();
            pages.Delete();
            Assert.True(changes >= 6);
            pages.Save();

            var back = new CanvasPages(dir, 400, 200);
            Assert.Equal(2, back.Pages.Count);
            Assert.Equal(1, back.Current);
            Assert.Equal([Paper.White, Paper.Black], back.Pages.Select(p => p.Paper));
            Assert.Equal([1, 2], back.Pages.Select(p => p.Doc.Strokes.Count));
            Assert.Equal(2, Directory.GetFiles(dir, "*.isf").Length);

            back.Delete();
            back.Delete();
            Assert.Single(back.Pages);
            Assert.True(back.CurrentPage.Doc.IsEmpty);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingOrBrokenSaveStartsWithOneBlankPage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pentastic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Single(new CanvasPages(dir, 400, 200).Pages);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "pages.json"), "{not json");
            Assert.Single(new CanvasPages(dir, 400, 200).Pages);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    static Color[] Pixels(System.Windows.Media.Imaging.BitmapSource b)
    {
        var px = new byte[b.PixelWidth * b.PixelHeight * 4];
        b.CopyPixels(px, b.PixelWidth * 4, 0);
        return Enumerable.Range(0, px.Length / 4).Select(i => Color.FromArgb(px[i * 4 + 3], px[i * 4 + 2], px[i * 4 + 1], px[i * 4])).ToArray();
    }

    static void RunSta(Action a)
    {
        Exception? error = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }
}
