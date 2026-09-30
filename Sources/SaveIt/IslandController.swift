import AppKit
import SwiftUI
import ServiceManagement

/// Owns the panel, status item, hover tracking and the downloader.
final class IslandController: NSObject, NSWindowDelegate, NSMenuDelegate {
    let model = IslandModel()
    private var panel: IslandPanel!
    private var hosting: NSHostingView<IslandRootView>!
    private var statusItem: NSStatusItem!
    private let downloader = Downloader()
    private let bootstrapper = ToolBootstrapper()

    private var screen: NSScreen?
    private var notchCenterX: CGFloat = 0
    private var screenTop: CGFloat = 0

    private var monitors: [Any] = []
    private var enteredAt: Date?
    private var exitedAt: Date?
    private var recheck: DispatchWorkItem?
    private var badgeTimer: DispatchWorkItem?
    private var pickerOpen = false
    private var manualOpen = false      // opened from the menu: stay until the cursor visits the island
    private var forced = false          // --saveit-state: don't auto-collapse
    private var engaged = false         // user typed, pasted or clicked in the island this session
    private var insideSince: Date?      // cursor continuously inside the expanded island since
    private var keyTaken = false        // island took keyboard focus this session
    private var ytdlpVersion: String?
    private var updating = false
    private var debugKeyLog = false     // log key-focus hand-back (forced-state test runs only)

    static let hoverDelay: TimeInterval = 0.12
    static let leaveDelay: TimeInterval = 0.6
    static let hoverInflateX: CGFloat = 12
    static let liveHoverMargin: CGFloat = 6
    static let keyDelay: TimeInterval = 0.35    // dwell inside the island before it takes keyboard focus
    static let topSlop: CGFloat = 2             // NSRect.contains excludes maxY; cover the top pixel row

    // MARK: - Setup

    func start(forcedState: String?) {
        panel = IslandPanel(size: model.windowSize)
        panel.delegate = self
        hosting = NSHostingView(rootView: IslandRootView(model: model, actions: makeActions()))
        hosting.sizingOptions = []
        hosting.frame = NSRect(origin: .zero, size: model.windowSize)
        hosting.autoresizingMask = [.width, .height]
        panel.contentView = hosting

        setupStatusItem()
        layout()
        panel.orderFrontRegardless()

        NotificationCenter.default.addObserver(self, selector: #selector(screensChanged),
                                               name: NSApplication.didChangeScreenParametersNotification, object: nil)
        installMonitors()
        refreshVersion()

        if let s = forcedState { debugKeyLog = true; applyForcedState(s) }
    }

    @objc private func screensChanged() {
        layout()
    }

    /// Finds the notched screen (or falls back to the main screen) and pins the fixed-size window
    /// top-center over the notch.
    private func layout() {
        let notched = NSScreen.screens.first { $0.safeAreaInsets.top > 0 }
        guard let s = notched ?? NSScreen.main ?? NSScreen.screens.first else { return }
        screen = s
        let f = s.frame
        if notched != nil, let l = s.auxiliaryTopLeftArea, let r = s.auxiliaryTopRightArea {
            model.hasNotch = true
            model.notchWidth = max(80, r.minX - l.maxX)
            model.notchHeight = s.safeAreaInsets.top
            notchCenterX = (l.maxX + r.minX) / 2
        } else {
            model.hasNotch = false
            model.notchWidth = 185
            model.notchHeight = 32
            notchCenterX = f.midX
        }
        screenTop = f.maxY
        let size = model.windowSize
        hosting.frame = NSRect(origin: .zero, size: size)
        panel.setFrame(NSRect(x: (notchCenterX - size.width / 2).rounded(), y: screenTop - size.height,
                              width: size.width, height: size.height), display: true)
    }

    // MARK: - Geometry helpers (screen coordinates)

    /// Visible island rect, extended above the screen top so y == frame.maxY still counts as inside.
    private func islandRect(_ m: IslandMetrics) -> NSRect {
        NSRect(x: notchCenterX - m.width / 2, y: screenTop - m.height,
               width: m.width, height: m.height + Self.topSlop)
    }

