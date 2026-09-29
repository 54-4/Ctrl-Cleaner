using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CtrlCleaner
{
    public partial class MainWindow : Window
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenEventLog(string? lpUNCServerName, string lpSourceName, out IntPtr phEventLog);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ClearEventLog(IntPtr hEventLog, string? lpBackupFileName);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseEventLog(IntPtr hEventLog);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI   = 0x00000002;
        private const uint SHERB_NOSOUND        = 0x00000004;

        private bool _cleaning = false;
        private string? _tempVideoPath;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += (_, _) =>
            {
                BgVideo.Stop();
                BgVideo.Source = null;
                if (_tempVideoPath != null)
                    try { File.Delete(_tempVideoPath); } catch { }
            };
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("CtrlCleaner.Assets.ctrl_1.mp4");
                if (stream != null)
                {
                    _tempVideoPath = Path.Combine(Path.GetTempPath(), $"ctrl_bg_{Guid.NewGuid():N}.mp4");
                    using var fs = new FileStream(_tempVideoPath, FileMode.Create, FileAccess.Write);
                    stream.CopyTo(fs);
                    BgVideo.Source = new Uri(_tempVideoPath, UriKind.Absolute);
                    BgVideo.MediaEnded += (s, _) => { BgVideo.Position = TimeSpan.Zero; BgVideo.Play(); };
                    BgVideo.Play();
                }
            }
            catch { }
        }

        private async void CleanBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_cleaning) return;
            _cleaning = true;
            CleanBtn.IsEnabled = false;
            StatusLabel.Text = "[ CLEANING... ]";
            ProgressBar.Value = 0;
            ClearLog();

            await Task.Run(() => RunAllClean());

            StatusLabel.Text = "[ DONE ]";
            ProgressBar.Value = 100;
            Log("");
            Log("╔══════════════════════════════════════════════╗", LogColor.Header);
            Log("║             CLEANING COMPLETE                ║", LogColor.Header);
            Log("╚══════════════════════════════════════════════╝", LogColor.Header);
            Log("Recommend restarting for best results.", LogColor.Info);

            CleanBtn.IsEnabled = true;
            _cleaning = false;
        }

        private void RunAllClean()
        {
            var steps = new (string label, Action action, double weight)[]
            {
                ("Windows\\Temp",                       CleanWindowsTemp,                                                                                       6),
                ("Prefetch",                            CleanPrefetch,                                                                                          5),
                ("User Temp",                           CleanUserTemp,                                                                                          6),
                ("Log files",                           CleanLogFiles,                                                                                          5),
                ("DNS Cache",                           FlushDns,                                                                                               2),
                ("Thumbnail Cache",                     CleanThumbnails,                                                                                        2),
                ("Recycle Bin",                         CleanRecycleBin,                                                                                        2),
                ("Windows Event Logs",                  CleanEventLogs,                                                                                         6),
                ("LocalAppData\\Temp",                  () => CleanFolder("LocalAppData\\Temp",          GetLocalAppData("Temp")),                              3),
                ("LocalAppData\\Cache",                 () => CleanFolder("LocalAppData\\Cache",         GetLocalAppData("cache")),                             2),
                ("LocalAppData\\CrashDumps",            () => CleanFolder("CrashDumps",                  GetLocalAppData("CrashDumps")),                        2),
                ("LocalAppData\\D3DSCache",             () => CleanFolder("D3DSCache",                   GetLocalAppData("D3DSCache")),                         2),
                ("WebCache",                            () => CleanFolder("WebCache",                     GetLocalAppData(@"Microsoft\Windows\WebCache")),        2),
                ("IE Temp Files",                       () => CleanFolder("IE Temp",                     GetLocalAppData(@"Microsoft\Windows\Temporary Internet Files")), 2),
                ("IE History",                          () => CleanFolder("IE History",                  GetLocalAppData(@"Microsoft\Windows\History")),         2),
                ("Explorer Cache",                      () => CleanFolder("Explorer Cache",               GetLocalAppData(@"Microsoft\Windows\Explorer")),        2),
                ("INetCache",                           () => CleanFolder("INetCache",                    GetLocalAppData(@"Microsoft\Windows\INetCache")),       2),
                ("INetCookies",                         () => CleanFolder("INetCookies",                  GetLocalAppData(@"Microsoft\Windows\INetCookies")),     2),
                ("WER LocalAppData",                    () => CleanFolder("WER",                          GetLocalAppData(@"Microsoft\Windows\WER")),             2),
                ("Notifications",                       () => CleanFolder("Notifications",                GetLocalAppData(@"Microsoft\Windows\Notifications")),   2),
                ("Clipboard Cache",                     () => CleanFolder("Clipboard",                    GetLocalAppData(@"Microsoft\Windows\Clipboard")),       2),
                ("PowerShell History",                  () => CleanFolder("PS History",                   GetLocalAppData(@"Microsoft\Windows\PowerShell")),      2),
                ("Recent Files (Local)",                () => CleanFolder("Recent Local",                 GetLocalAppData(@"Microsoft\Windows\Recent")),          2),
                ("Recent Files (Roaming)",              () => CleanFolder("Recent Roaming",               GetRoamingAppData(@"Microsoft\Windows\Recent")),        2),
                ("Cookies",                             () => CleanFolder("Cookies",                      GetRoamingAppData(@"Microsoft\Windows\Cookies")),       2),
                ("PSReadLine",                          () => CleanFolder("PSReadLine",                   GetRoamingAppData(@"Microsoft\Windows\PowerShell\PSReadLine")), 2),
                ("WER ProgramData",                     () => CleanFolder("WER PD",                       GetProgramData(@"Microsoft\Windows\WER")),              2),
                ("EventLogs ProgramData",               () => CleanFolder("EventLogs PD",                 GetProgramData(@"Microsoft\Windows\EventLogs")),        2),
                ("Diagnosis",                           () => CleanFolder("Diagnosis",                    GetProgramData(@"Microsoft\Windows\Diagnosis")),        2),
                ("Windows\\Logs",                       () => CleanFolder("WinLogs",                      GetSystemRoot("Logs")),                                 2),
                ("Windows\\Panther",                    () => CleanFolder("Panther",                      GetSystemRoot("Panther")),                              2),
                ("Windows\\Debug",                      () => CleanFolder("Debug",                        GetSystemRoot("Debug")),                                2),
                ("Windows\\Minidump",                   () => CleanFolder("Minidump",                     GetSystemRoot("Minidump")),                             2),
                ("System32\\LogFiles",                  () => CleanFolder("LogFiles",                     GetSystemRoot(@"System32\LogFiles")),                   2),
                ("winevt\\Logs",                        () => CleanFolder("winevt Logs",                  GetSystemRoot(@"System32\winevt\Logs")),                2),
                ("SRU",                                 () => CleanFolder("SRU",                          GetSystemRoot(@"System32\sru")),                        2),
                ("Tasks",                               () => CleanFolder("Tasks",                        GetSystemRoot(@"System32\Tasks")),                      2),
                ("SoftwareDistribution\\Download",      CleanWuDownload,                                                                                        3),
                ("Firefox Profiles",                    CleanFirefoxProfiles,                                                                                   3),
                ("Registry MRUs",                       CleanRegistryKeys,                                                                                      5),
                ("WinRAR History",                      CleanWinRarRegistry,                                                                                    1),
                ("JumpLists (Auto)",                    CleanJumpListsAuto,                                                                                     2),
                ("JumpLists (Custom)",                  CleanJumpListsCustom,                                                                                   2),
                ("AppCompatCache",                      CleanAppCompatCache,                                                                                    2),
                ("USN Journal",                         CleanUsnJournal,                                                                                        2),
            };

            double totalWeight = steps.Sum(s => s.weight);
            double done = 0;

            foreach (var (label, action, weight) in steps)
            {
                try { action(); }
                catch (Exception ex) { LogErr(label, ex.Message); }
                done += weight;
                Dispatcher.Invoke(() => ProgressBar.Value = done / totalWeight * 100.0);
            }
        }

        private void CleanWindowsTemp()
        {
            string dir = GetSystemRoot("Temp");
            LogStep("Windows\\Temp");
            CleanFolderContents("WinTemp", dir);
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
        }

        private void CleanPrefetch()
        {
            string pfDir = GetSystemRoot("Prefetch");
            LogStep("Prefetch");
            if (!Directory.Exists(pfDir)) { LogSkip("Prefetch"); return; }
            int count = 0;
            foreach (string pattern in new[] { "*.pf", "*.db" })
                foreach (string f in SafeGetFiles(pfDir, pattern))
                    if (SafeDeleteFile(f)) count++;
            LogOk($"Prefetch — {count} files removed");
        }

        private void CleanUserTemp()
        {
            string tmp = Path.GetTempPath();
            LogStep("User Temp");
            CleanFolderContents("UserTemp", tmp);
            try { if (!Directory.Exists(tmp)) Directory.CreateDirectory(tmp); } catch { }
        }

        private void CleanLogFiles()
        {
            LogStep("*.log files");
            string[] roots = { GetSystemRoot(""), GetProgramData(""), GetLocalAppData("") };
            int count = 0;
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string f in SafeGetFiles(root, "*.log", recursive: true))
                    if (SafeDeleteFile(f)) count++;
            }
            LogOk($"*.log — {count} files removed");
        }

        private void FlushDns()
        {
            LogStep("DNS Cache");
            RunCmd("ipconfig", "/flushdns");
            LogOk("DNS Cache flushed");
        }

        private void CleanThumbnails()
        {
            LogStep("Thumbnail cache");
            string dir = GetLocalAppData(@"Microsoft\Windows\Explorer");
            if (!Directory.Exists(dir)) { LogSkip("Thumbnails"); return; }
            int count = 0;
            foreach (string f in SafeGetFiles(dir, "thumbcache_*.db"))
                if (SafeDeleteFile(f)) count++;
            LogOk($"Thumbnails — {count} files removed");
        }

        private void CleanRecycleBin()
        {
            LogStep("Recycle Bin");
            SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            LogOk("Recycle Bin emptied");
        }

        private void CleanEventLogs()
        {
            LogStep("Windows Event Logs");
            foreach (string log in new[] { "Application", "System", "Security", "Setup" })
            {
                try
                {
                    if (OpenEventLog(null, log, out IntPtr h) && h != IntPtr.Zero)
                    {
                        ClearEventLog(h, null);
                        CloseEventLog(h);
                    }
                }
                catch { }
            }
            try
            {
                var result = RunCmdCapture("wevtutil", "el");
                foreach (string line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string name = line.Trim();
                    if (!string.IsNullOrEmpty(name))
                        RunCmd("wevtutil", $"cl \"{name}\"");
                }
            }
            catch { }
            CleanFolderContents("winevt", GetSystemRoot(@"System32\winevt\Logs"));
            LogOk("Event Logs cleared");
        }

        private void CleanWuDownload()
        {
            LogStep("SoftwareDistribution\\Download");
            RunCmdTimeout("sc", "stop wuauserv", 4000);
            RunCmdTimeout("sc", "stop bits",     4000);
            RunCmdTimeout("sc", "stop cryptsvc", 4000);
            Thread.Sleep(1000);
            CleanFolderContents("WU Download", GetSystemRoot(@"SoftwareDistribution\Download"));
            RunCmdTimeout("sc", "start wuauserv", 3000);
            RunCmdTimeout("sc", "start bits",     3000);
            RunCmdTimeout("sc", "start cryptsvc", 3000);
            LogOk("WU Download cleaned");
        }

        private void CleanFirefoxProfiles()
        {
            LogStep("Firefox profiles");
            var ffDirs = new[]
            {
                Path.Combine(GetRoamingAppData(""), "Mozilla", "Firefox", "Profiles"),
                Path.Combine(GetLocalAppData(""),   "Mozilla", "Firefox", "Profiles"),
            };
            string[] targets = {
                "places.sqlite", "formhistory.sqlite", "permissions.sqlite",
                "content-prefs.sqlite", "cookies.sqlite", "cookies.sqlite-wal"
            };
            string[] targetDirs = { "cache", "cache1", "cache2", "cache3", "storage" };
            int count = 0;
            foreach (string ffRoot in ffDirs)
            {
                if (!Directory.Exists(ffRoot)) continue;
                foreach (string profile in Directory.GetDirectories(ffRoot))
                {
                    if (!Path.GetFileName(profile).Contains(".default")) continue;
                    foreach (string t in targets)
                    {
                        string fp = Path.Combine(profile, t);
                        if (File.Exists(fp) && SafeDeleteFile(fp)) count++;
                    }
                    foreach (string td in targetDirs)
                    {
                        string dp = Path.Combine(profile, td);
                        if (Directory.Exists(dp) && SafeDeleteDir(dp)) count++;
                    }
                }
            }
            LogOk($"Firefox — {count} items removed");
        }

        private void CleanFolder(string label, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            LogStep(label);
            CleanFolderContents(label, path);
        }

        private void CleanRegistryKeys()
        {
            LogStep("Registry MRUs");
            var keys = new[]
            {
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU",
                @"HKCU\Software\Microsoft\Windows\Shell\BagMRU",
                @"HKCU\Software\Microsoft\Windows\Shell\Bags",
                @"HKCU\Software\Microsoft\Windows\ShellNoRoam\BagMRU",
                @"HKCU\Software\Microsoft\Windows\ShellNoRoam\Bags",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedMRU",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\CIDSizeMRU",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\AutomaticDestinations",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\CustomDestinations",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Map Network Drive MRU",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Search",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Search\RecentApps",
                @"HKCU\Software\Microsoft\Command Processor",
                @"HKCU\Software\Microsoft\PowerShell",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RunHistory",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery",
                @"HKCU\Software\Microsoft\Terminal Server Client\Default",
                @"HKCU\Software\Microsoft\Terminal Server Client\Servers",
                @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentApps",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Search\SearchHistory",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StreamMRU",
                @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData",
            };
            int count = keys.Count(DeleteRegistryKey);
            LogOk($"Registry — {count} keys cleaned");
        }

        private void CleanWinRarRegistry()
        {
            LogStep("WinRAR history");
            var keys = new[]
            {
                @"HKCU\Software\WinRAR\ArcHistory",
                @"HKCU\Software\WinRAR\DialogEditHistory\ArcName",
                @"HKCU\Software\WinRAR\DialogEditHistory\DictSize",
                @"HKCU\Software\WinRAR\DialogEditHistory\ExtrPath",
            };
            int count = keys.Count(DeleteRegistryKey);
            LogOk($"WinRAR registry — {count} keys cleaned");
        }

        private void CleanJumpListsAuto()
        {
            LogStep("JumpLists AutomaticDestinations");
            CleanFolderContents("JumpLists-Auto", GetRoamingAppData(@"Microsoft\Windows\Recent\AutomaticDestinations"));
            LogOk("JumpLists AutomaticDestinations cleared");
        }

        private void CleanJumpListsCustom()
        {
            LogStep("JumpLists CustomDestinations");
            CleanFolderContents("JumpLists-Custom", GetRoamingAppData(@"Microsoft\Windows\Recent\CustomDestinations"));
            LogOk("JumpLists CustomDestinations cleared");
        }

        private void CleanAppCompatCache()
        {
            LogStep("AppCompatCache");
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache", writable: true);
                if (key != null)
                {
                    key.DeleteValue("AppCompatCache", throwOnMissingValue: false);
                    LogOk("AppCompatCache cleared");
                }
                else LogSkip("AppCompatCache (no access)");
            }
            catch (Exception ex) { LogErr("AppCompatCache", ex.Message); }
        }

        private void CleanUsnJournal()
        {
            LogStep("USN Journal");
            RunCmd("fsutil", "usn deletejournal /D /N C:");
            LogOk("USN Journal cleared");
        }

        private void CleanFolderContents(string label, string path)
        {
            if (!Directory.Exists(path)) { LogSkip(label); return; }
            int count = DeleteFolderRecursive(path, topLevel: true);
            LogOk($"{label} — {count} items removed");
        }

        private int DeleteFolderRecursive(string path, bool topLevel = false)
        {
            int count = 0;
            string[] files;
            try { files = Directory.GetFiles(path); } catch { files = Array.Empty<string>(); }
            foreach (string f in files)
            {
                if (_tempVideoPath != null &&
                    string.Equals(f, _tempVideoPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SafeDeleteFile(f)) count++;
            }
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(path); } catch { return count; }
            foreach (string d in subdirs)
            {
                if (topLevel)
                {
                    if (!SafeDeleteDir(d))
                        count += DeleteFolderRecursive(d, topLevel: false);
                    else
                        count++;
                }
                else
                {
                    count += DeleteFolderRecursive(d, topLevel: false);
                    try { Directory.Delete(d, false); count++; } catch { }
                }
            }
            return count;
        }

        private bool SafeDeleteFile(string path)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        private bool SafeDeleteDir(string path)
        {
            try { Directory.Delete(path, recursive: true); return true; }
            catch { return false; }
        }

        private string[] SafeGetFiles(string dir, string pattern, bool recursive = false)
        {
            try
            {
                return Directory.GetFiles(dir, pattern,
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            }
            catch { return Array.Empty<string>(); }
        }

        private bool DeleteRegistryKey(string fullKey)
        {
            try
            {
                var (hive, sub) = ParseRegKey(fullKey);
                if (hive == null) return false;
                hive.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                return true;
            }
            catch { return false; }
        }

        private static (RegistryKey? hive, string sub) ParseRegKey(string fullKey)
        {
            int idx = fullKey.IndexOf('\\');
            if (idx < 0) return (null, "");
            string root = fullKey[..idx].ToUpperInvariant();
            string sub  = fullKey[(idx + 1)..];
            RegistryKey? hive = root switch
            {
                "HKCU" or "HKEY_CURRENT_USER"   => Registry.CurrentUser,
                "HKLM" or "HKEY_LOCAL_MACHINE"  => Registry.LocalMachine,
                "HKCR" or "HKEY_CLASSES_ROOT"   => Registry.ClassesRoot,
                "HKU"  or "HKEY_USERS"          => Registry.Users,
                "HKCC" or "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
                _ => null
            };
            return (hive, sub);
        }

        private static string GetLocalAppData(string sub) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), sub);

        private static string GetRoamingAppData(string sub) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), sub);

        private static string GetProgramData(string sub) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), sub);

        private static string GetSystemRoot(string sub) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), sub);

        private static void RunCmd(string exe, string args)
        {
            try
            {
                var p = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe, Arguments = args,
                        CreateNoWindow = true, UseShellExecute = false
                    }
                };
                p.Start();
                p.WaitForExit(5000);
            }
            catch { }
        }

        private static void RunCmdTimeout(string exe, string args, int timeoutMs)
        {
            try
            {
                var p = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe, Arguments = args,
                        CreateNoWindow = true, UseShellExecute = false
                    }
                };
                p.Start();
                p.WaitForExit(timeoutMs);
                if (!p.HasExited) try { p.Kill(); } catch { }
            }
            catch { }
        }

        private static string RunCmdCapture(string exe, string args)
        {
            try
            {
                var p = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe, Arguments = args,
                        CreateNoWindow = true, UseShellExecute = false,
                        RedirectStandardOutput = true
                    }
                };
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                return output;
            }
            catch { return ""; }
        }

        private enum LogColor { Normal, Ok, Warn, Err, Info, Header, Skip }

        private void Log(string msg, LogColor color = LogColor.Normal)
        {
            Dispatcher.Invoke(() =>
            {
                string prefix = color switch
                {
                    LogColor.Ok     => "[+] ",
                    LogColor.Warn   => "[!] ",
                    LogColor.Err    => "[x] ",
                    LogColor.Info   => "[*] ",
                    LogColor.Skip   => "[-] ",
                    _               => "    ",
                };
                string colorCode = color switch
                {
                    LogColor.Ok     => "#FF6666",
                    LogColor.Warn   => "#FF8800",
                    LogColor.Err    => "#FF2222",
                    LogColor.Info   => "#FF4444",
                    LogColor.Skip   => "#661111",
                    LogColor.Header => "#FF2222",
                    _               => "#CC3333",
                };
                var run = new System.Windows.Documents.Run($"{prefix}{msg}\n")
                {
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorCode)!)
                };
                LogBox.Inlines.Add(run);
                LogScroller.ScrollToEnd();
            });
        }

        private void LogStep(string label)  => Log($"Cleaning: {label}...", LogColor.Info);
        private void LogOk(string msg)      => Log(msg, LogColor.Ok);
        private void LogErr(string l, string e) => Log($"FAILED {l}: {e}", LogColor.Err);
        private void LogSkip(string label)  => Log($"Skipped: {label}", LogColor.Skip);

        private void ClearLog() => Dispatcher.Invoke(() => LogBox.Inlines.Clear());
    }
}
