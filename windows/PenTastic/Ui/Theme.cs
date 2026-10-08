using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PenTastic;

static class Theme
{
    public static bool Dark { get; private set; }

    static readonly (string Key, uint Dark, uint Light)[] Palette =
    [
        ("WindowBgBrush", 0xFF0F1520, 0xFFF3F5F9),
        ("CardBrush", 0xE61A2436, 0xF2FFFFFF),
        ("CardBorderBrush", 0xFF2A3852, 0xFFE1E6EE),
        ("TextBrush", 0xFFE8EDF5, 0xFF121A26),
        ("TextDimBrush", 0xFF94A3BD, 0xFF5B6B85),
        ("SubtleBrush", 0xFF243149, 0xFFEEF1F6),
        ("HoverOverlayBrush", 0x1CFFFFFF, 0x12000000),
        ("AccentBrush", 0xFFFFD166, 0xFFFFC94A),
        ("AccentTextBrush", 0xFF1A1405, 0xFF1A1405),
        ("GoodBrush", 0xFF52D49A, 0xFF16A06A),
        ("IdleBrush", 0xFFFF9F43, 0xFFD9771E),
        ("BackdropBrush", 0xFF0B111B, 0xFFE4E8EF),
    ];

    public static void Init(System.Windows.Application app)
    {
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PenTastic;component/Ui/Styles.xaml") });
        Apply(app);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) app.Dispatcher.BeginInvoke(() => Apply(app));
        };
    }

    static void Apply(System.Windows.Application app)
    {
        Dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        foreach (var (key, dark, light) in Palette)
        {
            var brush = new SolidColorBrush(ToColor(Dark ? dark : light));
            brush.Freeze();
            app.Resources[key] = brush;
        }
        foreach (Window w in app.Windows) Backdrop(w);
    }

    public static System.Windows.Media.Color ToColor(uint argb) =>
        System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static void Backdrop(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            w.SourceInitialized += (_, _) => Backdrop(w);
            return;
        }
        int dark = Dark ? 1 : 0, mica = 2;
        DwmSetWindowAttribute(hwnd, 20 , ref dark, sizeof(int));
        if (DwmSetWindowAttribute(hwnd, 38 , ref mica, sizeof(int)) == 0)
        {
            w.Background = Brushes.Transparent;
            HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor = Colors.Transparent;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        else w.SetResourceReference(Window.BackgroundProperty, "WindowBgBrush");
    }

    public static ImageSource Logo(int size)
    {
        using var bmp = AppIcon.Render(size);
        using var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        png.Position = 0;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = png;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
