using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SaveIt
{
    /// <summary>Thrown for setup failures that already carry a user-facing message.</summary>
    internal sealed class SetupException : Exception
    {
        public SetupException(string message) : base(message) { }
    }

    /// <summary>Locates yt-dlp / ffmpeg and downloads them on first use into %LOCALAPPDATA%\SaveIt\bin.</summary>
    internal static class Tools
    {
        public const string YtdlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        public const string YtdlpSumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
        public const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        public const string FfmpegSumsUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256";
        const string FfmpegZipName = "ffmpeg-master-latest-win64-gpl.zip";

        static string LocalAppData
        {
            get
            {
                // Honour the environment variable first (CI points it at a fresh directory).
                var env = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                return !string.IsNullOrEmpty(env) ? env : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
        }

        public static string AppDir => Path.Combine(LocalAppData, "SaveIt");
        public static string BinDir => Path.Combine(AppDir, "bin");

        /// <summary>SAVEIT_NO_PATH_TOOLS=1 ignores tools on PATH (used by CI to exercise the bootstrap).</summary>
        static bool UsePath => Environment.GetEnvironmentVariable("SAVEIT_NO_PATH_TOOLS") != "1";

        /// <summary>Full path of "yt-dlp" / "ffmpeg" / "ffprobe": our bin dir first, then PATH.</summary>
        public static string? Resolve(string tool)
        {
            var exe = tool + ".exe";
            var local = Path.Combine(BinDir, exe);
            if (File.Exists(local)) return local;
            if (!UsePath) return null;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var raw in path.Split(Path.PathSeparator))
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    var c = Path.Combine(dir, exe);
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
            return null;
        }

        public static bool Ready => Resolve("yt-dlp") != null && Resolve("ffmpeg") != null;

        /// <summary>Downloads whatever is missing. <paramref name="report"/> is called from a
        /// background thread with (fraction 0...1, status line).</summary>
        public static async Task EnsureAsync(Action<double, string> report, CancellationToken ct)
        {
            bool needY = Resolve("yt-dlp") == null;
            bool needF = Resolve("ffmpeg") == null;
            if (!needY && !needF) return;
            Directory.CreateDirectory(BinDir);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SaveIt/1.0 (Windows)");

            // Byte-weighted ranges: yt-dlp ≈ 18 MB, ffmpeg zip ≈ 140 MB.
            double yWeight = needY ? (needF ? 0.12 : 1.0) : 0.0;
            double fWeight = 1.0 - yWeight;

            if (needY)
            {
                report(0, "Downloading yt-dlp…");
                string sums = await http.GetStringAsync(YtdlpSumsUrl, ct).ConfigureAwait(false);
                string? expected = FindHash(sums, "yt-dlp.exe");
                if (expected == null) throw new SetupException("Couldn't verify yt-dlp (no checksum published). Try again later.");

                var final = Path.Combine(BinDir, "yt-dlp.exe");
                var tmp = final + ".download";
                string actual = await DownloadAsync(http, YtdlpUrl, tmp, (done, total) =>
                {
                    double f = total > 0 ? (double)done / total : 0;
                    report(yWeight * f, "Downloading yt-dlp… " + Mb(done, total));
                }, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tmp);
                    throw new SetupException("The yt-dlp download didn't pass verification. Try again.");
                }
                File.Move(tmp, final, true);
            }

            if (needF)
            {
                double b = yWeight;
                report(b, "Downloading ffmpeg…");
                string? expected = null;
                try
                {
                    string sums = await http.GetStringAsync(FfmpegSumsUrl, ct).ConfigureAwait(false);
                    expected = FindHash(sums, FfmpegZipName);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* checksum list is best effort for ffmpeg */ }

                var zip = Path.Combine(BinDir, "ffmpeg.zip.download");
                string actual = await DownloadAsync(http, FfmpegUrl, zip, (done, total) =>
                {
                    double f = total > 0 ? (double)done / total : 0;
                    report(b + fWeight * 0.95 * f, "Downloading ffmpeg… " + Mb(done, total));
                }, ct).ConfigureAwait(false);
                if (expected != null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(zip);
                    throw new SetupException("The ffmpeg download didn't pass verification. Try again.");
                }

                report(b + fWeight * 0.96, "Unpacking ffmpeg…");
                await Task.Run(() => ExtractFfmpeg(zip, ct), ct).ConfigureAwait(false);
                TryDelete(zip);
            }
            report(1, "Ready");
        }

        static void ExtractFfmpeg(string zipPath, CancellationToken ct)
        {
            var wanted = new[] { "ffmpeg.exe", "ffprobe.exe" };
            int found = 0;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = e.Name;
                    if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!e.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase)) continue;
                    var dest = Path.Combine(BinDir, name.ToLowerInvariant());
                    var tmp = dest + ".download";
                    e.ExtractToFile(tmp, true);
                    File.Move(tmp, dest, true);
                    found++;
                }
            }
            if (found < 2) throw new SetupException("The ffmpeg download was missing ffmpeg.exe. Try again.");
        }

        static async Task<string> DownloadAsync(HttpClient http, string url, string dest, Action<long, long> progress, CancellationToken ct)
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    var sw = Stopwatch.StartNew();
                    long lastReport = -1000;
                    int n;
                    while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        hash.AppendData(buf, 0, n);
                        done += n;
                        if (sw.ElapsedMilliseconds - lastReport >= 80)
                        {
                            lastReport = sw.ElapsedMilliseconds;
                            progress(done, total);
                        }
                    }
                    progress(done, total);
                }
            }
            catch
            {
                TryDelete(dest);
                throw;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        /// <summary>Finds "&lt;sha256&gt;  &lt;file&gt;" in a sha256sum-style listing.</summary>
        static string? FindHash(string listing, string file)
        {
            foreach (var line in listing.Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var name = parts[^1].TrimStart('*');
                if (string.Equals(name, file, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64) return parts[0];
            }
            return null;
        }

        static string Mb(long done, long total)
        {
            double d = done / 1048576.0;
            if (total > 0) return $"{d:0} of {total / 1048576.0:0} MB";
            return $"{d:0} MB";
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>Friendly message for a setup failure.</summary>
        public static string Humanize(Exception e)
        {
            if (e is SetupException) return e.Message;
            if (e is HttpRequestException || e is TaskCanceledException || e is IOException && e.InnerException is System.Net.Sockets.SocketException)
                return "Couldn't download yt-dlp/ffmpeg. Check your connection and retry.";
            if (e is UnauthorizedAccessException || e is IOException)
                return "Couldn't save SaveIt's tools: " + Trim(e.Message);
            return "Setup failed: " + Trim(e.Message);
        }

        static string Trim(string s)
        {
            s = s.Trim();
            return s.Length > 90 ? s.Substring(0, 87) + "…" : s;
        }

        /// <summary>Runs a tool to completion, capturing stdout+stderr (off the UI thread).</summary>
        public static (int status, string output) Run(string exe, IEnumerable<string> args, int timeoutMs = 180_000)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    return (124, "Timed out");
                }
                p.WaitForExit();
                return (p.ExitCode, o.Result + e.Result);
            }
            catch (Exception ex)
            {
                return (126, ex.Message);
            }
        }

        public static string? YtdlpVersion()
        {
            var bin = Resolve("yt-dlp");
            if (bin == null) return null;
            var r = Run(bin, new[] { "--version" }, 60_000);
            return r.status == 0 ? r.output.Trim() : null;
        }

        /// <summary>`yt-dlp -U` (or first-time install). Returns (ok, message).</summary>
        public static (bool ok, string message) UpdateYtdlp()
        {
            var bin = Resolve("yt-dlp");
            if (bin == null)
            {
                try
                {
                    EnsureAsync((_, _) => { }, CancellationToken.None).GetAwaiter().GetResult();
                    return (true, $"Installed yt-dlp {YtdlpVersion() ?? ""}.".Replace(" .", "."));
                }
                catch (Exception e)
                {
                    return (false, Humanize(e));
                }
            }
            var before = YtdlpVersion() ?? "";
            var result = Run(bin, new[] { "-U" });
            var after = YtdlpVersion() ?? before;
            bool ok = result.status == 0;
            if (ok && before != after) return (true, $"Updated yt-dlp {before} → {after}.");
            if (ok) return (true, $"yt-dlp {after} is already the latest version.");
            var tail = string.Join("\n", result.output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).TakeLast(4));
            return (false, "Update failed.\n" + tail);
        }
    }
}
