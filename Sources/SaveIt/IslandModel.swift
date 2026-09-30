import AppKit
import SwiftUI
import Observation

enum Phase: Equatable { case idle, downloading, done, error }

/// What the island is currently showing. Drives the shape metrics and which content view is on screen.
enum IslandMode: Equatable {
    case collapsed
    case live          // live-activity pill while downloading
    case liveDone      // green check pill after finishing
    case liveError     // red pill after failing
    case expanded(Phase)

    var isExpanded: Bool { if case .expanded = self { return true }; return false }
}

/// Animatable metrics of the black blob.
struct IslandMetrics: Equatable {
    var width: CGFloat
    var height: CGFloat
    var bottomRadius: CGFloat
    var earRadius: CGFloat
}

enum Layout {
    static let expandedWidth: CGFloat = 460
    static let liveExtension: CGFloat = 42        // extra width each side of the notch for the live activity
    static let maxEar: CGFloat = 14
    static let shadowPad: CGFloat = 44
    static let contentTop: CGFloat = 10           // gap below the notch before content starts
    static let contentBottom: CGFloat = 16
    static let sidePad: CGFloat = 20
    static let rowHeight: CGFloat = 36
    static let pillRowHeight: CGFloat = 28
    static let chipHeight: CGFloat = 24
    static let rowGap: CGFloat = 10

    static var innerWidth: CGFloat { expandedWidth - 2 * sidePad }
    static var maxContentHeight: CGFloat { rowHeight + rowGap + chipHeight + rowGap + pillRowHeight }
}

@Observable
final class IslandModel {
    // Geometry (points) of the hardware notch; set by the controller.
    var notchWidth: CGFloat = 185
    var notchHeight: CGFloat = 32
    var hasNotch = true

    // UI state
    var isExpanded = false
    var phase: Phase = .idle
    var badgeVisible = false            // live done / error pill after finishing
    var link = ""
    var clipboardURL: String?
    var focusToken = 0                  // bumped to request text-field focus

    var quality: Quality {
        didSet { UserDefaults.standard.set(quality.rawValue, forKey: "quality") }
    }
    var folder: URL {
        didSet { UserDefaults.standard.set(folder.path, forKey: "folder") }
    }

    // Download state
    var title = ""
    var progress: Double = 0
    var speed = ""
    var eta = ""
    var stage: DownloadProgress.Stage = .starting
    var resultURL: URL?
    var errorMessage = ""
    var doneBounce = 0
    var lastRequest: (url: String, quality: Quality)?

    init() {
        let d = UserDefaults.standard
        Self.migrateLegacyPrefs(into: d)
        quality = Quality(rawValue: d.string(forKey: "quality") ?? "") ?? .best
        if let p = d.string(forKey: "folder"), FileManager.default.fileExists(atPath: p) {
            folder = URL(fileURLWithPath: p, isDirectory: true)
        } else {
            folder = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
                ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads")
        }
    }

    /// One-time copy of the download folder / quality from the app's previous bundle id, for keys
    /// this domain doesn't have yet.
    private static func migrateLegacyPrefs(into d: UserDefaults) {
        guard d.string(forKey: "folder") == nil || d.string(forKey: "quality") == nil,
              let old = UserDefaults(suiteName: "com.sam.snag") else { return } // legacy prefs migration
        for key in ["folder", "quality"] where d.string(forKey: key) == nil {
            if let v = old.string(forKey: key) { d.set(v, forKey: key) }
        }
    }

    var mode: IslandMode {
        if isExpanded { return .expanded(phase) }
        switch phase {
        case .downloading: return .live
        case .done where badgeVisible: return .liveDone
        case .error where badgeVisible: return .liveError
        default: return .collapsed
        }
    }

    var showsChip: Bool { phase == .idle && link.isEmpty && clipboardURL != nil }

    func contentHeight(for phase: Phase) -> CGFloat {
        switch phase {
        case .idle:
            return Layout.rowHeight + Layout.rowGap + Layout.pillRowHeight
                + (showsChip ? Layout.chipHeight + Layout.rowGap : 0)
        case .downloading: return 20 + 12 + 6 + 12 + Layout.pillRowHeight
        case .done: return 40 + 14 + Layout.pillRowHeight
        case .error: return 48 + 14 + Layout.pillRowHeight
        }
    }

    var metrics: IslandMetrics {
        switch mode {
        case .collapsed:
            return IslandMetrics(width: notchWidth, height: notchHeight,
                                 bottomRadius: hasNotch ? 10 : notchHeight / 2, earRadius: 0)
        case .live, .liveDone, .liveError:
            return IslandMetrics(width: notchWidth + 2 * Layout.liveExtension, height: notchHeight,
                                 bottomRadius: min(14, notchHeight / 2), earRadius: 6)
        case .expanded(let p):
            let h = notchHeight + Layout.contentTop + contentHeight(for: p) + Layout.contentBottom
            return IslandMetrics(width: Layout.expandedWidth, height: h, bottomRadius: 28, earRadius: Layout.maxEar)
        }
    }

    /// Fixed window size: biggest expanded blob + ears + shadow padding. Never changes while running.
    var windowSize: CGSize {
        CGSize(width: Layout.expandedWidth + 2 * Layout.maxEar + 2 * Layout.shadowPad,
               height: notchHeight + Layout.contentTop + Layout.maxContentHeight + Layout.contentBottom + Layout.shadowPad)
    }

    var folderName: String { FileManager.default.displayName(atPath: folder.path) }
}

extension String {
    /// Returns a trimmed http(s) URL string if this looks like one.
    var httpURL: String? {
        let t = trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.contains(" "), !t.contains("\n"),
              let u = URL(string: t), let s = u.scheme?.lowercased(),
              s == "http" || s == "https", u.host != nil else { return nil }
        return t
    }

    /// "https://www.youtube.com/watch?v=abc" → "youtube.com/watch?v=abc" (shortened)
    var shortLink: String {
        guard let u = URL(string: self), let host = u.host else { return self }
        var h = host
        if h.hasPrefix("www.") { h = String(h.dropFirst(4)) }
        var rest = u.path
        if let q = u.query { rest += "?" + q }
        let full = h + rest
        return full.count > 34 ? String(full.prefix(33)) + "…" : full
    }
}
