import Foundation
import CryptoKit

/// Finds yt-dlp / ffmpeg and, when they're missing, downloads them into
/// `~/Library/Application Support/SaveIt/bin` on first use.
///
/// Lookup order: /opt/homebrew/bin, /usr/local/bin, ~/.yoinks/bin, then the app-support bin.
/// Environment overrides (testing):
///   SAVEIT_TOOLS_DIR=<path>              use <path> instead of the app-support bin
///   SAVEIT_TOOLS_ONLY_APP_SUPPORT=1      look only in the app-support bin (and keep Homebrew off PATH)
enum ToolManager {
    static let home = FileManager.default.homeDirectoryForCurrentUser.path
    static let env = ProcessInfo.processInfo.environment

    static var appSupportBin: URL {
        if let o = env["SAVEIT_TOOLS_DIR"], !o.isEmpty {
            return URL(fileURLWithPath: (o as NSString).expandingTildeInPath, isDirectory: true)
        }
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: "\(home)/Library/Application Support")
        return base.appendingPathComponent("SaveIt/bin", isDirectory: true)
    }

    static var onlyAppSupport: Bool { env["SAVEIT_TOOLS_ONLY_APP_SUPPORT"] == "1" }

    static var searchDirs: [String] {
        if onlyAppSupport { return [appSupportBin.path] }
        return ["/opt/homebrew/bin", "/usr/local/bin", "\(home)/.yoinks/bin", appSupportBin.path]
    }

    /// PATH for child processes (GUI apps don't inherit the shell PATH). yt-dlp also looks here
    /// for a JavaScript runtime (deno/node) used for some YouTube formats.
    static var toolPATH: String {
        var dirs = searchDirs
        if !onlyAppSupport { dirs.append("\(home)/.deno/bin") }
        dirs += ["/usr/bin", "/bin", "/usr/sbin", "/sbin"]
        return dirs.joined(separator: ":")
    }

    static func resolve(_ tool: String) -> String? {
        for dir in searchDirs {
            let path = "\(dir)/\(tool)"
            if FileManager.default.isExecutableFile(atPath: path) { return path }
        }
        return nil
    }

    /// Tools that must be downloaded before a download can run.
    static var missing: [String] {
        ["yt-dlp", "ffmpeg"].filter { resolve($0) == nil }
    }

    static var isAppleSilicon: Bool {
        var v: Int32 = 0
        var size = MemoryLayout<Int32>.size
        return sysctlbyname("hw.optional.arm64", &v, &size, nil, 0) == 0 && v == 1
    }
}

struct SetupError: LocalizedError {
    var message: String
    var errorDescription: String? { message }
}

/// Downloads the missing tools with progress. Callbacks arrive on the main queue.
final class ToolBootstrapper: NSObject, URLSessionDownloadDelegate {
    var onProgress: ((String, Double) -> Void)?   // (label, overall 0...1)

    private struct Step {
        var name: String            // installed file name in the bin dir
        var label: String           // shown in the UI
        var url: URL
        var gzip: Bool
        var sha256FromSums: Bool    // verify against yt-dlp's SHA2-256SUMS
        var required: Bool
        var versionArgs: [String]
    }

    private var steps: [Step] = []
    private var index = 0
    private var session: URLSession!
    private var stepContinuation: ((Result<URL, Error>) -> Void)?
    private var expectedSums: [String: String] = [:]
    private(set) var isRunning = false

    static let ytdlpBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/"
    static let ffmpegBase = "https://github.com/eugeneware/ffmpeg-static/releases/latest/download/"

    func run(completion: @escaping (Result<Void, Error>) -> Void) {
        guard !isRunning else { return }
        let missing = ToolManager.missing
        guard !missing.isEmpty else { completion(.success(())); return }
        isRunning = true
        let arch = ToolManager.isAppleSilicon ? "arm64" : "x64"
        steps = []
        if missing.contains("yt-dlp") {
            steps.append(Step(name: "yt-dlp", label: "yt-dlp", url: URL(string: Self.ytdlpBase + "yt-dlp_macos")!,
                              gzip: false, sha256FromSums: true, required: true, versionArgs: ["--version"]))
        }
        if missing.contains("ffmpeg") {
            steps.append(Step(name: "ffmpeg", label: "ffmpeg", url: URL(string: Self.ffmpegBase + "ffmpeg-darwin-\(arch).gz")!,
                              gzip: true, sha256FromSums: false, required: true, versionArgs: ["-version"]))
            if ToolManager.resolve("ffprobe") == nil {
                steps.append(Step(name: "ffprobe", label: "ffprobe", url: URL(string: Self.ffmpegBase + "ffprobe-darwin-\(arch).gz")!,
                                  gzip: true, sha256FromSums: false, required: false, versionArgs: ["-version"]))
            }
        }
        index = 0
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 60
        cfg.timeoutIntervalForResource = 30 * 60
        session = URLSession(configuration: cfg, delegate: self, delegateQueue: nil)

        let finish: (Result<Void, Error>) -> Void = { [weak self] r in
            self?.session.finishTasksAndInvalidate()
            self?.isRunning = false
            DispatchQueue.main.async { completion(r) }
        }
        do {
            try FileManager.default.createDirectory(at: ToolManager.appSupportBin, withIntermediateDirectories: true)
        } catch {
            finish(.failure(SetupError(message: "Couldn't create \(ToolManager.appSupportBin.path)")))
            return
        }
        report("Preparing…", 0)
        let proceed = { [weak self] in self?.runStep(finish) }
        if steps.contains(where: { $0.sha256FromSums }) {
            fetchSums { [weak self] r in
                switch r {
                case .success(let sums): self?.expectedSums = sums; proceed()
                case .failure(let e): finish(.failure(e))
                }
            }
        } else {
            proceed()
        }
    }

