import SwiftUI
import AppKit

enum Anim {
    /// Main morph spring for the blob.
    static let island = Animation.spring(response: 0.42, dampingFraction: 0.82)
    static let contentIn = Animation.smooth(duration: 0.32).delay(0.09)
    static let contentOut = Animation.easeOut(duration: 0.12)
    static let progress = Animation.smooth(duration: 0.35)
}

struct IslandActions {
    var submit: () -> Void = {}
    var useClipboard: () -> Void = {}
    var chooseFolder: () -> Void = {}
    var revealFolder: () -> Void = {}
    var cancel: () -> Void = {}
    var revealResult: () -> Void = {}
    var openResult: () -> Void = {}
    var retry: () -> Void = {}
    var reset: () -> Void = {}
}

private struct BlurFade: ViewModifier {
    var radius: CGFloat
    func body(content: Content) -> some View { content.blur(radius: radius) }
}

extension AnyTransition {
    /// Content enters after the blob has started growing and leaves before it shrinks.
    static var island: AnyTransition {
        .asymmetric(
            insertion: .opacity
                .combined(with: .scale(scale: 0.96, anchor: .top))
                .combined(with: .modifier(active: BlurFade(radius: 6), identity: BlurFade(radius: 0)))
                .animation(Anim.contentIn),
            removal: .opacity
                .combined(with: .scale(scale: 0.98, anchor: .top))
                .combined(with: .modifier(active: BlurFade(radius: 4), identity: BlurFade(radius: 0)))
                .animation(Anim.contentOut)
        )
    }
}

extension Font {
    static func rounded(_ size: CGFloat, _ weight: Font.Weight = .medium) -> Font {
        .system(size: size, weight: weight, design: .rounded)
    }
}

struct IslandRootView: View {
    @Bindable var model: IslandModel
    var actions: IslandActions

    var body: some View {
        let m = model.metrics
        let size = model.windowSize
        let expanded = model.mode.isExpanded

        ZStack(alignment: .top) {
            IslandShape(m)
                .fill(Color.black)
                .shadow(color: .black.opacity(expanded ? 0.45 : 0), radius: 18, x: 0, y: 8)

            IslandShape(m, closed: false)
                .stroke(Color.white.opacity(expanded ? 0.06 : 0), lineWidth: 1)

            content
                .frame(width: size.width, height: size.height, alignment: .top)
                .clipShape(IslandShape(m))
        }
        .frame(width: size.width, height: size.height, alignment: .top)
        .animation(Anim.island, value: m)
        .animation(Anim.island, value: expanded)
        .environment(\.colorScheme, .dark)
    }

    @ViewBuilder private var content: some View {
        ZStack(alignment: .top) {
            switch model.mode {
            case .collapsed:
                Color.clear.frame(width: 1, height: 1)
            case .live:
                LiveActivityView(model: model).transition(.island)
            case .liveDone:
                LiveBadgeView(model: model, success: true).transition(.island)
            case .liveError:
                LiveBadgeView(model: model, success: false).transition(.island)
            case .expanded(let phase):
                ExpandedContainer(model: model) {
                    switch phase {
                    case .idle: IdleView(model: model, actions: actions)
                    case .setup: SetupView(model: model)
                    case .downloading: DownloadingView(model: model, actions: actions)
                    case .done: DoneView(model: model, actions: actions)
                    case .error: ErrorView(model: model, actions: actions)
                    }
                }
                .id(phase)
                .transition(.island)
            }
            if model.mode.isExpanded {
                SocialBar(notchWidth: model.notchWidth)
                    .frame(width: Layout.expandedWidth, height: model.notchHeight)
                    .transition(.island)
            }
        }
    }
}

// MARK: - Creator links (the band beside the camera)

private enum Social {
    static let xHandle = "ClipCenter101"
    static let xURL = URL(string: "https://x.com/ClipCenter101")!
    static let discord = "samiamfx"
}

private struct SocialBar: View {
    var notchWidth: CGFloat
    @State private var copied = false

