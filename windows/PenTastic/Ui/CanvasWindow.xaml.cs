using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PenTastic;

partial class CanvasWindow : Window
{
    static readonly Color White = Colors.White, Black = Color.FromRgb(0x0C, 0x0C, 0x0E);
    const double MinZoom = 0.1, MaxZoom = 8;

    readonly CanvasPages _pages;
    readonly Action<Paper> _paperChosen;
    readonly Action<int, int> _pageShown;
    readonly DispatcherTimer _saveTimer;
    bool _fitted = true, _settingPaper;
    bool _full;
    WindowState _restoreState;
    WindowStyle _restoreStyle;
    ResizeMode _restoreResize;

    public Paper Paper { get; private set; } = Paper.White;

    CanvasDocument _doc => _pages.CurrentPage.Doc;

    public CanvasWindow(CanvasPages pages, Action<Paper> paperChosen, Action<int, int> pageShown)
    {
        InitializeComponent();
        _pages = pages;
        _paperChosen = paperChosen;
        _pageShown = pageShown;
        Page.Width = _doc.Width;
        Page.Height = _doc.Height;
        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher) { IsEnabled = false };
        _pages.Changed += () =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        ShowPage(announce: false);
        Theme.Backdrop(this);
        AppStatus.Current.PropertyChanged += OnStatus;
        UpdateFooter();
        ContentRendered += (_, _) => FitToWindow();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) SetFullScreen(!_full);
            else if (e.Key == Key.Escape && _full) SetFullScreen(false);
            else return;
            e.Handled = true;
        };
        MainWindow.HideWhenMinimized(this);
        Closing += (_, e) =>
        {
            if (MainWindow.Quitting) return;
            e.Cancel = true;
            Hide();
        };
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            _pages.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"canvas save failed: {e.Message}");
        }
    }

    void ShowPage(bool announce = true)
    {
        Ink.Strokes = _doc.Strokes;
        SetPaper(_pages.CurrentPage.Paper);
        PageText.Text = $"{_pages.Current + 1} / {_pages.Pages.Count}";
        PrevPage.IsEnabled = _pages.Current > 0;
        NextPage.IsEnabled = _pages.Current < _pages.Pages.Count - 1;
        if (!announce) return;
        _paperChosen(_pages.CurrentPage.Paper);
        _pageShown(_pages.Current + 1, _pages.Pages.Count);
    }

    void GoPrev(object sender, RoutedEventArgs e)
    {
        _pages.Go(_pages.Current - 1);
        ShowPage();
    }

    void GoNext(object sender, RoutedEventArgs e)
    {
        _pages.Go(_pages.Current + 1);
        ShowPage();
    }

    void AddPage(object sender, RoutedEventArgs e)
    {
        _pages.Add();
        ShowPage();
    }

    void DeletePage(object sender, RoutedEventArgs e)
    {
        if (!_doc.IsEmpty && MessageBox.Show(this, $"Delete page {_pages.Current + 1} and its drawing? This can't be undone.", "Delete page",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _pages.Delete();
        ShowPage();
    }

    public void ShowCanvas()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        MainWindow.BringToFront(this);
    }

    public void Draw(Sample[] samples, DrawState style)
    {
        _doc.Apply(samples, style);
        var last = samples[^1];
        bool near = last.Kind is not (PenKind.HoverExit or PenKind.Cancel);
        PenRing.Visibility = near ? Visibility.Visible : Visibility.Collapsed;
        if (!near) return;
        double size = (last.Kind is PenKind.Down or PenKind.Move ? 6 : 14) / Zoom.ScaleX;
        PenRing.Width = PenRing.Height = size;
        PenRing.StrokeThickness = 1.5 / Zoom.ScaleX;
        Canvas.SetLeft(PenRing, last.X / 65535.0 * _doc.Width - size / 2);
        Canvas.SetTop(PenRing, last.Y / 65535.0 * _doc.Height - size / 2);
    }

    public void Command(InkCommand command)
    {
        switch (command)
        {
            case InkCommand.Undo: _doc.Undo(); break;
            case InkCommand.Redo: _doc.Redo(); break;
            case InkCommand.Clear: _doc.Clear(); break;
        }
    }

    public void SetPaper(Paper paper)
    {
        Paper = paper == Paper.White ? Paper.White : Paper.Black;
        _pages.SetPaper(Paper);
        var color = Paper == Paper.White ? White : Black;
        Sheet.Background = new SolidColorBrush(color);
        PenRing.Stroke = new SolidColorBrush(Paper == Paper.White ? Color.FromRgb(0x5B, 0x6B, 0x85) : Color.FromRgb(0xC9, 0xD4, 0xE6));
        _settingPaper = true;
        (Paper == Paper.White ? PaperWhite : PaperBlack).IsChecked = true;
        _settingPaper = false;
    }

    void PaperChecked(object sender, RoutedEventArgs e)
    {
        if (_settingPaper) return;
        var paper = sender == PaperWhite ? Paper.White : Paper.Black;
        SetPaper(paper);
        _paperChosen(paper);
    }

    void SetZoom(double z, bool fitted = false)
    {
        z = Math.Clamp(z, MinZoom, MaxZoom);
        Zoom.ScaleX = Zoom.ScaleY = z;
        ZoomText.Text = $"{Math.Round(z * 100)}%";
        _fitted = fitted;
        UpdateFooter();
    }

    void ToggleFullScreen(object sender, RoutedEventArgs e) => SetFullScreen(!_full);

    void SetFullScreen(bool on)
    {
        if (on == _full) return;
        _full = on;
        if (on)
        {
            (_restoreState, _restoreStyle, _restoreResize) = (WindowState, WindowStyle, ResizeMode);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            WindowState = _restoreState;
        }
        Bar.Visibility = Footer.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ExitFull.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Page.Margin = new Thickness(on ? 0 : 36);
        if (_fitted) Dispatcher.BeginInvoke(FitToWindow, DispatcherPriority.Loaded);
    }

    void Fit(object sender, RoutedEventArgs e) => FitToWindow();

    void FitToWindow()
    {
        double w = Scroller.ActualWidth - 2 * Page.Margin.Left, h = Scroller.ActualHeight - 2 * Page.Margin.Top;
        if (w > 0 && h > 0) SetZoom(Math.Min(w / _doc.Width, h / _doc.Height), fitted: true);
    }

    void ScrollerResized(object sender, SizeChangedEventArgs e)
    {
        if (_fitted) FitToWindow();
    }

    void ZoomIn(object sender, RoutedEventArgs e) => SetZoom(Zoom.ScaleX * 1.25);
    void ZoomOut(object sender, RoutedEventArgs e) => SetZoom(Zoom.ScaleX / 1.25);

    void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetZoom(Zoom.ScaleX * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    void Copy(object sender, RoutedEventArgs e) => Clipboard.SetImage(_doc.Render(Paper == Paper.White ? White : Black));

    void Save(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG on the paper|*.png|PNG with a transparent background|*.png",
            FileName = $"PenTastic {DateTime.Now:yyyy-MM-dd HHmm}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != true) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_doc.Render(dialog.FilterIndex == 2 ? null : Paper == Paper.White ? White : Black)));
        using (var file = File.Create(dialog.FileName)) encoder.Save(file);
        Log.Write($"canvas saved: {dialog.FileName}");
    }

    void OnStatus(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppStatus.Phone)) Dispatcher.BeginInvoke(UpdateFooter);
    }

    void UpdateFooter()
    {
        var who = AppStatus.Current.Phone is { } phone ? $"Drawing from {phone}" : "Waiting for your phone";
        Footer.Text = $"{who}  ·  {_doc.Width:0} × {_doc.Height:0}  ·  Switch the phone to Draw mode to draw here. Undo, redo and clear are on the phone.";
    }
}
