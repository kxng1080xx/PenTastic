using System.Drawing.Drawing2D;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

sealed class TestPad : Form
{
    const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;

    Bitmap? _ink;
    Point? _lastInk;
    int _pen, _contact, _other;
    uint _minP = uint.MaxValue, _maxP;

    public string Summary => _pen == 0 && _other == 0
        ? "No pointer input received yet."
        : $"Pen frames: {_pen} (in contact {_contact}), pressure {(_contact > 0 ? _minP : 0)}..{_maxP} of 1024, non-pen pointer input: {_other}.";

    public TestPad()
    {
        Text = "PenTastic test pad";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        BackColor = Color.White;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) Close(); };
        FormClosed += (_, _) => Log.Write("test pad: " + Summary);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is not (WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP))
        {
            base.WndProc(ref m);
            return;
        }

        uint id = (uint)(m.WParam.ToInt64() & 0xFFFF);
        if (Native.GetPointerType(id, out var type) && type == Native.PT_PEN && Native.GetPointerPenInfo(id, out var info))
        {
            _pen++;
            var p = PointToClient(new Point(info.pointerInfo.ptPixelLocation.X, info.pointerInfo.ptPixelLocation.Y));
            if ((info.pointerInfo.pointerFlags & Native.POINTER_FLAG_INCONTACT) != 0)
            {
                _contact++;
                _minP = Math.Min(_minP, info.pressure);
                _maxP = Math.Max(_maxP, info.pressure);
                _ink ??= new Bitmap(ClientSize.Width, ClientSize.Height);
                using var g = Graphics.FromImage(_ink);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var stroke = new Pen(Color.FromArgb(30, 60, 160), 1 + info.pressure / 1024f * 15)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(stroke, _lastInk ?? p, p);
                _lastInk = p;
            }
            else _lastInk = null;
        }
        else _other++;

        Invalidate();
        m.Result = IntPtr.Zero;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_ink != null) e.Graphics.DrawImage(_ink, 0, 0);
        TextRenderer.DrawText(e.Graphics, $"{Summary}\nEsc or right-click to close.", Font, new Point(16, 16), Color.Black);
    }
}
