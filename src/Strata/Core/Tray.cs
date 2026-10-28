using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Strata.Core;

/// <summary>
/// Tray icon: a way back to Settings/Exit even if the dock is hidden, and the channel for
/// notifications (pomodoro done, upcoming events). On Windows 10/11 balloons show as toasts.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public Tray(Action openSettings, Action toggleTaskbar, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => openSettings());
        menu.Items.Add("Show / hide Windows taskbar", null, (_, _) => toggleTaskbar());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Strata", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Strata",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => openSettings();
    }

    public void Notify(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(5000);
    }

    /// <summary>Three stacked bars: the Strata mark, drawn at runtime so no asset is needed.</summary>
    public static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var colors = new[] { Color.FromArgb(255, 123, 147, 255), Color.FromArgb(255, 91, 157, 255), Color.FromArgb(255, 72, 214, 200) };
            for (int i = 0; i < 3; i++)
            {
                int inset = 2 + (2 - i) * 3;
                using var path = RoundedRect(new RectangleF(inset, 4 + i * 9, 32 - inset * 2, 7), 3.5f);
                using var brush = new SolidBrush(colors[i]);
                g.FillPath(brush, path);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
