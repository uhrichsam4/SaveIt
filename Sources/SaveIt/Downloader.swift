import Foundation

/// Quality presets offered in the island. Raw values are persisted in UserDefaults
/// and accepted by the headless test mode (`best|1080|720|mp3`).
enum Quality: String, CaseIterable, Identifiable {
    case best, p1080 = "1080", p720 = "720", mp3

    var id: String { rawValue }

    var label: String {
        switch self {
        case .best: return "Best"
        case .p1080: return "1080p"
        case .p720: return "720p"
        case .mp3: return "MP3"
        }
    }

    var symbol: String { self == .mp3 ? "music.note" : "film" }

    /// yt-dlp arguments for this preset (checked against `yt-dlp --help`).
    /// `-t mp4` = `--merge-output-format mp4 --remux-video mp4 -S vcodec:h264,lang,quality,res,fps,hdr:12,acodec:aac`.
    var ytdlpArgs: [String] {
        switch self {
        case .best: return ["-t", "mp4"]
        case .p1080: return ["--merge-output-format", "mp4", "--remux-video", "mp4", "-S", "res:1080,vcodec:h264,acodec:aac"]
        case .p720: return ["--merge-output-format", "mp4", "--remux-video", "mp4", "-S", "res:720,vcodec:h264,acodec:aac"]
        case .mp3: return ["-t", "mp3"]
        }
    }
}

/// Live progress snapshot, delivered on the main queue.
struct DownloadProgress {
    var fraction: Double      // 0...1, overall (weighted across merged streams)
    var speed: String         // e.g. "3.2MiB/s"
    var eta: String           // e.g. "00:12"
    var stage: Stage

    enum Stage: Equatable { case starting, downloading, processing }
}

enum DownloadOutcome {
    case success(URL)
    case failure(String)
    case cancelled
}

/// Runs yt-dlp for one URL. Used by both the UI and the `--saveit-test-download` headless mode.
/// All callbacks fire on the main queue.
final class Downloader {
    var onTitle: ((String) -> Void)?
    var onProgress: ((DownloadProgress) -> Void)?
    var onFinish: ((DownloadOutcome) -> Void)?
    /// Raw parsed progress line, for the headless test output.
    var onRawProgress: ((String) -> Void)?
    /// Extra yt-dlp arguments (headless test hooks only, e.g. `--limit-rate`).
    var extraArgs: [String] = []

    private var process: Process?
    private var cancelled = false          // main
    private var finished = false           // main
    private var startedAt = Date()

    // ioQueue state
    private var stdoutBuffer = Data()
    private var stderrBuffer = Data()
    private var lastError: String?
    private var finalPath: String?
    private var partFormats: [String] = []   // e.g. ["137", "140"] for a merged download
    private var lastFraction: Double = 0
    private var touchedFiles = Set<String>() // files this download created (from progress filename/tmpfilename + before_dl filename)
    private let ioQueue = DispatchQueue(label: "saveit.downloader.io")

    /// Field separator for machine lines: ASCII unit separator, never in filenames.
    static let sep = "\u{1F}"

    static var toolPATH: String { ToolManager.toolPATH }
    static func resolve(_ tool: String) -> String? { ToolManager.resolve(tool) }

    var isRunning: Bool { process != nil }

