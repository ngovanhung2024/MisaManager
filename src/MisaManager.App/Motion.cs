using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MisaManager.App;

/// <summary>Cheap opacity/translate animations that honour the Windows "animate controls" setting.</summary>
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEntranceChanged));
    public static bool GetEntrance(DependencyObject element) => (bool)element.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject element, bool value) => element.SetValue(EntranceProperty, value);

    private static void OnEntranceChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement target || args.NewValue is not true) return;
        target.Loaded += OnEntranceLoaded;
    }

    private static void OnEntranceLoaded(object sender, RoutedEventArgs e)
    {
        var target = (FrameworkElement)sender;
        target.Loaded -= OnEntranceLoaded;
        FadeIn(target);
    }

    public static void FadeIn(UIElement target, double fromX = 0, double fromY = 6, int milliseconds = 170)
    {
        if (!Enabled) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        target.Opacity = 1;
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        var slide = new TranslateTransform(fromX, fromY);
        target.RenderTransform = slide;
        if (fromX != 0) slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, duration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        if (fromY != 0) slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, duration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Soft repeating fade used for the "busy" status dot.</summary>
    public static void Pulse(UIElement target, bool on)
    {
        if (!on || !Enabled)
        {
            target.BeginAnimation(UIElement.OpacityProperty, null);
            target.Opacity = 1;
            return;
        }
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(650))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
    }

    /// <summary>One-shot background flash so changed rows are easy to notice.</summary>
    public static void Flash(UIElement target, int milliseconds = 450)
    {
        if (!Enabled) return;
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
}
