using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LiveCaptions10;

/// <summary>Draws the "CC" tray icon (blue = captions on, grey = off).</summary>
internal static class IconFactory
{
    public static Icon Create(int size, bool active)
    {
        using var bitmap = Render(size, active);
        // The HICON is kept for the app's lifetime (two small icons), so it is never destroyed.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public static Bitmap Render(int size, bool active)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        float inset = Math.Max(0.5f, size * 0.03f);
        var box = new RectangleF(inset, size * 0.14f, size - 2 * inset, size * 0.72f);
        using (var path = RoundedRect(box, size * 0.16f))
        using (var fill = new SolidBrush(active ? Color.FromArgb(0x00, 0x67, 0xC0) : Color.FromArgb(0x70, 0x70, 0x70)))
        {
            g.FillPath(fill, path);
        }

        using var font = new Font("Segoe UI", size * 0.46f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("CC", font, Brushes.White, new RectangleF(box.X - size, box.Y + size * 0.02f, box.Width + 2 * size, box.Height), format);
        return bitmap;
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Flat, light menu look close to Windows 10's own context menus.</summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer() : base(new MenuColors()) => RoundedEdges = false;

    sealed class MenuColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(0xF9, 0xF9, 0xF9);
        static readonly Color Hover = Color.FromArgb(0xE5, 0xE5, 0xE5);
        static readonly Color Edge = Color.FromArgb(0xCC, 0xCC, 0xCC);
        static readonly Color Check = Color.FromArgb(0xCC, 0xE4, 0xF7);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Edge;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Color.FromArgb(0xD7, 0xD7, 0xD7);
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Check;
        public override Color CheckSelectedBackground => Check;
        public override Color CheckPressedBackground => Check;
        public override Color ButtonSelectedBorder => Hover;
    }
}

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "LiveCaptions10";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