    /// Hover target while not expanded: the notch (plus margin). In live-activity mode only the
    /// notch itself counts, so hovering neighbouring menu bar icons doesn't open the island.
    private var hotRect: NSRect {
        let live: Bool
        switch model.mode {
        case .live, .liveDone, .liveError: live = true
        default: live = false
        }
        let margin = live ? Self.liveHoverMargin : Self.hoverInflateX
        return NSRect(x: notchCenterX - model.notchWidth / 2 - margin, y: screenTop - model.notchHeight,
                      width: model.notchWidth + 2 * margin, height: model.notchHeight + Self.topSlop)
    }

    // MARK: - Hover tracking (global + local mouse monitors, no Accessibility needed)

    private func installMonitors() {
        // Cursor tracking uses an adaptive poll of NSEvent.mouseLocation rather than a global
        // mouseMoved monitor: a global monitor makes the WindowServer deliver *every* mouse event
        // (120–1000 Hz) to us, which costs 1–3% CPU while the user merely moves the mouse.
        startPolling(interval: Self.farInterval)
        // Events on our own window (expanded) still give instant pass-through updates.
        let moveMask: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseDragged, .rightMouseDragged]
        if let l = NSEvent.addLocalMonitorForEvents(matching: moveMask, handler: { [weak self] e in self?.evaluate(); return e }) {
            monitors.append(l)
        }
        // Click anywhere else collapses the island.
        if let g = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown], handler: { [weak self] _ in
            guard let self, self.model.isExpanded, !self.pickerOpen, !self.forced else { return }
            if !self.islandRect(self.model.metrics).contains(NSEvent.mouseLocation) { self.collapse() }
        }) { monitors.append(g) }
        // Esc collapses; typing/clicking engages; standard edit shortcuts (no Edit menu in an
        // agent app); right-click on the island shows the menu.
        if let l = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .leftMouseDown, .rightMouseDown], handler: { [weak self] e in
            guard let self else { return e }
            if e.type == .keyDown, e.window === self.panel, self.model.isExpanded {
                if e.keyCode == 53 { self.collapse(); return nil }
                self.engaged = true
                if e.modifierFlags.intersection(.deviceIndependentFlagsMask).subtracting([.capsLock, .numericPad, .function]) == .command,
                   let ch = e.charactersIgnoringModifiers?.lowercased() {
                    let sel: Selector?
                    switch ch {
                    case "v": sel = #selector(NSText.paste(_:))
                    case "c": sel = #selector(NSText.copy(_:))
                    case "x": sel = #selector(NSText.cut(_:))
                    case "a": sel = #selector(NSText.selectAll(_:))
                    case "z": sel = Selector(("undo:"))
                    default: sel = nil
                    }
                    if let sel, NSApp.sendAction(sel, to: nil, from: self) { return nil }
                }
                return e
            }
            if e.type == .leftMouseDown, e.window === self.panel, self.model.isExpanded {
                self.engaged = true
                self.takeKey()
                return e
            }
            if e.type == .rightMouseDown, e.window === self.panel {
                let menu = self.buildMenu()
                NSMenu.popUpContextMenu(menu, with: e, for: self.hosting)
                return nil
            }
            return e
        }) { monitors.append(l) }
    }

    static let farInterval: TimeInterval = 1.0 / 10    // cursor far from the island
    static let nearInterval: TimeInterval = 1.0 / 30   // near the notch or expanded
    static let nearDistance: CGFloat = 220
    private var pollTimer: Timer?

    private func startPolling(interval: TimeInterval) {
        if let t = pollTimer, abs(t.timeInterval - interval) < 0.001 { return }
        pollTimer?.invalidate()
        let t = Timer(timeInterval: interval, repeats: true) { [weak self] _ in self?.evaluate() }
        t.tolerance = interval * 0.2
        RunLoop.main.add(t, forMode: .common)
        pollTimer = t
    }

    private func adaptPolling(_ p: NSPoint) {
        let near = model.isExpanded || enteredAt != nil
            || hotRect.insetBy(dx: -Self.nearDistance, dy: -Self.nearDistance).contains(p)
        startPolling(interval: near ? Self.nearInterval : Self.farInterval)
    }

    private func scheduleRecheck(after t: TimeInterval) {
        recheck?.cancel()
        let w = DispatchWorkItem { [weak self] in self?.evaluate() }
        recheck = w
        DispatchQueue.main.asyncAfter(deadline: .now() + t, execute: w)
    }

    /// Stay open while the cursor is away only if the user is really working in the island —
    /// never just because an accidental brush left text in the field.
    private var holdOpen: Bool {
        forced || pickerOpen || manualOpen || (engaged && model.phase == .idle && !model.link.isEmpty)
    }

    private func evaluate() {
        let p = NSEvent.mouseLocation
        let now = Date()
        defer { adaptPolling(p) }
        if !model.isExpanded {
            if hotRect.contains(p) {
                if let t = enteredAt {
                    if now.timeIntervalSince(t) >= Self.hoverDelay { enteredAt = nil; expand() }
                } else {
                    enteredAt = now
                    scheduleRecheck(after: Self.hoverDelay + 0.01)
                }
            } else {
                enteredAt = nil
            }
            return
        }
        let inside = islandRect(model.metrics).contains(p)
        if panel.ignoresMouseEvents == inside { panel.ignoresMouseEvents = !inside }
        if inside {
            exitedAt = nil
            manualOpen = false
            if let t = insideSince {
                if !keyTaken, now.timeIntervalSince(t) >= Self.keyDelay { takeKey() }
            } else {
                insideSince = now
                scheduleRecheck(after: Self.keyDelay + 0.01)
            }
            return
        }
        insideSince = nil
        if holdOpen { exitedAt = nil; return }
        if let t = exitedAt {
            if now.timeIntervalSince(t) >= Self.leaveDelay { collapse() }
        } else {
            exitedAt = now
            scheduleRecheck(after: Self.leaveDelay + 0.01)
        }
    }

    // MARK: - Expand / collapse

    /// Hover-expand never takes keyboard focus; see `takeKey()`.
    func expand(manual: Bool = false) {
        guard !model.isExpanded else { return }
        logKey("expand(manual: \(manual)) at \(NSEvent.mouseLocation), hot \(hotRect)")
        manualOpen = manual
        exitedAt = nil
        insideSince = nil
        keyTaken = false
        engaged = false
        refreshClipboard()
        withAnimation(Anim.island) { model.isExpanded = true }
        panel.ignoresMouseEvents = !islandRect(model.metrics).contains(NSEvent.mouseLocation)
        if manual { takeKey() }
    }

    /// Take keyboard focus: after a deliberate dwell, a click inside, or "Open SaveIt".
    private func takeKey() {
        guard model.isExpanded else { return }
        keyTaken = true
        panel.allowsKey = true
        if !panel.isKeyWindow { panel.makeKey() }
        model.focusToken += 1
        logKey("takeKey")
    }

    func collapse() {
        guard model.isExpanded else { return }
        recheck?.cancel()
        manualOpen = false
        exitedAt = nil
        insideSince = nil
        engaged = false
        keyTaken = false
        panel.ignoresMouseEvents = true
        let seenResult = model.phase == .done || model.phase == .error
        withAnimation(Anim.island.delay(0.04)) {
            model.isExpanded = false
            if seenResult {
                model.badgeVisible = false
                model.phase = .idle
            }
        }
        // Hand keyboard focus back once the blob has settled.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.55) { [weak self] in
            guard let self, !self.model.isExpanded else { return }
            if NSApp.isActive { NSApp.hide(nil) }          // panel.canHide == false, so it stays up
            self.panel.allowsKey = false
            guard self.panel.isKeyWindow else { return }
            if self.model.mode == .collapsed {
                // Invisible over the hardware notch, so a reorder can't be seen.
                self.panel.orderOut(nil)
                self.panel.orderFrontRegardless()
                self.logKey("collapsed: reordered")
            } else {
                // Live pill is visible: don't reorder (it would blink). Briefly activate ourselves
                // and hide: AppKit then re-activates the previous app, which takes key focus back.
                // The panel has canHide == false, so the pill stays on screen throughout.
                self.panel.makeFirstResponder(nil)
                NSApp.activate(ignoringOtherApps: true)
                DispatchQueue.main.async {
                    NSApp.hide(nil)
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { self.logKey("live: activate+hide") }
                }
            }
        }
    }

    private func logKey(_ what: String) {
        guard debugKeyLog else { return }
        FileHandle.standardError.write(Data("saveit-debug: \(what); panel key = \(panel.isKeyWindow)\n".utf8))
    }

    func windowDidResignKey(_ notification: Notification) {
        guard model.isExpanded, !pickerOpen, !forced else { return }
        // Only collapse if the cursor isn't on the island (e.g. user clicked another app).
        if !islandRect(model.metrics).contains(NSEvent.mouseLocation) { collapse() }
    }

    private func refreshClipboard() {
        model.clipboardURL = NSPasteboard.general.string(forType: .string)?.httpURL
    }

    // MARK: - Actions

    private func makeActions() -> IslandActions {
        IslandActions(
            submit: { [weak self] in self?.submit() },
            useClipboard: { [weak self] in
                guard let self, let c = self.model.clipboardURL else { return }
                self.engaged = true
                self.model.link = c
                self.takeKey()
            },
            chooseFolder: { [weak self] in self?.chooseFolder() },
            revealFolder: { [weak self] in self?.revealFolder() },
            cancel: { [weak self] in self?.downloader.cancel() },
            revealResult: { [weak self] in
                guard let u = self?.model.resultURL else { return }
                NSWorkspace.shared.activateFileViewerSelecting([u])
            },
            openResult: { [weak self] in
                guard let u = self?.model.resultURL else { return }
                NSWorkspace.shared.open(u)
            },
            retry: { [weak self] in
                guard let self, let r = self.model.lastRequest else { return }
                self.startDownload(url: r.url, quality: r.quality)
            },
            reset: { [weak self] in
                guard let self else { return }
                withAnimation(Anim.island) {
                    self.model.phase = .idle
                    self.model.badgeVisible = false
                }
                self.refreshClipboard()
                self.model.focusToken += 1
            }
        )
    }

    private func submit() {
        let typed = model.link.trimmingCharacters(in: .whitespacesAndNewlines)
        let url: String?
        if typed.isEmpty { url = model.clipboardURL } else { url = typed.httpURL }
        guard let url else { NSSound.beep(); return }
        startDownload(url: url, quality: model.quality)
    }

    private func startDownload(url: String, quality: Quality) {
        guard !downloader.isRunning, !bootstrapper.isRunning else { return }
        badgeTimer?.cancel()
        model.lastRequest = (url, quality)
        if !ToolManager.missing.isEmpty {
            runSetup(thenDownload: url, quality: quality)
            return
        }
        model.title = ""
        model.speed = ""; model.eta = ""
        model.stage = .starting
        model.progress = 0
        model.resultURL = nil
        withAnimation(Anim.island) {
            model.link = ""
            model.badgeVisible = false
            model.phase = .downloading
        }

        downloader.onTitle = { [weak self] t in self?.model.title = t }
        downloader.onProgress = { [weak self] p in
            guard let self, self.model.phase == .downloading else { return }
            self.model.progress = p.fraction
            self.model.speed = p.speed
            self.model.eta = p.eta
            self.model.stage = p.stage
        }
        downloader.onFinish = { [weak self] outcome in self?.finished(outcome) }
        downloader.start(url: url, folder: model.folder, quality: quality)
    }

    /// First run: download yt-dlp / ffmpeg into Application Support, then continue the download.
    private func runSetup(thenDownload url: String, quality: Quality) {
        model.setupProgress = 0
        model.setupLabel = "Preparing…"
        withAnimation(Anim.island) {
            model.link = ""
            model.badgeVisible = false
            model.phase = .setup
        }
        bootstrapper.onProgress = { [weak self] label, f in
            self?.model.setupLabel = label
            self?.model.setupProgress = f
        }
        bootstrapper.run { [weak self] result in
            guard let self else { return }
            switch result {
            case .success:
                self.refreshVersion()
                self.startDownload(url: url, quality: quality)
            case .failure(let e):
                self.model.errorTitle = "Setup failed"
                self.model.errorMessage = e.localizedDescription
                withAnimation(Anim.island) {
                    self.model.phase = .error
                    self.model.badgeVisible = true
                }
                self.scheduleBadgeHide(after: 5)
            }
        }
    }

    private func finished(_ outcome: DownloadOutcome) {
        switch outcome {
        case .success(let u):
            model.resultURL = u
            model.progress = 1
            withAnimation(Anim.island) {
                model.phase = .done
                model.badgeVisible = true
            }
            scheduleBadgeHide(after: 4)
        case .failure(let msg):
            model.errorTitle = "Download failed"
            model.errorMessage = msg
            withAnimation(Anim.island) {
                model.phase = .error
                model.badgeVisible = true
            }
            scheduleBadgeHide(after: 5)
        case .cancelled:
            withAnimation(Anim.island) {
                model.phase = .idle
                model.badgeVisible = false
            }
        }
    }

    private func scheduleBadgeHide(after t: TimeInterval) {
        badgeTimer?.cancel()
        let w = DispatchWorkItem { [weak self] in
            guard let self, !self.forced else { return }
            withAnimation(Anim.island) { self.model.badgeVisible = false }
        }
        badgeTimer = w
        DispatchQueue.main.asyncAfter(deadline: .now() + t, execute: w)
    }

    @objc func chooseFolder() {
        pickerOpen = true
        engaged = true
        if !model.isExpanded { expand(manual: true) }
        NSApp.activate(ignoringOtherApps: true)
        let op = NSOpenPanel()
        op.canChooseDirectories = true
        op.canChooseFiles = false
        op.canCreateDirectories = true
        op.allowsMultipleSelection = false
        op.prompt = "Use Folder"
        op.message = "Choose where SaveIt saves downloads"
        op.directoryURL = model.folder
        op.level = NSWindow.Level(rawValue: panel.level.rawValue + 1)
        op.begin { [weak self] resp in
            guard let self else { return }
            if resp == .OK, let u = op.url { self.model.folder = u }
            self.pickerOpen = false
            self.manualOpen = true           // give the user a moment to move back / away
            self.takeKey()
            self.evaluate()
        }
    }

    @objc func revealFolder() {
        NSWorkspace.shared.activateFileViewerSelecting([model.folder])
    }

    // MARK: - Menu

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let b = statusItem.button {
            b.image = NSImage(systemSymbolName: "arrow.down.circle", accessibilityDescription: "SaveIt")
            b.image?.isTemplate = true
        }
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.delegate = self
        statusItem.menu = menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        for item in buildMenu().items { menu.addItem(item.copy() as! NSMenuItem) }
    }

    private func buildMenu() -> NSMenu {
        let m = NSMenu()
        m.autoenablesItems = false
        func add(_ title: String, _ sel: Selector, _ key: String = "") -> NSMenuItem {
            let i = NSMenuItem(title: title, action: sel, keyEquivalent: key)
            i.target = self
            m.addItem(i)
            return i
        }
        _ = add("Open SaveIt", #selector(openFromMenu))
        m.addItem(.separator())
        _ = add("Choose Download Folder…", #selector(chooseFolder))
        _ = add("Open Download Folder", #selector(revealFolder))
        m.addItem(.separator())
        let status = SMAppService.mainApp.status
        let login = add(status == .requiresApproval ? "Launch at Login (needs approval)" : "Launch at Login",
                        #selector(toggleLogin))
        login.state = status == .enabled ? .on : .off
        m.addItem(.separator())
        let ver = NSMenuItem(title: "yt-dlp \(ytdlpVersion ?? "not found")", action: nil, keyEquivalent: "")
        ver.isEnabled = false
        m.addItem(ver)
        let upd = add(updating ? "Updating yt-dlp…" : "Update yt-dlp", #selector(updateYTDLP))
        upd.isEnabled = !updating
        m.addItem(.separator())
        _ = add("Quit SaveIt", #selector(quit), "q")
        return m
    }

    @objc private func openFromMenu() {
        // Let the menu finish closing before we take key.
        DispatchQueue.main.async { self.expand(manual: true) }
    }

    @objc private func toggleLogin() {
        let svc = SMAppService.mainApp
        if svc.status == .requiresApproval {
            SMAppService.openSystemSettingsLoginItems()
            return
        }
        do {
            if svc.status == .enabled { try svc.unregister() } else { try svc.register() }
        } catch {
            NSLog("SaveIt: launch at login toggle failed: \(error.localizedDescription)")
            NSSound.beep()
        }
    }

    private func refreshVersion() {
        YTDLPUpdater.version { [weak self] v in self?.ytdlpVersion = v }
    }

    @objc private func updateYTDLP() {
        guard !updating else { return }
        updating = true
        YTDLPUpdater.update { [weak self] ok, message in
            guard let self else { return }
            self.updating = false
            self.refreshVersion()
            NSApp.activate(ignoringOtherApps: true)
            let a = NSAlert()
            a.messageText = ok ? "yt-dlp" : "Couldn't update yt-dlp"
            a.informativeText = message
            a.alertStyle = ok ? .informational : .warning
            a.runModal()
        }
    }

    @objc private func quit() {
        downloader.cancel()
        NSApp.terminate(nil)
    }

    // MARK: - Forced states for screenshots

    private func applyForcedState(_ s: String) {
        let fakeTitle = "Big Buck Bunny — 4K Remaster (Official Trailer)"
        switch s {
        case "collapsed":
            return
        case "expanded":
            forced = true
            model.clipboardURL = "https://www.youtube.com/watch?v=BaW_jenozKc"
            model.isExpanded = true
            panel.ignoresMouseEvents = false
        case "live-focus-test":
            // Self-test (no cursor movement): take focus via "Open SaveIt", collapse into the live
            // pill, then report whether keyboard focus was handed back. Quits when done.
            model.lastRequest = ("https://example.com/v", .best)
            model.title = fakeTitle
            model.progress = 0.4
            model.stage = .downloading
            model.phase = .downloading
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                self.logKey("front app before: \(NSWorkspace.shared.frontmostApplication?.localizedName ?? "?")")
                self.expand(manual: true)
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { self.collapse() }
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.2) {
                self.logKey("final (mode \(self.model.mode)); front app: \(NSWorkspace.shared.frontmostApplication?.localizedName ?? "?")")
                NSApp.terminate(nil)
            }
        case "setup", "setup-expanded", "setup-error":
            forced = true
            model.setupProgress = 0.41
            model.setupLabel = "Downloading yt-dlp · 14.5 of 35.4 MB"
            model.phase = .setup
            model.isExpanded = s != "setup"
            if s == "setup-error" {
                model.lastRequest = ("https://example.com/v", .best)
                model.errorTitle = "Setup failed"
                model.errorMessage = "Couldn't download the tools: The Internet connection appears to be offline."
                model.phase = .error
            }
        case "downloading", "downloading-expanded":
            forced = s == "downloading-expanded"
            model.lastRequest = ("https://example.com/v", .best)
            model.title = fakeTitle
            model.progress = 0.64
            model.speed = "8.4MiB/s"
            model.eta = "00:07"
            model.stage = .downloading
            model.phase = .downloading
            model.isExpanded = s == "downloading-expanded"
        case "done", "done-expanded":
            forced = true
            model.resultURL = URL(fileURLWithPath: NSHomeDirectory() + "/Downloads/\(fakeTitle).mp4")
            model.progress = 1
            model.phase = .done
            model.badgeVisible = true
            model.isExpanded = s == "done-expanded"
        case "error":
            forced = true
            model.lastRequest = ("https://example.com/v", .best)
            model.errorMessage = "The uploader has not made this video available in your country. Try a different link or a VPN-free network."
            model.phase = .error
            model.badgeVisible = true
            model.isExpanded = true
        default:
            FileHandle.standardError.write(Data("Unknown --saveit-state '\(s)'\n".utf8))
        }
    }
}
