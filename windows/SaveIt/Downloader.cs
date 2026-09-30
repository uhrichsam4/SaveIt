using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SaveIt
{
    /// <summary>Runs yt-dlp for one URL (port of the macOS Downloader). Used by both the UI and the
    /// `--saveit-test-download` headless mode. All callbacks fire through <c>post</c> (the UI thread).</summary>
    internal sealed class Downloader
    {
        public Action<string>? OnTitle;
        public Action<DownloadProgress>? OnProgress;
        public Action<Outcome>? OnFinish;
        /// <summary>Raw parsed progress line, for the headless test output.</summary>
        public Action<string>? OnRawProgress;
        /// <summary>Extra yt-dlp arguments (headless test hooks only, e.g. --limit-rate).</summary>
        public List<string> ExtraArgs { get; } = new();

        readonly Action<Action> post;
        Process? process;
        bool setupRunning;
        CancellationTokenSource? setupCts;
        bool cancelled;            // UI thread
        bool finished;             // UI thread
        DateTime startedAt = DateTime.Now;

        // Parser state (guarded by `gate`).
        readonly object gate = new();
        string? lastError;
        string? finalPath;
        string[] partFormats = Array.Empty<string>();
        double lastFraction;
        HashSet<string> touchedFiles = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Field separator for machine lines: ASCII unit separator, never in filenames.</summary>
        public const char Sep = '\u001F';

        public Downloader(Action<Action> post) { this.post = post; }

        public bool IsRunning => process != null || setupRunning;

        public void Start(string url, string folder, Quality quality)
        {
            if (IsRunning) return;
            cancelled = false; finished = false; startedAt = DateTime.Now;

            if (!Tools.Ready)
            {
                // First use: fetch yt-dlp / ffmpeg with a "Setting up…" progress state.
                setupRunning = true;
                var cts = new CancellationTokenSource();
                setupCts = cts;
                Emit(new DownloadProgress(0, "", "", Stage.Setup, "Preparing…"));
                Task.Run(async () =>
                {
                    Exception? error = null;
                    try
                    {
                        await Tools.EnsureAsync((f, s) => Emit(new DownloadProgress(f, "", "", Stage.Setup, s)), cts.Token);
                    }
                    catch (Exception e) { error = e; }
                    post(() =>
                    {
                        setupRunning = false;
                        setupCts = null;
                        if (cancelled || (error is OperationCanceledException && cts.IsCancellationRequested)) { Finish(Outcome.Cancelled); return; }
                        if (error != null) { Finish(Outcome.Failure(Tools.Humanize(error))); return; }
                        Launch(url, folder, quality);
                    });
                });
                return;
            }
            Launch(url, folder, quality);
        }

        void Launch(string url, string folder, Quality quality)
        {
            var ytdlp = Tools.Resolve("yt-dlp");
            if (ytdlp == null) { Finish(Outcome.Failure("yt-dlp isn't installed. Retry to set it up.")); return; }
            try { Directory.CreateDirectory(folder); } catch { }

            char s = Sep;
            var args = new List<string>
            {
                "--newline", "--no-playlist", "--no-colors", "--progress",
                "--progress-delta", "0.15",
                "--encoding", "utf-8",
                "-P", folder,
                "-o", "%(title).150B.%(ext)s",
                "--progress-template",
                $"download:SAVEITP{s}%(info.format_id)s{s}%(progress._percent)s{s}%(progress._speed_str)s{s}%(progress._eta_str)s{s}%(progress.filename)s{s}%(progress.tmpfilename)s",
                "--progress-template", $"postprocess:SAVEITPP{s}%(progress.postprocessor)s{s}%(progress.status)s",
                "--print", $"before_dl:SAVEITT{s}%(format_id)s{s}%(filename)s{s}%(title)s",
                "--print", $"after_move:SAVEITF{s}%(filepath)s",
            };
            var ffmpeg = Tools.Resolve("ffmpeg");
            if (ffmpeg != null) { args.Add("--ffmpeg-location"); args.Add(Path.GetDirectoryName(ffmpeg)!); }
            args.AddRange(quality.YtdlpArgs());
            args.AddRange(ExtraArgs);
            args.Add("--");
            args.Add(url);

            var psi = new ProcessStartInfo(ytdlp)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = folder,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = Tools.BinDir + Path.PathSeparator + path;
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";

            lock (gate)
            {
                lastError = null; finalPath = null;
                partFormats = Array.Empty<string>(); lastFraction = 0;
                touchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var p = new Process { StartInfo = psi };
            try
            {
                p.Start();
            }
            catch (Exception e)
            {
                p.Dispose();
                Finish(Outcome.Failure("Couldn't start yt-dlp: " + e.Message));
                return;
            }
            process = p;
            try { p.StandardInput.Close(); } catch { }
            Emit(new DownloadProgress(0, "", "", Stage.Starting));

            // Completion = both pipes at EOF + process exited. Only then is every line parsed.
            var outThread = new Thread(() => Drain(p.StandardOutput.BaseStream)) { IsBackground = true, Name = "saveit.stdout" };
            var errThread = new Thread(() => Drain(p.StandardError.BaseStream)) { IsBackground = true, Name = "saveit.stderr" };
            outThread.Start();
            errThread.Start();
            Task.Run(() =>
            {
                outThread.Join();
                errThread.Join();
                p.WaitForExit();
                int code;
                try { code = p.ExitCode; } catch { code = -1; }
                post(() => ProcessEnded(p, code));
            });
        }

        /// <summary>Reads a pipe to EOF on a background thread, feeding complete lines to the parser.</summary>
        void Drain(Stream stream)
        {
            var buf = new byte[8192];
            var line = new List<byte>(512);
            try
            {
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        if (b == (byte)'\n' || b == (byte)'\r')
                        {
                            if (line.Count > 0) { HandleBytes(line); line.Clear(); }
                        }
                        else line.Add(b);
                    }
                }
            }
            catch { }
            if (line.Count > 0) HandleBytes(line);
        }

        void HandleBytes(List<byte> bytes)
        {
            var text = Encoding.UTF8.GetString(bytes.ToArray());
            if (text.Length == 0) return;
            lock (gate) Handle(text);
        }

        /// <summary>Stops yt-dlp and its children (ffmpeg) without blocking the caller.</summary>
        public void Cancel()
        {
            if (setupRunning)
            {
                cancelled = true;
                setupCts?.Cancel();
                return;
            }
            var p = process;
            if (p == null) return;
            bool exited;
            try { exited = p.HasExited; } catch { exited = true; }
            if (exited) return;
            cancelled = true;
            Task.Run(() =>
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            });
        }

        // MARK: - Parsing (under `gate`, background threads)

        void Track(string path)
        {
            var t = path.Trim();
            if (t.Length == 0 || t == "NA" || t == "-") return;
            touchedFiles.Add(t);
        }

        void Handle(string line)
        {
            if (line.StartsWith("SAVEITP" + Sep, StringComparison.Ordinal))
            {
                var f = line.Split(Sep);
                if (f.Length < 5) return;
                if (f.Length >= 7) { Track(f[5]); Track(f[6]); }
                double.TryParse(f[2].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct);
                double fraction = OverallFraction(f[1], pct);
                string speed = Clean(f[3]), eta = Clean(f[4]);
                string raw = string.Format(CultureInfo.InvariantCulture, "progress {0:0.0}% (part {1} {2:0.0}%) speed={3} eta={4}", fraction * 100, f[1], pct, speed, eta);
                post(() => OnRawProgress?.Invoke(raw));
                Emit(new DownloadProgress(fraction, speed, eta, Stage.Downloading));
            }
            else if (line.StartsWith("SAVEITPP" + Sep, StringComparison.Ordinal))
            {
                var f = line.Split(Sep);
                string pp = f.Length > 1 ? f[1] : "", st = f.Length > 2 ? f[2] : "";
                post(() => OnRawProgress?.Invoke($"postprocess {pp} {st}"));
                lastFraction = 1;
                Emit(new DownloadProgress(1, "", "", Stage.Processing));
            }
            else if (line.StartsWith("SAVEITT" + Sep, StringComparison.Ordinal))
            {
                var f = line.Split(Sep);
                if (f.Length < 4) return;
                partFormats = f[1].Split('+', StringSplitOptions.RemoveEmptyEntries);
                Track(f[2]);
                string title = string.Join(Sep, f.Skip(3));
                post(() => OnTitle?.Invoke(title));
            }
            else if (line.StartsWith("SAVEITF" + Sep, StringComparison.Ordinal))
            {
                finalPath = line.Substring(("SAVEITF" + Sep).Length).Trim();
            }
            else if (line.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                lastError = line;
            }
        }

        static string Clean(string s)
        {
            var t = s.Trim();
            return (t == "NA" || t.StartsWith("Unknown", StringComparison.Ordinal)) ? "" : t;
        }

        /// <summary>Merged downloads (video+audio) report 0→100% per stream. Weight them so the
        /// overall bar only moves forward: video ≈ 85%, audio ≈ 15%.</summary>
        double OverallFraction(string formatId, double percent)
        {
            double p = Math.Max(0, Math.Min(1, percent / 100));
            double value = p;
            int idx = Array.IndexOf(partFormats, formatId);
            if (partFormats.Length >= 2 && idx >= 0)
            {
                double[] weights = partFormats.Length == 2
                    ? new[] { 0.85, 0.15 }
                    : Enumerable.Repeat(1.0 / partFormats.Length, partFormats.Length).ToArray();
                double before = weights.Take(idx).Sum();
                value = before + weights[idx] * p;
            }
            value = Math.Max(lastFraction, Math.Min(1, value));   // never move backwards
            lastFraction = value;
            return value;
        }

        void Emit(DownloadProgress p) => post(() => OnProgress?.Invoke(p));

        // MARK: - Completion (UI thread)

        void ProcessEnded(Process p, int status)
        {
            if (!ReferenceEquals(process, p)) return;
            process = null;
            string? path, err;
            HashSet<string> files;
            lock (gate) { path = finalPath; err = lastError; files = new HashSet<string>(touchedFiles, StringComparer.OrdinalIgnoreCase); }
            p.Dispose();
            if (cancelled)
            {
                var since = startedAt;
                Task.Run(() =>
                {
                    CleanUp(files, since);
                    post(() => Finish(Outcome.Cancelled));
                });
                return;
            }
            if (status == 0 && !string.IsNullOrEmpty(path)) Finish(Outcome.Success(path!));
            else if (status == 0) Finish(Outcome.Failure("yt-dlp finished but didn't report a file"));
            else Finish(Outcome.Failure(Humanize(err)));
        }

        /// <summary>Deletes only the files this download produced: the tracked output/temp names plus the
        /// derived .part, .ytdl, -FragN and merger .temp. names. Files older than the download are never touched.</summary>
        public static void CleanUp(IEnumerable<string> tracked, DateTime start)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tracked)
            {
                string full;
                try { full = Path.GetFullPath(t); } catch { continue; }
                var b = full.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? full.Substring(0, full.Length - 5) : full;
                candidates.Add(b); candidates.Add(b + ".part"); candidates.Add(b + ".ytdl"); candidates.Add(b + ".part.ytdl");
                var ext = Path.GetExtension(b);
                if (!string.IsNullOrEmpty(ext))
                    candidates.Add(Path.Combine(Path.GetDirectoryName(b) ?? "", Path.GetFileNameWithoutExtension(b) + ".temp" + ext));
                // Fragment files: "<tmpfilename>-Frag12" (+ ".part")
                var dir = Path.GetDirectoryName(b);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                var name = Path.GetFileName(b);
                try
                {
                    foreach (var item in Directory.EnumerateFiles(dir))
                    {
                        var n = Path.GetFileName(item);
                        if (n.StartsWith(name + ".part-Frag", StringComparison.OrdinalIgnoreCase) || n.StartsWith(name + "-Frag", StringComparison.OrdinalIgnoreCase))
                            candidates.Add(item);
                    }
                }
                catch { }
            }
            var cutoff = start.AddSeconds(-1);
            foreach (var c in candidates)
            {
                // Killed processes can hold handles for a moment; retry briefly.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        if (!File.Exists(c)) break;
                        if (File.GetLastWriteTime(c) < cutoff) break;
                        File.Delete(c);
                        break;
                    }
                    catch { Thread.Sleep(150); }
                }
            }
        }

        void Finish(Outcome o)
        {
            if (finished) return;
            finished = true;
            OnFinish?.Invoke(o);
        }

        public const string OutdatedHint = "try Update yt-dlp in the tray menu";

        /// <summary>"ERROR: [youtube] abc123: Video unavailable. This video is private" → "Video unavailable. This video is private".</summary>
        public static string Humanize(string? line)
        {
            if (line == null) return "Download failed";
            var s = line;
            if (s.StartsWith("ERROR:", StringComparison.Ordinal)) s = s.Substring(6).Trim();
            var site = "The site";
            if (s.StartsWith("[", StringComparison.Ordinal))
            {
                int close = s.IndexOf(']');
                if (close > 0)
                {
                    var ex = s.Substring(1, close - 1).ToLowerInvariant();
                    if (ex.StartsWith("youtube", StringComparison.Ordinal)) site = "YouTube";
                    s = s.Substring(close + 1).Trim();
                    // Drop a leading "<video id>: " prefix.
                    int colon = s.IndexOf(": ", StringComparison.Ordinal);
                    if (colon >= 0 && colon <= 40 && !s.Substring(0, colon).Contains(' ')) s = s.Substring(colon + 2);
                }
            }
            var lower = s.ToLowerInvariant();
            if (lower.Contains("unsupported url")) return "That link isn't a supported video page";
            if (lower.Contains("failed to resolve") || lower.Contains("getaddrinfo failed") || lower.Contains("timed out")
                || lower.Contains("network is unreachable") || lower.Contains("no connection could be made")
                || lower.Contains("winerror 10060") || lower.Contains("winerror 10051") || lower.Contains("winerror 11001")
                || lower.Contains("connection reset") || lower.Contains("remote end closed connection"))
                return "Couldn't reach that site. Check the link or your connection.";
            if (lower.Contains("http error 404")) return "That page wasn't found (404)";
            // Typical symptoms of an outdated yt-dlp (site changed its player / signatures).
            if (lower.Contains("http error 403") || lower.Contains("sign in to confirm")
                || lower.Contains("unable to extract") || lower.Contains("unable to download api")
                || lower.Contains("nsig") || lower.Contains("signature") || lower.Contains("precondition check failed")
                || lower.Contains("some formats may be missing") || lower.Contains("requested format is not available"))
                return $"{site} changed something — {OutdatedHint}";
            int r = s.IndexOf(" Use --", StringComparison.Ordinal);
            if (r >= 0) s = s.Substring(0, r);
            r = s.IndexOf("; please report", StringComparison.Ordinal);
            if (r >= 0) s = s.Substring(0, r);
            s = s.Trim();
            if (s.Length > 110) s = s.Substring(0, 107) + "…";
            return s.Length == 0 ? "Download failed" : s;
        }
    }
}
