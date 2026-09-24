using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SecondBrain.App;

internal sealed class TrayController : IDisposable
{
    private readonly Drawing.Icon icon = CreateIcon();
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.NotifyIcon notification;
    internal bool Visible => notification.Visible;

    public TrayController(Action showControls, Action openReader, Action exit)
    {
        menu.Items.Add("Open controls", null, (_, _) => showControls());
        menu.Items.Add("Open reader", null, (_, _) => openReader());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Second Brain", null, (_, _) => exit());
        notification = new Forms.NotifyIcon { Icon = icon, Text = "Second Brain", ContextMenuStrip = menu, Visible = true };
        notification.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) showControls(); };
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(35, 58, 54));
        using var foreground = new Drawing.SolidBrush(Drawing.Color.FromArgb(207, 237, 153));
        using var font = new Drawing.Font("Segoe UI", 22, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        graphics.FillEllipse(background, 0, 0, 31, 31);
        graphics.DrawString("B", font, foreground, 6, 1);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Drawing.Icon.FromHandle(handle); return (Drawing.Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    public void Dispose()
    {
        notification.Visible = false;
        notification.Dispose();
        menu.Dispose();
        icon.Dispose();
    }
}
