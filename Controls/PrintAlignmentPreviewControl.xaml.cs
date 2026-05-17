using System.Drawing;
using System.IO;
using RectangleF = System.Drawing.RectangleF;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BoothDesktop.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace BoothDesktop.Controls;

public partial class PrintAlignmentPreviewControl : UserControl
{
    private const double CanvasMargin = 10;

    public PrintAlignmentPreviewControl()
    {
        InitializeComponent();
    }

    public void Update(PrintAlignmentPreviewState state)
    {
        SummaryLine.Text = state.SummaryText;
        DetailLine.Text = state.DetailText;
        Redraw(state);
    }

    private void Redraw(PrintAlignmentPreviewState state)
    {
        PaperCanvas.Children.Clear();

        var paperW = state.PaperWidthHundredths;
        var paperH = state.PaperHeightHundredths;
        if (paperW <= 0 || paperH <= 0)
        {
            paperW = 400;
            paperH = 600;
        }

        var canvasW = PaperCanvas.Width;
        var canvasH = PaperCanvas.Height;
        var scale = Math.Min((canvasW - CanvasMargin * 2) / paperW, (canvasH - CanvasMargin * 2) / paperH);
        var offsetX = (canvasW - paperW * scale) / 2;
        var offsetY = (canvasH - paperH * scale) / 2;

        RectD Map(RectangleF r) => new(
            offsetX + r.X * scale,
            offsetY + r.Y * scale,
            r.Width * scale,
            r.Height * scale);

        var paperTarget = BoothPrintLayout.GetDefaultPaperTarget(state.PaperIsPortrait);
        var content = state.ContentWidthHundredths > 0 && state.ContentHeightHundredths > 0
            ? new SizeF(state.ContentWidthHundredths, state.ContentHeightHundredths)
            : new SizeF(paperTarget.Width * 0.67f, paperTarget.Height * 0.67f);

        var dest = BoothPrintLayout.ComputeAlignedDestRect(content, paperTarget, state.Alignment);
        var paperScreen = Map(paperTarget);
        var destScreen = Map(dest);

        // Paper shadow
        var shadow = new Rectangle
        {
            Width = paperScreen.Width + 4,
            Height = paperScreen.Height + 4,
            Fill = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            RadiusX = 4,
            RadiusY = 4
        };
        Canvas.SetLeft(shadow, paperScreen.X - 2);
        Canvas.SetTop(shadow, paperScreen.Y + 3);
        PaperCanvas.Children.Add(shadow);

        // Paper
        var paper = new Rectangle
        {
            Width = paperScreen.Width,
            Height = paperScreen.Height,
            Fill = Brushes.WhiteSmoke,
            Stroke = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
            StrokeThickness = 2,
            RadiusX = 2,
            RadiusY = 2
        };
        Canvas.SetLeft(paper, paperScreen.X);
        Canvas.SetTop(paper, paperScreen.Y);
        PaperCanvas.Children.Add(paper);

        // Printable hint (inset 2%)
        var inset = new RectangleF(paperW * 0.02f, paperH * 0.02f, paperW * 0.96f, paperH * 0.96f);
        var insetScreen = Map(inset);
        var printable = new Rectangle
        {
            Width = insetScreen.Width,
            Height = insetScreen.Height,
            Stroke = new SolidColorBrush(Color.FromArgb(90, 100, 100, 100)),
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(printable, insetScreen.X);
        Canvas.SetTop(printable, insetScreen.Y);
        PaperCanvas.Children.Add(printable);

        // Print image
        var imgBrush = CreateSampleBrush(state);
        var printFill = new Rectangle
        {
            Width = Math.Max(1, destScreen.Width),
            Height = Math.Max(1, destScreen.Height),
            Fill = imgBrush,
            Stroke = new SolidColorBrush(Color.FromRgb(212, 175, 55)),
            StrokeThickness = 2.5
        };
        Canvas.SetLeft(printFill, destScreen.X);
        Canvas.SetTop(printFill, destScreen.Y);
        PaperCanvas.Children.Add(printFill);

        // Clip indicator if print extends past paper
        if (dest.Left < 0 || dest.Top < 0 || dest.Right > paperW || dest.Bottom > paperH)
        {
            var warn = new TextBlock
            {
                Text = "? extends past paper",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 80, 80)),
                FontWeight = FontWeights.SemiBold
            };
            Canvas.SetLeft(warn, paperScreen.X + 4);
            Canvas.SetTop(warn, paperScreen.Y + 4);
            PaperCanvas.Children.Add(warn);
        }

