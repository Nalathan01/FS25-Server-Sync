using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

internal sealed class LocalFile
{
    public string Path;
    public long Length;
    public bool InStore;
}

internal sealed class ZipInfo
{
    public string Version;
    public string IconFilename;
    public bool IsMap;
}

internal sealed class PlanRow
{
    public string ModName;
    public string FileName;
    public string Title;
    public string Version;
    public string Author;
    public string Url;
    public bool ModHub;
    public long Bytes = -1;
    public string Status;
    public string Source;
    public string LocalVersion;
    public string IconPath;
    public bool Verified;
    public bool InTarget;
    public bool IsDir;
    public string RemoveMode;
    public string TargetPath;
    public string ErrorCode;
    public string ErrorArg;
    public bool IsMap;
    public string ServerHash;
    public string ServerKey;
    public string CompareWith;
}

internal sealed class Plan
{
    public string ServerId;
    public string BaseUrl;
    public string Target;
    public string Library;
    public bool SameVolume;
    public bool Thorough;
    public bool ForeignTarget;
    public int ServerTotal;
    public int InactiveOnServer;
    public bool CanManage;
    public bool DlcKnown;
    public List<Dictionary<string, object>> Dlcs = new List<Dictionary<string, object>>();
    public DateTime Created;
    public string MapModName;
    public string MapTitle;
    public List<string> MapTitles = new List<string>();
    public string RunningMap;
    public string RunningSlot;
    public bool RunningMatched;
    public int MapCount;
    public bool MapsRootKnown;
    public bool TargetHasMap;
    public List<string> MapFolders = new List<string>();
    public List<string> TargetOtherMaps = new List<string>();
    public List<PlanRow> Rows = new List<PlanRow>();
}

internal sealed class Job
{
    public string Kind;
    public string ServerId;
    public string Phase = "start";
    public int Done;
    public int Total;
    public string Current;
    public long BytesDone;
    public long BytesTotal;
    public bool Finished;
    public string ErrorCode;
    public string ErrorArg;
    public int Compared;
    public int Identical;
    public int Saved;
    public long SavedBytes;
    public volatile bool Cancel;

    public void Step(string phase, int total)
    {
        Phase = phase;
        Total = total;
        Done = 0;
        Current = null;
    }

    public void Fail(Exception ex)
    {
        AppError ae = ex as AppError;
        ErrorCode = ae != null ? ae.Code : "unexpected";
        ErrorArg = ae != null ? ae.Arg : ex.Message;
    }

    public void ThrowIfCancelled()
    {
        if (Cancel) throw new AppError("cancelled", null);
    }
}

internal static partial class Sync
{
    public const string StoreFolderName = "_ServerDownloads";
    public const string MarkerName = ".serversync.json";
    public const string LegacyMarkerName = ".serverabgleich.json";

    public static bool HasMarker(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        return File.Exists(Path.Combine(dir, MarkerName)) || File.Exists(Path.Combine(dir, LegacyMarkerName));
    }
    private static readonly Dictionary<string, ZipInfo> ZipCache = new Dictionary<string, ZipInfo>(StringComparer.OrdinalIgnoreCase);

    public static string StoreDir(string library)
    {
        return Path.Combine(library, StoreFolderName);
    }