    var body: some View {
        HStack(spacing: 0) {
            SocialLink(help: "Open @\(Social.xHandle) on X") {
                NSWorkspace.shared.open(Social.xURL)
            } label: {
                Text("𝕏").font(.system(size: 12, weight: .bold))
                Text(Social.xHandle).font(.rounded(11.5, .semibold))
            }
            Spacer(minLength: notchWidth + 12)
            SocialLink(help: "Discord @\(Social.discord) — click to copy") {
                let pb = NSPasteboard.general
                pb.clearContents()
                pb.setString(Social.discord, forType: .string)
                withAnimation(.smooth(duration: 0.25)) { copied = true }
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) {
                    withAnimation(.smooth(duration: 0.25)) { copied = false }
                }
            } label: {
                Image(systemName: copied ? "checkmark.circle.fill" : "bubble.left.fill")
                    .font(.system(size: 10.5, weight: .semibold))
                    .foregroundStyle(copied ? Color.green : Color.white)
                    .contentTransition(.symbolEffect(.replace))
                Text(copied ? "Copied" : Social.discord)
                    .font(.rounded(11.5, .semibold))
                    .contentTransition(.opacity)
            }
        }
        .padding(.horizontal, Layout.sidePad)
    }
}

private struct SocialLink<L: View>: View {
    var help: String
    var action: () -> Void
    @ViewBuilder var label: L
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) { label }
                .foregroundStyle(.white)
                .padding(.horizontal, 8)
                .frame(height: 22)
                .background(Capsule().fill(Color.white.opacity(hover ? 0.12 : 0)))
                .opacity(hover ? 1 : 0.6)
                .lineLimit(1)
                .fixedSize()
        }
        .buttonStyle(PressStyle())
        .onHover { h in withAnimation(.smooth(duration: 0.18)) { hover = h } }
        .help(help)
    }
}

/// Positions expanded content below the notch, never under the camera.
private struct ExpandedContainer<C: View>: View {
    var model: IslandModel
    @ViewBuilder var inner: C
    var body: some View {
        inner
            .frame(width: Layout.innerWidth, alignment: .topLeading)
            .padding(.top, model.notchHeight + Layout.contentTop)
    }
}

// MARK: - Live activity

