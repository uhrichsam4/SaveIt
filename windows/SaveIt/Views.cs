using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SaveIt
{
    /// <summary>A content view shown inside the island. Refresh() re-reads state; Tick() receives the
    /// spring-smoothed progress every frame while it moves.</summary>
    internal abstract class IslandView : Canvas
    {
        protected readonly Island I;
        protected IslandView(Island island) { I = island; }
        public virtual void Refresh() { }
        public virtual void Tick(double progress) { }
        public virtual void Appear() => Anim.In(this);
    }

    // MARK: - Creator links (top band)

    internal sealed class SocialBar : Grid
    {
        const string XHandle = "ClipCenter101";
        const string XUrl = "https://x.com/ClipCenter101";
        const string Discord = "samiamfx";

        readonly Grid bubbleIcon = new() { Width = 11, Height = 11 };
        readonly TextBlock discordText;
        DispatcherTimer? copiedTimer;

        public SocialBar(Island island)
        {
            Width = L.ExpandedWidth;
            Height = L.BandHeight;
            Margin = new Thickness(0);

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(Icons.XLogo(11, Theme.White));
            left.Children.Add(Spaced(Theme.Text(XHandle, 11.5, FontWeights.SemiBold, Theme.White)));
            var xLink = Link(left, $"Open @{XHandle} on X", () => island.OpenUrl(XUrl));
            xLink.HorizontalAlignment = HorizontalAlignment.Left;
            xLink.Margin = new Thickness(L.SidePad - 8, 0, 0, 0);

            bubbleIcon.Children.Add(Icons.Bubble(10.5, Theme.White));
            discordText = Theme.Text(Discord, 11.5, FontWeights.SemiBold, Theme.White);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(bubbleIcon);
            right.Children.Add(Spaced(discordText));
            var dLink = Link(right, $"Discord @{Discord} — click to copy", () =>
            {
                if (island.CopyText(Discord)) ShowCopied();
            });
            dLink.HorizontalAlignment = HorizontalAlignment.Right;
            dLink.Margin = new Thickness(0, 0, L.SidePad - 8, 0);

            Children.Add(xLink);
            Children.Add(dLink);
        }

        static FrameworkElement Spaced(FrameworkElement e)
        {
            e.Margin = new Thickness(5, 0, 0, 0);
            return e;
        }

        /// <summary>Dimmed until hovered, with a faint capsule behind it on hover.</summary>
        static Pressable Link(FrameworkElement label, string tip, Action action)
        {
            var hoverBg = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)) { Opacity = 0 };
            var p = new Pressable
            {
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(8, 0, 8, 0),
                Background = hoverBg,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tip,
            };
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Opacity = 0.6;
            p.Child = label;
            p.MouseEnter += (_, _) =>
            {
                label.BeginAnimation(OpacityProperty, Anim.To(1, 180));
                hoverBg.BeginAnimation(Brush.OpacityProperty, Anim.To(1, 180));
            };
            p.MouseLeave += (_, _) =>
            {
                label.BeginAnimation(OpacityProperty, Anim.To(0.6, 180));
                hoverBg.BeginAnimation(Brush.OpacityProperty, Anim.To(0, 180));
            };
            p.Click += action;
            return p;
        }

        void ShowCopied()
        {
            Swap(true);
            copiedTimer?.Stop();
            copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            copiedTimer.Tick += (_, _) => { copiedTimer?.Stop(); Swap(false); };
            copiedTimer.Start();
        }

        void Swap(bool copied)
        {
            // Quick cross-fade of icon + text.
            var fadeOut = Anim.To(0, 110, Anim.QuickOut);
            var tb = discordText;
            var ic = bubbleIcon;
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)) { EasingFunction = Anim.QuickOut };
            a.Completed += (_, _) =>
            {
                tb.Text = copied ? "Copied" : Discord;
                tb.Foreground = Theme.White;
                ic.Children.Clear();
                ic.Children.Add(copied ? Icons.CheckCircle(11, Theme.Green, Theme.Black) : Icons.Bubble(10.5, Theme.White));
                tb.BeginAnimation(OpacityProperty, Anim.To(1, 200));
                ic.BeginAnimation(OpacityProperty, Anim.To(1, 200));
                if (copied)
                {
                    var s = new ScaleTransform(0.6, 0.6);
                    ic.RenderTransformOrigin = new Point(0.5, 0.5);
                    ic.RenderTransform = s;
                    var sp = Anim.FromTo(0.6, 1, 500, Anim.Spring(0.3, 0.55, 0.5));
                    s.BeginAnimation(ScaleTransform.ScaleXProperty, sp);
                    s.BeginAnimation(ScaleTransform.ScaleYProperty, sp);
                }
            };
            tb.BeginAnimation(OpacityProperty, a);
            ic.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    // MARK: - Live activity pill

    internal sealed class ProgressRing : Grid
    {
        const double Size = 16, Thick = 2.6;
        readonly Path arc;
        double last = -1;

        public ProgressRing()
        {
            Width = Size; Height = Size;
            Children.Add(new Ellipse { Stroke = Theme.W(0.18), StrokeThickness = Thick });
            arc = new Path
            {
                Stroke = Theme.White,
                StrokeThickness = Thick,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            Children.Add(arc);
        }

        public void Set(double progress)
        {
            double p = Math.Max(0.02, Math.Min(1, progress));
            if (Math.Abs(p - last) < 0.0005) return;
            last = p;
            double r = (Size - Thick) / 2, c = Size / 2;
            if (p >= 0.9995)
            {
                arc.Data = new EllipseGeometry(new Point(c, c), r, r);
                return;
            }
            double a = p * 2 * Math.PI;
            var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c, c - r), false, false);
                ctx.ArcTo(end, new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true, true);
            }
            g.Freeze();
            arc.Data = g;
        }
    }

    internal sealed class LiveView : IslandView
    {
        readonly ProgressRing ring = new();
        readonly TextBlock middle;
        readonly TextBlock percent;
        int lastPct = -1;

        public LiveView(Island island) : base(island)
        {
            Width = L.LiveWidth; Height = L.LiveHeight;
            SetLeft(ring, (L.LiveExtension - 16) / 2); SetTop(ring, (L.LiveHeight - 16) / 2);
            Children.Add(ring);

            middle = Theme.Text("", 11, FontWeights.Medium, Theme.W(0.55));
            middle.Width = L.BandWidth - 8;
            middle.TextAlignment = TextAlignment.Center;
            SetLeft(middle, L.LiveExtension + 4); SetTop(middle, (L.LiveHeight - 15) / 2);
            middle.Height = 15;
            Children.Add(middle);

            percent = Theme.Text("0%", 12, FontWeights.SemiBold, Theme.White);
            percent.Width = L.LiveExtension;
            percent.TextAlignment = TextAlignment.Center;
            percent.Height = 16;
            SetLeft(percent, L.LiveExtension + L.BandWidth - 1); SetTop(percent, (L.LiveHeight - 16) / 2);
            Children.Add(percent);
        }

        public override void Refresh()
        {
            middle.Text = I.Stage == Stage.Setup ? "Setting up…" : (I.VideoTitle.Length > 0 ? I.VideoTitle : "Starting…");
        }

        public override void Tick(double progress)
        {
            ring.Set(progress);
            int pct = (int)Math.Floor(progress * 100 + 1e-9);
            if (pct != lastPct) { lastPct = pct; percent.Text = pct + "%"; }
        }
    }

    internal sealed class BadgeView : IslandView
    {
        readonly FrameworkElement icon;
        readonly bool success;

        public BadgeView(Island island, bool success) : base(island)
        {
            this.success = success;
            Width = L.LiveWidth; Height = L.LiveHeight;
            icon = success ? Icons.CheckCircle(17, Theme.Green, Theme.Black) : Icons.ErrorCircle(17, Theme.Red, Theme.Black);
            SetLeft(icon, (L.LiveExtension - 17) / 2); SetTop(icon, (L.LiveHeight - 17) / 2);
            Children.Add(icon);

            var mid = Theme.Text(success ? System.IO.Path.GetFileName(island.ResultPath ?? "") : island.ErrorMessage, 11, FontWeights.Medium, Theme.W(0.55));
            mid.Width = L.BandWidth - 8; mid.Height = 15;
            mid.TextAlignment = TextAlignment.Center;
            SetLeft(mid, L.LiveExtension + 4); SetTop(mid, (L.LiveHeight - 15) / 2);
            Children.Add(mid);

            var label = Theme.Text(success ? "Done" : "Failed", 11, FontWeights.SemiBold, success ? Theme.Green : Theme.Red);
            label.Width = L.LiveExtension; label.Height = 15;
            label.TextAlignment = TextAlignment.Center;
            SetLeft(label, L.LiveExtension + L.BandWidth); SetTop(label, (L.LiveHeight - 15) / 2);
            Children.Add(label);
        }

        public override void Appear()
        {
            base.Appear();
            // symbolEffect(.bounce) after a beat.
            var s = new ScaleTransform(1, 1);
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = s;
            var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(150) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.72, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), Anim.QuickOut));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560)), Anim.Spring(0.3, 0.5, 0.45)));
            a.Freeze();
            s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    // MARK: - Expanded: idle

    internal sealed class IdleView : IslandView
    {
        readonly Border field;
        readonly TextBox box;
        readonly TextBlock placeholder;
        readonly Pressable chip;
        readonly Border chipHost;
        readonly TextBlock chipText;
        readonly FrameworkElement row0;
        readonly DockPanel row2;
        readonly QualityPicker picker;
        readonly TextBlock folderText;
        bool chipShown;
        bool settingText;

        public IdleView(Island island) : base(island)
        {
            Width = L.InnerWidth;
            Height = L.MaxContentHeight;

            // Row 0: link field + download button
            var r0 = new Grid { Width = L.InnerWidth, Height = L.RowHeight };
            r0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(L.RowHeight + 10) });

            box = new TextBox
            {
                Background = Theme.Clear,
                BorderThickness = new Thickness(0),
                Foreground = Theme.White,
                CaretBrush = Theme.White,
                SelectionBrush = Theme.W(0.5),
                FontFamily = Theme.Font,
                FontSize = 14,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null,
                AcceptsReturn = false,
            };
            TextOptions.SetTextRenderingMode(box, TextRenderingMode.Grayscale);
            placeholder = Theme.Text("Paste a video link…", 14, FontWeights.Normal, Theme.W(0.4));
            placeholder.IsHitTestVisible = false;
            placeholder.Margin = new Thickness(2, 0, 0, 0);
            var fieldGrid = new Grid();
            fieldGrid.Children.Add(placeholder);
            fieldGrid.Children.Add(box);
            field = new Border
            {
                Height = L.RowHeight,
                CornerRadius = new CornerRadius(L.RowHeight / 2),
                Background = Theme.W(0.10),
                BorderBrush = Theme.W(0.05),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 0, 13, 0),
                Child = fieldGrid,
                Cursor = Cursors.IBeam,
            };
            field.MouseLeftButtonDown += (_, _) => box.Focus();
            r0.Children.Add(field);

            var go = new Pressable
            {
                Width = L.RowHeight,
                Height = L.RowHeight,
                CornerRadius = new CornerRadius(L.RowHeight / 2),
                Background = Theme.White,
                Child = Icons.ArrowDown(15, Theme.Black),
                ToolTip = "Download",
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            go.Click += island.Submit;
            Grid.SetColumn(go, 1);
            r0.Children.Add(go);
            row0 = r0;
            Children.Add(r0);

            box.Text = island.Link;
            placeholder.Visibility = island.Link.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            box.TextChanged += (_, _) =>
            {
                placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
                if (!settingText) island.LinkEdited(box.Text);
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; island.Submit(); }
            };
            box.GotKeyboardFocus += (_, _) => field.BorderBrush = Theme.W(0.14);
            box.LostKeyboardFocus += (_, _) => field.BorderBrush = Theme.W(0.05);

            // Chip: "Use copied link · domain/…"
            var chipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            chipRow.Children.Add(Icons.Link(11, Theme.W(0.85)));
            chipText = Theme.Text("", 12, FontWeights.Normal, Theme.W(0.85));
            chipText.Margin = new Thickness(6, 0, 0, 0);
            chipText.MaxWidth = L.InnerWidth - 40;
            chipRow.Children.Add(chipText);
            chip = new Pressable
            {
                Height = L.ChipHeight,
                CornerRadius = new CornerRadius(L.ChipHeight / 2),
                Background = Theme.W(0.09),
                Padding = new Thickness(10, 0, 10, 0),
                Child = chipRow,
                PressScale = 0.96,
            };
            chip.Click += island.UseClipboard;
            chipHost = new Border { Child = chip, HorizontalAlignment = HorizontalAlignment.Left };
            SetTop(chipHost, L.RowHeight + L.RowGap);
            Children.Add(chipHost);

            // Row 2: quality pills, folder, reveal
            row2 = new DockPanel { Width = L.InnerWidth, Height = L.PillRowHeight, LastChildFill = false };
            picker = new QualityPicker(island.Quality, island.SetQuality);
            DockPanel.SetDock(picker, Dock.Left);
            row2.Children.Add(picker);

            var reveal = new Pressable
            {
                Width = L.PillRowHeight,
                Height = L.PillRowHeight,
                CornerRadius = new CornerRadius(L.PillRowHeight / 2),
                Background = Theme.W(0.08),
                Child = Icons.ArrowUpRight(11, Theme.W(0.85)),
                ToolTip = "Show folder in Explorer",
            };
            reveal.Click += island.RevealFolder;
            DockPanel.SetDock(reveal, Dock.Right);
            row2.Children.Add(reveal);

            var folderRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            folderRow.Children.Add(Icons.Folder(12, Theme.W(0.85)));
            folderText = Theme.Text("", 12, FontWeights.Normal, Theme.W(0.85));
            folderText.Margin = new Thickness(6, 0, 0, 0);
            folderText.MaxWidth = 150 - 20 - 18;
            folderRow.Children.Add(folderText);
            var folder = new Pressable
            {
                Height = L.PillRowHeight,
                MaxWidth = 150,
                CornerRadius = new CornerRadius(L.PillRowHeight / 2),
                Background = Theme.W(0.08),
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 8, 0),
                Child = folderRow,
                ToolTip = "Choose download folder",
            };
            folder.Click += island.ChooseFolder;
            DockPanel.SetDock(folder, Dock.Right);
            row2.Children.Add(folder);
            Children.Add(row2);

            chipShown = island.ShowsChip;
            chipHost.Opacity = chipShown ? 1 : 0;
            chipHost.Visibility = chipShown ? Visibility.Visible : Visibility.Hidden;
            SetTop(row2, Row2Top(chipShown));
            Refresh();
        }

        static double Row2Top(bool chip) => L.RowHeight + L.RowGap + (chip ? L.ChipHeight + L.RowGap : 0);

        public override void Appear()
        {
            // Rows arrive one after another (~35ms apart) behind the island's growth.
            Anim.In(row0, 100, 340, -5, 0.98);
            int i = 1;
            if (chipShown) Anim.In(chipHost, 100 + 35 * i++, 340, -5, 0.98);
            Anim.In(row2, 100 + 35 * i, 340, -5, 0.98);
        }

        public override void Refresh()
        {
            if (box.Text != I.Link)
            {
                settingText = true;
                box.Text = I.Link;
                box.CaretIndex = box.Text.Length;
                settingText = false;
            }
            if (I.ClipboardUrl != null) chipText.Text = "Use copied link · " + Links.Short(I.ClipboardUrl);
            folderText.Text = Links.FolderName(I.Folder);
            folderText.ToolTip = I.Folder;
            picker.Select(I.Quality, animated: IsLoaded);

            bool show = I.ShowsChip;
            if (show != chipShown)
            {
                chipShown = show;
                var ease = Anim.Spring(0.42, 0.82, 0.7);
                row2.BeginAnimation(TopProperty, Anim.To(Row2Top(show), 700, ease));
                if (show)
                {
                    chipHost.Visibility = Visibility.Visible;
                    Anim.In(chipHost, 60, 260, -3, 0.95);
                }
                else
                {
                    Anim.Out(chipHost, () => { if (!chipShown) chipHost.Visibility = Visibility.Hidden; }, 140);
                }
                chipHost.IsHitTestVisible = show;
            }
        }

        public void FocusField()
        {
            if (!box.IsKeyboardFocusWithin)
            {
                box.Focus();
                Keyboard.Focus(box);
                box.CaretIndex = box.Text.Length;
            }
        }

        /// <summary>Little "no" shake when Enter is pressed without a usable link.</summary>
        public void Shake()
        {
            var t = new TranslateTransform();
            field.RenderTransform = t;
            var a = new DoubleAnimationUsingKeyFrames();
            double[] xs = { -7, 6, -4, 3, -1.5, 0 };
            for (int k = 0; k < xs.Length; k++)
                a.KeyFrames.Add(new EasingDoubleKeyFrame(xs[k], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55 * (k + 1))), Anim.Smooth));
            a.Freeze();
            t.BeginAnimation(TranslateTransform.XProperty, a);
        }
    }

    internal sealed class QualityPicker : Border
    {
        const double PillW = 50, Gap = 2;
        readonly Border indicator;
        readonly TranslateTransform move = new();
        readonly TextBlock[] labels = new TextBlock[Qualities.All.Length];
        Quality selected;

        public QualityPicker(Quality initial, Action<Quality> onSelect)
        {
            selected = initial;
            Height = L.PillRowHeight;
            CornerRadius = new CornerRadius(L.PillRowHeight / 2);
            Background = Theme.W(0.07);
            Padding = new Thickness(2);
            VerticalAlignment = VerticalAlignment.Center;

            var grid = new Grid();
            indicator = new Border
            {
                Width = PillW,
                Height = L.PillRowHeight - 4,
                CornerRadius = new CornerRadius((L.PillRowHeight - 4) / 2),
                Background = Theme.W(0.18),
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = move,
            };
            grid.Children.Add(indicator);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < Qualities.All.Length; i++)
            {
                var q = Qualities.All[i];
                var label = Theme.Text(q.Label(), 12, FontWeights.SemiBold, Theme.W(0.55));
                label.HorizontalAlignment = HorizontalAlignment.Center;
                labels[i] = label;
                var b = new Pressable
                {
                    Width = PillW,
                    Height = L.PillRowHeight - 4,
                    CornerRadius = new CornerRadius((L.PillRowHeight - 4) / 2),
                    Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0),
                    Child = label,
                    PressScale = 0.95,
                };
                b.Click += () => { Select(q, true); onSelect(q); };
                row.Children.Add(b);
            }
            grid.Children.Add(row);
            Child = grid;
            Select(initial, false, force: true);
        }

        public void Select(Quality q, bool animated, bool force = false)
        {
            if (q == selected && !force) return;
            selected = q;
            int idx = Array.IndexOf(Qualities.All, q);
            double x = idx * (PillW + Gap);
            if (animated)
                move.BeginAnimation(TranslateTransform.XProperty, Anim.To(x, 550, Anim.Spring(0.3, 0.8, 0.55)));
            else
            {
                move.BeginAnimation(TranslateTransform.XProperty, null);
                move.X = x;
            }
            for (int i = 0; i < labels.Length; i++)
                labels[i].Foreground = i == idx ? Theme.White : Theme.W(0.55);
        }
    }

    // MARK: - Expanded: downloading

    internal sealed class DownloadingView : IslandView
    {
        readonly Grid iconHost = new() { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock title;
        readonly TextBlock percent;
        readonly Border fill;
        readonly TextBlock status;
        int lastPct = -1;
        string iconKind = "";

        public DownloadingView(Island island) : base(island)
        {
            Width = L.InnerWidth; Height = 78;

            var r0 = new Grid { Width = L.InnerWidth, Height = 20 };
            r0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            r0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            iconHost.Margin = new Thickness(0, 0, 8, 0);
            r0.Children.Add(iconHost);
            title = Theme.Text("", 14, FontWeights.SemiBold, Theme.White);
            Grid.SetColumn(title, 1);
            r0.Children.Add(title);
            percent = Theme.Text("0%", 14, FontWeights.SemiBold, Theme.White);
            percent.TextAlignment = TextAlignment.Right;
            percent.HorizontalAlignment = HorizontalAlignment.Right;
            percent.Width = 46;
            Grid.SetColumn(percent, 2);
            r0.Children.Add(percent);
            Children.Add(r0);

            var track = new Border { Width = L.InnerWidth, Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.W(0.14) };
            fill = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.White, HorizontalAlignment = HorizontalAlignment.Left };
            track.Child = fill;
            SetTop(track, 32);
            Children.Add(track);

            var r2 = new DockPanel { Width = L.InnerWidth, Height = L.PillRowHeight, LastChildFill = true };
            var cancel = new Pressable
            {
                Height = L.PillRowHeight,
                CornerRadius = new CornerRadius(L.PillRowHeight / 2),
                Background = Theme.W(0.12),
                Padding = new Thickness(14, 0, 14, 0),
                Child = Theme.Text("Cancel", 12, FontWeights.SemiBold, Theme.White),
            };
            cancel.Click += island.CancelDownload;
            DockPanel.SetDock(cancel, Dock.Right);
            r2.Children.Add(cancel);
            status = Theme.Text("", 12, FontWeights.Normal, Theme.W(0.55));
            status.Margin = new Thickness(0, 0, 8, 0);
            r2.Children.Add(status);
            SetTop(r2, 50);
            Children.Add(r2);
            Refresh();
        }

        public override void Refresh()
        {
            bool setup = I.Stage == Stage.Setup;
            string kind = setup ? "setup" : (I.LastQuality == Quality.Mp3 ? "mp3" : "film");
            if (kind != iconKind)
            {
                iconKind = kind;
                iconHost.Children.Clear();
                iconHost.Children.Add(kind switch
                {
                    "setup" => Icons.ArrowDown(12, Theme.W(0.6)),
                    "mp3" => Icons.Music(12, Theme.W(0.6)),
                    _ => Icons.Film(12, Theme.W(0.6)),
                });
            }
            title.Text = setup ? "Setting up SaveIt…" : (I.VideoTitle.Length == 0 ? "Fetching video info…" : I.VideoTitle);
            title.ToolTip = I.VideoTitle.Length > 0 ? I.VideoTitle : null;
            status.Text = StatusLine();
        }

        string StatusLine()
        {
            switch (I.Stage)
            {
                case Stage.Setup: return I.SetupStatus.Length > 0 ? I.SetupStatus : "Getting yt-dlp and ffmpeg…";
                case Stage.Starting: return "Starting…";
                case Stage.Processing: return I.LastQuality == Quality.Mp3 ? "Converting to MP3…" : "Finishing up…";
                default:
                    var parts = new System.Collections.Generic.List<string>();
                    if (I.Speed.Length > 0) parts.Add(I.Speed);
                    if (I.Eta.Length > 0) parts.Add(I.Eta + " left");
                    return parts.Count == 0 ? "Downloading…" : string.Join("  ·  ", parts);
            }
        }

        public override void Tick(double progress)
        {
            fill.Width = Math.Max(6, L.InnerWidth * Math.Clamp(progress, 0, 1));
            int pct = (int)Math.Floor(progress * 100 + 1e-9);
            if (pct != lastPct) { lastPct = pct; percent.Text = pct + "%"; }
        }
    }

    // MARK: - Expanded: done / error

    internal sealed class DoneView : IslandView
    {
        readonly FrameworkElement check;

        public DoneView(Island island) : base(island)
        {
            Width = L.InnerWidth; Height = 82;

            var head = new StackPanel { Orientation = Orientation.Horizontal, Height = 40 };
            check = Icons.CheckCircle(30, Theme.Green, Theme.White);
            check.Margin = new Thickness(0, 0, 12, 0);
            head.Children.Add(check);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Theme.Text("Downloaded", 11, FontWeights.SemiBold, Theme.Green));
            string name = System.IO.Path.GetFileName(island.ResultPath ?? "");
            var nameText = Theme.Text(Theme.MiddleTrim(name, L.InnerWidth - 44, 14, FontWeights.SemiBold), 14, FontWeights.SemiBold, Theme.White);
            nameText.Margin = new Thickness(0, 1, 0, 0);
            texts.Children.Add(nameText);
            head.Children.Add(texts);
            var row0 = new Pressable
            {
                Width = L.InnerWidth,
                Height = 40,
                Child = head,
                PressScale = 0.985,
                ToolTip = "Show in Explorer",
            };
            row0.Click += island.RevealResult;
            Children.Add(row0);

            var r1 = new DockPanel { Width = L.InnerWidth, Height = L.PillRowHeight, LastChildFill = false };
            var newLink = new PillButton("New link", Icons.Plus(10, Theme.White), false, island.Reset);
            DockPanel.SetDock(newLink, Dock.Left);
            r1.Children.Add(newLink);
            var open = new PillButton("Open", Icons.Play(10, Theme.Black), true, island.OpenResult);
            DockPanel.SetDock(open, Dock.Right);
            r1.Children.Add(open);
            var show = new PillButton("Show in Explorer", Icons.Folder(11, Theme.White), false, island.RevealResult) { Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(show, Dock.Right);
            r1.Children.Add(show);
            SetTop(r1, 54);
            Children.Add(r1);
        }

        public override void Appear()
        {
            base.Appear();
            // symbolEffect(.bounce.up)
            var s = new ScaleTransform(1, 1);
            check.RenderTransformOrigin = new Point(0.5, 0.5);
            check.RenderTransform = s;
            var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(120) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), Anim.QuickOut));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(640)), Anim.Spring(0.3, 0.5, 0.5)));
            a.Freeze();
            s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    internal sealed class ErrorView : IslandView
    {
        public ErrorView(Island island) : base(island)
        {
            Width = L.InnerWidth; Height = 90;

            var head = new Grid { Width = L.InnerWidth, Height = 48, ClipToBounds = true };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var warn = Icons.Warning(24, Theme.Orange, Theme.Black);
            warn.HorizontalAlignment = HorizontalAlignment.Left;
            warn.Margin = new Thickness(3, 0, 0, 0);
            head.Children.Add(warn);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Theme.Text("Download failed", 11, FontWeights.SemiBold, Theme.Orange));
            var msg = Theme.Text(island.ErrorMessage, 12.5, FontWeights.Medium, Theme.White);
            msg.TextWrapping = TextWrapping.Wrap;
            msg.TextTrimming = TextTrimming.CharacterEllipsis;
            msg.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            msg.LineHeight = 16;
            msg.MaxHeight = 32;
            msg.Margin = new Thickness(0, 1, 0, 0);
            msg.ToolTip = island.ErrorMessage;
            texts.Children.Add(msg);
            Grid.SetColumn(texts, 1);
            head.Children.Add(texts);
            Children.Add(head);

            var r1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var dismiss = new PillButton("Dismiss", Icons.Cross(10, Theme.White), false, island.Reset) { Margin = new Thickness(0, 0, 8, 0) };
            r1.Children.Add(dismiss);
            r1.Children.Add(new PillButton("Retry", Icons.Retry(11, Theme.Black), true, island.Retry));
            var holder = new Grid { Width = L.InnerWidth, Height = L.PillRowHeight };
            holder.Children.Add(r1);
            SetTop(holder, 62);
            Children.Add(holder);
        }
    }
}
