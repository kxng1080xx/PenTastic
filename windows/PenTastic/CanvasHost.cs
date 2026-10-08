using System.Windows.Threading;

namespace PenTastic;

sealed class CanvasHost(Dispatcher ui, Func<System.Drawing.Size> canvasSize)
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenTastic", "Canvas");

    CanvasWindow? _window;

    public event Action<Paper>? PaperChosen;
    public event Action<int, int>? PageShown;

    CanvasWindow Window => _window ??= Create();

    CanvasWindow Create()
    {
        var size = canvasSize();
        return new CanvasWindow(new CanvasPages(Folder, size.Width, size.Height), p => PaperChosen?.Invoke(p), (i, n) => PageShown?.Invoke(i, n));
    }

    public void Show() => ui.BeginInvoke(() => Window.ShowCanvas());
    public void Draw(Sample[] samples, DrawState style) => ui.BeginInvoke(() => Window.Draw(samples, style));
    public void Command(InkCommand command) => ui.BeginInvoke(() => Window.Command(command));
    public void SetPaper(Paper paper) => ui.BeginInvoke(() => Window.SetPaper(paper));
    public void SaveNow() => _window?.SaveNow();
}