private struct ProgressRing: View {
    var progress: Double
    var body: some View {
        ZStack {
            Circle().stroke(Color.white.opacity(0.18), lineWidth: 2.6)
            Circle()
                .trim(from: 0, to: max(0.02, progress))
                .stroke(Color.white, style: StrokeStyle(lineWidth: 2.6, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(Anim.progress, value: progress)
        }
        .frame(width: 16, height: 16)
    }
}

private struct PercentText: View {
    var progress: Double
    var size: CGFloat = 13
    var body: some View {
        Text("\(Int((progress * 100).rounded(.down)))%")
            .font(.rounded(size, .semibold))
            .monospacedDigit()
            .contentTransition(.numericText(value: progress))
            .animation(Anim.progress, value: Int(progress * 100))
            .foregroundStyle(.white)
    }
}

private struct LiveActivityView: View {
    var model: IslandModel
    private var liveProgress: Double { model.phase == .setup ? model.setupProgress : model.progress }
    var body: some View {
        HStack(spacing: 0) {
            ProgressRing(progress: liveProgress)
                .frame(width: Layout.liveExtension)
            Color.clear.frame(width: model.notchWidth)
            PercentText(progress: liveProgress, size: 12)
                .frame(width: Layout.liveExtension)
                .offset(x: -1)
        }
        .frame(height: model.notchHeight)
    }
}

private struct LiveBadgeView: View {
    var model: IslandModel
    var success: Bool
    @State private var appeared = false
    var body: some View {
        HStack(spacing: 0) {
            Image(systemName: success ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .font(.system(size: 17, weight: .semibold))
                .foregroundStyle(success ? Color.green : Color.red)
                .symbolEffect(.bounce, value: appeared)
                .frame(width: Layout.liveExtension)
            Color.clear.frame(width: model.notchWidth)
            Text(success ? "Done" : "Failed")
                .font(.rounded(11, .semibold))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
                .foregroundStyle(success ? Color.green : Color.red)
                .frame(width: Layout.liveExtension)
        }
        .frame(height: model.notchHeight)
        .onAppear { DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { appeared = true } }
    }
}

// MARK: - Expanded: idle

private struct IdleView: View {
    @Bindable var model: IslandModel
    var actions: IslandActions
    @FocusState private var fieldFocused: Bool
    @State private var shown = false

    var body: some View {
        VStack(alignment: .leading, spacing: Layout.rowGap) {
            HStack(spacing: 10) {
                TextField("", text: $model.link, prompt: Text("Paste a video link…").foregroundStyle(Color.white.opacity(0.4)))
                    .textFieldStyle(.plain)
                    .font(.rounded(14))
                    .foregroundStyle(.white)
                    .focused($fieldFocused)
                    .onSubmit(actions.submit)
                    .padding(.horizontal, 14)
                    .frame(height: Layout.rowHeight)
                    .background(Capsule().fill(Color.white.opacity(0.10)))
                    .overlay(Capsule().strokeBorder(Color.white.opacity(fieldFocused ? 0.14 : 0.05), lineWidth: 1))

                Button(action: actions.submit) {
                    Image(systemName: "arrow.down")
                        .font(.system(size: 15, weight: .bold))
                        .foregroundStyle(.black)
                        .frame(width: Layout.rowHeight, height: Layout.rowHeight)
                        .background(Circle().fill(Color.white))
                }
                .buttonStyle(PressStyle())
                .help("Download")
            }
            .stagger(0, shown)

            if model.showsChip, let clip = model.clipboardURL {
                Button(action: actions.useClipboard) {
                    HStack(spacing: 6) {
                        Image(systemName: "link").font(.system(size: 10, weight: .bold))
                        Text("Use copied link · \(clip.shortLink)").lineLimit(1)
                    }
                    .font(.rounded(12))
                    .foregroundStyle(Color.white.opacity(0.85))
                    .padding(.horizontal, 10)
                    .frame(height: Layout.chipHeight)
                    .background(Capsule().fill(Color.white.opacity(0.09)))
                }
                .buttonStyle(PressStyle())
                .stagger(1, shown)
                .transition(.opacity.combined(with: .scale(scale: 0.95, anchor: .topLeading)))
            }

            HStack(spacing: 8) {
                QualityPicker(selection: $model.quality)
                Spacer(minLength: 6)
                Button(action: actions.chooseFolder) {
                    HStack(spacing: 6) {
                        Image(systemName: "folder.fill").font(.system(size: 11))
                        Text(model.folderName).lineLimit(1).truncationMode(.middle)
                    }
                    .font(.rounded(12))
                    .foregroundStyle(Color.white.opacity(0.85))
                    .padding(.horizontal, 10)
                    .frame(maxWidth: 150)
                    .frame(height: Layout.pillRowHeight)
                    .background(Capsule().fill(Color.white.opacity(0.08)))
                }
                .buttonStyle(PressStyle())
                .help("Choose download folder")

                Button(action: actions.revealFolder) {
                    Image(systemName: "arrow.up.forward")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(Color.white.opacity(0.85))
                        .frame(width: Layout.pillRowHeight, height: Layout.pillRowHeight)
                        .background(Circle().fill(Color.white.opacity(0.08)))
                }
                .buttonStyle(PressStyle())
                .help("Show folder in Finder")
            }
            .stagger(model.showsChip ? 2 : 1, shown)
        }
        .animation(Anim.island, value: model.showsChip)
        .onAppear {
            shown = true
            requestFocus()
        }
        .onChange(of: model.focusToken) { requestFocus() }
    }

    private func requestFocus() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { fieldFocused = true }
    }
}

private struct QualityPicker: View {
    @Binding var selection: Quality
    @Namespace private var ns
    var body: some View {
        HStack(spacing: 2) {
            ForEach(Quality.allCases) { q in
                Button { withAnimation(.spring(response: 0.3, dampingFraction: 0.8)) { selection = q } } label: {
                    Text(q.label)
                        .font(.rounded(12, .semibold))
                        .foregroundStyle(selection == q ? Color.white : Color.white.opacity(0.55))
                        .frame(width: 50, height: Layout.pillRowHeight - 4)
                        .background {
                            if selection == q {
                                Capsule().fill(Color.white.opacity(0.18))
                                    .matchedGeometryEffect(id: "sel", in: ns)
                            }
                        }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(Capsule().fill(Color.white.opacity(0.07)))
    }
}

// MARK: - Expanded: downloading

private struct DownloadingView: View {
    var model: IslandModel
    var actions: IslandActions

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                Image(systemName: model.lastRequest?.quality.symbol ?? "film")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(Color.white.opacity(0.6))
                Text(model.title.isEmpty ? "Fetching video info…" : model.title)
                    .font(.rounded(14, .semibold))
                    .foregroundStyle(.white)
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 8)
                PercentText(progress: model.progress, size: 14)
                    .frame(width: 46, alignment: .trailing)
            }
            .frame(height: 20)

            Capsule().fill(Color.white.opacity(0.14))
                .frame(width: Layout.innerWidth, height: 6)
                .overlay(alignment: .leading) {
                    Capsule().fill(Color.white)
                        .frame(width: max(6, Layout.innerWidth * model.progress), height: 6)
                        .animation(Anim.progress, value: model.progress)
                }

            HStack(spacing: 8) {
                Text(statusLine)
                    .font(.rounded(12))
                    .monospacedDigit()
                    .foregroundStyle(Color.white.opacity(0.55))
                    .lineLimit(1)
                Spacer()
                Button(action: actions.cancel) {
                    Text("Cancel")
                        .font(.rounded(12, .semibold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 14)
                        .frame(height: Layout.pillRowHeight)
                        .background(Capsule().fill(Color.white.opacity(0.12)))
                }
                .buttonStyle(PressStyle())
            }
        }
    }

    private var statusLine: String {
        switch model.stage {
        case .starting: return "Starting…"
        case .processing: return model.lastRequest?.quality == .mp3 ? "Converting to MP3…" : "Finishing up…"
        case .downloading:
            var parts: [String] = []
            if !model.speed.isEmpty { parts.append(model.speed) }
            if !model.eta.isEmpty { parts.append("\(model.eta) left") }
            return parts.isEmpty ? "Downloading…" : parts.joined(separator: "  ·  ")
        }
    }
}

// MARK: - Expanded: first-run setup

private struct SetupView: View {
    var model: IslandModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                Image(systemName: "shippingbox.fill")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(Color.white.opacity(0.6))
                    .symbolEffect(.pulse, options: .repeating)
                Text("Setting up… (first run)")
                    .font(.rounded(14, .semibold))
                    .foregroundStyle(.white)
                    .lineLimit(1)
                Spacer(minLength: 8)
                PercentText(progress: model.setupProgress, size: 14)
                    .frame(width: 46, alignment: .trailing)
            }
            .frame(height: 20)

            Capsule().fill(Color.white.opacity(0.14))
                .frame(width: Layout.innerWidth, height: 6)
                .overlay(alignment: .leading) {
                    Capsule().fill(Color.white)
                        .frame(width: max(6, Layout.innerWidth * model.setupProgress), height: 6)
                        .animation(Anim.progress, value: model.setupProgress)
                }

            HStack(spacing: 8) {
                Text(model.setupLabel)
                    .font(.rounded(12))
                    .monospacedDigit()
                    .foregroundStyle(Color.white.opacity(0.55))
                    .lineLimit(1)
                    .contentTransition(.opacity)
                    .animation(.smooth(duration: 0.25), value: model.setupLabel)
                Spacer()
                Text("One-time download of yt-dlp + ffmpeg")
                    .font(.rounded(11))
                    .foregroundStyle(Color.white.opacity(0.35))
                    .lineLimit(1)
            }
            .frame(height: Layout.pillRowHeight)
        }
    }
}

