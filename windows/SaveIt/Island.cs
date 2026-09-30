using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SaveIt
{
    /// <summary>The island window and its controller (port of IslandController + IslandModel + IslandRootView).
    /// Borderless, layered, topmost, never in the taskbar; pinned top-center of the primary monitor.
    /// The shape morph is a real spring stepped on CompositionTarget.Rendering.</summary>
    internal sealed class Island : Window
    {
        // MARK: - State (IslandModel)

        public bool IsExpandedState { get; private set; }
        public Phase Phase { get; private set; } = Phase.Idle;
        public bool BadgeVisible { get; private set; }
        public string Link { get; private set; } = "";
        public string? ClipboardUrl { get; private set; }
        public Quality Quality { get; private set; }
        public string Folder { get; private set; }
        public string VideoTitle { get; private set; } = "";
        public double Progress { get; private set; }
        public string Speed { get; private set; } = "";
        public string Eta { get; private set; } = "";
        public Stage Stage { get; private set; } = Stage.Starting;
        public string SetupStatus { get; private set; } = "";
        public string? ResultPath { get; private set; }
        public string ErrorMessage { get; private set; } = "";
        public Quality LastQuality => lastRequest?.quality ?? Quality;
        (string url, Quality quality)? lastRequest;

        public bool ShowsChip => Phase == Phase.Idle && Link.Length == 0 && ClipboardUrl != null;

        enum Mode { Collapsed, Live, LiveDone, LiveError, Expanded }

        Mode CurrentMode
        {
            get
            {
                if (IsExpandedState) return Mode.Expanded;
                return Phase switch
                {
                    Phase.Downloading => Mode.Live,
                    Phase.Done when BadgeVisible => Mode.LiveDone,
                    Phase.Error when BadgeVisible => Mode.LiveError,
                    _ => Mode.Collapsed,
                };
            }
        }

        double ContentHeight(Phase p) => p switch
        {
            Phase.Idle => L.RowHeight + L.RowGap + L.PillRowHeight + (ShowsChip ? L.ChipHeight + L.RowGap : 0),
            Phase.Downloading => 20 + 12 + 6 + 12 + L.PillRowHeight,
            Phase.Done => 40 + 14 + L.PillRowHeight,
            _ => 48 + 14 + L.PillRowHeight,
        };

        (double w, double h, double br, double ear) Metrics()
        {
            switch (CurrentMode)
            {
                case Mode.Collapsed:
                    return (L.CollapsedWidth, L.CollapsedHeight, L.CollapsedHeight / 2, 0);
                case Mode.Expanded:
                    return (L.ExpandedWidth, L.BandHeight + L.ContentTop + ContentHeight(Phase) + L.ContentBottom, 28, L.MaxEar);
                default:
                    return (L.LiveWidth, L.LiveHeight, Math.Min(14, L.LiveHeight / 2), 6);
            }
        }

        // MARK: - Visuals

        readonly Grid root;
        readonly System.Windows.Shapes.Path shape;
        readonly System.Windows.Shapes.Path stroke;
        readonly Canvas host;
        readonly DropShadowEffect shadow;
        readonly Spring sW, sH, sBr, sEar, sShadow, sStroke, sProgress, sVisible;
        bool animating;
        TimeSpan lastRender = TimeSpan.Zero;
        IslandView? currentView;
        string viewKey = "";
        SocialBar? social;

        // MARK: - Controller state

        readonly Settings settings;
        readonly Downloader downloader;
        readonly bool forced;             // --saveit-state: don't auto-collapse
        readonly string? forcedState;
        IntPtr hwnd;
        double scale = 1;
        Native.RECT winRect;
        readonly DispatcherTimer poll;
        DispatcherTimer? badgeTimer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        double? enteredAt, exitedAt, insideSince;
        double lastFullscreenCheck = -10;
        bool fullscreenHidden;
        bool pickerOpen;
        bool manualOpen;                  // opened from the tray: stay until the cursor visits the island
        bool engaged;                     // user typed, pasted or clicked in the island this session
        bool keyTaken;                    // island took keyboard focus this session
        IntPtr previousForeground;

        public Action? ShowContextMenu;

        const double HoverDelay = 0.12;
        const double LeaveDelay = 0.6;
        const double KeyDelay = 0.35;      // dwell inside the island before it takes keyboard focus
        const double HoverInflateX = 40;   // hot zone beyond the collapsed pill, each side
        const double HotHeight = 10;       // hot zone height (includes the top pixel row)
        const double LiveHoverMargin = 6;
        const double TopSlop = 2;          // px above the monitor edge that still counts
        const double NearDistance = 220;

        public Island(Settings settings, string? forcedState)
        {
            this.settings = settings;
            this.forcedState = forcedState;
            forced = forcedState != null && forcedState != "collapsed" && forcedState != "downloading";
            Quality = settings.QualityValue;
            Folder = settings.FolderValue;
            downloader = new Downloader(a => Dispatcher.BeginInvoke(a));

            Title = "SaveIt";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0;
            Width = L.WindowWidth; Height = L.WindowHeight;
            UseLayoutRounding = true;
            FocusVisualStyle = null;

            var (w, h, br, ear) = Metrics();
            sW = new Spring(w); sH = new Spring(h); sBr = new Spring(br); sEar = new Spring(ear);
            sShadow = new Spring(0, epsilon: 0.002);
            sStroke = new Spring(0.16, epsilon: 0.002);
            sProgress = new Spring(0, 0.35, 1.0, 0.0004);
            sVisible = new Spring(1, 0.3, 1.0, 0.002);

            shadow = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 36,
                ShadowDepth = 8,
                Direction = 270,
                Opacity = 0,
                RenderingBias = RenderingBias.Performance,
            };
            shape = new System.Windows.Shapes.Path { Fill = Brushes.Black };
            stroke = new System.Windows.Shapes.Path { Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.16, IsHitTestVisible = false };
            host = new Canvas { Width = L.WindowWidth, Height = L.WindowHeight };
            root = new Grid { Width = L.WindowWidth, Height = L.WindowHeight, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            root.Children.Add(shape);
            root.Children.Add(stroke);
            root.Children.Add(host);
            Content = root;
            ApplyFrame();

            poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
            poll.Tick += (_, _) => Evaluate();

            SourceInitialized += OnSourceInitialized;
            DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Background);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Deactivated += OnDeactivated;
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            PreviewMouseRightButtonUp += OnPreviewRightUp;
            Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetExStyle(hwnd);
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            ex &= ~Native.WS_EX_APPWINDOW;
            Native.SetExStyle(hwnd, ex);
            Reposition();
            poll.Start();
            if (forcedState != null) ApplyForcedState(forcedState);
            SyncViews();
        }

        void OnDisplayChanged(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Background);

        /// <summary>Pins the fixed-size window top-center on the primary monitor (physical pixels).</summary>
        void Reposition()
        {
            if (hwnd == IntPtr.Zero) return;
            var mon = Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, Native.MONITOR_DEFAULTTOPRIMARY);
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfo(mon, ref mi)) return;
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            try
            {
                if (Native.GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0) s = dx / 96.0;
            }
            catch { }
            scale = s;
            int wpx = (int)Math.Ceiling(L.WindowWidth * s);
            int hpx = (int)Math.Ceiling(L.WindowHeight * s);
            int x = mi.rcMonitor.Left + (mi.rcMonitor.Width - wpx) / 2;
            int y = mi.rcMonitor.Top;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, wpx, hpx, Native.SWP_NOACTIVATE);
            Native.GetWindowRect(hwnd, out winRect);
        }

        void EnsureTopmost()
        {
            if (hwnd != IntPtr.Zero)
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void SetExFlag(long flag, bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            long ex = Native.GetExStyle(hwnd);
            long next = on ? ex | flag : ex & ~flag;
            if (next != ex) Native.SetExStyle(hwnd, next);
        }

        void SetClickThrough(bool on) => SetExFlag(Native.WS_EX_TRANSPARENT, on);
        void SetNoActivate(bool on) => SetExFlag(Native.WS_EX_NOACTIVATE, on);

        // MARK: - Rendering (spring loop)

        void StartLoop()
        {
            if (animating) return;
            animating = true;
            lastRender = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }

        void StopLoop()
        {
            if (!animating) return;
            animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        void OnRendering(object? sender, EventArgs e)
        {
            var t = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds);
            if (t == lastRender) return;   // Rendering can fire more than once per frame
            double dt = lastRender == TimeSpan.Zero ? 1 / 60.0 : (t - lastRender).TotalSeconds;
            lastRender = t;
            dt = Math.Clamp(dt, 0.001, 1 / 20.0);
            sW.Step(dt); sH.Step(dt); sBr.Step(dt); sEar.Step(dt);
            sShadow.Step(dt); sStroke.Step(dt); sProgress.Step(dt); sVisible.Step(dt);
            ApplyFrame();
            if (sW.Settled && sH.Settled && sBr.Settled && sEar.Settled && sShadow.Settled && sStroke.Settled && sProgress.Settled && sVisible.Settled)
            {
                StopLoop();
                if (sShadow.Value <= 0.001) shape.Effect = null;
            }
        }

        void ApplyFrame()
        {
            double cx = L.WindowWidth / 2;
            var fillGeo = IslandGeometry.Build(cx, sW.Value, sH.Value, sBr.Value, sEar.Value, true);
            shape.Data = fillGeo;
            host.Clip = fillGeo;
            stroke.Data = IslandGeometry.Build(cx, sW.Value, sH.Value, sBr.Value, sEar.Value, false);
            stroke.Opacity = Math.Max(0, sStroke.Value);
            if (sShadow.Value > 0.001)
            {
                if (shape.Effect == null) shape.Effect = shadow;
                shadow.Opacity = Math.Clamp(sShadow.Value, 0, 1);
            }
            root.Opacity = Math.Clamp(sVisible.Value, 0, 1);
            currentView?.Tick(sProgress.Value);
        }

        /// <summary>Re-targets the springs and swaps content after any state change.</summary>
        void Update()
        {
            var (w, h, br, ear) = Metrics();
            sW.Target = w; sH.Target = h; sBr.Target = br; sEar.Target = ear;
            var mode = CurrentMode;
            sShadow.Target = mode == Mode.Expanded ? 0.45 : 0;
            sStroke.Target = mode switch { Mode.Expanded => 0.07, Mode.Collapsed => 0.16, _ => 0.09 };
            if (Progress + 0.0005 < sProgress.Value && Phase == Phase.Downloading) sProgress.Snap(Progress);
            sProgress.Target = Progress;
            sVisible.Target = fullscreenHidden && mode != Mode.Expanded ? 0 : 1;
            SyncViews();
            StartLoop();
        }

        string KeyFor(Mode m) => m switch
        {
            Mode.Expanded => "x-" + Phase,
            Mode.Live => "live",
            Mode.LiveDone => "badge-done",
            Mode.LiveError => "badge-error",
            _ => "",
        };

        IslandView? CreateView(string key) => key switch
        {
            "x-Idle" => new IdleView(this),
            "x-Downloading" => new DownloadingView(this),
            "x-Done" => new DoneView(this),
            "x-Error" => new ErrorView(this),
            "live" => new LiveView(this),
            "badge-done" => new BadgeView(this, true),
            "badge-error" => new BadgeView(this, false),
            _ => null,
        };

        void SyncViews()
        {
            if (hwnd == IntPtr.Zero) return;
            var mode = CurrentMode;
            var key = KeyFor(mode);
            if (key != viewKey)
            {
                var old = currentView;
                if (old != null) Anim.Out(old, () => host.Children.Remove(old));
                viewKey = key;
                currentView = CreateView(key);
                if (currentView != null)
                {
                    if (mode == Mode.Expanded)
                    {
                        Canvas.SetLeft(currentView, L.ContentLeft);
                        Canvas.SetTop(currentView, L.ContentY);
                    }
                    else
                    {
                        Canvas.SetLeft(currentView, (L.WindowWidth - L.LiveWidth) / 2);
                        Canvas.SetTop(currentView, 0);
                    }
                    host.Children.Add(currentView);
                    currentView.Refresh();
                    currentView.Tick(sProgress.Value);
                    currentView.Appear();
                }
            }
            else
            {
                currentView?.Refresh();
            }

            bool wantSocial = mode == Mode.Expanded;
            if (wantSocial && social == null)
            {
                social = new SocialBar(this);
                Canvas.SetLeft(social, (L.WindowWidth - L.ExpandedWidth) / 2);
                Canvas.SetTop(social, 0);
                host.Children.Add(social);
                Anim.In(social);
            }
            else if (!wantSocial && social != null)
            {
                var s = social;
                social = null;
                Anim.Out(s, () => host.Children.Remove(s));
            }
        }

        // MARK: - Geometry helpers (physical screen pixels)

        (double l, double t, double r, double b) CenteredRect(double wDip, double hDip)
        {
            double cx = winRect.Left + winRect.Width / 2.0;
            return (cx - wDip * scale / 2, winRect.Top - TopSlop, cx + wDip * scale / 2, winRect.Top + hDip * scale);
        }

        static bool Contains((double l, double t, double r, double b) rc, Native.POINT p) =>
            p.X >= rc.l && p.X < rc.r && p.Y >= rc.t && p.Y < rc.b;

        (double l, double t, double r, double b) IslandRect()
        {
            var (w, h, _, _) = Metrics();
            return CenteredRect(w, h);
        }

        /// <summary>Hover target while not expanded: a zone around the pill that includes the top
        /// pixel row. In live mode only the pill itself (plus a small margin) counts.</summary>
        (double l, double t, double r, double b) HotRect()
        {
            var mode = CurrentMode;
            if (mode == Mode.Collapsed) return CenteredRect(L.CollapsedWidth + 2 * HoverInflateX, HotHeight);
            return CenteredRect(L.LiveWidth + 2 * LiveHoverMargin, L.LiveHeight);
        }

        bool CursorInsideIsland()
        {
            return Native.GetCursorPos(out var p) && Contains(IslandRect(), p);
        }

        // MARK: - Hover tracking (adaptive GetCursorPos poll: ~30 Hz near the top, ~10 Hz otherwise)

        double Now => clock.Elapsed.TotalSeconds;

        void AdaptPolling(Native.POINT p)
        {
            var hot = HotRect();
            double m = NearDistance * scale;
            bool near = IsExpandedState || enteredAt != null
                || (p.X >= hot.l - m && p.X < hot.r + m && p.Y >= hot.t - m && p.Y < hot.b + m);
            var iv = TimeSpan.FromMilliseconds(near ? 33 : 100);
            if (poll.Interval != iv) poll.Interval = iv;
        }

        /// <summary>Stay open while the cursor is away only if the user is really working in the island.</summary>
        bool HoldOpen => forced || pickerOpen || manualOpen || (engaged && Phase == Phase.Idle && Link.Length > 0);

        static bool ButtonPressed()
        {
            // Query every tick so the "pressed since last call" bit never goes stale.
            bool l = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8001) != 0;
            bool r = (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8001) != 0;
            bool m = (Native.GetAsyncKeyState(Native.VK_MBUTTON) & 0x8001) != 0;
            return l || r || m;
        }

        void Evaluate()
        {
            if (hwnd == IntPtr.Zero || !Native.GetCursorPos(out var p)) return;
            double now = Now;
            bool pressed = ButtonPressed();
            try
            {
                if (!IsExpandedState)
                {
                    CheckFullscreen(now);
                    if (Contains(HotRect(), p) && !fullscreenHidden)
                    {
                        if (enteredAt is double t)
                        {
                            if (now - t >= HoverDelay) { enteredAt = null; Expand(false); }
                        }
                        else enteredAt = now;
                    }
                    else enteredAt = null;
                    return;
                }

                bool inside = Contains(IslandRect(), p);
                SetClickThrough(!inside);
                if (!inside && pressed && !forced && !pickerOpen)
                {
                    Collapse();   // click anywhere else collapses
                    return;
                }
                if (inside)
                {
                    exitedAt = null;
                    manualOpen = false;
                    if (insideSince is double s)
                    {
                        if (!keyTaken && now - s >= KeyDelay) TakeKey();
                    }
                    else insideSince = now;
                    return;
                }
                insideSince = null;
                if (HoldOpen) { exitedAt = null; return; }
                if (exitedAt is double x)
                {
                    if (now - x >= LeaveDelay) Collapse();
                }
                else exitedAt = now;
            }
            finally
            {
                AdaptPolling(p);
            }
        }

        void CheckFullscreen(double now)
        {
            if (now - lastFullscreenCheck < 0.5) return;
            lastFullscreenCheck = now;
            bool fs = !forced && FullscreenForeground();
            if (fs != fullscreenHidden)
            {
                fullscreenHidden = fs;
                Update();
            }
        }

        /// <summary>True when a fullscreen app / game / presentation owns the foreground.</summary>
        bool FullscreenForeground()
        {
            try
            {
                if (Native.SHQueryUserNotificationState(out int st) == 0 && (st == 2 || st == 3 || st == 4)) return true;
            }
            catch { }
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == hwnd || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow()) return false;
            var cls = Native.ClassName(fg);
            if (cls == "WorkerW" || cls == "Progman" || cls == "Shell_TrayWnd") return false;
            long style = Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64();
            if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return false;   // maximized normal window
            if (!Native.GetWindowRect(fg, out var r)) return false;
            var mon = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfo(mon, ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        // MARK: - Expand / collapse

        /// <summary>Hover-expand never takes keyboard focus; see TakeKey().</summary>
        public void Expand(bool manual)
        {
            if (IsExpandedState) return;
            manualOpen = manual;
            exitedAt = null;
            insideSince = null;
            keyTaken = false;
            engaged = false;
            RefreshClipboard();
            ButtonPressed();   // reset stale "pressed since" bits
            IsExpandedState = true;
            Update();
            SetClickThrough(!CursorInsideIsland());
            EnsureTopmost();
            if (manual) TakeKey();
        }

        /// <summary>Take keyboard focus: after a deliberate dwell, a click inside, or "Open SaveIt".</summary>
        void TakeKey()
        {
            if (!IsExpandedState) return;
            keyTaken = true;
            var fg = Native.GetForegroundWindow();
            if (fg != hwnd && fg != IntPtr.Zero) previousForeground = fg;
            SetNoActivate(false);
            ForceForeground();
            Activate();
            Dispatcher.BeginInvoke(new Action(FocusField), DispatcherPriority.Input);
        }

        void ForceForeground()
        {
            var fg = Native.GetForegroundWindow();
            if (fg == hwnd) return;
            uint me = Native.GetCurrentThreadId();
            uint other = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out _) : 0;
            bool attached = other != 0 && other != me && Native.AttachThreadInput(me, other, true);
            try
            {
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(me, other, false);
            }
        }

        void FocusField()
        {
            if (IsExpandedState && currentView is IdleView iv && IsActive) iv.FocusField();
        }

        public void Collapse()
        {
            if (!IsExpandedState) return;
            manualOpen = false;
            exitedAt = null;
            insideSince = null;
            engaged = false;
            keyTaken = false;
            SetClickThrough(true);
            bool seenResult = Phase == Phase.Done || Phase == Phase.Error;
            IsExpandedState = false;
            if (seenResult)
            {
                BadgeVisible = false;
                Phase = Phase.Idle;
            }
            Update();
            HandFocusBack();
        }

        void HandFocusBack()
        {
            Keyboard.ClearFocus();
            bool wasForeground = Native.GetForegroundWindow() == hwnd;
            SetNoActivate(true);
            if (wasForeground && previousForeground != IntPtr.Zero && Native.IsWindow(previousForeground))
                Native.SetForegroundWindow(previousForeground);
            previousForeground = IntPtr.Zero;
        }

        void OnDeactivated(object? sender, EventArgs e)
        {
            if (!IsExpandedState || pickerOpen || forced) return;
            // Only collapse if the cursor isn't on the island (e.g. user clicked another app).
            if (!CursorInsideIsland()) Collapse();
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsExpandedState) return;
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Collapse();
                return;
            }
            engaged = true;
        }

        void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsExpandedState) return;
            engaged = true;
            if (!keyTaken || !IsActive) TakeKey();
        }

        void OnPreviewRightUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<TextBox>(d) != null) return;
            e.Handled = true;
            ShowContextMenu?.Invoke();
        }

        static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            DependencyObject? cur = d;
            while (cur != null)
            {
                if (cur is T t) return t;
                cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur);
            }
            return null;
        }

        void RefreshClipboard()
        {
            ClipboardUrl = null;
            try
            {
                if (Clipboard.ContainsText()) ClipboardUrl = Links.HttpUrl(Clipboard.GetText());
            }
            catch { }
        }

        // MARK: - Actions (called by the views)

        public void Submit()
        {
            var typed = Link.Trim();
            string? url = typed.Length == 0 ? ClipboardUrl : Links.HttpUrl(typed);
            if (url == null)
            {
                System.Media.SystemSounds.Beep.Play();
                (currentView as IdleView)?.Shake();
                return;
            }
            StartDownload(url, Quality);
        }

        public void UseClipboard()
        {
            if (ClipboardUrl == null) return;
            engaged = true;
            Link = ClipboardUrl;
            Update();
            TakeKey();
        }

        public void LinkEdited(string text)
        {
            if (text == Link) return;
            Link = text;
            engaged = true;
            Update();
        }

        public void SetQuality(Quality q)
        {
            engaged = true;
            Quality = q;
            settings.Quality = q.Raw();
            settings.Save();
        }

        public void CancelDownload() => downloader.Cancel();

        public void RevealResult()
        {
            if (ResultPath == null) return;
            try
            {
                if (File.Exists(ResultPath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ResultPath}\"") { UseShellExecute = false });
                else
                    OpenPath(Path.GetDirectoryName(ResultPath) ?? Folder);
            }
            catch { }
        }

        public void OpenResult()
        {
            if (ResultPath != null) OpenPath(ResultPath);
        }

        public void Retry()
        {
            if (lastRequest is { } r) StartDownload(r.url, r.quality);
        }

        public void Reset()
        {
            Phase = Phase.Idle;
            BadgeVisible = false;
            RefreshClipboard();
            Update();
            Dispatcher.BeginInvoke(new Action(FocusField), DispatcherPriority.Input);
        }

        public void OpenUrl(string url) => OpenPath(url);

        public bool CopyText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); return true; }
                catch { Thread.Sleep(30); }
            }
            return false;
        }

        static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        public void RevealFolder()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = false });
            }
            catch { }
        }

        public void ChooseFolder()
        {
            if (pickerOpen) return;
            pickerOpen = true;
            engaged = true;
            if (!IsExpandedState) Expand(true);
            SetNoActivate(false);
            SetClickThrough(false);
            ForceForeground();
            try
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Choose where SaveIt saves downloads",
                    InitialDirectory = Folder,
                    Multiselect = false,
                };
                if (dlg.ShowDialog(this) == true && !string.IsNullOrEmpty(dlg.FolderName))
                {
                    Folder = dlg.FolderName;
                    settings.Folder = Folder;
                    settings.Save();
                }
            }
            catch { }
            pickerOpen = false;
            manualOpen = true;           // give the user a moment to move back / away
            Update();
            TakeKey();
            Evaluate();
        }

        // MARK: - Downloading

        void StartDownload(string url, Quality quality)
        {
            if (downloader.IsRunning) return;
            badgeTimer?.Stop();
            lastRequest = (url, quality);
            VideoTitle = "";
            Speed = ""; Eta = "";
            Stage = Stage.Starting;
            SetupStatus = "";
            Progress = 0;
            sProgress.Snap(0);
            ResultPath = null;
            Link = "";
            BadgeVisible = false;
            Phase = Phase.Downloading;

            downloader.OnTitle = t => { VideoTitle = t; if (Phase == Phase.Downloading) currentView?.Refresh(); };
            downloader.OnProgress = p =>
            {
                if (Phase != Phase.Downloading) return;
                Progress = p.Fraction;
                Speed = p.Speed;
                Eta = p.Eta;
                Stage = p.Stage;
                if (p.Stage == Stage.Setup) SetupStatus = p.Status;
                Update();
            };
            downloader.OnFinish = Finished;
            Update();
            downloader.Start(url, Folder, quality);
        }

        void Finished(Outcome o)
        {
            switch (o.Kind)
            {
                case OutcomeKind.Success:
                    ResultPath = o.Path;
                    Progress = 1;
                    Phase = Phase.Done;
                    BadgeVisible = true;
                    Update();
                    ScheduleBadgeHide(4);
                    break;
                case OutcomeKind.Failure:
                    ErrorMessage = o.Message ?? "Download failed";
                    Phase = Phase.Error;
                    BadgeVisible = true;
                    Update();
                    ScheduleBadgeHide(5);
                    break;
                default:
                    Phase = Phase.Idle;
                    BadgeVisible = false;
                    RefreshClipboard();
                    Update();
                    Dispatcher.BeginInvoke(new Action(FocusField), DispatcherPriority.Input);
                    break;
            }
        }

        void ScheduleBadgeHide(double seconds)
        {
            badgeTimer?.Stop();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (forced) return;
                BadgeVisible = false;
                Update();
            };
            badgeTimer = t;
            t.Start();
        }

        public bool IsBusy => downloader.IsRunning;

        public void Shutdown()
        {
            downloader.Cancel();
            poll.Stop();
            StopLoop();
        }

        // MARK: - Forced states for screenshots

        void ApplyForcedState(string s)
        {
            const string fakeTitle = "Big Buck Bunny — 4K Remaster (Official Trailer)";
            switch (s)
            {
                case "collapsed":
                    return;
                case "expanded":
                    ClipboardUrl = "https://www.youtube.com/watch?v=BaW_jenozKc";
                    IsExpandedState = true;
                    break;
                case "downloading":
                case "downloading-expanded":
                    lastRequest = ("https://example.com/v", Quality.Best);
                    VideoTitle = fakeTitle;
                    Progress = 0.64;
                    Speed = "8.4MiB/s";
                    Eta = "00:07";
                    Stage = Stage.Downloading;
                    Phase = Phase.Downloading;
                    IsExpandedState = s == "downloading-expanded";
                    break;
                case "setup":
                    lastRequest = ("https://example.com/v", Quality.Best);
                    Progress = 0.37;
                    Stage = Stage.Setup;
                    SetupStatus = "Downloading ffmpeg… 52 of 140 MB";
                    Phase = Phase.Downloading;
                    IsExpandedState = true;
                    break;
                case "done":
                case "done-expanded":
                    ResultPath = Path.Combine(Settings.DefaultFolder(), fakeTitle + ".mp4");
                    Progress = 1;
                    Phase = Phase.Done;
                    BadgeVisible = true;
                    IsExpandedState = s == "done-expanded";
                    break;
                case "error":
                    lastRequest = ("https://example.com/v", Quality.Best);
                    ErrorMessage = "The uploader has not made this video available in your country. Try a different link or a VPN-free network.";
                    Phase = Phase.Error;
                    BadgeVisible = true;
                    IsExpandedState = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown --saveit-state '{s}'");
                    return;
            }
            Update();
            if (IsExpandedState) SetClickThrough(false);
        }
    }
}
