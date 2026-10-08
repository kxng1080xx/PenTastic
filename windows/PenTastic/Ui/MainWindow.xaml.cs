using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QRCoder;

namespace PenTastic;

partial class MainWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string PrefsKey = @"Software\PenTastic";

    public static bool MinimizeToTrayOn
    {
        get => Registry.CurrentUser.OpenSubKey(PrefsKey)?.GetValue("MinimizeToTray") is not 0;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(PrefsKey);
            key.SetValue("MinimizeToTray", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    public static void HideWhenMinimized(Window w)
    {
        var restore = w.WindowState;
        w.StateChanged += (_, _) =>
        {
            if (w.WindowState != WindowState.Minimized)
            {
                restore = w.WindowState;
                return;
            }
            if (!MinimizeToTrayOn) return;
            w.Hide();
            w.WindowState = restore;
        };
    }

    readonly Pairing _pairing;
    readonly Task<string?> _bluetooth;
    readonly CanvasHost _canvas;
    readonly Action _testPen;
    readonly Func<System.Drawing.Rectangle> _display;
    readonly Action<System.Windows.Forms.Screen> _useDisplay;
    bool _pairingShown;

    public static bool Quitting { get; set; }

    public MainWindow(Pairing pairing, Task<string?> bluetooth, CanvasHost canvas, Action testPen,
        Func<System.Drawing.Rectangle> display, Action<System.Windows.Forms.Screen> useDisplay)
    {
        (_pairing, _bluetooth, _canvas, _testPen, _display, _useDisplay) = (pairing, bluetooth, canvas, testPen, display, useDisplay);
        InitializeComponent();
        ShowDisplays();
        DataContext = AppStatus.Current;
        Logo.Source = Theme.Logo(92);
        Subtitle.Text = $"S Pen tablet for {Environment.MachineName}";
        Version.Text = $"PenTastic {typeof(MainWindow).Assembly.GetName().Version?.ToString(2) ?? "1.0"} · works offline, nothing leaves your devices";
        StartWithWindows.IsChecked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("PenTastic") != null;
        MinimizeToTray.IsChecked = MinimizeToTrayOn;
        HideWhenMinimized(this);
        Theme.Backdrop(this);

        _pairing.Paired += name => Dispatcher.BeginInvoke(() =>
        {
            HidePairing();
            PairText.Text = $"Paired with {name}. It can now connect over Wi-Fi and Bluetooth too.";
        });
        Closing += (_, e) =>
        {
            if (Quitting) return;
            e.Cancel = true;
            HidePairing();
            Hide();
        };
    }

    public void ShowDisplays()
    {
        DisplayList.Children.Clear();
        var current = _display();
        foreach (var s in Displays.All)
        {
            var item = new System.Windows.Controls.RadioButton
            {
                Style = (Style)FindResource("SegmentItem"),
                Content = Displays.Label(s),
                GroupName = "Display",
                IsChecked = s.Bounds == current,
            };
            item.Checked += (_, _) =>
            {
                _useDisplay(s);
                Displays.Identify(s.Bounds);
            };
            DisplayList.Children.Add(item);
        }
    }

    void IdentifyDisplays(object sender, RoutedEventArgs e) => Displays.Identify(_display());

    public void ShowDashboard(bool pair = false)
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        BringToFront(this);
        if (pair && !_pairingShown) ShowPairing();
    }

    public static void BringToFront(Window w)
    {
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    void TogglePairing(object sender, RoutedEventArgs e)
    {
        if (_pairingShown) HidePairing();
        else ShowPairing();
    }

    void ShowPairing()
    {
        var bluetooth = _bluetooth.IsCompletedSuccessfully ? _bluetooth.Result : null;
        var hosts = Pairing.Addresses();
        var link = Pairing.Link(_pairing.ServerId, _pairing.StartPairing(), Protocol.Port, hosts, Environment.MachineName, bluetooth);
        using var data = new QRCodeGenerator().CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, [0, 0, 0], [255, 255, 255]);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        Qr.Source = image;
        QrArea.Visibility = Visibility.Visible;
        PairText.Text = "On the phone, open the Camera app, point it at this code and tap the PenTastic link. "
                        + $"The code works once. Reachable by {string.Join(", ", hosts.Append(bluetooth == null ? "no Bluetooth" : "Bluetooth"))}.";
        PairButton.Content = "Hide code";
        _pairingShown = true;
    }

    void HidePairing()
    {
        if (!_pairingShown) return;
        _pairing.StopPairing();
        QrArea.Visibility = Visibility.Collapsed;
        Qr.Source = null;
        PairText.Text = "Needed once for Wi-Fi and Bluetooth. USB works without pairing.";
        PairButton.Content = "Show pairing code";
        _pairingShown = false;
    }

    void OpenCanvas(object sender, RoutedEventArgs e) => _canvas.Show();
    void TestPen(object sender, RoutedEventArgs e) => _testPen();
    void OpenLog(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });

    void MinimizeToTrayClick(object sender, RoutedEventArgs e) => MinimizeToTrayOn = MinimizeToTray.IsChecked == true;

    void StartWithWindowsClick(object sender, RoutedEventArgs e)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartWithWindows.IsChecked == true) run.SetValue("PenTastic", $"\"{Environment.ProcessPath}\" --background");
        else run.DeleteValue("PenTastic", false);
    }

    void Firewall(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        var commands = "netsh advfirewall firewall delete rule name=PenTastic & "
                       + $"netsh advfirewall firewall add rule name=PenTastic dir=in action=allow program=\"{exe}\" enable=yes profile=private,domain";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {commands}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