// MARK: - Expanded: done / error

private struct DoneView: View {
    var model: IslandModel
    var actions: IslandActions

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Button(action: actions.revealResult) {
                HStack(spacing: 12) {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 30, weight: .semibold))
                        .foregroundStyle(.white, Color.green)
                        .symbolEffect(.bounce.up, value: model.doneBounce)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Downloaded")
                            .font(.rounded(11, .semibold))
                            .foregroundStyle(Color.green)
                        Text(model.resultURL?.lastPathComponent ?? "")
                            .font(.rounded(14, .semibold))
                            .foregroundStyle(.white)
                            .lineLimit(1)
                            .truncationMode(.middle)
                    }
                    Spacer(minLength: 0)
                }
                .frame(height: 40)
                .contentShape(Rectangle())
            }
            .buttonStyle(PressStyle(scale: 0.985))
            .help("Show in Finder")

            HStack(spacing: 8) {
                PillButton(title: "New link", icon: "plus", action: actions.reset)
                Spacer()
                PillButton(title: "Show in Finder", icon: "magnifyingglass", action: actions.revealResult)
                PillButton(title: "Open", icon: "play.fill", prominent: true, action: actions.openResult)
            }
        }
        .onAppear { DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { model.doneBounce += 1 } }
    }
}

private struct ErrorView: View {
    var model: IslandModel
    var actions: IslandActions

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 12) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(Color.orange)
                    .frame(width: 30)
                VStack(alignment: .leading, spacing: 2) {
                    Text(model.errorTitle)
                        .font(.rounded(11, .semibold))
                        .foregroundStyle(Color.orange)
                    Text(model.errorMessage)
                        .font(.rounded(12.5, .medium))
                        .foregroundStyle(.white)
                        .lineLimit(2)
                        .truncationMode(.tail)
                        .help(model.errorMessage)
                }
                Spacer(minLength: 0)
            }
            .frame(height: 48, alignment: .center)
            .clipped()

            HStack(spacing: 8) {
                Spacer()
                PillButton(title: "Dismiss", icon: "xmark", action: actions.reset)
                PillButton(title: "Retry", icon: "arrow.clockwise", prominent: true, action: actions.retry)
            }
        }
    }
}

