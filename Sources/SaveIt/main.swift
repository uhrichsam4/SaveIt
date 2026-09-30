import AppKit

// SaveIt — a notch "Dynamic Island" video downloader.
//
// Test hooks:
//   --saveit-state=collapsed|expanded|downloading|downloading-expanded|done|done-expanded|error
//   --saveit-test-download <url> <folder> <best|1080|720|mp3> [--limit-rate R] [--cancel-at PCT]
//     (headless, same Downloader class)

let args = CommandLine.arguments

func runHeadless(_ rest: [String]) -> Never {
    guard rest.count >= 3, let q = Quality(rawValue: rest[2]) else {
        FileHandle.standardError.write(Data("usage: SaveIt --saveit-test-download <url> <folder> <best|1080|720|mp3> [--limit-rate R] [--cancel-at PCT]\n".utf8))
        exit(2)
    }
    let folder = URL(fileURLWithPath: (rest[1] as NSString).expandingTildeInPath, isDirectory: true)
    try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    let d = Downloader()
    // Optional test flags after the 3 positionals: --limit-rate <rate>, --cancel-at <percent>
    var cancelAt: Double?
    var i = 3
    while i + 1 < rest.count {
        switch rest[i] {
        case "--limit-rate": d.extraArgs += ["--limit-rate", rest[i + 1]]
        case "--cancel-at": cancelAt = Double(rest[i + 1])
        default: break
        }
        i += 2
    }
    var lastPct = -1
    var lastFraction = 0.0
    var cancelRequested = false
    d.onTitle = { print("title: \($0)") }
    d.onRawProgress = { print($0) }
    d.onProgress = { p in
        if p.fraction + 1e-9 < lastFraction { print("WARNING: progress went backwards \(lastFraction) -> \(p.fraction)") }
        lastFraction = max(lastFraction, p.fraction)
        let pct = Int(p.fraction * 100)
        if pct != lastPct { lastPct = pct; print("overall: \(pct)% stage=\(p.stage)") }
        if let c = cancelAt, !cancelRequested, p.fraction * 100 >= c {
            cancelRequested = true
            print("cancelling at \(pct)%")
            d.cancel()
        }
    }
    d.onFinish = { o in
        switch o {
        case .success(let u): print("done: \(u.path)"); exit(0)
        case .failure(let m): print("error: \(m)"); exit(1)
        case .cancelled: print("cancelled"); exit(1)
        }
    }
    setvbuf(stdout, nil, _IOLBF, 0)
    d.start(url: rest[0], folder: folder, quality: q)
    RunLoop.main.run()
    exit(0)
}

if let i = args.firstIndex(of: "--saveit-test-download") {
    runHeadless(Array(args[(i + 1)...]))
}

let forcedState = args.first { $0.hasPrefix("--saveit-state=") }.map { String($0.dropFirst("--saveit-state=".count)) }

final class AppDelegate: NSObject, NSApplicationDelegate {
    let controller = IslandController()
    func applicationDidFinishLaunching(_ notification: Notification) {
        controller.start(forcedState: forcedState)
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