    public static string CleanVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return "0";
        return Regex.Replace(v.Trim(), @"[^0-9A-Za-z.\-]", "-");
    }

    private static bool VersionEq(string local, string server)
    {
        if (string.IsNullOrEmpty(server)) return true;
        return string.Equals(CleanVersion(local), CleanVersion(server), StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateFolders(string library, string target)
    {
        if (string.IsNullOrEmpty(library)) throw new AppError("noLibrary", null);
        if (string.IsNullOrEmpty(target)) throw new AppError("noTarget", null);
        string lib = AppEnv.NormalizeDir(library);
        string tgt = AppEnv.NormalizeDir(target);
        if (Path.GetPathRoot(tgt).TrimEnd('\\').Equals(tgt, StringComparison.OrdinalIgnoreCase)) throw new AppError("targetIsDrive", tgt);
        if (AppEnv.SamePath(lib, tgt) || AppEnv.IsInside(lib, tgt)) throw new AppError("targetIsLibrary", tgt);
        if (AppEnv.IsInside(tgt, StoreDir(lib))) throw new AppError("targetInStore", tgt);
        if (AppEnv.SamePath(Path.GetDirectoryName(tgt), lib) && Directory.Exists(tgt) && Directory.EnumerateFiles(tgt, "*.zip").Any() && !HasMarker(tgt))
            throw new AppError("targetIsCategory", tgt);
    }

    public static ZipInfo ReadZip(string path)
    {
        string key = path + "|" + AppEnv.Fingerprint(path);
        lock (ZipCache)
        {
            ZipInfo cached;
            if (ZipCache.TryGetValue(key, out cached)) return cached;
        }
        ZipInfo info = new ZipInfo();
        try
        {
            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, "modDesc.xml", StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    XmlDocument doc = new XmlDocument();
                    using (Stream s = entry.Open()) doc.Load(s);
                    XmlNode v = doc.SelectSingleNode("/modDesc/version");
                    XmlNode ic = doc.SelectSingleNode("/modDesc/iconFilename");
                    info.IsMap = doc.SelectSingleNode("/modDesc/maps/map") != null;
                    if (v != null) info.Version = v.InnerText.Trim();
                    if (ic != null) info.IconFilename = ic.InnerText.Trim();
                }
            }
        }
        catch (Exception) { }
        lock (ZipCache) ZipCache[key] = info;
        return info;
    }

    private static string VersionOf(LocalFile f)
    {
        return ReadZip(f.Path).Version;
    }

    public static Dictionary<string, List<LocalFile>> IndexLibrary(Config cfg, string library, string target)
    {
        Dictionary<string, List<LocalFile>> index = new Dictionary<string, List<LocalFile>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(library) || !Directory.Exists(library)) throw new AppError("libraryMissing", library);
        List<string> excluded = cfg.Servers.Select(s => s.Target).Where(e => !string.IsNullOrEmpty(e)).ToList();
        string store = StoreDir(library);
        List<string> roots = new List<string>();
        roots.Add(library);
        foreach (string dir in Directory.GetDirectories(library))
        {
            if (AppEnv.SamePath(dir, store) || NativeLinks.IsReparsePoint(dir)) continue;
            if (excluded.Any(e => AppEnv.SamePath(e, dir))) continue;
            roots.Add(dir);
        }
        foreach (string root in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception) { continue; }
            foreach (string f in files) Add(index, Path.GetFileNameWithoutExtension(f), f, false);
        }
        if (Directory.Exists(store))
        {
            foreach (string f in Directory.EnumerateFiles(store, "*.zip", SearchOption.TopDirectoryOnly)) Add(index, Path.GetFileNameWithoutExtension(f), f, true);
        }
        foreach (string other in excluded)
        {
            if (AppEnv.SamePath(other, target) || !Directory.Exists(other) || !NativeLinks.SameVolume(other, target)) continue;
            foreach (string f in Directory.EnumerateFiles(other, "*.zip", SearchOption.TopDirectoryOnly)) Add(index, Path.GetFileNameWithoutExtension(f), f, true);
        }
        return index;
    }

    private static void Add(Dictionary<string, List<LocalFile>> index, string name, string path, bool inStore)
    {
        List<LocalFile> list;
        if (!index.TryGetValue(name, out list)) { list = new List<LocalFile>(); index[name] = list; }
        LocalFile lf = new LocalFile();
        lf.Path = path;
        lf.InStore = inStore;
        try { lf.Length = new FileInfo(path).Length; } catch (Exception) { lf.Length = -1; }
        list.Add(lf);
    }

    public static string ServerKey(string baseUrl, string fileName, long bytes, string version)
    {
        return baseUrl + "|" + fileName + "|" + bytes.ToString(CultureInfo.InvariantCulture) + "|" + CleanVersion(version);
    }

    public static string GameHash(Config cfg, string path, string modName, Job job)
    {
        string key = path + "|" + AppEnv.Fingerprint(path) + "|" + modName;
        lock (Config.Sync)
        {
            string cached;
            if (cfg.LocalHashes.TryGetValue(key, out cached)) return cached;
        }
        string h;
        try
        {
            using (MD5 md5 = MD5.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                byte[] buffer = new byte[1 << 20];
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (job != null) job.ThrowIfCancelled();
                    md5.TransformBlock(buffer, 0, read, null, 0);
                }
                byte[] name = Encoding.ASCII.GetBytes(modName);
                md5.TransformFinalBlock(name, 0, name.Length);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in md5.Hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                h = sb.ToString();
            }
        }
        catch (AppError) { throw; }
        catch (Exception ex) { throw new AppError("fileFailed", path + ": " + ex.Message); }
        lock (Config.Sync) cfg.LocalHashes[key] = h;
        return h;
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch (Exception) { return -1; }
    }

    private static string PlacedKey(string path)
    {
        return AppEnv.NormalizeDir(path).ToLowerInvariant();
    }

    public static void MarkPlaced(Config cfg, string path)
    {
        long len = SizeOf(path);
        if (len < 0) return;
        lock (Config.Sync) cfg.Placed[PlacedKey(path)] = len.ToString(CultureInfo.InvariantCulture);
    }

    public static void UnmarkPlaced(Config cfg, string path)
    {
        lock (Config.Sync) cfg.Placed.Remove(PlacedKey(path));
    }

    public static bool IsToolFile(Config cfg, string path)
    {
        long len = SizeOf(path);
        if (len < 0) return false;
        string v;
        lock (Config.Sync)
        {
            if (cfg.Placed.TryGetValue(PlacedKey(path), out v) && v == len.ToString(CultureInfo.InvariantCulture)) return true;
        }
        string name = Path.GetFileName(path);
        List<string> hashes;
        lock (Config.Sync) hashes = cfg.ServerHashes.Where(kv => kv.Key.IndexOf("|" + name + "|", StringComparison.OrdinalIgnoreCase) >= 0).Select(kv => kv.Value).ToList();
        if (hashes.Count == 0) return false;
        try { return hashes.Contains(GameHash(cfg, path, Path.GetFileNameWithoutExtension(path), null)); }
        catch (Exception) { return false; }
    }

    public static string TempDir
    {
        get { return Path.Combine(AppEnv.DataDir, "tmp"); }
    }

    public static void RetireStore(string library)
    {
        string store = StoreDir(library);
        if (string.IsNullOrEmpty(library) || !Directory.Exists(store)) return;
        try
        {
            Directory.Delete(store, true);
            AppEnv.Log("Ablage", store + " aufgeloest");
        }
        catch (Exception ex) { AppEnv.Log("Ablage", store + ": " + ex.Message); }
    }

    public static string TargetStamp(string target)
    {
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target)) return "";
        try
        {
            List<string> parts = new List<string>();
            foreach (string f in Directory.EnumerateFiles(target, "*.zip"))
            {
                FileInfo fi = new FileInfo(f);
                parts.Add(fi.Name + "|" + fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            foreach (string d in Directory.EnumerateDirectories(target)) parts.Add(Path.GetFileName(d) + "|dir");
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", parts);
        }
        catch (Exception) { return ""; }
    }

}
