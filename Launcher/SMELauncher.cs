// Sudden Mage Encounter launcher: keeps the game up to date from GitHub Releases, then starts it.
// Built by Tools/release.ps1 (Unity's Roslyn against .NET Framework 4, which every Windows 10/11 PC has).
//
// The latest release carries three files:
//   version.txt         "game=<n>" and "launcher=<n>"
//   SME-Windows.zip     the whole player build
//   SMELauncher.exe     this program
// The game lives in %LOCALAPPDATA%\SuddenMageEncounter\Game; the launcher can sit anywhere.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace SuddenMageEncounter.Launcher
{
    static class Program
    {
        // Bump when this file changes: players' launchers replace themselves when the release has a higher one.
        public const int LauncherVersion = 1;

        public const string Repo = "grandrx10/SME";
        public const string ExeName = "SuddenMageEncounter.exe";
        public static string Latest { get { return "https://github.com/" + Repo + "/releases/latest/download/"; } }

        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm(args));
        }
    }

    class LauncherForm : Form
    {
        static readonly Color Back = Color.FromArgb(16, 18, 40);
        static readonly Color Arcane = Color.FromArgb(70, 110, 255);

        readonly string[] _args;
        readonly Label _status;
        readonly Bar _bar;
        readonly Button _retry;
        readonly string _home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuddenMageEncounter");

        string GameDir { get { return Path.Combine(_home, "Game"); } }
        string VersionFile { get { return Path.Combine(_home, "version.txt"); } }

        public LauncherForm(string[] args)
        {
            _args = args;
            Text = "Sudden Mage Encounter";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 120);
            BackColor = Back;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var title = new Label
            {
                Text = "SUDDEN MAGE ENCOUNTER",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(0, 10, 420, 34),
            };
            _bar = new Bar { Bounds = new Rectangle(30, 54, 360, 16), Fill = Arcane };
            _status = new Label
            {
                ForeColor = Color.FromArgb(170, 185, 230),
                Font = new Font("Segoe UI", 9f),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(0, 78, 420, 22),
            };
            _retry = new Button
            {
                Text = "↻",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Arcane,
                FlatStyle = FlatStyle.Flat,
                Bounds = new Rectangle(190, 108, 40, 30),
                Visible = false,
            };
            _retry.FlatAppearance.BorderSize = 0;
            _retry.Click += delegate { _retry.Visible = false; ClientSize = new Size(420, 120); Start(); };
            Controls.AddRange(new Control[] { title, _bar, _status, _retry });

            Shown += delegate { Start(); };
        }

        void Start()
        {
            var t = new Thread(Run) { IsBackground = true };
            t.Start();
        }

        // ---------- the update, on a worker thread ----------

        void Run()
        {
            try
            {
                CleanOldLauncher();
                Say("Checking for updates...", -1);

                Dictionary<string, string> remote = null;
                try { remote = ParseVersion(GetString(Program.Latest + "version.txt")); }
                catch (Exception) { /* offline, or GitHub down: play what's installed */ }

                if (remote != null)
                {
                    int launcher;
                    string s;
                    if (remote.TryGetValue("launcher", out s) && int.TryParse(s, out launcher) && launcher > Program.LauncherVersion
                        && UpdateSelf())
                        return;

                    string game;
                    if (remote.TryGetValue("game", out game) && game != InstalledVersion())
                        InstallGame(game);
                }

                string exe = FindExe();
                if (exe == null)
                {
                    Fail(remote == null ? "Can't reach GitHub. Check your connection." : "Install failed.");
                    return;
                }
                Say("Starting...", 1);
                Process.Start(new ProcessStartInfo(exe, QuoteArgs(_args)) { WorkingDirectory = Path.GetDirectoryName(exe) });
                Ui(Close);
            }
            catch (Exception e)
            {
                // something broke mid-update: still play the old version if there is one
                string exe = FindExe();
                if (exe != null && !(e is GameRunningException))
                {
                    Process.Start(new ProcessStartInfo(exe, QuoteArgs(_args)) { WorkingDirectory = Path.GetDirectoryName(exe) });
                    Ui(Close);
                }
                else Fail(e is GameRunningException ? "Close the game first, then retry." : "Update failed: " + e.Message);
            }
        }

        void InstallGame(string version)
        {
            Directory.CreateDirectory(_home);
            string zip = Path.Combine(_home, "download.zip");
            string fresh = Path.Combine(_home, "Game.new");
            string old = Path.Combine(_home, "Game.old");

            Say("Downloading update...", 0);
            Download(Program.Latest + "SME-Windows.zip", zip);

            Say("Installing...", -1);
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            ZipFile.ExtractToDirectory(zip, fresh);
            File.Delete(zip);

            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (Directory.Exists(GameDir))
            {
                try { Directory.Move(GameDir, old); }
                catch (Exception) { Directory.Delete(fresh, true); throw new GameRunningException(); }
            }
            Directory.Move(fresh, GameDir);
            File.WriteAllText(VersionFile, version);
            try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
        }

        /// <summary>Swap in the newer launcher and restart it. False if this folder can't be written.</summary>
        bool UpdateSelf()
        {
            string self = Application.ExecutablePath;
            string fresh = self + ".new";
            string old = self + ".old";
            try
            {
                Say("Updating launcher...", 0);
                Download(Program.Latest + "SMELauncher.exe", fresh);
                if (File.Exists(old)) File.Delete(old);
                File.Move(self, old); // Windows lets a running exe be renamed, just not overwritten
                File.Move(fresh, self);
            }
            catch (Exception)
            {
                try { File.Delete(fresh); } catch { }
                if (!File.Exists(self) && File.Exists(old)) File.Move(old, self);
                return false;
            }
            Process.Start(new ProcessStartInfo(self, QuoteArgs(_args)));
            Ui(Close);
            return true;
        }

        void CleanOldLauncher()
        {
            string old = Application.ExecutablePath + ".old";
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { Thread.Sleep(200); } // the previous launcher may still be exiting
            }
        }

        string InstalledVersion()
        {
            try { return File.Exists(VersionFile) && FindExe() != null ? File.ReadAllText(VersionFile).Trim() : null; }
            catch { return null; }
        }

        string FindExe()
        {
            if (!Directory.Exists(GameDir)) return null;
            string exe = Path.Combine(GameDir, Program.ExeName);
            if (File.Exists(exe)) return exe;
            foreach (var f in Directory.GetFiles(GameDir, "*.exe"))
                if (!Path.GetFileName(f).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        // ---------- HTTP ----------

        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "SME-Launcher";
            req.AllowAutoRedirect = true; // github.com redirects release assets to its file host
            req.Timeout = 10000;
            req.ReadWriteTimeout = 30000;
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            return req;
        }

        static string GetString(string url)
        {
            using (var res = Request(url).GetResponse())
            using (var r = new StreamReader(res.GetResponseStream()))
                return r.ReadToEnd();
        }

        void Download(string url, string path)
        {
            string part = path + ".part";
            using (var res = (HttpWebResponse)Request(url).GetResponse())
            using (var input = res.GetResponseStream())
            using (var output = File.Create(part))
            {
                long total = res.ContentLength, done = 0;
                var buf = new byte[1 << 16];
                int n, lastShown = -1;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    output.Write(buf, 0, n);
                    done += n;
                    if (total > 0)
                    {
                        int pct = (int)(done * 100 / total);
                        if (pct != lastShown)
                        {
                            lastShown = pct;
                            Say(string.Format("Downloading update... {0} / {1} MB", done >> 20, total >> 20), done / (float)total);
                        }
                    }
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
        }

        static Dictionary<string, string> ParseVersion(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (var line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d.ContainsKey("game") ? d : null;
        }

        static string QuoteArgs(string[] args)
        {
            var parts = new List<string>();
            foreach (var a in args) parts.Add(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
            return string.Join(" ", parts);
        }

        // ---------- UI from the worker ----------

        void Ui(Action a)
        {
            if (IsDisposed) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        /// <param name="progress">0..1, or -1 for "busy"</param>
        void Say(string text, float progress)
        {
            Ui(delegate { _status.Text = text; _bar.Value = progress; });
        }

        void Fail(string text)
        {
            Ui(delegate { ClientSize = new Size(420, 150); _status.Text = text; _bar.Value = 0; _retry.Visible = true; });
        }

        class GameRunningException : Exception { }
    }

    /// <summary>A flat progress bar; a negative value sweeps back and forth.</summary>
    class Bar : Control
    {
        float _value;
        float _sweep;
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 16 };
        public Color Fill;

        public Bar()
        {
            DoubleBuffered = true;
            _timer.Tick += delegate { _sweep += 0.02f; if (_value < 0) Invalidate(); };
            _timer.Start();
        }

        public float Value { get { return _value; } set { _value = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(8, 8, 20));
            int w = Width, h = Height;
            using (var b = new SolidBrush(Fill))
            {
                if (_value >= 0) g.FillRectangle(b, 0, 0, (int)(w * Math.Min(1f, _value)), h);
                else
                {
                    float t = (float)(0.5 + 0.5 * Math.Sin(_sweep * 3));
                    int seg = w / 4;
                    g.FillRectangle(b, (int)((w - seg) * t), 0, seg, h);
                }
            }
            using (var ink = new Pen(Color.Black, 2)) g.DrawRectangle(ink, 1, 1, w - 2, h - 2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
