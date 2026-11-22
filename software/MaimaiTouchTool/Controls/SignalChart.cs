using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Theming;
using MaimaiTouchTool.ViewModels;

namespace MaimaiTouchTool.Controls;

public sealed class SignalChart : Control
{
    public static readonly StyledProperty<SignalChartModel?> ModelProperty =
        AvaloniaProperty.Register<SignalChart, SignalChartModel?>(nameof(Model));
    public SignalChartModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    private SignalChartModel? subscribed;

    private static readonly IBrush DarkBgBrush = new SolidColorBrush(Color.Parse("#111820"));
    private static readonly IBrush DarkGridBrush = new SolidColorBrush(Color.Parse("#35404D"));
    private static readonly IBrush DarkTextBrush = new SolidColorBrush(Color.Parse("#AFBAC6"));
    private static readonly IBrush DarkFilteredBrush = new SolidColorBrush(Color.Parse("#60A5FA"));
    private static readonly IBrush DarkBaselineBrush = new SolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush DarkDropBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush DarkTouchBrush = new SolidColorBrush(Color.Parse("#FB7185"));
    private static readonly IBrush DarkReleaseBrush = new SolidColorBrush(Color.Parse("#C084FC"));
    private static readonly IBrush DarkOutputBrush = new SolidColorBrush(Color.Parse("#22D3EE"));

