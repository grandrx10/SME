using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>A song as returned by a search (not yet in anyone's list).</summary>
    public struct SongInfo
    {
        public string id;       // YouTube video id (11 chars)
        public string title;
        public float duration;  // seconds
    }

    /// <summary>
    /// Wraps the bundled command-line tools in StreamingAssets/Tools:
    ///  - yt-dlp.exe: YouTube search / playlist listing / resolving a video to its audio stream URL
    ///  - ffmpeg.exe: decodes that stream to raw PCM (used by MusicStream)
    /// Every id that reaches a command line goes through IsValidId first (ids arrive from other players).
    /// </summary>
    public static class MusicTools
    {
        public const float MaxSongSeconds = 600f;
        public const int SearchResults = 10;
        public const int MaxPlaylistImport = 200;
        public const int MaxTitleLength = 100;

        /// <summary>Low-bitrate m4a (~50 kbps, ~6 KB/s): seeks quickly for mid-song joins and barely touches bandwidth.</summary>
        const string AudioFormat = "bestaudio[ext=m4a][abr<=80]/bestaudio[ext=m4a]/bestaudio";

        static readonly Regex IdRegex = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
        static readonly HashSet<Process> Running = new();

        public static string YtDlpPath { get; private set; }
        public static string FfmpegPath { get; private set; }
        public static bool Available => YtDlpPath != null && File.Exists(YtDlpPath) && File.Exists(FfmpegPath);

        static bool _init;

        /// <summary>Main thread: cache tool paths, kick off a yt-dlp self-update at most once a day (YouTube changes often).</summary>
        public static void Init()
        {
            if (_init) return;
            _init = true;
            string dir = Path.Combine(Application.streamingAssetsPath, "Tools");
            YtDlpPath = Path.Combine(dir, "yt-dlp.exe");
            FfmpegPath = Path.Combine(dir, "ffmpeg.exe");
            Application.quitting += KillAll;

            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (Available && PlayerPrefs.GetString("sme_ytdlp_update", "") != today)
            {
                PlayerPrefs.SetString("sme_ytdlp_update", today);
                PlayerPrefs.Save();
                Task.Run(() => Run(YtDlpPath, "-U", 120000));
            }
        }

        public static bool IsValidId(string id) => id != null && IdRegex.IsMatch(id);

        public static bool LooksLikeUrl(string s) =>
            s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || s.StartsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("youtu.be", StringComparison.OrdinalIgnoreCase) || s.StartsWith("music.youtube.com", StringComparison.OrdinalIgnoreCase);

        public static string CleanTitle(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return "Untitled";
            var sb = new StringBuilder();
            foreach (char c in t.Trim())
            {
                if (char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= MaxTitleLength) break;
            }
            return sb.Length > 0 ? sb.ToString() : "Untitled";
        }

        public static string FormatTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int s = (int)seconds;
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }

        /// <summary>
        /// Search YouTube, or list a pasted video / playlist URL. Live streams and songs without a known length are left out.
        /// Returns null + an error message on failure.
        /// </summary>
        public static async Task<(List<SongInfo> songs, string error)> Search(string query)
        {
            if (!Available) return (null, "Music tools missing (StreamingAssets/Tools)");
            query = CleanArg(query);
            if (query.Length == 0) return (new List<SongInfo>(), null);
            bool url = LooksLikeUrl(query);
            if (url && !query.StartsWith("http", StringComparison.OrdinalIgnoreCase)) query = "https://" + query;
            string target = url ? query : $"ytsearch{SearchResults}:{query}";
            string args = $"--flat-playlist --ignore-errors --no-warnings --encoding utf-8 --playlist-end {MaxPlaylistImport} " +
                          $"--print \"%(id)s %(duration)s %(title)s\" \"{target}\"";

            var (code, stdout, stderr) = await Task.Run(() => Run(YtDlpPath, args, 90000));
            var list = new List<SongInfo>();
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim('\r', ' ');
                int a = line.IndexOf(' ');
                if (a <= 0) continue;
                int b = line.IndexOf(' ', a + 1);
                if (b <= a) continue;
                string id = line.Substring(0, a);
                if (!IsValidId(id)) continue;
                if (!double.TryParse(line.Substring(a + 1, b - a - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur) || dur <= 0) continue;
                list.Add(new SongInfo { id = id, duration = (float)dur, title = CleanTitle(line.Substring(b + 1)) });
            }
            if (list.Count == 0 && code != 0) return (null, url ? "Couldn't read that link" : "Search failed: " + FirstLine(stderr));
            return (list, null);
        }

        /// <summary>Background-safe: the direct audio stream URL for a video, or null.</summary>
        public static async Task<string> ResolveStream(string id)
        {
            if (!Available || !IsValidId(id)) return null;
            string args = $"-f \"{AudioFormat}\" -g --no-playlist --no-warnings \"https://www.youtube.com/watch?v={id}\"";
            var (_, stdout, _) = await Task.Run(() => Run(YtDlpPath, args, 45000));
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("https://", StringComparison.Ordinal) && line.IndexOf('"') < 0) return line;
            }
            return null;
        }

        public static Process StartProcess(string exe, string args, bool redirectError = true)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = redirectError,
                StandardOutputEncoding = Encoding.UTF8,
            };
            if (redirectError) psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            var p = Process.Start(psi);
            if (p != null) lock (Running) Running.Add(p);
            return p;
        }

        public static void Forget(Process p)
        {
            if (p == null) return;
            lock (Running) Running.Remove(p);
        }

        public static void Kill(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { /* already gone */ }
            Forget(p);
        }

        static void KillAll()
        {
            Process[] all;
            lock (Running) { all = new Process[Running.Count]; Running.CopyTo(all); }
            foreach (var p in all) Kill(p);
        }

        /// <summary>Run a tool to completion (background thread).</summary>
        static (int code, string stdout, string stderr) Run(string exe, string args, int timeoutMs)
        {
            Process p = null;
            try
            {
                p = StartProcess(exe, args);
                if (p == null) return (-1, "", "couldn't start " + Path.GetFileName(exe));
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    Kill(p);
                    return (-1, "", "timed out");
                }
                p.WaitForExit();
                return (p.ExitCode, outTask.Result, errTask.Result);
            }
            catch (Exception e)
            {
                return (-1, "", e.Message);
            }
            finally
            {
                Forget(p);
                p?.Dispose();
            }
        }

        /// <summary>Make user text safe inside a double-quoted Windows argument.</summary>
        static string CleanArg(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
            {
                if (char.IsControl(c) || c == '"') continue;
                sb.Append(c);
                if (sb.Length >= 300) break;
            }
            return sb.ToString().TrimEnd('\\', ' ');
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "unknown error";
            foreach (var l in s.Split('\n'))
            {
                var t = l.Trim();
                if (t.Length > 0) return t.Length > 80 ? t.Substring(0, 80) + "..." : t;
            }
            return "unknown error";
        }
    }
}
