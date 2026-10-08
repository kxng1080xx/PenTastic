using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PenTastic;

sealed class CanvasDocument
{
    sealed class Step
    {
        public readonly List<Stroke> Added = [], Removed = [];
    }

    readonly List<Step> _undo = [], _redo = [];
    Step? _step;
    bool _replaying;
    Stroke? _active;
    DrawingAttributes? _style;
    IncrementalStrokeHitTester? _eraser;

    public StrokeCollection Strokes { get; } = new();
    public double Width { get; }
    public double Height { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsEmpty => Strokes.Count == 0 && _active == null;
    public event Action? Changed;

    public CanvasDocument(double width, double height)
    {
        (Width, Height) = (width, height);
        Strokes.StrokesChanged += (_, e) =>
        {
            if (_replaying || _step == null) return;
            _step.Added.AddRange(e.Added);
            _step.Removed.AddRange(e.Removed);
        };
    }

    public void Apply(IEnumerable<Sample> samples, DrawState style)
    {
        foreach (var s in samples)
        {
            var point = new StylusPoint(s.X / 65535.0 * Width, s.Y / 65535.0 * Height, PressureFactor(s.Pressure / 65535f));
            switch (s.Kind)
            {
                case PenKind.Down:
                case PenKind.Move when _step == null:
                    Begin(s.Flags.HasFlag(PenFlags.Eraser) ? BrushKind.Eraser : style.Brush, style);
                    Add(point);
                    break;
                case PenKind.Move:
                    Add(point);
                    break;
                case PenKind.Up:
                case PenKind.Cancel:
                    End();
                    break;
            }
        }
    }

    public static float PressureFactor(float pressure) => 0.2f + 0.6f * Math.Clamp(pressure, 0f, 1f);

    void Begin(BrushKind brush, DrawState style)
    {
        End();
        _step = new Step();
        double w = Math.Max(0.75, style.Width / 65535.0 * Width);
        if (brush == BrushKind.Eraser)
        {
            _eraser = Strokes.GetIncrementalStrokeHitTester(new EllipseStylusShape(w * 4, w * 4));
            _eraser.StrokeHit += (_, e) =>
            {
                var rest = e.GetPointEraseResults();
                if (rest.Count == 0) Strokes.Remove(e.HitStroke);
                else Strokes.Replace(e.HitStroke, rest);
            };
            return;
        }
        var color = Theme.ToColor(style.Color);
        _style = brush switch
        {
            BrushKind.Pencil => new DrawingAttributes { Width = w * 0.6, Height = w * 0.6, IgnorePressure = true, Color = Color.FromArgb((byte)(color.A * 200 / 255), color.R, color.G, color.B) },
            BrushKind.Marker => new DrawingAttributes { Width = w * 3, Height = w * 3, IgnorePressure = true, IsHighlighter = true, Color = color },
            _ => new DrawingAttributes { Width = w, Height = w, Color = color },
        };
        _style.FitToCurve = true;
        _style.StylusTip = StylusTip.Ellipse;
    }

    void Add(StylusPoint p)
    {
        if (_eraser != null) _eraser.AddPoint(new Point(p.X, p.Y));
        else if (_active != null) _active.StylusPoints.Add(p);
        else if (_style != null) Strokes.Add(_active = new Stroke(new StylusPointCollection { p }, _style));
    }

    public void End()
    {
        _eraser?.EndHitTesting();
        _eraser = null;
        _active = null;
        _style = null;
        if (_step is { } s)
        {
            var cancelled = s.Added.Intersect(s.Removed).ToHashSet();
            s.Added.RemoveAll(cancelled.Contains);
            s.Removed.RemoveAll(cancelled.Contains);
            if (s.Added.Count > 0 || s.Removed.Count > 0)
            {
                _undo.Add(s);
                Changed?.Invoke();
                _redo.Clear();
            }
        }
        _step = null;
    }

    public void Undo()
    {
        End();
        Replay(_undo, _redo, undo: true);
    }

    public void Redo()
    {
        End();
        Replay(_redo, _undo, undo: false);
    }

    public void Clear()
    {
        End();
        if (Strokes.Count == 0) return;
        _step = new Step();
        Strokes.Clear();
        End();
    }

    void Replay(List<Step> from, List<Step> to, bool undo)
    {
        if (from.Count == 0) return;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        _replaying = true;
        try
        {
            foreach (var s in undo ? step.Added : step.Removed) Strokes.Remove(s);
            Strokes.Add(new StrokeCollection(undo ? step.Removed : step.Added));
        }
        finally
        {
            _replaying = false;
        }
        to.Add(step);
        Changed?.Invoke();
    }

    public void Save(Stream stream) => Strokes.Save(stream);

    public static CanvasDocument Load(Stream stream, double width, double height)
    {
        var doc = new CanvasDocument(width, height);
        doc.Strokes.Add(new StrokeCollection(stream));
        return doc;
    }

    public BitmapSource Render(Color? paper)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (paper is { } p) dc.DrawRectangle(new SolidColorBrush(p), null, new Rect(0, 0, Width, Height));
            Strokes.Draw(dc);
        }
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
