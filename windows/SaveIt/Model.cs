using System;
using System.IO;
using System.Text.Json;

namespace SaveIt
{
    /// <summary>Quality presets. Raw values are persisted and accepted by the headless test mode (best|1080|720|mp3).</summary>
    internal enum Quality { Best, P1080, P720, Mp3 }

    internal static class Qualities
    {
        public static readonly Quality[] All = { Quality.Best, Quality.P1080, Quality.P720, Quality.Mp3 };

        public static string Raw(this Quality q) => q switch
        {
            Quality.P1080 => "1080",
            Quality.P720 => "720",
            Quality.Mp3 => "mp3",
            _ => "best",
        };

        public static Quality? Parse(string? s) => s switch
        {
            "best" => Quality.Best,
            "1080" => Quality.P1080,
            "720" => Quality.P720,
            "mp3" => Quality.Mp3,
            _ => null,
        };

        public static string Label(this Quality q) => q switch
        {
            Quality.P1080 => "1080p",
            Quality.P720 => "720p",
            Quality.Mp3 => "MP3",
            _ => "Best",
        };

        /// <summary>yt-dlp arguments for this preset (same as the macOS app).
        /// `-t mp4` = `--merge-output-format mp4 --remux-video mp4 -S vcodec:h264,lang,quality,res,fps,hdr:12,acodec:aac`.</summary>
        public static string[] YtdlpArgs(this Quality q) => q switch
        {
            Quality.P1080 => new[] { "--merge-output-format", "mp4", "--remux-video", "mp4", "-S", "res:1080,vcodec:h264,acodec:aac" },
            Quality.P720 => new[] { "--merge-output-format", "mp4", "--remux-video", "mp4", "-S", "res:720,vcodec:h264,acodec:aac" },
            Quality.Mp3 => new[] { "-t", "mp3" },
            _ => new[] { "-t", "mp4" },
        };
    }

    internal enum Phase { Idle, Downloading, Done, Error }

    internal enum Stage { Setup, Starting, Downloading, Processing }

    /// <summary>Live progress snapshot, delivered on the UI thread.</summary>
    internal readonly struct DownloadProgress
    {
        public DownloadProgress(double fraction, string speed, string eta, Stage stage, string status = "")
        {
            Fraction = fraction; Speed = speed; Eta = eta; Stage = stage; Status = status;
        }
        public double Fraction { get; }   // 0...1 overall (weighted across merged streams)
        public string Speed { get; }      // e.g. "3.2MiB/s"
        public string Eta { get; }        // e.g. "00:12"
        public Stage Stage { get; }
        public string Status { get; }     // setup status line ("Downloading ffmpeg… 12 of 140 MB")
    }

    internal enum OutcomeKind { Success, Failure, Cancelled }

    internal sealed record Outcome(OutcomeKind Kind, string? Path = null, string? Message = null)
    {
        public static Outcome Success(string path) => new(OutcomeKind.Success, path);
        public static Outcome Failure(string message) => new(OutcomeKind.Failure, null, message);
        public static readonly Outcome Cancelled = new(OutcomeKind.Cancelled);
    }

    /// <summary>Island geometry in DIPs (mirrors the macOS Layout enum; Windows has no notch,
    /// so the "notch band" is a fixed 32 DIP strip and the collapsed island is a small pill).</summary>
    internal static class L
    {
        public const double ExpandedWidth = 460;
        public const double BandHeight = 32;           // top band: creator links (mac: beside the camera)
        public const double BandWidth = 120;           // middle segment of the live pill
        public const double LiveExtension = 42;        // ring / percent segments either side
        public const double LiveWidth = BandWidth + 2 * LiveExtension;
        public const double LiveHeight = 32;
        public const double CollapsedWidth = 120;
        public const double CollapsedHeight = 7;
        public const double MaxEar = 14;
        public const double ShadowPad = 44;
        public const double ContentTop = 10;
        public const double ContentBottom = 16;
        public const double SidePad = 20;
        public const double RowHeight = 36;
        public const double PillRowHeight = 28;
        public const double ChipHeight = 24;
        public const double RowGap = 10;

        public const double InnerWidth = ExpandedWidth - 2 * SidePad;
        public const double MaxContentHeight = RowHeight + RowGap + ChipHeight + RowGap + PillRowHeight;

        /// <summary>Fixed window size: biggest expanded blob + ears + shadow padding.</summary>
        public const double WindowWidth = ExpandedWidth + 2 * MaxEar + 2 * ShadowPad;
        public const double WindowHeight = BandHeight + ContentTop + MaxContentHeight + ContentBottom + ShadowPad;

        public const double ContentLeft = (WindowWidth - InnerWidth) / 2;
        public const double ContentY = BandHeight + ContentTop;
    }

    /// <summary>Persisted in %APPDATA%\SaveIt\settings.json.</summary>
    internal sealed class Settings
    {
        public string Quality { get; set; } = "best";
        public string? Folder { get; set; }

        static string Dir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("APPDATA");
                var root = !string.IsNullOrEmpty(env) ? env : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(root, "SaveIt");
            }
        }

        static string FilePath => Path.Combine(Dir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, true);
            }
            catch { }
        }

        public static string DefaultFolder()
        {
            var d = Native.KnownFolder(Native.FOLDERID_Downloads);
            if (!string.IsNullOrEmpty(d)) return d!;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public Quality QualityValue => Qualities.Parse(Quality) ?? SaveIt.Quality.Best;

        public string FolderValue => !string.IsNullOrEmpty(Folder) && Directory.Exists(Folder) ? Folder! : DefaultFolder();
    }

    internal static class Links
    {
        /// <summary>Returns a trimmed http(s) URL string if the text looks like one.</summary>
        public static string? HttpUrl(string? s)
        {
            if (s == null) return null;
            var t = s.Trim();
            if (t.Length == 0 || t.Contains(' ') || t.Contains('\n')) return null;
            if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            if (string.IsNullOrEmpty(u.Host)) return null;
            return t;
        }

        /// <summary>"https://www.youtube.com/watch?v=abc" → "youtube.com/watch?v=abc" (shortened).</summary>
        public static string Short(string s)
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return s;
            var h = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host.Substring(4) : u.Host;
            var full = h + u.AbsolutePath + u.Query;
            if (full.EndsWith("/")) full = full.TrimEnd('/');
            return full.Length > 34 ? full.Substring(0, 33) + "…" : full;
        }

        public static string FolderName(string path)
        {
            var t = path.TrimEnd('\\', '/');
            var n = Path.GetFileName(t);
            return string.IsNullOrEmpty(n) ? path : n;
        }
    }
}
