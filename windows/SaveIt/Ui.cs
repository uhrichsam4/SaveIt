using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SaveIt
{
    internal static class Theme
    {
        public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static readonly Brush White = Solid(255, 255, 255, 1);
        public static readonly Brush Black = Solid(0, 0, 0, 1);
        public static readonly Brush Green = Solid(0x30, 0xD1, 0x58, 1);
        public static readonly Brush Red = Solid(0xFF, 0x45, 0x3A, 1);
        public static readonly Brush Orange = Solid(0xFF, 0x9F, 0x0A, 1);
        public static readonly Brush Clear = Solid(0, 0, 0, 0);

        static readonly Brush[] whites = new Brush[101];

        /// <summary>Frozen white brush with the given opacity (cached per percent).</summary>
        public static Brush W(double opacity)
        {
            int i = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 100);
            return whites[i] ??= Solid(255, 255, 255, i / 100.0);
        }

        public static Brush Solid(byte r, byte g, byte b, double a)
        {
            var br = new SolidColorBrush(Color.FromArgb((byte)Math.Round(a * 255), r, g, b));
            br.Freeze();
            return br;
        }

        public static TextBlock Text(string text, double size, FontWeight weight, Brush fg)
        {
            var t = new TextBlock
            {
                Text = text,
                FontFamily = Font,
                FontSize = size,
                FontWeight = weight,
                Foreground = fg,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
            };
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(t, TextRenderingMode.Grayscale);
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        public static TextBlock Glyph(string glyph, double size, Brush fg)
        {
            var t = new TextBlock
            {
                Text = glyph,
                FontFamily = Icons,
                FontSize = size,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            TextOptions.SetTextRenderingMode(t, TextRenderingMode.Grayscale);
            return t;
        }

        /// <summary>Middle-truncates to fit a width ("Big Buck Bun…Trailer).mp4").</summary>
        public static string MiddleTrim(string text, double maxWidth, double size, FontWeight weight)
        {
            var tf = new Typeface(Font, FontStyles.Normal, weight, FontStretches.Normal);
            double Measure(string s) => new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;
            if (Measure(text) <= maxWidth) return text;
            int lo = 1, hi = text.Length - 1, best = 1;
            while (lo <= hi)
            {
                int keep = (lo + hi) / 2;
                int head = (keep + 1) / 2, tail = keep - head;
                var candidate = text.Substring(0, head) + "…" + text.Substring(text.Length - tail);
                if (Measure(candidate) <= maxWidth) { best = keep; lo = keep + 1; } else hi = keep - 1;
            }
            int h2 = (best + 1) / 2, t2 = best - h2;
            return text.Substring(0, h2) + "…" + text.Substring(text.Length - t2);
        }
    }

    /// <summary>Vector icons drawn as paths (crisp at any DPI, no font dependency).</summary>
    internal static class Icons
    {
        static Path Stroked(string data, double size, Brush brush, double thickness, double box)
        {
            var p = new Path
            {
                Data = Geometry.Parse(data),
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = size,
                Height = size,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            p.RenderTransformOrigin = new Point(0, 0);
            p.LayoutTransform = new ScaleTransform(size / box, size / box);
            p.Width = box; p.Height = box;
            return p;
        }

        /// <summary>Download arrow (16 box).</summary>
        public static FrameworkElement ArrowDown(double size, Brush brush) =>
            Stroked("M8,2.2 L8,13.2 M3.4,8.8 L8,13.4 L12.6,8.8", size, brush, 2.3, 16);

        /// <summary>Arrow up-right ("reveal").</summary>
        public static FrameworkElement ArrowUpRight(double size, Brush brush) =>
            Stroked("M4,12 L12,4 M5.5,4 L12,4 L12,10.5", size, brush, 2.0, 16);

        public static FrameworkElement Plus(double size, Brush brush) =>
            Stroked("M8,3 L8,13 M3,8 L13,8", size, brush, 2.2, 16);

        public static FrameworkElement Cross(double size, Brush brush) =>
            Stroked("M4,4 L12,12 M12,4 L4,12", size, brush, 2.2, 16);

        public static FrameworkElement Retry(double size, Brush brush) =>
            Stroked("M12.6,5.2 A5.4,5.4 0 1 0 13.4,9.2 M12.9,1.8 L12.9,5.6 L9.1,5.6", size, brush, 2.0, 16);

        public static FrameworkElement Folder(double size, Brush brush)
        {
            var p = new Path
            {
                Data = Geometry.Parse("M1.5,4 C1.5,3.2 2.1,2.6 2.9,2.6 L6.1,2.6 L7.6,4.1 L13.1,4.1 C13.9,4.1 14.5,4.7 14.5,5.5 L14.5,12 C14.5,12.8 13.9,13.4 13.1,13.4 L2.9,13.4 C2.1,13.4 1.5,12.8 1.5,12 Z"),
                Fill = brush,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
                VerticalAlignment = VerticalAlignment.Center,
            };
            return p;
        }

        public static FrameworkElement Play(double size, Brush brush) => new Path
        {
            Data = Geometry.Parse("M4.5,2.8 L13,8 L4.5,13.2 Z"),
            Fill = brush,
            Stroke = brush,
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 16, Height = 16,
            Stretch = Stretch.None,
            LayoutTransform = new ScaleTransform(size / 16, size / 16),
            VerticalAlignment = VerticalAlignment.Center,
        };

        public static FrameworkElement Link(double size, Brush brush) =>
            Stroked("M6.8,9.2 L9.2,6.8 M7.4,4.6 L8.6,3.4 C9.8,2.2 11.8,2.2 13,3.4 C14.2,4.6 14.2,6.6 13,7.8 L11.8,9 M8.6,11.4 L7.4,12.6 C6.2,13.8 4.2,13.8 3,12.6 C1.8,11.4 1.8,9.4 3,8.2 L4.2,7", size, brush, 1.8, 16);

        public static FrameworkElement Film(double size, Brush brush) =>
            Stroked("M2,2.8 L14,2.8 L14,13.2 L2,13.2 Z M5,2.8 L5,13.2 M11,2.8 L11,13.2 M2,6.3 L5,6.3 M2,9.7 L5,9.7 M11,6.3 L14,6.3 M11,9.7 L14,9.7", size, brush, 1.4, 16);

        public static FrameworkElement Music(double size, Brush brush)
        {
            var g = new GeometryGroup { FillRule = FillRule.Nonzero };
            g.Children.Add(Geometry.Parse("M6,11.5 L6,3.5 L13,2 L13,10"));
            var p = new Path
            {
                Data = g,
                Stroke = brush,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            };
            var dg = new GeometryGroup();
            dg.Children.Add(new EllipseGeometry(new Point(4.2, 11.6), 2.0, 1.8));
            dg.Children.Add(new EllipseGeometry(new Point(11.2, 10.1), 2.0, 1.8));
            var dots = new Path
            {
                Data = dg,
                Fill = brush,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            };
            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(p);
            grid.Children.Add(dots);
            return grid;
        }

        /// <summary>Filled circle with a check (checkmark.circle.fill).</summary>
        public static FrameworkElement CheckCircle(double size, Brush circle, Brush check)
        {
            var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(new Ellipse { Fill = circle });
            var c = new Path
            {
                Data = Geometry.Parse("M4.6,8.3 L7,10.7 L11.6,5.6"),
                Stroke = check,
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            };
            grid.Children.Add(c);
            return grid;
        }

        /// <summary>Filled circle with "!" (exclamationmark.circle.fill).</summary>
        public static FrameworkElement ErrorCircle(double size, Brush circle, Brush mark)
        {
            var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(new Ellipse { Fill = circle });
            grid.Children.Add(new Path
            {
                Data = Geometry.Parse("M8,4.3 L8,8.9"),
                Stroke = mark,
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            });
            grid.Children.Add(new Path
            {
                Data = new EllipseGeometry(new Point(8, 11.4), 1.1, 1.1),
                Fill = mark,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            });
            return grid;
        }

        /// <summary>exclamationmark.triangle.fill</summary>
        public static FrameworkElement Warning(double size, Brush fill, Brush mark)
        {
            var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(new Path
            {
                Data = Geometry.Parse("M8,1.6 C8.5,1.6 8.9,1.85 9.15,2.3 L15,12.7 C15.5,13.6 14.9,14.6 13.9,14.6 L2.1,14.6 C1.1,14.6 0.5,13.6 1,12.7 L6.85,2.3 C7.1,1.85 7.5,1.6 8,1.6 Z"),
                Fill = fill,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            });
            grid.Children.Add(new Path
            {
                Data = Geometry.Parse("M8,5.6 L8,9.6"),
                Stroke = mark,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            });
            grid.Children.Add(new Path
            {
                Data = new EllipseGeometry(new Point(8, 12.0), 1.05, 1.05),
                Fill = mark,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            });
            return grid;
        }

        /// <summary>The X (formerly Twitter) mark, drawn as two strokes.</summary>
        public static FrameworkElement XLogo(double size, Brush brush)
        {
            var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(new Path
            {
                Data = Geometry.Parse("M1.2,1.5 L5.6,1.5 L14.8,14.5 L10.4,14.5 Z M3.4,2.7 L10.9,13.3 L12.6,13.3 L5.1,2.7 Z"),
                Fill = brush,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            });
            grid.Children.Add(new Path
            {
                Data = Geometry.Parse("M13.8,1.5 L9.2,6.7 M6.8,9.4 L2.2,14.5"),
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(size / 16, size / 16),
            });
            return grid;
        }

        /// <summary>bubble.left.fill</summary>
        public static FrameworkElement Bubble(double size, Brush brush) => new Path
        {
            Data = Geometry.Parse("M4.6,2 L11.4,2 C13.4,2 15,3.6 15,5.6 L15,8.4 C15,10.4 13.4,12 11.4,12 L7.2,12 L3.6,14.6 C3.2,14.9 2.7,14.6 2.8,14.1 L3.2,11.7 C1.9,11.1 1,9.8 1,8.4 L1,5.6 C1,3.6 2.6,2 4.6,2 Z"),
            Fill = brush,
            Width = 16, Height = 16,
            Stretch = Stretch.None,
            LayoutTransform = new ScaleTransform(size / 16, size / 16),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Borderless press-to-click control with the macOS PressStyle feel (scale 0.94 + dim).</summary>
    internal class Pressable : Border
    {
        public event Action? Click;
        public double PressScale { get; set; } = 0.94;
        readonly ScaleTransform scale = new(1, 1);
        bool pressed;

        public Pressable()
        {
            RenderTransform = scale;
            RenderTransformOrigin = new Point(0.5, 0.5);
            Cursor = Cursors.Hand;
            Background = Theme.Clear;   // hit-testable even when visually empty
            Focusable = false;
            SnapsToDevicePixels = true;
        }

        void Press(bool down)
        {
            if (pressed == down) return;
            pressed = down;
            var ease = down ? Anim.QuickOut : Anim.Spring(0.25, 0.7, 0.45);
            double ms = down ? 90 : 450;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(down ? PressScale : 1, ms, ease));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(down ? PressScale : 1, ms, ease));
            BeginAnimation(OpacityProperty, Anim.To(down ? 0.85 : 1, down ? 90 : 200, Anim.Smooth));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            CaptureMouse();
            Press(true);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!IsMouseCaptured) return;
            var p = e.GetPosition(this);
            ReleaseMouseCapture();
            Press(false);
            e.Handled = true;
            if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight) Click?.Invoke();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            Press(false);
        }
    }

    /// <summary>Pill button with icon + title (PillButton in the mac app).</summary>
    internal sealed class PillButton : Pressable
    {
        public PillButton(string title, FrameworkElement icon, bool prominent, Action action)
        {
            Height = L.PillRowHeight;
            CornerRadius = new CornerRadius(L.PillRowHeight / 2);
            Background = prominent ? Theme.White : Theme.W(0.12);
            Padding = new Thickness(12, 0, 12, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            icon.Margin = new Thickness(0, 0, 5, 0);
            row.Children.Add(icon);
            row.Children.Add(Theme.Text(title, 12, FontWeights.SemiBold, prominent ? Theme.Black : Theme.White));
            Child = row;
            Click += action;
        }
    }
}
