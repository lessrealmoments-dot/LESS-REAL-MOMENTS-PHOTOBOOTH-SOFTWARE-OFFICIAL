using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace BoothDesktop.Services;

/// <summary>4×6 portrait test sheet (1200×1800 @ 300 DPI) for alignment tuning.</summary>
internal static class BoothPrintTestImage
{
    public static Bitmap CreatePortrait4x6(string label)
    {
        const int w = 1200;
        const int h = 1800;
        var bmp = new Bitmap(w, h);
        bmp.SetResolution(300, 300);

        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var borderPen = new Pen(Color.FromArgb(220, 180, 60), 12f);
        g.DrawRectangle(borderPen, 24, 24, w - 48, h - 48);

        using var gridPen = new Pen(Color.FromArgb(80, 80, 80), 3f);
        g.DrawLine(gridPen, w / 2f, 60, w / 2f, h - 60);
        g.DrawLine(gridPen, 60, h / 2f, w - 60, h / 2f);

        using var cornerPen = new Pen(Color.CornflowerBlue, 6f);
        const int arm = 80;
        g.DrawLine(cornerPen, 60, 60, 60 + arm, 60);
        g.DrawLine(cornerPen, 60, 60, 60, 60 + arm);
        g.DrawLine(cornerPen, w - 60, 60, w - 60 - arm, 60);
        g.DrawLine(cornerPen, w - 60, 60, w - 60, 60 + arm);
        g.DrawLine(cornerPen, 60, h - 60, 60 + arm, h - 60);
        g.DrawLine(cornerPen, 60, h - 60, 60, h - 60 - arm);
        g.DrawLine(cornerPen, w - 60, h - 60, w - 60 - arm, h - 60);
        g.DrawLine(cornerPen, w - 60, h - 60, w - 60, h - 60 - arm);

        using var font = new Font("Segoe UI", 48, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", 28, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(40, 40, 40));

        var title = "Print alignment test";
        var titleSize = g.MeasureString(title, font);
        g.DrawString(title, font, brush, (w - titleSize.Width) / 2f, h * 0.12f);

        var labelSize = g.MeasureString(label, subFont);
        g.DrawString(label, subFont, brush, (w - labelSize.Width) / 2f, h * 0.12f + titleSize.Height + 16);

        var foot = "Adjust scale and position in Global settings";
        var footSize = g.MeasureString(foot, subFont);
        g.DrawString(foot, subFont, brush, (w - footSize.Width) / 2f, h * 0.82f);

        return bmp;
    }
}
