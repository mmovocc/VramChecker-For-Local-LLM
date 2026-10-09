using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GpuVramWidget
{
    // Persistent launcher preferences: %APPDATA%\GpuVramWidget\settings.ini (key=value, lists joined by '|').
    // Rendering options stay on the command line; these survive mode restarts and app updates.
    internal static class AppSettings
    {
        internal static readonly string Directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpuVramWidget");
        private static readonly string FilePath = Path.Combine(Directory, "settings.ini");

        internal static readonly List<string> ProfileDirs = new List<string>();
        internal static readonly List<string> Cleanup = new List<string>();      // process names, no ".exe"
        internal static readonly List<string> RestoreQueue = new List<string>(); // exe paths closed by the last cleanup
        internal static string LastProfile = "";
        internal static int GraceSeconds = 5;
        internal static GlassMaterial Material = GlassMaterial.Dark;
        // VRAM kept free beyond the computed need. Running out does not fail: the driver silently spills into
        // system RAM and prefill/decode drop by an order of magnitude (measured), so this is a safety margin.
        internal static int HeadroomMiB = 512;
        internal static double MinDecodeTps = 30;   // healthy ctx keeps at least this decode speed when full

        internal static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0 || raw.StartsWith("#")) continue;
                    string key = raw.Substring(0, eq).Trim(), value = raw.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "ProfileDirs": Split(value, ProfileDirs); break;
                        case "Cleanup": Split(value, Cleanup); break;
                        case "RestoreQueue": Split(value, RestoreQueue); break;
                        case "LastProfile": LastProfile = value; break;
                        case "GraceSeconds": GraceSeconds = Clamp(value, 1, 60, 5); break;
                        case "Material":
                            Material = value == "light" ? GlassMaterial.Light : value == "legacy" ? GlassMaterial.Legacy : GlassMaterial.Dark; break;
                        case "VramHeadroomMiB": HeadroomMiB = Clamp(value, 0, 8192, 512); break;
                        case "MinDecodeTps": MinDecodeTps = Clamp(value, 1, 500, 30); break;
                    }
                }
            }
            catch { }
        }

        internal static void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var sb = new StringBuilder();
                sb.AppendLine("# GpuVramWidget launcher settings");
                sb.AppendLine("ProfileDirs=" + string.Join("|", ProfileDirs));
                sb.AppendLine("Cleanup=" + string.Join("|", Cleanup));
                sb.AppendLine("RestoreQueue=" + string.Join("|", RestoreQueue));
                sb.AppendLine("LastProfile=" + LastProfile);
                sb.AppendLine("GraceSeconds=" + GraceSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Material=" + (Material == GlassMaterial.Light ? "light" : Material == GlassMaterial.Legacy ? "legacy" : "dark"));
                sb.AppendLine("VramHeadroomMiB=" + HeadroomMiB.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("MinDecodeTps=" + ((int)MinDecodeTps).ToString(CultureInfo.InvariantCulture));
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch { }
        }

        internal static bool IsCleanup(string processName)
        {
            foreach (string n in Cleanup) if (string.Equals(n, processName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static void SetCleanup(string processName, bool include)
        {
            Cleanup.RemoveAll(delegate (string n) { return string.Equals(n, processName, StringComparison.OrdinalIgnoreCase); });
            if (include && !ProcessTree.IsProtected(processName)) Cleanup.Add(processName);
            Save();
        }

        private static void Split(string value, List<string> target)
        {
            target.Clear();
            foreach (string part in value.Split('|')) if (part.Trim().Length > 0) target.Add(part.Trim());
        }

        private static int Clamp(string value, int min, int max, int fallback)
        {
            int n;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return fallback;
            return Math.Max(min, Math.Min(max, n));
        }
    }
}
