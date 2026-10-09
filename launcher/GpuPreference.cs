using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace GpuVramWidget
{
    // Per-app GPU choice, identical to Settings > System > Display > Graphics:
    // HKCU\Software\Microsoft\DirectX\UserGpuPreferences, value = exe path (or a Store app's AUMID),
    // data "GpuPreference=N;...". 1 = power saving (integrated GPU), 2 = high performance; 0/absent = let
    // Windows decide. Takes effect when the app restarts. Only the GpuPreference entry is ever written;
    // other per-app flags in the same value (AutoHDREnable, SwapEffectUpgradeEnable...) are preserved.
    internal static class GpuPreference
    {
        // Overridable only so tests can run against a scratch key instead of the real Windows setting.
        internal static string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

        internal sealed class Entry
        {
            public string Key;        // registry value name: exe path or AUMID
            public string Display;    // file name without extension, or the app id after '!'
            public int Current, AtStartup;
            public bool Packaged, Missing;
            public bool Changed { get { return Current != AtStartup; } }
        }

        private static Dictionary<string, int> _startup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal static DateTime StartupTime { get; private set; }
        internal static string SnapshotFile =Path.Combine(AppSettings.Directory, "gpu-preferences-startup.txt");

        // A process path from the widget: packaged apps live under WindowsApps but are keyed by AUMID,
        // so writing their path would do nothing; those are changed in Windows Settings instead.
        internal static bool Supported(string exePath)
        {
            return !string.IsNullOrEmpty(exePath) && exePath.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0;
        }

        internal static int Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            using (var reg = Registry.CurrentUser.OpenSubKey(Key))
                return reg != null ? PreferenceOf(reg.GetValue(key) as string) : 0;
        }

        internal static void Set(string exePath, int preference)
        {
            if (!Supported(exePath)) throw new InvalidOperationException("Store apps: set this in Windows Settings > Display > Graphics");
            SetRaw(exePath, preference);
        }

        // preference 0 removes only the GpuPreference entry; the value is deleted when nothing else is left.
        internal static void SetRaw(string key, int preference)
        {
            using (var reg = Registry.CurrentUser.CreateSubKey(Key))
            {
                var entries = Parse(reg.GetValue(key) as string ?? "");
                if (preference > 0) entries["GpuPreference"] = preference.ToString();
                else entries.Remove("GpuPreference");
                if (entries.Count == 0) { reg.DeleteValue(key, false); return; }
                var parts = new List<string>();
                foreach (var e in entries) parts.Add(e.Key + "=" + e.Value + ";");
                reg.SetValue(key, string.Concat(parts.ToArray()), RegistryValueKind.String);
            }
        }

        // Every value that carries a GpuPreference (non-app values such as DirectXUserGlobalSettings have none).
        internal static Dictionary<string, int> ReadAll()
        {
            var all = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var reg = Registry.CurrentUser.OpenSubKey(Key))
            {
                if (reg == null) return all;
                foreach (string name in reg.GetValueNames())
                {
                    int pref = PreferenceOf(reg.GetValue(name) as string);
                    if (pref > 0) all[name] = pref;
                }
            }
            return all;
        }

        // Called once at widget start: the state "restore" returns to, also written to a text file as a record.
        internal static void TakeStartupSnapshot()
        {
            try { _startup = ReadAll(); } catch { _startup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); }
            StartupTime = DateTime.Now;
            try
            {
                Directory.CreateDirectory(AppSettings.Directory);
                var sb = new StringBuilder();
                sb.AppendLine("# GPU preferences when GpuVramWidget started, " + StartupTime.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("# 1 = integrated (power saving), 2 = discrete (high performance); apps not listed use the Windows default.");
                foreach (var e in _startup) sb.AppendLine(e.Value + "\t" + e.Key);
                File.WriteAllText(SnapshotFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // Union of apps that have a preference now or had one at startup.
        internal static List<Entry> Entries()
        {
            var current = ReadAll();
            var keys = new HashSet<string>(current.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(_startup.Keys);
            var list = new List<Entry>();
            foreach (string key in keys)
            {
                int now, then;
                current.TryGetValue(key, out now); _startup.TryGetValue(key, out then);
                bool packaged = key.IndexOf('!') >= 0 && key.IndexOf('\\') < 0;
                list.Add(new Entry { Key = key, Display = DisplayName(key), Current = now, AtStartup = then,
                    Packaged = packaged, Missing = !packaged && !File.Exists(key) });
            }
            list.Sort(delegate (Entry a, Entry b)
            {
                if (a.Changed != b.Changed) return a.Changed ? -1 : 1;
                return string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        internal static int ChangedSinceStartup()
        {
            int n = 0;
            foreach (var e in Entries()) if (e.Changed) n++;
            return n;
        }

        // Puts every app back to the preference it had when the widget started. Returns how many changed.
        internal static int RestoreStartup()
        {
            int n = 0;
            foreach (var e in Entries())
                if (e.Changed) { SetRaw(e.Key, e.AtStartup); n++; }
            return n;
        }

        internal static void RevertToStartup(string key)
        {
            int then;
            _startup.TryGetValue(key, out then);
            SetRaw(key, then);
        }

        internal static string DisplayName(string key)
        {
            int bang = key.LastIndexOf('!');
            if (bang >= 0 && key.IndexOf('\\') < 0) return key.Substring(bang + 1);   // AUMID: Publisher.App_hash!AppId
            try { return Path.GetFileNameWithoutExtension(key); } catch (ArgumentException) { return key; }
        }

        private static int PreferenceOf(string data)
        {
            string value; int preference;
            return data != null && Parse(data).TryGetValue("GpuPreference", out value) && int.TryParse(value, out preference) ? preference : 0;
        }

        private static Dictionary<string, string> Parse(string data)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in data.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) entries[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            return entries;
        }
    }
}