        AddAxisLabels(paperScreen, destScreen);
        AddOrientationBadge(paperScreen, state.PaperIsPortrait);
    }

    private static System.Windows.Media.Brush CreateSampleBrush(PrintAlignmentPreviewState state)
    {
        if (string.IsNullOrEmpty(state.SampleImagePath) || !File.Exists(state.SampleImagePath))
        {
            return new SolidColorBrush(Color.FromRgb(60, 55, 75));
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(state.SampleImagePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        }
        catch
        {
            return new SolidColorBrush(Color.FromRgb(60, 55, 75));
        }
    }

    private void AddAxisLabels(RectD paper, RectD dest)
    {
        var accent = new SolidColorBrush(Color.FromRgb(212, 175, 55));
        var muted = new SolidColorBrush(Color.FromRgb(160, 160, 170));

        AddLabel("TOP", paper.X + paper.Width / 2 - 14, paper.Y - 18, muted, 10);
        AddLabel("BOTTOM", paper.X + paper.Width / 2 - 22, paper.Bottom + 4, muted, 10);
        AddLabel("LEFT", paper.X - 36, paper.Y + paper.Height / 2 - 6, muted, 10);
        AddLabel("RIGHT", paper.Right + 6, paper.Y + paper.Height / 2 - 6, muted, 10);

        // Horizontal nudge arrow
        if (Math.Abs(dest.X - (paper.X + (paper.Width - dest.Width) / 2)) > 2)
        {
            var arrow = new Line
            {
                X1 = paper.X + paper.Width / 2,
                Y1 = dest.Y + dest.Height + 12,
                X2 = dest.X + dest.Width / 2,
                Y2 = dest.Y + dest.Height + 12,
                Stroke = accent,
                StrokeThickness = 2
            };
            PaperCanvas.Children.Add(arrow);
            AddLabel("? X ?", dest.X + dest.Width / 2 - 16, dest.Y + dest.Height + 16, accent, 10);
        }

        AddLabel("+X ?", paper.Right + 8, paper.Y + 8, accent, 11);
        AddLabel("+Y ?", paper.Right + 8, paper.Y + 24, accent, 11);
    }

    private void AddOrientationBadge(RectD paper, bool portrait)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 37, 32, 48)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock
            {
                Text = portrait ? "Paper: Portrait (tall)" : "Paper: Landscape (wide)",
                FontSize = 11,
                Foreground = Brushes.White
            }
        };
        Canvas.SetLeft(badge, paper.X + 6);
        Canvas.SetTop(badge, paper.Bottom - 28);
        PaperCanvas.Children.Add(badge);
    }

    private void AddLabel(string text, double x, double y, System.Windows.Media.Brush brush, double size)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontWeight = FontWeights.SemiBold
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        PaperCanvas.Children.Add(tb);
    }

    private readonly record struct RectD(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
    }
}

public sealed class PrintAlignmentPreviewState
{
    public required string SummaryText { get; init; }
    public required string DetailText { get; init; }
    public required EffectivePrinterAlignment Alignment { get; init; }
    public float PaperWidthHundredths { get; init; } = 400;
    public float PaperHeightHundredths { get; init; } = 600;
    public bool PaperIsPortrait => PaperHeightHundredths > PaperWidthHundredths;
    public float ContentWidthHundredths { get; init; }
    public float ContentHeightHundredths { get; init; }
    public string? SampleImagePath { get; init; }
}