    func start(url: String, folder: URL, quality: Quality) {
        guard process == nil else { return }
        guard let ytdlp = Self.resolve("yt-dlp") else {
            finish(.failure("yt-dlp isn't installed yet. Retry to download it."))
            return
        }
        let s = Self.sep
        var args = [
            "--newline", "--no-playlist", "--no-colors", "--progress",
            "--progress-delta", "0.15",
            "-P", folder.path,
            "-o", "%(title).150B.%(ext)s",
            "--progress-template",
            "download:SAVEITP\(s)%(info.format_id)s\(s)%(progress._percent)s\(s)%(progress._speed_str)s\(s)%(progress._eta_str)s\(s)%(progress.filename)s\(s)%(progress.tmpfilename)s",
            "--progress-template", "postprocess:SAVEITPP\(s)%(progress.postprocessor)s\(s)%(progress.status)s",
            "--print", "before_dl:SAVEITT\(s)%(format_id)s\(s)%(filename)s\(s)%(title)s",
            "--print", "after_move:SAVEITF\(s)%(filepath)s",
        ]
        if let ffmpeg = Self.resolve("ffmpeg") { args += ["--ffmpeg-location", ffmpeg] }
        args += quality.ytdlpArgs
        args += extraArgs
        args += ["--", url]

        let p = Process()
        p.executableURL = URL(fileURLWithPath: ytdlp)
        p.arguments = args
        var env = ProcessInfo.processInfo.environment
        // GUI apps don't inherit the shell PATH; yt-dlp needs ffmpeg and a JS runtime (deno/node).
        env["PATH"] = Self.toolPATH
        env["PYTHONUNBUFFERED"] = "1"
        p.environment = env

        let out = Pipe(), err = Pipe()
        p.standardOutput = out
        p.standardError = err
        p.standardInput = FileHandle.nullDevice

        ioQueue.sync {
            stdoutBuffer = Data(); stderrBuffer = Data()
            lastError = nil; finalPath = nil
            partFormats = []; lastFraction = 0; touchedFiles = []
        }
        cancelled = false; finished = false; startedAt = Date()

        // Completion = both pipes at EOF + process exited. Only then is every line parsed.
        let group = DispatchGroup()
        group.enter(); group.enter(); group.enter()
        p.terminationHandler = { _ in group.leave() }
        drain(out.fileHandleForReading, isErr: false, group: group)
        drain(err.fileHandleForReading, isErr: true, group: group)

        process = p
        do {
            try p.run()
            emit(DownloadProgress(fraction: 0, speed: "", eta: "", stage: .starting))
        } catch {
            process = nil
            try? out.fileHandleForWriting.close(); try? err.fileHandleForWriting.close()
            finish(.failure("Couldn't start yt-dlp: \(error.localizedDescription)"))
            return
        }
        // Close our copies of the write ends so the readers see EOF when yt-dlp exits.
        try? out.fileHandleForWriting.close()
        try? err.fileHandleForWriting.close()

        group.notify(queue: ioQueue) { [weak self] in
            guard let self else { return }
            self.consume(Data("\n".utf8), isErr: false)
            self.consume(Data("\n".utf8), isErr: true)
            let status = p.terminationStatus
            DispatchQueue.main.async { self.processEnded(status: status) }
        }
    }

    /// Reads a pipe to EOF on a background thread, feeding lines to the serial parser.
    private func drain(_ h: FileHandle, isErr: Bool, group: DispatchGroup) {
        let t = Thread { [weak self] in
            while true {
                let d = h.availableData
                if d.isEmpty { break }
                self?.ioQueue.sync { self?.consume(d, isErr: isErr) }
            }
            group.leave()
        }
        t.name = isErr ? "saveit.stderr" : "saveit.stdout"
        t.start()
    }

    /// Stops yt-dlp and its children (ffmpeg) without blocking the caller.
    func cancel() {
        guard let p = process, p.isRunning else { return }
        cancelled = true
        let pid = p.processIdentifier
        DispatchQueue.global(qos: .userInitiated).async {
            let pkill = Process()
            pkill.executableURL = URL(fileURLWithPath: "/usr/bin/pkill")
            pkill.arguments = ["-TERM", "-P", String(pid)]
            try? pkill.run()
            pkill.waitUntilExit()
            if p.isRunning { p.terminate() }
        }
    }

    // MARK: - Parsing (ioQueue)

    private func consume(_ data: Data, isErr: Bool) {
        guard !data.isEmpty else { return }
        if isErr { stderrBuffer.append(data) } else { stdoutBuffer.append(data) }
        while true {
            let buf = isErr ? stderrBuffer : stdoutBuffer
            guard let nl = buf.firstIndex(where: { $0 == 0x0A || $0 == 0x0D }) else { break }
            let lineData = buf[buf.startIndex..<nl]
            let rest = buf[buf.index(after: nl)...]
            if isErr { stderrBuffer = Data(rest) } else { stdoutBuffer = Data(rest) }
            if let line = String(data: lineData, encoding: .utf8), !line.isEmpty { handle(line) }
        }
    }

    private func fields(_ line: String) -> [String] {
        line.components(separatedBy: Self.sep)
    }

    private func track(_ path: String) {
        let t = path.trimmingCharacters(in: .whitespaces)
        guard !t.isEmpty, t != "NA", t != "-" else { return }
        touchedFiles.insert(t)
    }

    private func handle(_ line: String) {
        if line.hasPrefix("SAVEITP" + Self.sep) {
            let f = fields(line)
            guard f.count >= 5 else { return }
            if f.count >= 7 { track(f[5]); track(f[6]) }
            let pct = Double(f[2].trimmingCharacters(in: .whitespaces)) ?? 0
            let fraction = overallFraction(formatID: f[1], percent: pct)
            let speed = clean(f[3]), eta = clean(f[4])
            let raw = String(format: "progress %.1f%% (part %@ %.1f%%) speed=%@ eta=%@", fraction * 100, f[1], pct, speed, eta)
            DispatchQueue.main.async { self.onRawProgress?(raw) }
            emit(DownloadProgress(fraction: fraction, speed: speed, eta: eta, stage: .downloading))
        } else if line.hasPrefix("SAVEITPP" + Self.sep) {
            let f = fields(line)
            let pp = f.count > 1 ? f[1] : ""
            let st = f.count > 2 ? f[2] : ""
            DispatchQueue.main.async { self.onRawProgress?("postprocess \(pp) \(st)") }
            lastFraction = 1
            emit(DownloadProgress(fraction: 1, speed: "", eta: "", stage: .processing))
        } else if line.hasPrefix("SAVEITT" + Self.sep) {
            let f = fields(line)
            guard f.count >= 4 else { return }
            partFormats = f[1].split(separator: "+").map(String.init)
            track(f[2])
            let title = f[3...].joined(separator: Self.sep)
            DispatchQueue.main.async { self.onTitle?(title) }
        } else if line.hasPrefix("SAVEITF" + Self.sep) {
            finalPath = String(line.dropFirst(("SAVEITF" + Self.sep).count))
        } else if line.hasPrefix("ERROR:") {
            lastError = line
        }
    }

