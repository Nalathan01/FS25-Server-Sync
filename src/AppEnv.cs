using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

internal sealed class AppError : Exception
{
    public readonly string Code;
    public readonly string Arg;

    public AppError(string code, string arg) : base(code + (arg == null ? "" : ": " + arg))
    {
        Code = code;
        Arg = arg;
    }
}

internal static class AppEnv
{
    public static string DataDir;
    public static string SettingsDir;
    public static string ModsRoot;
    public static string MapsRoot;
    public static string GameSettingsPath;
    public static string GameInstallPath;

    public static readonly JavaScriptSerializer Json = CreateSerializer();

    private static JavaScriptSerializer CreateSerializer()
    {
        JavaScriptSerializer s = new JavaScriptSerializer();
        s.MaxJsonLength = int.MaxValue;
        s.RecursionLimit = 256;
        return s;
    }

    private static string gameDocumentsDir;

    public static string GameDocumentsDir
    {
        get
        {
            if (gameDocumentsDir == null) gameDocumentsDir = FindGameDocumentsDir();
            return gameDocumentsDir;
        }
    }

    private static string FindGameDocumentsDir()
    {
        string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"My Games\FarmingSimulator2025");
        if (File.Exists(Path.Combine(docs, "gameSettings.xml"))) return docs;
        try
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            foreach (string pkg in Directory.GetDirectories(packages, "GIANTSSoftware.FarmingSimulator25PC_*"))
            {
                string local = Path.Combine(pkg, @"LocalCache\Local");
                if (File.Exists(Path.Combine(local, "gameSettings.xml"))) return local;
            }
        }
        catch (Exception) { }
        return docs;
    }

    public static void Init()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        GameSettingsPath = Path.Combine(GameDocumentsDir, "gameSettings.xml");

        string settingsDir = null;
        foreach (string cand in new string[] { exeDir, Path.Combine(exeDir, "_Tools"), Path.GetDirectoryName(exeDir) })
        {
            if (!string.IsNullOrEmpty(cand) && File.Exists(Path.Combine(cand, "settings.json"))) { settingsDir = cand; break; }
        }
        if (settingsDir != null)
        {
            try
            {
                Dictionary<string, object> s = Json.DeserializeObject(File.ReadAllText(Path.Combine(settingsDir, "settings.json"))) as Dictionary<string, object>;
                if (s != null)
                {
                    ModsRoot = GetString(s, "ModsRoot");
                    MapsRoot = GetString(s, "MapsRoot");
                    string gip = GetString(s, "GameInstallPath");
                    if (!string.IsNullOrEmpty(gip) && Directory.Exists(gip)) GameInstallPath = gip;
                    string gsp = GetString(s, "GameSettingsPath");
                    if (!string.IsNullOrEmpty(gsp)) GameSettingsPath = gsp;
                }
            }
            catch (Exception) { }
            SettingsDir = settingsDir;
            DataDir = Path.Combine(settingsDir, "FS25 Server Sync");
        }
        if (DataDir == null) DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FS25 Server Sync");
        Directory.CreateDirectory(DataDir);
        if (SettingsDir != null) MoveFromSettingsDir();
        if (string.IsNullOrEmpty(MapsRoot) && !string.IsNullOrEmpty(ModsRoot)) MapsRoot = Path.Combine(ModsRoot, "Karten");
    }

    public static string FindInstallPath()
    {
        if (!string.IsNullOrEmpty(GameInstallPath) && Directory.Exists(GameInstallPath)) return GameInstallPath;
        try
        {
            string steam = ReadRegistry(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") ?? ReadRegistry(@"SOFTWARE\Valve\Steam", "InstallPath");
            if (!string.IsNullOrEmpty(steam))
            {
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    {
                        string candidate = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), @"steamapps\common\Farming Simulator 25");
                        if (Directory.Exists(candidate)) { GameInstallPath = candidate; return candidate; }
                    }
                }
            }
        }
        catch (Exception) { }
        foreach (string c in UninstallLocations().Concat(EpicLocations()))
        {
            if (IsGameDir(c)) { GameInstallPath = c; return c; }
        }
        foreach (string c in new string[] { @"C:\Program Files (x86)\Farming Simulator 25", @"C:\Program Files\Farming Simulator 25", @"C:\Program Files (x86)\Farming Simulator 2025", @"C:\Program Files\Farming Simulator 2025", @"C:\Program Files\Epic Games\FarmingSimulator25" })
        {
            if (Directory.Exists(c)) { GameInstallPath = c; return c; }
        }
        return null;
    }

    public static string GameExe(string installDir)
    {
        if (string.IsNullOrEmpty(installDir)) return null;
        string exe = Path.Combine(installDir, "FarmingSimulator2025.exe");
        return File.Exists(exe) ? exe : null;
    }

    public static bool IsSteamInstall(string installDir)
    {
        return !string.IsNullOrEmpty(installDir) && installDir.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsGameDir(string dir)
    {
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) && GameExe(dir) != null;
    }

    private static IEnumerable<string> UninstallLocations()
    {
        List<string> found = new List<string>();
        foreach (Microsoft.Win32.RegistryHive hive in new Microsoft.Win32.RegistryHive[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
        {
            foreach (Microsoft.Win32.RegistryView view in new Microsoft.Win32.RegistryView[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using (Microsoft.Win32.RegistryKey root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view))
                    using (Microsoft.Win32.RegistryKey un = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (un == null) continue;
                        foreach (string sub in un.GetSubKeyNames())
                        {
                            using (Microsoft.Win32.RegistryKey k = un.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string name = k.GetValue("DisplayName") as string ?? "";
                                if (!Regex.IsMatch(name, @"(Farming Simulator|Landwirtschafts-?Simulator) ?(25|2025)", RegexOptions.IgnoreCase)) continue;
                                string loc = (k.GetValue("InstallLocation") as string ?? "").Trim('"', ' ');
                                if (loc.Length > 0) found.Add(loc.TrimEnd('\\'));
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
        }
        return found;
    }

    private static IEnumerable<string> EpicLocations()
    {
        List<string> found = new List<string>();
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return found;
            foreach (string f in Directory.GetFiles(dir, "*.item"))
            {
                try
                {
                    Dictionary<string, object> d = Json.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                    if (d == null) continue;
                    string name = GetString(d, "DisplayName") ?? "";
                    if (!Regex.IsMatch(name, @"Farming Simulator ?(25|2025)", RegexOptions.IgnoreCase)) continue;
                    string loc = GetString(d, "InstallLocation");
                    if (!string.IsNullOrEmpty(loc)) found.Add(loc.TrimEnd('\\'));
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return found;
    }

    private static string ReadRegistry(string subKey, string name)
    {
        using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey))
        {
            if (key == null) return null;
            object v = key.GetValue(name);
            return v == null ? null : v.ToString();
        }
    }

    public static bool IsStoreVersion
    {
        get { return GameDocumentsDir.IndexOf(@"\Packages\GIANTSSoftware.FarmingSimulator25PC_", StringComparison.OrdinalIgnoreCase) >= 0; }
    }

    public static string StoreAppId()
    {
        string family = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(GameDocumentsDir)));
        string name = family.Substring(0, family.LastIndexOf('_'));
        foreach (string root in GamingRoots())
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(root))
                {
                    string manifest = Path.Combine(sub, @"Content\appxmanifest.xml");
                    if (!File.Exists(manifest)) continue;
                    XmlDocument doc = new XmlDocument();
                    doc.Load(manifest);
                    XmlNode identity = doc.SelectSingleNode("//*[local-name()='Identity']");
                    if (identity == null || identity.Attributes["Name"] == null || !string.Equals(identity.Attributes["Name"].Value, name, StringComparison.OrdinalIgnoreCase)) continue;
                    XmlNode app = doc.SelectSingleNode("//*[local-name()='Application']");
                    if (app != null && app.Attributes["Id"] != null) return family + "!" + app.Attributes["Id"].Value;
                }
            }
            catch (Exception) { }
        }
        return null;
    }

    public static HashSet<string> InstalledDlcs()
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> dirs = new List<string>();
        string install = FindInstallPath();
        if (install != null) dirs.Add(Path.Combine(install, "pdlc"));
        dirs.Add(Path.Combine(GameDocumentsDir, "pdlc"));
        if (IsStoreVersion)
            foreach (string root in GamingRoots())
            {
                try { foreach (string sub in Directory.GetDirectories(root)) dirs.Add(Path.Combine(sub, @"Content\pdlc")); }
                catch (Exception) { }
            }
        bool any = false;
        foreach (string d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            any = true;
            foreach (string f in Directory.GetFiles(d, "*.dlc")) result.Add(Path.GetFileName(f));
            foreach (string f in Directory.GetDirectories(d)) if (f.EndsWith(".dlc", StringComparison.OrdinalIgnoreCase)) result.Add(Path.GetFileName(f));
        }
        return any ? result : null;
    }

    private static List<string> GamingRoots()
    {
        List<string> roots = new List<string>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string drive = d.RootDirectory.FullName;
                List<string> cands = new List<string>();
                cands.Add(Path.Combine(drive, "XboxGames"));
                string gr = Path.Combine(drive, ".GamingRoot");
                if (File.Exists(gr))
                {
                    byte[] b = File.ReadAllBytes(gr);
                    if (b.Length > 8)
                    {
                        string rel = Encoding.Unicode.GetString(b, 8, b.Length - 8).Trim(new char[] { (char)0, (char)32, (char)92 });
                        if (rel.Length > 0) cands.Add(Path.Combine(drive, rel));
                    }
                }
                foreach (string c in cands) if (Directory.Exists(c) && !roots.Any(r => SamePath(r, c))) roots.Add(c);
            }
            catch (Exception) { }
        }
        return roots;
    }

    private static void MoveFromSettingsDir()
    {
        foreach (string name in new string[] { "serversync.json", "serversync.log" })
        {
            string from = Path.Combine(SettingsDir, name);
            string to = Path.Combine(DataDir, name);
            if (File.Exists(from) && !File.Exists(to)) { try { File.Move(from, to); } catch (Exception) { } }
        }
        string icons = Path.Combine(SettingsDir, "icons");
        string iconsTo = Path.Combine(DataDir, "icons");
        if (Directory.Exists(icons) && !Directory.Exists(iconsTo))
        {
            bool ours = true;
            foreach (string f in Directory.GetFiles(icons)) if (!f.EndsWith("_192.png", StringComparison.OrdinalIgnoreCase)) { ours = false; break; }
            if (ours && Directory.GetDirectories(icons).Length == 0) { try { Directory.Move(icons, iconsTo); } catch (Exception) { } }
        }
    }

    public static string DefaultLibrary()
    {
        if (!string.IsNullOrEmpty(ModsRoot) && Directory.Exists(ModsRoot)) return ModsRoot;
        string mods = Path.Combine(GameDocumentsDir, "mods");
        return Directory.Exists(mods) ? mods : null;
    }

    private static string Squash(string s)
    {
        return Regex.Replace(s ?? "", "[^0-9A-Za-zÄÖÜäöüß]", "").ToLowerInvariant();
    }

    private static string SafeName(string s)
    {
        return Regex.Replace(s ?? "", @"[\\/:*?""<>|]", "").Trim().TrimEnd('.');
    }

    public static string SuggestTarget(string serverName, string mapTitle)
    {
        string name = SafeName(serverName);
        if (name.Length == 0) name = "Server";
        string map = SafeName(mapTitle);
        if (map.Length > 0 && Squash(name).IndexOf(Squash(map), StringComparison.Ordinal) < 0) name += "-" + map;
        if (!string.IsNullOrEmpty(MapsRoot) && Directory.Exists(MapsRoot)) return Path.Combine(MapsRoot, name);
        return Path.Combine(GameDocumentsDir, "mods_" + name);
    }

    public static string UiLanguage()
    {
        string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return (lang == "de" || lang == "fr") ? lang : "en";
    }

    public static string GetString(Dictionary<string, object> d, string key)
    {
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    public static List<string> GetStrings(Dictionary<string, object> d, string key)
    {
        List<string> result = new List<string>();
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return result;
        System.Collections.IList list = v as System.Collections.IList;
        if (list == null) { result.Add(Convert.ToString(v, CultureInfo.InvariantCulture)); return result; }
        foreach (object o in list) if (o != null) result.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
        return result;
    }

    public static bool GetBool(Dictionary<string, object> d, string key)
    {
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return false;
        if (v is bool) return (bool)v;
        return string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);
    }

    public static void Log(string action, string details)
    {
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + action;
        if (!string.IsNullOrEmpty(details)) line += ": " + details;
        try { File.AppendAllText(Path.Combine(DataDir, "serversync.log"), line + Environment.NewLine, new UTF8Encoding(true)); }
        catch (Exception) { }
    }

    public static bool GameRunning()
    {
        bool running = false;
        foreach (Process p in Process.GetProcesses())
        {
            try { if (p.ProcessName.IndexOf("FarmingSimulator", StringComparison.OrdinalIgnoreCase) >= 0) running = true; }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return running;
    }

    public static string Fingerprint(string path)
    {
        if (!File.Exists(path)) return "";
        FileInfo fi = new FileInfo(path);
        return fi.Length.ToString(CultureInfo.InvariantCulture) + "_" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    public static string NormalizeDir(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\');
    }

    public static bool SamePath(string a, string b)
    {
        if (a == null || b == null) return false;
        return string.Equals(NormalizeDir(a), NormalizeDir(b), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInside(string path, string folder)
    {
        if (path == null || folder == null) return false;
        string p = NormalizeDir(path) + "\\";
        string f = NormalizeDir(folder) + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    public static string ActiveModsDirectoryOverride()
    {
        if (!File.Exists(GameSettingsPath)) return null;
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(GameSettingsPath);
            XmlElement node = doc.SelectSingleNode("//modsDirectoryOverride") as XmlElement;
            if (node == null || !string.Equals(node.GetAttribute("active"), "true", StringComparison.OrdinalIgnoreCase)) return null;
            string dir = node.GetAttribute("directory");
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception) { return null; }
    }

    public static void SetModsDirectoryOverride(string directory)
    {
        if (!File.Exists(GameSettingsPath)) throw new AppError("noGameSettings", GameSettingsPath);
        if (GameRunning()) throw new AppError("gameRunning", null);
        XmlDocument doc = new XmlDocument();
        doc.PreserveWhitespace = true;
        doc.Load(GameSettingsPath);
        XmlElement node = doc.SelectSingleNode("//modsDirectoryOverride") as XmlElement;
        if (node == null)
        {
            node = doc.CreateElement("modsDirectoryOverride");
            doc.DocumentElement.AppendChild(node);
        }
        if (string.IsNullOrEmpty(directory))
        {
            node.SetAttribute("active", "false");
        }
        else
        {
            node.SetAttribute("active", "true");
            node.SetAttribute("directory", directory);
        }
        string backupDir = Path.Combine(Path.GetDirectoryName(GameSettingsPath), "_ServerSyncBackups");
        Directory.CreateDirectory(backupDir);
        File.Copy(GameSettingsPath, Path.Combine(backupDir, "gameSettings.xml." + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".bak"), true);
        XmlWriterSettings ws = new XmlWriterSettings();
        ws.Encoding = new UTF8Encoding(false);
        using (XmlWriter w = XmlWriter.Create(GameSettingsPath, ws))
        {
            doc.Save(w);
        }
        Log("Aktiviert", string.IsNullOrEmpty(directory) ? "(Standard-Mods-Ordner)" : directory);
    }
}
