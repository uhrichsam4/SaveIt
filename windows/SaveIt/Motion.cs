using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SaveIt
{
    /// <summary>Damped harmonic spring (unit mass) parameterised like SwiftUI: response (period of the
    /// undamped oscillation, seconds) and damping fraction. Stepped explicitly from CompositionTarget.Rendering.</summary>
    internal sealed class Spring
    {
        public double Value;
        public double Velocity;
        public double Target;
        public double Response;
        public double Damping;
        readonly double epsilon;

        public Spring(double value, double response = 0.42, double damping = 0.82, double epsilon = 0.02)
        {
            Value = Target = value;
            Response = response;
            Damping = damping;
            this.epsilon = epsilon;
        }

        public bool Settled => Math.Abs(Target - Value) <= epsilon && Math.Abs(Velocity) <= epsilon * 4;

        public void Snap(double v) { Value = Target = v; Velocity = 0; }

        /// <summary>Advances the spring by dt seconds using fixed 1/240s semi-implicit Euler sub-steps.</summary>
        public void Step(double dt)
        {
            if (Settled) { Value = Target; Velocity = 0; return; }
            double omega = 2 * Math.PI / Response;
            double k = omega * omega;
            double c = 2 * Damping * omega;
            int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
            double h = dt / n;
            for (int i = 0; i < n; i++)
            {
                double a = -k * (Value - Target) - c * Velocity;
                Velocity += a * h;
                Value += Velocity * h;
            }
            if (Settled) { Value = Target; Velocity = 0; }
        }
    }

    /// <summary>Easing curve that follows the analytic step response of the same damped spring, so
    /// WPF storyboard animations (pills, selection, content) feel like the island's morph.</summary>
    internal sealed class SpringEase : EasingFunctionBase
    {
        public static readonly DependencyProperty ResponseProperty = DependencyProperty.Register(nameof(Response), typeof(double), typeof(SpringEase), new PropertyMetadata(0.42));
        public static readonly DependencyProperty DampingProperty = DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(0.82));
        public static readonly DependencyProperty SecondsProperty = DependencyProperty.Register(nameof(Seconds), typeof(double), typeof(SpringEase), new PropertyMetadata(0.6));

        public double Response { get => (double)GetValue(ResponseProperty); set => SetValue(ResponseProperty, value); }
        public double Damping { get => (double)GetValue(DampingProperty); set => SetValue(DampingProperty, value); }
        /// <summary>The animation's Duration in seconds (maps normalised time back to seconds).</summary>
        public double Seconds { get => (double)GetValue(SecondsProperty); set => SetValue(SecondsProperty, value); }

        protected override double EaseInCore(double t)
        {
            // EasingMode defaults to EaseOut, which WPF computes as 1 - EaseIn(1 - t);
            // we therefore return the mirrored curve here so EaseOut yields the spring response.
            return 1 - Response01(1 - t);
        }

        double Response01(double t)
        {
            if (t >= 1) return 1;
            double time = t * Seconds;
            double w0 = 2 * Math.PI / Response;
            double z = Math.Min(Damping, 0.999);
            double wd = w0 * Math.Sqrt(1 - z * z);
            double env = Math.Exp(-z * w0 * time);
            return 1 - env * (Math.Cos(wd * time) + z * w0 / wd * Math.Sin(wd * time));
        }

        protected override Freezable CreateInstanceCore() => new SpringEase();
    }

    internal static class Anim
    {
        public static readonly IEasingFunction Smooth = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
        public static readonly IEasingFunction QuickOut = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });

        static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

        public static IEasingFunction Spring(double response, double damping, double seconds)
        {
            var e = new SpringEase { Response = response, Damping = damping, Seconds = seconds };
            e.Freeze();
            return e;
        }

        public static DoubleAnimation To(double to, double ms, IEasingFunction? ease = null, double delayMs = 0)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = ease ?? Smooth,
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
            };
            a.Freeze();
            return a;
        }

        public static DoubleAnimation FromTo(double from, double to, double ms, IEasingFunction? ease = null, double delayMs = 0)
        {
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = ease ?? Smooth,
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
            };
            a.Freeze();
            return a;
        }

        /// <summary>Opacity + translate + scale transform pair used by all content transitions.</summary>
        public static (ScaleTransform scale, TranslateTransform move) Transforms(FrameworkElement e, Point origin)
        {
            if (e.RenderTransform is TransformGroup g && g.Children.Count == 2 && g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
                return (s, t);
            var sc = new ScaleTransform(1, 1);
            var tr = new TranslateTransform();
            var grp = new TransformGroup();
            grp.Children.Add(sc);
            grp.Children.Add(tr);
            e.RenderTransform = grp;
            e.RenderTransformOrigin = origin;
            return (sc, tr);
        }

        /// <summary>Content enters after the blob has started growing: fade + slight drop + scale.</summary>
        public static void In(FrameworkElement e, double delayMs = 90, double ms = 320, double fromY = -5, double fromScale = 0.96)
        {
            var (s, t) = Transforms(e, new Point(0.5, 0));
            // Base values hold the "before" pose during the BeginTime delay.
            e.Opacity = 0; t.Y = fromY; s.ScaleX = s.ScaleY = fromScale;
            e.BeginAnimation(UIElement.OpacityProperty, FromTo(0, 1, ms, Smooth, delayMs));
            t.BeginAnimation(TranslateTransform.YProperty, FromTo(fromY, 0, ms, Smooth, delayMs));
            s.BeginAnimation(ScaleTransform.ScaleXProperty, FromTo(fromScale, 1, ms, Smooth, delayMs));
            s.BeginAnimation(ScaleTransform.ScaleYProperty, FromTo(fromScale, 1, ms, Smooth, delayMs));
        }

        /// <summary>Content leaves before the blob shrinks: quick fade + tiny scale down.</summary>
        public static void Out(FrameworkElement e, Action done, double ms = 120)
        {
            var (s, _) = Transforms(e, new Point(0.5, 0));
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = QuickOut };
            a.Completed += (_, _) => done();
            e.IsHitTestVisible = false;
            e.BeginAnimation(UIElement.OpacityProperty, a);
            s.BeginAnimation(ScaleTransform.ScaleXProperty, To(0.98, ms, QuickOut));
            s.BeginAnimation(ScaleTransform.ScaleYProperty, To(0.98, ms, QuickOut));
        }
    }

    /// <summary>The single black blob: top edge flush with the screen top, concave "ears" flaring out
    /// at the top corners, rounded bottom corners (port of IslandShape.swift).</summary>
    internal static class IslandGeometry
    {
        public static Geometry Build(double centerX, double width, double height, double bottomRadius, double earRadius, bool closed)
        {
            double w = Math.Max(0, width), h = Math.Max(0, height);
            double ear = Math.Max(0, Math.Min(earRadius, h * 0.45));
            double br = Math.Max(0, Math.Min(Math.Min(bottomRadius, w / 2), h - ear));
            double left = centerX - w / 2, right = centerX + w / 2;
            const double top = 0;
            double bottom = h;

            var g = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(left - ear, top), true, closed);
                if (ear > 0.01)
                    c.ArcTo(new Point(left, top + ear), new Size(ear, ear), 0, false, SweepDirection.Clockwise, true, true);
                else
                    c.LineTo(new Point(left, top), true, true);
                if (br > 0.01)
                {
                    c.LineTo(new Point(left, bottom - br), true, true);
                    c.ArcTo(new Point(left + br, bottom), new Size(br, br), 0, false, SweepDirection.Counterclockwise, true, true);
                    c.LineTo(new Point(right - br, bottom), true, true);
                    c.ArcTo(new Point(right, bottom - br), new Size(br, br), 0, false, SweepDirection.Counterclockwise, true, true);
                }
                else
                {
                    c.LineTo(new Point(left, bottom), true, true);
                    c.LineTo(new Point(right, bottom), true, true);
                }
                if (ear > 0.01)
                {
                    c.LineTo(new Point(right, top + ear), true, true);
                    c.ArcTo(new Point(right + ear, top), new Size(ear, ear), 0, false, SweepDirection.Clockwise, true, true);
                }
                else
                {
                    c.LineTo(new Point(right, top), true, true);
                }
                c.LineTo(new Point(right + ear, top), closed, true);
            }
            g.Freeze();
            return g;
        }
    }
}