    private func clean(_ s: String) -> String {
        let t = s.trimmingCharacters(in: .whitespaces)
        return (t == "NA" || t == "Unknown" || t.hasPrefix("Unknown")) ? "" : t
    }

    /// Merged downloads (video+audio) report 0→100% per stream. Weight them so the
    /// overall bar only moves forward: video ≈ 85%, audio ≈ 15%.
    private func overallFraction(formatID: String, percent: Double) -> Double {
        let p = max(0, min(1, percent / 100))
        var value = p
        if partFormats.count >= 2, let idx = partFormats.firstIndex(of: formatID) {
            let weights: [Double] = partFormats.count == 2 ? [0.85, 0.15]
                : Array(repeating: 1.0 / Double(partFormats.count), count: partFormats.count)
            let before = weights.prefix(idx).reduce(0, +)
            value = before + weights[idx] * p
        }
        value = max(lastFraction, min(1, value))   // never move backwards
        lastFraction = value
        return value
    }

    private func emit(_ p: DownloadProgress) {
        DispatchQueue.main.async { self.onProgress?(p) }
    }

    // MARK: - Completion (main)

    private func processEnded(status: Int32) {
        process = nil
        let (path, err, files) = ioQueue.sync { (finalPath, lastError, touchedFiles) }
        if cancelled {
            let since = startedAt
            DispatchQueue.global(qos: .utility).async {
                Self.cleanUp(files, createdAfter: since)
                DispatchQueue.main.async { self.finish(.cancelled) }
            }
            return
        }
        if status == 0, let path {
            finish(.success(URL(fileURLWithPath: path)))
        } else if status == 0 {
            finish(.failure("yt-dlp finished but didn't report a file"))
        } else {
            finish(.failure(Self.humanize(err)))
        }
    }

    /// Deletes only the files this download produced: the tracked output/temp names plus the
    /// derived `.part`, `.ytdl`, `-FragN` and merger `.temp.` names. Files older than the
    /// download are never touched.
    static func cleanUp(_ tracked: Set<String>, createdAfter start: Date) {
        let fm = FileManager.default
        var candidates = Set<String>()
        for t in tracked {
            let base = t.hasSuffix(".part") ? String(t.dropLast(5)) : t
            candidates.formUnion([base, base + ".part", base + ".ytdl", base + ".part.ytdl"])
            let url = URL(fileURLWithPath: base)
            let ext = url.pathExtension
            if !ext.isEmpty {
                candidates.insert(url.deletingPathExtension().path + ".temp." + ext)
            }
            // Fragment files: "<tmpfilename>-Frag12" (+ ".part")
            let dir = url.deletingLastPathComponent().path
            let partName = (base as NSString).lastPathComponent + ".part-Frag"
            let plainName = (base as NSString).lastPathComponent + "-Frag"
            if let items = try? fm.contentsOfDirectory(atPath: dir) {
                for i in items where i.hasPrefix(partName) || i.hasPrefix(plainName) {
                    candidates.insert(dir + "/" + i)
                }
            }
        }
        for c in candidates where fm.fileExists(atPath: c) {
            let mdate = (try? fm.attributesOfItem(atPath: c)[.modificationDate] as? Date) ?? .distantPast
            if mdate >= start.addingTimeInterval(-1) { try? fm.removeItem(atPath: c) }
        }
    }

    private func finish(_ o: DownloadOutcome) {
        guard !finished else { return }
        finished = true
        onFinish?(o)
    }

    static let outdatedHint = "try Update yt-dlp in the menu"