// MARK: - Controls

private struct PillButton: View {
    var title: String
    var icon: String
    var prominent = false
    var action: () -> Void
    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                Image(systemName: icon).font(.system(size: 10, weight: .bold))
                Text(title).font(.rounded(12, .semibold))
            }
            .foregroundStyle(prominent ? Color.black : Color.white)
            .padding(.horizontal, 12)
            .frame(height: Layout.pillRowHeight)
            .background(Capsule().fill(prominent ? Color.white : Color.white.opacity(0.12)))
        }
        .buttonStyle(PressStyle())
    }
}

/// Rows of the input view arrive one after another (~35ms apart) behind the container transition.
private struct Stagger: ViewModifier {
    var index: Int
    var shown: Bool
    func body(content: Content) -> some View {
        content
            .opacity(shown ? 1 : 0)
            .offset(y: shown ? 0 : -5)
            .scaleEffect(shown ? 1 : 0.98, anchor: .top)
            .animation(.smooth(duration: 0.34).delay(0.1 + 0.035 * Double(index)), value: shown)
    }
}

extension View {
    fileprivate func stagger(_ index: Int, _ shown: Bool) -> some View {
        modifier(Stagger(index: index, shown: shown))
    }
}

struct PressStyle: ButtonStyle {
    var scale: CGFloat = 0.94
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? scale : 1)
            .opacity(configuration.isPressed ? 0.85 : 1)
            .animation(.spring(response: 0.25, dampingFraction: 0.7), value: configuration.isPressed)
    }
}
