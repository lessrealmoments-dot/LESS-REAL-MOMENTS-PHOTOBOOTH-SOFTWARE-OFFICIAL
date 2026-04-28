using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BoothDesktop.Controls;

public partial class LayoutMiniPreview : UserControl
{
    public static readonly DependencyProperty PreviewKeyProperty = DependencyProperty.Register(
        nameof(PreviewKey),
        typeof(string),
        typeof(LayoutMiniPreview),
        new PropertyMetadata("", OnPreviewKeyChanged));

    public string PreviewKey
    {
        get => (string)GetValue(PreviewKeyProperty);
        set => SetValue(PreviewKeyProperty, value);
    }

    public LayoutMiniPreview()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
    }

    private static void OnPreviewKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((LayoutMiniPreview)d).Rebuild();

    private void Rebuild()
    {
        SlotCanvas.Children.Clear();
        var key = (PreviewKey ?? "").ToLowerInvariant();
        switch (key)
        {
            case "h3":
                BuildLandscapeTwoUp();
                break;
            case "h2":
                BuildFourGrid();
                break;
            case "af1":
            default:
                BuildSixMirror();
                break;
        }
    }

    private void AddSlot(double x, double y, double w, double h)
    {
        var r = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0xD4, 0xAF, 0x37)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1)
        };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        SlotCanvas.Children.Add(r);
    }

    /// <summary>3 shots × 2 columns (mirrored), like cleo and heart af 1.</summary>
    private void BuildSixMirror()
    {
        OuterFrame.Width = 120;
        OuterFrame.Height = 180;
        SlotCanvas.Width = 120;
        SlotCanvas.Height = 180;
        const double gap = 6;
        const double top = 10;
        double colW = (120 - gap * 3) / 2;
        double rowH = (180 - top - gap * 4) / 3;
        for (int row = 0; row < 3; row++)
        {
            double y = top + row * (rowH + gap);
            AddSlot(gap, y, colW, rowH);
            AddSlot(gap * 2 + colW, y, colW, rowH);
        }
    }

    /// <summary>2×2 grid, four photos.</summary>
    private void BuildFourGrid()
    {
        OuterFrame.Width = 120;
        OuterFrame.Height = 180;
        SlotCanvas.Width = 120;
        SlotCanvas.Height = 180;
        const double m = 8;
        double cellW = (120 - m * 3) / 2;
        double cellH = (180 - m * 3) / 2;
        AddSlot(m, m, cellW, cellH);
        AddSlot(m * 2 + cellW, m, cellW, cellH);
        AddSlot(m, m * 2 + cellH, cellW, cellH);
        AddSlot(m * 2 + cellW, m * 2 + cellH, cellW, cellH);
    }

    /// <summary>Landscape strip: two large slots side by side.</summary>
    private void BuildLandscapeTwoUp()
    {
        OuterFrame.Width = 180;
        OuterFrame.Height = 120;
        SlotCanvas.Width = 180;
        SlotCanvas.Height = 120;
        const double m = 10;
        double slotW = (180 - m * 3) / 2;
        double slotH = 120 - m * 2;
        AddSlot(m, m, slotW, slotH);
        AddSlot(m * 2 + slotW, m, slotW, slotH);
    }
}