    /// "ERROR: [youtube] abc123: Video unavailable. This video is private" → "Video unavailable. This video is private"
    static func humanize(_ line: String?) -> String {
        guard var s = line else { return "Download failed" }
        if s.hasPrefix("ERROR:") { s = String(s.dropFirst(6)).trimmingCharacters(in: .whitespaces) }
        var site = "The site"
        if s.hasPrefix("["), let close = s.firstIndex(of: "]") {
            let ex = s[s.index(after: s.startIndex)..<close].lowercased()
            if ex.hasPrefix("youtube") { site = "YouTube" }
            s = String(s[s.index(after: close)...]).trimmingCharacters(in: .whitespaces)
            // Drop a leading "<video id>: " prefix.
            if let colon = s.range(of: ": "), s.distance(from: s.startIndex, to: colon.lowerBound) <= 40,
               !s[s.startIndex..<colon.lowerBound].contains(" ") {
                s = String(s[colon.upperBound...])
            }
        }
        let lower = s.lowercased()
        if lower.contains("unsupported url") { return "That link isn't a supported video page" }
        if lower.contains("failed to resolve") || lower.contains("nodename nor servname") || lower.contains("timed out")
            || lower.contains("network is unreachable") {
            return "Couldn't reach that site. Check the link or your connection."
        }
        if lower.contains("http error 404") { return "That page wasn't found (404)" }
        // Typical symptoms of an outdated yt-dlp (site changed its player / signatures).
        if lower.contains("http error 403") || lower.contains("sign in to confirm")
            || lower.contains("unable to extract") || lower.contains("unable to download api")
            || lower.contains("nsig") || lower.contains("signature") || lower.contains("precondition check failed")
            || lower.contains("some formats may be missing") || lower.contains("requested format is not available") {
            return "\(site) changed something — \(outdatedHint)"
        }
        if let r = s.range(of: " Use --") { s = String(s[..<r.lowerBound]) }
        if let r = s.range(of: "; please report") { s = String(s[..<r.lowerBound]) }
        s = s.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.count > 110 { s = String(s.prefix(107)) + "…" }
        return s.isEmpty ? "Download failed" : s
    }
}

/// yt-dlp version lookup and self-update (runs off the main thread).
enum YTDLPUpdater {
    /// Calls back on main with e.g. "2026.08.19", or nil.
    static func version(_ done: @escaping (String?) -> Void) {
        DispatchQueue.global(qos: .utility).async {
            var v: String?
            if let bin = Downloader.resolve("yt-dlp") {
                let r = run(bin, ["--version"])
                if r.status == 0 { v = r.output.trimmingCharacters(in: .whitespacesAndNewlines) }
            }
            DispatchQueue.main.async { done(v) }
        }
    }

    /// Upgrades via Homebrew when the binary lives under a Homebrew prefix, otherwise `yt-dlp -U`.
    /// Calls back on main with (success, message).
    static func update(_ done: @escaping (Bool, String) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            guard let bin = Downloader.resolve("yt-dlp") else {
                DispatchQueue.main.async { done(false, "yt-dlp isn't installed.") }
                return
            }
            let before = run(bin, ["--version"]).output.trimmingCharacters(in: .whitespacesAndNewlines)
            let real = (bin as NSString).resolvingSymlinksInPath
            let result: (status: Int32, output: String)
            if real.hasPrefix("/opt/homebrew/") || bin.hasPrefix("/opt/homebrew/") {
                result = run("/opt/homebrew/bin/brew", ["upgrade", "yt-dlp"])
            } else if real.hasPrefix("/usr/local/") || bin.hasPrefix("/usr/local/") {
                result = run("/usr/local/bin/brew", ["upgrade", "yt-dlp"])
            } else {
                result = run(bin, ["-U"])
            }
            let after = run(bin, ["--version"]).output.trimmingCharacters(in: .whitespacesAndNewlines)
            let ok = result.status == 0
            let msg: String
            if ok && before != after {
                msg = "Updated yt-dlp \(before) → \(after)."
            } else if ok {
                msg = "yt-dlp \(after) is already the latest version."
            } else {
                let tail = result.output.split(separator: "\n").suffix(4).joined(separator: "\n")
                msg = "Update failed.\n\(tail)"
            }
            DispatchQueue.main.async { done(ok, msg) }
        }
    }

    @discardableResult
    static func run(_ path: String, _ args: [String], timeout: TimeInterval = 600,
                    stdoutTo file: URL? = nil) -> (status: Int32, output: String) {
        guard FileManager.default.isExecutableFile(atPath: path) else { return (127, "\(path) not found") }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = Downloader.toolPATH
        p.environment = env
        let pipe = Pipe()
        var outHandle: FileHandle?
        if let file {
            FileManager.default.createFile(atPath: file.path, contents: nil)
            outHandle = try? FileHandle(forWritingTo: file)
            p.standardOutput = outHandle ?? FileHandle.nullDevice
        } else {
            p.standardOutput = pipe
        }
        p.standardError = pipe
        p.standardInput = FileHandle.nullDevice
        do { try p.run() } catch { return (126, error.localizedDescription) }
        let killer = DispatchWorkItem { if p.isRunning { p.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: killer)
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        killer.cancel()
        try? outHandle?.close()
        return (p.terminationStatus, String(decoding: data, as: UTF8.self))
    }
}
