using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;

namespace SaveIt
{
    // SaveIt — a "Dynamic Island" video downloader for Windows.
    //
    // Test hooks:
    //   --saveit-state=collapsed|expanded|downloading|downloading-expanded|done|done-expanded|error|setup
    //   --saveit-test-download <url> <folder> <best|1080|720|mp3> [--limit-rate R] [--cancel-at PCT]
    //     (headless, same Downloader + first-run tool bootstrap; exit code 0/1; output to stdout
    //      and, if SAVEIT_LOG is set, appended to that file)
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            int i = Array.IndexOf(args, "--saveit-test-download");
            if (i >= 0) return Headless.Run(args.Skip(i + 1).ToArray());

            string? forced = args.FirstOrDefault(a => a.StartsWith("--saveit-state=", StringComparison.Ordinal))?.Substring("--saveit-state=".Length);

            Mutex? single = null;
            if (forced == null)
            {
                single = new Mutex(true, @"Local\SaveIt.Island.SingleInstance", out bool created);
                if (!created) return 0;
            }

            System.Windows.Forms.Application.EnableVisualStyles();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log.Write("unhandled: " + e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);

            var island = new Island(Settings.Load(), forced);
            island.Show();
            var tray = new Tray(island);
            app.Exit += (_, _) => { tray.Dispose(); single?.ReleaseMutex(); };
            app.Run();
            GC.KeepAlive(single);
            return 0;
        }
    }

    internal static class Log
    {
        public static void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(Tools.AppDir);
                File.AppendAllText(Path.Combine(Tools.AppDir, "saveit.log"), $"{DateTime.Now:u} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }

    /// <summary>`--saveit-test-download`: runs the same Downloader (including first-run setup) without UI.</summary>
    internal static class Headless
    {
        static TextWriter? logFile;

        static void Out(string line)
        {
            try { Console.Out.WriteLine(line); Console.Out.Flush(); } catch { }
            try { logFile?.WriteLine(line); logFile?.Flush(); } catch { }
        }

        static void AttachParentConsole()
        {
            // WinExe: when stdout isn't redirected, attach to the launching console so output is visible.
            var h = Native.GetStdHandle(-11);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                if (Native.AttachConsole(-1))
                {
                    var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                    Console.SetOut(stdout);
                }
            }
            else
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            }
        }

        public static int Run(string[] rest)
        {
            AttachParentConsole();
            var logPath = Environment.GetEnvironmentVariable("SAVEIT_LOG");
            if (!string.IsNullOrEmpty(logPath))
            {
                try { logFile = new StreamWriter(logPath, true, new UTF8Encoding(false)) { AutoFlush = true }; } catch { }
            }

            Quality? q = rest.Length >= 3 ? Qualities.Parse(rest[2]) : null;
            if (q == null)
            {
                Out("usage: SaveIt --saveit-test-download <url> <folder> <best|1080|720|mp3> [--limit-rate R] [--cancel-at PCT]");
                return 2;
            }
            var folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rest[1]));
            Directory.CreateDirectory(folder);

            var queue = new BlockingCollection<Action>();
            var d = new Downloader(a => { try { queue.Add(a); } catch (InvalidOperationException) { } });
            double? cancelAt = null;
            for (int i = 3; i + 1 < rest.Length; i += 2)
            {
                switch (rest[i])
                {
                    case "--limit-rate": d.ExtraArgs.Add("--limit-rate"); d.ExtraArgs.Add(rest[i + 1]); break;
                    case "--cancel-at":
                        if (double.TryParse(rest[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) cancelAt = c;
                        break;
                }
            }

            Out($"SaveIt {Tray.Version} headless: {rest[0]} -> {folder} ({q.Value.Raw()})");
            Out($"tools dir: {Tools.BinDir}");
            Out($"yt-dlp: {Tools.Resolve("yt-dlp") ?? "missing (will download)"}");
            Out($"ffmpeg: {Tools.Resolve("ffmpeg") ?? "missing (will download)"}");

            int exitCode = -1;
            int lastPct = -1, lastSetupPct = -1;
            double lastFraction = 0;
            bool cancelRequested = false;
            Stage lastStage = Stage.Starting;

            d.OnTitle = t => Out("title: " + t);
            d.OnRawProgress = Out;
            d.OnProgress = p =>
            {
                if (p.Stage == Stage.Setup)
                {
                    int sp = (int)(p.Fraction * 100);
                    if (sp / 5 != lastSetupPct / 5 || sp == 100) { lastSetupPct = sp; Out($"setup: {sp}% {p.Status}"); }
                    lastStage = p.Stage;
                    return;
                }
                if (lastStage == Stage.Setup) { lastFraction = 0; lastPct = -1; Out("setup: complete"); }
                lastStage = p.Stage;
                if (p.Fraction + 1e-9 < lastFraction) Out($"WARNING: progress went backwards {lastFraction} -> {p.Fraction}");
                lastFraction = Math.Max(lastFraction, p.Fraction);
                int pct = (int)(p.Fraction * 100);
                if (pct != lastPct) { lastPct = pct; Out($"overall: {pct}% stage={p.Stage.ToString().ToLowerInvariant()}"); }
                if (cancelAt is double ca && !cancelRequested && p.Fraction * 100 >= ca)
                {
                    cancelRequested = true;
                    Out($"cancelling at {pct}%");
                    d.Cancel();
                }
            };
            d.OnFinish = o =>
            {
                switch (o.Kind)
                {
                    case OutcomeKind.Success: Out("done: " + o.Path); exitCode = 0; break;
                    case OutcomeKind.Failure: Out("error: " + o.Message); exitCode = 1; break;
                    default: Out("cancelled"); exitCode = 1; break;
                }
                queue.CompleteAdding();
            };

            d.Start(rest[0], folder, q.Value);
            try
            {
                foreach (var action in queue.GetConsumingEnumerable())
                {
                    action();
                    if (exitCode >= 0) break;
                }
            }
            catch (Exception e)
            {
                Out("error: " + e.Message);
                exitCode = 1;
            }
            logFile?.Dispose();
            return exitCode < 0 ? 1 : exitCode;
        }
    }
}
