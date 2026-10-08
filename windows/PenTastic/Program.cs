using System.Diagnostics;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-icon")
        {
            using var file = File.Create(args[1]);
            AppIcon.WriteIco(file, 16, 20, 24, 32, 40, 48, 64, 256);
            return;
        }

        using var single = new Mutex(true, @"Local\PenTastic", out var first);
        if (!first) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Log.Write("start");
        Mirror.KeepTimersPrecise();

        PenInjector pen;
        try
        {
            pen = new PenInjector(Displays.Chosen().Bounds);
        }
        catch (Exception e)
        {
            Log.Write($"CreateSyntheticPointerDevice failed: {e.Message}");
            MessageBox.Show($"Could not create the synthetic pen (needs Windows 10 1809 or later).\n\n{e.Message}", "PenTastic");
            return;
        }
        Log.Write($"pen ready, target {pen.Target}");

        if (args.Contains("--test-pad"))
        {
            Application.Run(OpenTestPad(pen));
            pen.Dispose();
            return;
        }
        if (args.Contains("--test-circle"))
        {
            Thread.Sleep(5000);
            DrawTestCircle(pen);
            Thread.Sleep(1000);
            pen.Dispose();
            return;
        }

        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"ui error: {e.Exception}");
            e.Handled = true;
        };
        using var stop = new CancellationTokenSource();
        app.Startup += async (_, _) => await StartAsync(app, pen, stop.Token, background: args.Contains("--background"));
        app.Run();
        stop.Cancel();
        pen.Dispose();
        Log.Write("quit");
    }

    static async Task StartAsync(System.Windows.Application app, PenInjector pen, CancellationToken stop, bool background)
    {
        Theme.Init(app);
        var splash = background ? null : new SplashWindow();
        splash?.Show();
        var shown = Stopwatch.StartNew();

        splash?.Say("Getting the pen ready");
        var canvas = new CanvasHost(app.Dispatcher, () => pen.Target.Size);
        var pairing = new Pairing(Path.Combine(Log.Dir, "pairing.json"));
        var bluetooth = Task.Run(BluetoothServer.AddressAsync);
        var server = new Server(pen, canvas, pairing);
        canvas.PaperChosen += paper => server.SendToPhone(new PaperChoice(paper));
        canvas.PageShown += (index, count) => server.SendToPhone(new PageInfo((byte)Math.Min(index, 255), (byte)Math.Min(count, 255)));

        void UseDisplay(Screen s)
        {
            if (pen.Target == s.Bounds) return;
            pen.Release();
            pen.Target = s.Bounds;
            server.SendToPhone(new TargetUpdate(Mapping.Describe(s.Bounds)));
            Log.Write($"display: {s.DeviceName} {s.Bounds}");
        }

        splash?.Say("Listening for your phone");
        _ = server.RunAsync(stop);
        if (Adb.Find() is { } adb)
            new Thread(() => Adb.Watch(adb, s => AppStatus.Current.Usb = s, stop)) { IsBackground = true, Name = "adb" }.Start();
        else
        {
            Log.Write("adb not found");
            AppStatus.Current.Usb = "USB: adb not found";
        }

        splash?.Say("Checking Bluetooth");
        await Task.WhenAny(bluetooth, Task.Delay(1500));

        var dashboard = new MainWindow(pairing, bluetooth, canvas, () => OpenTestPad(pen).Show(), () => pen.Target, s =>
        {
            Displays.Choose(s);
            UseDisplay(s);
        });
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => app.Dispatcher.BeginInvoke(() =>
        {
            UseDisplay(Displays.Chosen());
            dashboard.ShowDisplays();
        });
        var tray = Tray(app, dashboard, canvas, pen);
        pairing.Paired += name => app.Dispatcher.BeginInvoke(() => tray.ShowBalloonTip(3000, "PenTastic", $"Paired with {name}", ToolTipIcon.Info));
        app.Exit += (_, _) =>
        {
            canvas.SaveNow();
            tray.Dispose();
        };

        if (splash == null) return;
        splash.Say("Ready");
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, 1500 - shown.ElapsedMilliseconds)));
        dashboard.ShowDashboard();
        await splash.FadeOutAsync();
        splash.Close();
    }

    static NotifyIcon Tray(System.Windows.Application app, MainWindow dashboard, CanvasHost canvas, PenInjector pen)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PenTastic", null, (_, _) => dashboard.ShowDashboard());
        menu.Items.Add("Open canvas", null, (_, _) => canvas.Show());
        menu.Items.Add("Pair a phone…", null, (_, _) => dashboard.ShowDashboard(pair: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Test the pen", null, (_, _) => OpenTestPad(pen).Show());
        menu.Items.Add("Open log", null, (_, _) => Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit PenTastic", null, (_, _) =>
        {
            MainWindow.Quitting = true;
            app.Shutdown();
        });

        var tray = new NotifyIcon { Icon = AppIcon.Create(SystemInformation.SmallIconSize), Text = "PenTastic", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) dashboard.ShowDashboard(); };
        void Tooltip()
        {
            var s = AppStatus.Current;
            var text = $"PenTastic\n{(s.Connected ? s.Detail : s.Headline)}\n{s.Usb}";
            tray.Text = text.Length > 127 ? text[..127] : text;
        }
        AppStatus.Current.PropertyChanged += (_, _) => app.Dispatcher.BeginInvoke(Tooltip);
        Tooltip();
        return tray;
    }

    static TestPad OpenTestPad(PenInjector pen)
    {
        var pad = new TestPad();
        pad.Shown += (_, _) => Task.Run(() =>
        {
            Thread.Sleep(1000);
            DrawTestCircle(pen);
            Thread.Sleep(500);
            try { pad.BeginInvoke(() => Log.Write("test pad: " + pad.Summary)); }
            catch (InvalidOperationException) { }
        });
        return pad;
    }

    static void DrawTestCircle(PenInjector pen)
    {
        Log.Write("test circle");
        var t = pen.Target;
        double r = Math.Min(t.Width, t.Height) / 4.0, rx = r / t.Width, ry = r / t.Height;
        const int steps = 240;

        for (int i = 0; i < 10; i++)
        {
            pen.Handle(PenKind.Hover, 0.5 + rx, 0.5, 0, 0, 0, PenFlags.None);
            Thread.Sleep(5);
        }
        for (int i = 0; i <= steps; i++)
        {
            double a = 2 * Math.PI * i / steps;
            double pressure = 0.1 + 0.9 * Math.Sin(a / 2);
            pen.Handle(i == 0 ? PenKind.Down : PenKind.Move, 0.5 + rx * Math.Cos(a), 0.5 + ry * Math.Sin(a), pressure, 0, 0, PenFlags.None);
            Thread.Sleep(5);
        }
        pen.Handle(PenKind.Up, 0.5 + rx, 0.5, 0, 0, 0, PenFlags.None);
        pen.Handle(PenKind.HoverExit, 0.5 + rx, 0.5, 0, 0, 0, PenFlags.None);
    }
}

static class Log
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenTastic");
    public static readonly string FilePath = Path.Combine(Dir, "pentastic.log");
    static readonly object Gate = new();

    static Log()
    {
        Directory.CreateDirectory(Dir);
        var f = new FileInfo(FilePath);
        if (f.Exists && f.Length > 1_000_000) f.Delete();
    }

    public static void Write(string message)
    {
        lock (Gate) File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
    }
}