    private static readonly IBrush LightBgBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush LightGridBrush = new SolidColorBrush(Color.Parse("#E2E8F0"));
    private static readonly IBrush LightTextBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush LightFilteredBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush LightBaselineBrush = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush LightDropBrush = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush LightTouchBrush = new SolidColorBrush(Color.Parse("#E11D48"));
    private static readonly IBrush LightReleaseBrush = new SolidColorBrush(Color.Parse("#9333EA"));
    private static readonly IBrush LightOutputBrush = new SolidColorBrush(Color.Parse("#0891B2"));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty) { Subscribe(); InvalidateVisual(); }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
        ThemeManager.Instance.ThemeChanged += OnThemeChanged;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
        if (subscribed is not null) subscribed.PropertyChanged -= ModelChanged;
        subscribed = null;
        base.OnDetachedFromVisualTree(e);
    }
    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void Subscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= ModelChanged;
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += ModelChanged;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SignalChartModel.Revision)) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 160 || Bounds.Height < 180) return;

        var isLight = ThemeManager.Instance.IsLight;
        var bgBrush = isLight ? LightBgBrush : DarkBgBrush;
        var gridBrush = isLight ? LightGridBrush : DarkGridBrush;
        var textBrush = isLight ? LightTextBrush : DarkTextBrush;
        var filteredBrush = isLight ? LightFilteredBrush : DarkFilteredBrush;
        var baselineBrush = isLight ? LightBaselineBrush : DarkBaselineBrush;
        var dropBrush = isLight ? LightDropBrush : DarkDropBrush;
        var touchBrush = isLight ? LightTouchBrush : DarkTouchBrush;
        var releaseBrush = isLight ? LightReleaseBrush : DarkReleaseBrush;
        var outputBrush = isLight ? LightOutputBrush : DarkOutputBrush;

        context.FillRectangle(bgBrush, new Rect(Bounds.Size));
        var all = Model?.Points() ?? [];
        var valid = all.Where(p => p.Valid).ToArray();
        var left = 50.0;
        var width = Bounds.Width - left - 14;
        var paneHeight = (Bounds.Height - 90) / 2;
        var top = new Rect(left, 20, width, paneHeight);
        var bottom = new Rect(left, top.Bottom + 30, width, paneHeight);
        var end = all.Count > 0 ? (double)all[^1].TimeMs : 0;
        double X(SignalChartPoint p) => left + (p.TimeMs - end + SignalChartModel.WindowMs) / SignalChartModel.WindowMs * width;
        var absoluteMin = valid.Length > 0 ? valid.Min(p => Math.Min(p.Filtered, p.Baseline)) : 0;
        var absoluteMax = valid.Length > 0 ? valid.Max(p => Math.Max(p.Filtered, p.Baseline)) : 100;
        var dropMin = valid.Length > 0 ? Math.Min(0, valid.Min(p => p.Drop)) : 0;
        var dropMax = valid.Length > 0 ? Math.Max(8, valid.Max(p => Math.Max(p.CandidateThreshold, Math.Max(p.Drop, p.TouchThreshold)))) : 100;
        var absolutePadding = Math.Max(4, (absoluteMax - absoluteMin) * 0.1);
        var dropPadding = Math.Max(2, (dropMax - dropMin) * 0.1);
        var minA = absoluteMin - absolutePadding;
        var maxA = absoluteMax + absolutePadding;
        var minD = dropMin - dropPadding;
        var maxD = dropMax + dropPadding;
        double Y(Rect pane, double value, double min, double max) => pane.Bottom - (value - min) / (max - min) * pane.Height;

        void Label(string text, double x, double y, IBrush? brush = null) => context.DrawText(
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, brush ?? textBrush), new Point(x, y));
        void Axes(Rect pane, double min, double max)
        {
            for (var i = 0; i <= 2; i++)
            {
                var value = min + (max - min) * i / 2;
                var y = Y(pane, value, min, max);
                context.DrawLine(new Pen(gridBrush, 1), new Point(pane.Left, y), new Point(pane.Right, y));
                Label(value.ToString("0", CultureInfo.CurrentCulture), 3, y - 7);
            }
            for (var i = 0; i <= 3; i++)
            {
                var x = pane.Left + pane.Width * i / 3;
                context.DrawLine(new Pen(gridBrush, 1), new Point(x, pane.Top), new Point(x, pane.Bottom));
            }
        }
        Axes(top, minA, maxA);
        Axes(bottom, minD, maxD);
        Label(Localizer.Instance["Chart.Absolute"], top.Left, 3);
        Label(Localizer.Instance["Chart.Difference"], bottom.Left, bottom.Top - 17);
        for (var i = 0; i <= 3; i++) Label($"{i * 10 - 30}", left + width * i / 3 - 8, bottom.Bottom + 15);
        Label(Localizer.Instance["Chart.Time"], left, Bounds.Height - 15);
        if (valid.Length == 0) { Label(Localizer.Instance["Chart.Empty"], left + 10, top.Top + 12); return; }

        void Curve(Rect pane, Func<SignalChartPoint, double> value, double min, double max, IBrush brush)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                var open = false;
                foreach (var point in all)
                {
                    if (!point.Valid || !point.Connect)
                    {
                        if (open) path.EndFigure(false);
                        open = false;
                    }
                    if (!point.Valid) continue;
                    var position = new Point(X(point), Y(pane, value(point), min, max));
                    if (!open) { path.BeginFigure(position, false); open = true; }
                    else path.LineTo(position);
                }
                if (open) path.EndFigure(false);
            }
            using (context.PushClip(pane)) context.DrawGeometry(null, new Pen(brush, 1.5), geometry);
        }
        Curve(top, p => p.Filtered, minA, maxA, filteredBrush);
        Curve(top, p => p.Baseline, minA, maxA, baselineBrush);
        Curve(bottom, p => p.Drop, minD, maxD, dropBrush);
        Curve(bottom, p => p.TouchThreshold, minD, maxD, touchBrush);
        Curve(bottom, p => p.ReleaseThreshold, minD, maxD, releaseBrush);
        if (valid.Any(p => p.CandidateThreshold > 0)) Curve(bottom, p => p.CandidateThreshold > 0 ? p.CandidateThreshold : p.TouchThreshold, minD, maxD, outputBrush);
        for (var i = 1; i < all.Count; i++)
        {
            if (all[i].Valid && all[i].Connect && all[i - 1].HardwareTouched)
                context.FillRectangle(touchBrush, new Rect(X(all[i - 1]), bottom.Bottom + 3,
                    Math.Max(1, X(all[i]) - X(all[i - 1])), 3));
            if (all[i].Valid && all[i].Connect && all[i - 1].Touched)
                context.FillRectangle(outputBrush, new Rect(X(all[i - 1]), bottom.Bottom + 7,
                    Math.Max(1, X(all[i]) - X(all[i - 1])), 3));
        }
    }
}
