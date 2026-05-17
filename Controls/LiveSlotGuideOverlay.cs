using System.Windows;
using System.Windows.Media;

namespace BoothDesktop.Controls;

/// <summary>
/// Dims the live camera preview outside a centered window matching the active shot's aspect ratio.
/// Matches compositor center-crop: guests frame themselves in this box, not a mini strip position.
/// </summary>
public sealed class LiveSlotGuideOverlay : FrameworkElement
{
    public static readonly DependencyProperty DimOpacityProperty =
        DependencyProperty.Register(nameof(DimOpacity), typeof(double), typeof(LiveSlotGuideOverlay),
            new FrameworkPropertyMetadata(0.8, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen SlotBorderPen = CreateSlotBorderPen();

    private double _slotWidth;
    private double _slotHeight;

    public double DimOpacity
    {
        get => (double)GetValue(DimOpacityProperty);
        set => SetValue(DimOpacityProperty, value);
    }

    /// <summary>Slot size from template.xml (W×H). Position on the strip is ignored for live guide.</summary>
    public void SetCaptureSlotSize(double slotWidth, double slotHeight)
    {
        _slotWidth = slotWidth;
        _slotHeight = slotHeight;
        InvalidateVisual();
    }

    public void ClearGuide()
    {
        _slotWidth = 0;
        _slotHeight = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var viewW = ActualWidth;
        var viewH = ActualHeight;
        if (viewW <= 1 || viewH <= 1 || _slotWidth <= 0 || _slotHeight <= 0)
            return;

        var hole = ComputeCenteredAspectFitRect(_slotWidth, _slotHeight, viewW, viewH);
        if (hole.Width <= 1 || hole.Height <= 1)
            return;

        var opacity = Math.Clamp(DimOpacity, 0.05, 0.95);
        var dim = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 0, 0, 0));
        dim.Freeze();

        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, viewW, viewH)));
        geometry.Children.Add(new RectangleGeometry(hole));
        geometry.Freeze();

        dc.DrawGeometry(dim, null, geometry);
        dc.DrawRectangle(null, SlotBorderPen, hole);
    }

    /// <summary>Largest centered rect with slot aspect ratio that fits inside the live preview.</summary>
    internal static Rect ComputeCenteredAspectFitRect(double slotW, double slotH, double viewW, double viewH)
    {
        var slotAspect = slotW / slotH;
        var viewAspect = viewW / viewH;

        double rw, rh, rx, ry;
        if (slotAspect > viewAspect)
        {
            rw = viewW;
            rh = viewW / slotAspect;
            rx = 0;
            ry = (viewH - rh) * 0.5;
        }
        else
        {
            rh = viewH;
            rw = viewH * slotAspect;
            ry = 0;
            rx = (viewW - rw) * 0.5;
        }

        return new Rect(rx, ry, rw, rh);
    }

    private static Pen CreateSlotBorderPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, 212, 175, 55)), 2);
        pen.Freeze();
        return pen;
    }
}