    private func report(_ label: String, _ stepFraction: Double) {
        let n = max(1, steps.count)
        let overall = min(1, (Double(index) + max(0, min(1, stepFraction))) / Double(n))
        DispatchQueue.main.async { self.onProgress?(label, overall) }
    }

    private func fetchSums(_ done: @escaping (Result<[String: String], Error>) -> Void) {
        let url = URL(string: Self.ytdlpBase + "SHA2-256SUMS")!
        session.dataTask(with: url) { data, resp, err in
            guard let data, err == nil, (resp as? HTTPURLResponse)?.statusCode == 200,
                  let text = String(data: data, encoding: .utf8) else {
                done(.failure(SetupError(message: "Couldn't download yt-dlp checksums. Check your internet connection.")))
                return
            }
            var sums: [String: String] = [:]
            for line in text.split(separator: "\n") {
                let parts = line.split(separator: " ", omittingEmptySubsequences: true)
                if parts.count >= 2 { sums[String(parts[parts.count - 1])] = String(parts[0]).lowercased() }
            }
            done(.success(sums))
        }.resume()
    }

    private func runStep(_ finish: @escaping (Result<Void, Error>) -> Void) {
        guard index < steps.count else {
            report("Ready", 1)
            finish(.success(()))
            return
        }
        let step = steps[index]
        report("Downloading \(step.label)…", 0)
        stepContinuation = { [weak self] result in
            guard let self else { return }
            switch result {
            case .failure(let e):
                if step.required { finish(.failure(e)) } else { self.index += 1; self.runStep(finish) }
            case .success(let tmp):
                do {
                    try self.install(step, from: tmp)
                    self.index += 1
                    self.runStep(finish)
                } catch {
                    if step.required { finish(.failure(error)) } else { self.index += 1; self.runStep(finish) }
                }
            }
        }
        session.downloadTask(with: step.url).resume()
    }

    /// Verify, decompress, install, de-quarantine and smoke-test one tool (background queue).
    private func install(_ step: Step, from tmp: URL) throws {
        let fm = FileManager.default
        defer { try? fm.removeItem(at: tmp) }
        report("Installing \(step.label)…", 0.97)
        if step.sha256FromSums {
            guard let expected = expectedSums[step.url.lastPathComponent] else {
                throw SetupError(message: "No published checksum for \(step.url.lastPathComponent).")
            }
            let data = try Data(contentsOf: tmp, options: .mappedIfSafe)
            let actual = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            guard actual == expected else {
                throw SetupError(message: "\(step.label) failed its checksum check (download corrupted?).")
            }
        }
        let dest = ToolManager.appSupportBin.appendingPathComponent(step.name)
        let staged = ToolManager.appSupportBin.appendingPathComponent(".\(step.name).new")
        try? fm.removeItem(at: staged)
        if step.gzip {
            let r = YTDLPUpdater.run("/usr/bin/gunzip", ["-c", tmp.path], stdoutTo: staged)
            guard r.status == 0 else { throw SetupError(message: "Couldn't unpack \(step.label).") }
        } else {
            try fm.copyItem(at: tmp, to: staged)
        }
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: staged.path)
        removexattr(staged.path, "com.apple.quarantine", 0)

        var ok = YTDLPUpdater.run(staged.path, step.versionArgs, timeout: 120).status == 0
        if !ok {
            // Unsigned arm64 binaries won't run at all; an ad-hoc signature fixes that.
            _ = YTDLPUpdater.run("/usr/bin/codesign", ["--force", "-s", "-", staged.path])
            ok = YTDLPUpdater.run(staged.path, step.versionArgs, timeout: 120).status == 0
        }
        guard ok else {
            try? fm.removeItem(at: staged)
            throw SetupError(message: "The downloaded \(step.label) doesn't run on this Mac.")
        }
        try? fm.removeItem(at: dest)
        try fm.moveItem(at: staged, to: dest)
    }

    // MARK: URLSessionDownloadDelegate

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard index < steps.count else { return }
        let step = steps[index]
        let mb = { (b: Int64) in String(format: "%.1f", Double(b) / 1_048_576) }
        if totalBytesExpectedToWrite > 0 {
            let f = Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)
            report("Downloading \(step.label) · \(mb(totalBytesWritten)) of \(mb(totalBytesExpectedToWrite)) MB", f * 0.95)
        } else {
            report("Downloading \(step.label) · \(mb(totalBytesWritten)) MB", 0.5)
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        // `location` is deleted when this returns: move it somewhere we own first.
        let keep = FileManager.default.temporaryDirectory.appendingPathComponent("saveit-\(UUID().uuidString)")
        let status = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else {
            complete(.failure(SetupError(message: "Download of \(downloadTask.originalRequest?.url?.lastPathComponent ?? "tool") failed (HTTP \(status)).")))
            return
        }
        do {
            try FileManager.default.moveItem(at: location, to: keep)
            complete(.success(keep))
        } catch {
            complete(.failure(error))
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error {
            complete(.failure(SetupError(message: "Couldn't download the tools: \(error.localizedDescription)")))
        }
    }

    private func complete(_ r: Result<URL, Error>) {
        guard let c = stepContinuation else { return }
        stepContinuation = nil
        c(r)
    }
}
