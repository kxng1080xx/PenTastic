using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PenTastic;

static class Displays
{
    const string PrefsKey = @"Software\PenTastic";

    public static Screen[] All => Screen.AllScreens;

    public static string Number(Screen s) =>
        s.DeviceName.StartsWith(@"\\.\DISPLAY", StringComparison.Ordinal) ? s.DeviceName[11..] : (Array.IndexOf(All, s) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Label(Screen s) => $"{Number(s)}  ·  {s.Bounds.Width} × {s.Bounds.Height}" + (s.Primary ? "  ·  main" : "");

    public static Screen Chosen()
    {
        var name = Registry.CurrentUser.OpenSubKey(PrefsKey)?.GetValue("Display") as string;
        return All.FirstOrDefault(s => s.DeviceName == name) ?? Screen.PrimaryScreen ?? All[0];
    }

    public static void Choose(Screen s)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PrefsKey);
        key.SetValue("Display", s.DeviceName);
    }

    public static void Identify(Rectangle chosen)
    {
        foreach (var s in All)
        {
            bool on = s.Bounds == chosen;
            var fore = on ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
            var size = new Size(Math.Min(340, s.Bounds.Width / 3), Math.Min(220, s.Bounds.Height / 3));
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = on ? Color.FromArgb(0xFF, 0xC9, 0x4A) : Color.FromArgb(0x20, 0x24, 0x2C),
                Bounds = new Rectangle(s.Bounds.Left + (s.Bounds.Width - size.Width) / 2, s.Bounds.Top + (s.Bounds.Height - size.Height) / 2, size.Width, size.Height),
            };
            form.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter,
                ForeColor = fore,
                Font = new Font("Segoe UI Semibold", 13),
                Text = on ? "The pen is here" : "",
            });
            form.Controls.Add(new Label
            {
                Dock = DockStyle.Top,
                Height = size.Height * 3 / 5,
                TextAlign = ContentAlignment.BottomCenter,
                ForeColor = fore,
                Font = new Font("Segoe UI Semibold", 54),
                Text = Number(s),
            });
            var timer = new System.Windows.Forms.Timer { Interval = 1800 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                form.Close();
            };
            form.Show();
            timer.Start();
        }
    }
}
