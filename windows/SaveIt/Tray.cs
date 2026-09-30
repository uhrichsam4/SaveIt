using System;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace SaveIt
{
    /// <summary>Notification-area icon and its menu (the macOS status item equivalent).</summary>
    internal sealed class Tray : IDisposable
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "SaveIt";

        readonly Island island;
        readonly Dispatcher ui;
        readonly Forms.NotifyIcon icon;
        readonly Forms.ContextMenuStrip menu;
        string? ytdlpVersion;
        bool updating;

        public Tray(Island island)
        {
            this.island = island;
            ui = island.Dispatcher;
            menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
            menu.Opening += (_, e) => { Rebuild(); e.Cancel = false; };
            icon = new Forms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = $"SaveIt {Version}",
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) ui.BeginInvoke(new Action(() => island.Expand(true)));
            };
            island.ShowContextMenu = () => { Rebuild(); menu.Show(Forms.Cursor.Position); };
            RefreshVersion();
        }

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
                int plus = v.IndexOf('+');
                return plus > 0 ? v.Substring(0, plus) : v;
            }
        }

        static Icon LoadIcon()
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SaveIt.ico");
                if (s != null) return new Icon(s, Forms.SystemInformation.SmallIconSize);
            }
            catch { }
            return SystemIcons.Application;
        }

        void Rebuild()
        {
            menu.Items.Clear();
            Add("Open SaveIt", () => ui.BeginInvoke(new Action(() => island.Expand(true)))).Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(new Forms.ToolStripSeparator());
            Add("Choose Download Folder…", () => ui.BeginInvoke(new Action(island.ChooseFolder)));
            Add("Open Download Folder", () => ui.BeginInvoke(new Action(island.RevealFolder)));
            menu.Items.Add(new Forms.ToolStripSeparator());
            var upd = Add(updating ? "Updating yt-dlp…" : "Update yt-dlp", UpdateYtdlp);
            upd.Enabled = !updating;
            var login = Add("Start with Windows", ToggleStartup);
            login.Checked = StartupEnabled;
            var ver = new Forms.ToolStripMenuItem("yt-dlp " + (ytdlpVersion ?? (Tools.Resolve("yt-dlp") == null ? "not installed yet" : "…"))) { Enabled = false };
            menu.Items.Add(ver);
            menu.Items.Add(new Forms.ToolStripSeparator());
            Add("Quit SaveIt", Quit);
        }

        Forms.ToolStripMenuItem Add(string title, Action action)
        {
            var item = new Forms.ToolStripMenuItem(title);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        void RefreshVersion()
        {
            Task.Run(() =>
            {
                var v = Tools.YtdlpVersion();
                ui.BeginInvoke(new Action(() => ytdlpVersion = v));
            });
        }

        void UpdateYtdlp()
        {
            if (updating) return;
            if (island.IsBusy)
            {
                Notify("SaveIt", "Wait for the current download to finish, then update yt-dlp.", false);
                return;
            }
            updating = true;
            Task.Run(() =>
            {
                var (ok, message) = Tools.UpdateYtdlp();
                ui.BeginInvoke(new Action(() =>
                {
                    updating = false;
                    RefreshVersion();
                    Notify(ok ? "yt-dlp" : "Couldn't update yt-dlp", message, !ok);
                }));
            });
        }

        static void Notify(string title, string message, bool warning)
        {
            System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK,
                warning ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information,
                System.Windows.MessageBoxResult.OK, System.Windows.MessageBoxOptions.DefaultDesktopOnly);
        }

        static string ExePath => Environment.ProcessPath ?? "";

        static bool StartupEnabled
        {
            get
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                    return k?.GetValue(RunValue) is string s && s.Length > 0;
                }
                catch { return false; }
            }
        }

        /// <summary>HKCU Run toggle. Off by default; only ever changed by the user from this menu.</summary>
        static void ToggleStartup()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (StartupEnabled) k.DeleteValue(RunValue, false);
                else k.SetValue(RunValue, $"\"{ExePath}\"");
            }
            catch
            {
                System.Media.SystemSounds.Beep.Play();
            }
        }

        void Quit()
        {
            ui.BeginInvoke(new Action(() =>
            {
                island.Shutdown();
                Dispose();
                System.Windows.Application.Current.Shutdown();
            }));
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
        }
    }
}
