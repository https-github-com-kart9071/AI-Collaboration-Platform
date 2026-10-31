using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AICollaborationPlatform.App.Views;

/// <summary>
/// Smooth mouse-wheel and touchpad scrolling. WPF jumps a fixed distance for every wheel message, however small;
/// a touchpad sends lots of small, uneven ones, so lists move in visible jerks. This glides towards the target
/// instead, by a distance proportional to how far the wheel or finger moved.
/// <para>Use: <c>local:SmoothScroll.IsEnabled="True"</c> on a ScrollViewer, or on a control that contains one (e.g. a DataGrid).
/// A list inside it that can scroll on its own (an editor, a drop-down) still gets the wheel first.</para>
/// </summary>
public static class SmoothScroll
{
    /// <summary>How long the glide takes to cover most of the distance (it eases out, like a browser).</summary>
    public static readonly TimeSpan TimeConstant = TimeSpan.FromMilliseconds(70);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Pixels per wheel notch. Default: Windows' "lines per notch" setting × 40px (one table row per line).</summary>
    public static readonly DependencyProperty PixelsPerNotchProperty = DependencyProperty.RegisterAttached(
        "PixelsPerNotch", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty GlideProperty = DependencyProperty.RegisterAttached(
        "Glide", typeof(Glide), typeof(SmoothScroll));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetPixelsPerNotch(DependencyObject element) => (double)element.GetValue(PixelsPerNotchProperty);

    public static void SetPixelsPerNotch(DependencyObject element, double value) => element.SetValue(PixelsPerNotchProperty, value);

    /// <summary>How far one wheel message scrolls: proportional to its delta (120 = one notch), so touchpads scroll as far as the finger moves.</summary>
    public static double Distance(int wheelDelta, double pixelsPerNotch) => -wheelDelta / 120.0 * pixelsPerNotch;

    /// <summary>One frame of the glide: closes a share of the remaining gap that depends on the frame time, so speed doesn't depend on frame rate.</summary>
    public static double Step(double current, double target, TimeSpan elapsed)
    {
        var next = current + (target - current) * (1 - Math.Exp(-elapsed.TotalMilliseconds / TimeConstant.TotalMilliseconds));
        return Math.Abs(target - next) < 0.5 ? target : next;
    }

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement ui) return;
        ui.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) ui.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var host = (DependencyObject)sender;
        var scroller = host as ScrollViewer ?? FindDescendant<ScrollViewer>(host);
        if (scroller is null || e.Handled) return;

        // A list that scrolls item by item measures its offset in items, not pixels: leave it to WPF.
        if (scroller.CanContentScroll && VirtualizingPanel.GetScrollUnit(host) == ScrollUnit.Item) return;

        // Something inside that scrolls by itself (a multi-line editor, a people list) keeps the wheel.
        if (FindAncestor<ScrollViewer>(e.OriginalSource as DependencyObject) is { ScrollableHeight: > 0 } inner && inner != scroller)
        {
            return;
        }

        e.Handled = true; // also stops read-only text boxes, which swallow the wheel without scrolling
        if (scroller.ScrollableHeight <= 0) return;

        var perNotch = GetPixelsPerNotch(host);
        if (double.IsNaN(perNotch)) perNotch = SystemParameters.WheelScrollLines * 40.0;

        if (scroller.GetValue(GlideProperty) is not Glide glide)
        {
            glide = new Glide(scroller);
            scroller.SetValue(GlideProperty, glide);
        }

        glide.ScrollBy(Distance(e.Delta, perNotch));
    }

    /// <summary>Moves one ScrollViewer towards a target offset, a little each frame.</summary>
    private sealed class Glide(ScrollViewer scroller)
    {
        private double _current;
        private double _target;
        private TimeSpan _lastFrame;
        private bool _running;

        public void ScrollBy(double distance)
        {
            if (!_running)
            {
                // Start from wherever the list is now (it may have been dragged or moved with the keyboard).
                _current = _target = scroller.VerticalOffset;
                _lastFrame = TimeSpan.Zero;
                _running = true;
                CompositionTarget.Rendering += OnFrame;
            }

            _target = Math.Clamp(_target + distance, 0, scroller.ScrollableHeight);
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == _lastFrame) return; // Rendering can fire twice for one frame
            var elapsed = _lastFrame == TimeSpan.Zero ? TimeSpan.FromMilliseconds(16) : now - _lastFrame;
            _lastFrame = now;

            // Someone else moved the list meanwhile (scrollbar drag, keyboard): stop and let them.
            if (Math.Abs(scroller.VerticalOffset - Math.Clamp(_current, 0, scroller.ScrollableHeight)) > 2)
            {
                Stop();
                return;
            }

            _current = Step(_current, _target, elapsed);
            scroller.ScrollToVerticalOffset(_current);
            if (_current == _target) Stop();
        }

        private void Stop()
        {
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = child as T ?? FindDescendant<T>(child);
            if (found is not null) return found;
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }
}
