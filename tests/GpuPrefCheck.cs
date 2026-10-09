using System;
using System.IO;
using Microsoft.Win32;
using GpuVramWidget;

// GPU preference snapshot/restore against a scratch registry key (HKCU\Software\GpuVramWidgetTest),
// never the real Windows setting; the key and the temp snapshot file are removed afterwards.
internal static class GpuPrefCheck
{
    private const string TestKey = @"Software\GpuVramWidgetTest\UserGpuPreferences";
    private static int _failures;

    private static void Expect(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) _failures++; }

    private static string Raw(string name)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(TestKey)) return k != null ? k.GetValue(name) as string : null;
    }

    private static int Main()
    {
        GpuPreference.Key = TestKey;
        GpuPreference.SnapshotFile = Path.Combine(Path.GetTempPath(), "gpupref-check.txt");
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\GpuVramWidgetTest", false);
            using (var k = Registry.CurrentUser.CreateSubKey(TestKey))
            {
                k.SetValue(@"C:\Apps\A.exe", "GpuPreference=1;AutoHDREnable=2097;");
                k.SetValue(@"C:\Apps\B.exe", "GpuPreference=2;");
                k.SetValue(@"C:\Apps\C.exe", "AutoHDREnable=1;");
                k.SetValue("Vendor.App_hash!Main", "GpuPreference=1;");
                k.SetValue("DirectXUserGlobalSettings", "SwapEffectUpgradeEnable=1;");
            }
            GpuPreference.TakeStartupSnapshot();
            Expect(File.ReadAllText(GpuPreference.SnapshotFile).Contains("1\tC:\\Apps\\A.exe"), "startup snapshot written");
            Expect(GpuPreference.Entries().Count == 3, "lists only values with a GpuPreference");
            Expect(GpuPreference.Entries().Exists(e => e.Packaged && e.Display == "Main"), "store app shown by its AUMID app id");

            GpuPreference.SetRaw(@"C:\Apps\A.exe", 0);      // back to default, keep AutoHDR
            GpuPreference.SetRaw(@"C:\Apps\B.exe", 1);
            GpuPreference.SetRaw(@"C:\Apps\D.exe", 2);      // new entry
            Expect(Raw(@"C:\Apps\A.exe") == "AutoHDREnable=2097;", "default removes only GpuPreference");
            Expect(GpuPreference.ChangedSinceStartup() == 3, "3 changes detected");
            var first = GpuPreference.Entries()[0];
            Expect(first.Changed, "changed apps sorted first");

            Expect(GpuPreference.RestoreStartup() == 3, "restore reports 3");
            Expect(GpuPreference.Get(@"C:\Apps\A.exe") == 1 && Raw(@"C:\Apps\A.exe").Contains("AutoHDREnable=2097"), "A back to iGPU, AutoHDR kept");
            Expect(GpuPreference.Get(@"C:\Apps\B.exe") == 2, "B back to dGPU");
            Expect(Raw(@"C:\Apps\D.exe") == null, "D (added after start) removed");
            Expect(Raw(@"C:\Apps\C.exe") == "AutoHDREnable=1;" && Raw("DirectXUserGlobalSettings") == "SwapEffectUpgradeEnable=1;", "unrelated values untouched");
            Expect(GpuPreference.ChangedSinceStartup() == 0, "nothing left to restore");

            GpuPreference.SetRaw("Vendor.App_hash!Main", 2);
            GpuPreference.RevertToStartup("Vendor.App_hash!Main");
            Expect(GpuPreference.Get("Vendor.App_hash!Main") == 1, "single revert");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\GpuVramWidgetTest", false);
            try { File.Delete(GpuPreference.SnapshotFile); } catch { }
        }
        Console.WriteLine(_failures == 0 ? "PASS: GPU preference snapshot and restore" : "FAILED: " + _failures);
        return _failures == 0 ? 0 : 1;
    }
}
