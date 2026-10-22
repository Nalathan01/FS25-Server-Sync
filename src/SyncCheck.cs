using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Sync
{
    public static Plan Check(Config cfg, ServerEntry server, bool refreshHashes, Job job)
    {
        string library = cfg.Library;
        ValidateFolders(library, server.Target);
        string baseUrl = ServerPage.BaseUrl(server.Url);
        string target = AppEnv.NormalizeDir(server.Target);

        job.Step("fetch", 1);
        bool canManage;
        List<ServerMod> serverDlcs;
        List<ServerMod> all = ServerPage.Fetch(baseUrl, out canManage, out serverDlcs);
        List<ServerMod> active = all.Where(m => m.Active).ToList();
        job.ThrowIfCancelled();

        Dictionary<ServerMod, AppError> probeErrors = new Dictionary<ServerMod, AppError>();
        job.Step("probe", active.Count);
        ParallelOptions po = new ParallelOptions();
        po.MaxDegreeOfParallelism = 8;
        Parallel.ForEach(active, po, m =>
        {
            if (job.Cancel) return;
            m.IsMap = ServerPage.IsMapMod(baseUrl, m.Index);
            if (m.Url != null)
            {
                try { m.Bytes = ServerPage.ProbeSize(m.Url); }
                catch (AppError ex) { lock (probeErrors) probeErrors[m] = ex; }
                catch (Exception ex) { lock (probeErrors) probeErrors[m] = new AppError("probeFailed", ex.Message); }
            }
            Interlocked.Increment(ref job.Done);
        });
        job.ThrowIfCancelled();

        string runSlot, runMap;
        ServerPage.FetchRunning(baseUrl, out runSlot, out runMap);
        HashSet<ServerMod> skipMaps = new HashSet<ServerMod>();
        bool runMatched = false;
        if (runMap != null)
        {
            List<ServerMod> maps = active.Where(m => m.IsMap).ToList();
            List<ServerMod> running = maps.Where(m => SameName(m.Title, runMap) || SameName(m.ModName, runMap) || SameName(m.ModName, "FS25_" + runMap)).ToList();
            if (running.Count == 1)
            {
                runMatched = true;
                foreach (ServerMod m in maps) if (m != running[0]) skipMaps.Add(m);
            }
        }

        job.Step("scan", 1);
        Dictionary<string, List<LocalFile>> index = IndexLibrary(cfg, library, target);
        ClearTemp();

        if (refreshHashes)
        {
            lock (Config.Sync)
            {
                foreach (string k in cfg.ServerHashes.Keys.Where(k => k.StartsWith(baseUrl + "|", StringComparison.OrdinalIgnoreCase)).ToList()) cfg.ServerHashes.Remove(k);
            }
        }

        job.Step("compare", active.Count);
        Plan plan = new Plan();
        plan.ServerId = server.Id;
        plan.BaseUrl = baseUrl;
        plan.Target = target;
        plan.Library = library;
        plan.SameVolume = NativeLinks.SameVolume(library, target);
        plan.Thorough = refreshHashes;
        plan.ServerTotal = active.Count;
        plan.InactiveOnServer = all.Count(m => !m.Active);
        plan.CanManage = canManage;
        HashSet<string> localDlcs = AppEnv.InstalledDlcs();
        plan.DlcKnown = localDlcs != null;
        foreach (ServerMod d in serverDlcs.Where(x => x.Active))
        {
            Dictionary<string, object> info = new Dictionary<string, object>();
            info["Title"] = d.Title;
            info["File"] = d.FileName;
            info["Version"] = d.Version;
            info["Installed"] = localDlcs == null ? (object)null : localDlcs.Contains(d.FileName);
            plan.Dlcs.Add(info);
        }
        plan.Created = DateTime.Now;
        plan.RunningMap = runMap;
        plan.RunningSlot = runSlot;
        plan.RunningMatched = runMatched;
        plan.ForeignTarget = Directory.Exists(target) && !HasMarker(target) && Directory.EnumerateFileSystemEntries(target).Any();

        HashSet<string> expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ServerMod m in active)
        {
            job.ThrowIfCancelled();
            job.Current = m.Title;
            if (skipMaps.Contains(m))
            {
                PlanRow skip = new PlanRow();
                skip.ModName = m.ModName;
                skip.FileName = m.FileName;
                skip.Title = m.Title;
                skip.Version = m.Version;
                skip.Author = m.Author;
                skip.ModHub = m.ModHub;
                skip.Bytes = m.Bytes;
                skip.IsMap = true;
                skip.Status = "skipmap";
                plan.Rows.Add(skip);
                job.Done++;
                continue;
            }
            expected.Add(m.FileName);
            AppError probeError;
            probeErrors.TryGetValue(m, out probeError);
            List<LocalFile> cands;
            if (!index.TryGetValue(m.ModName, out cands)) cands = new List<LocalFile>();
            PlanRow row = CompareRow(cfg, baseUrl, m, target, cands, probeError, job);
            row.IsMap = m.IsMap;
            plan.Rows.Add(row);
            job.Done++;
        }

        if (canManage)
        {
            foreach (ServerMod m in all.Where(x => !x.Active))
            {
                PlanRow r = new PlanRow();
                r.ModName = m.ModName;
                r.FileName = m.FileName;
                r.Title = m.Title;
                r.Version = m.Version;
                r.Author = m.Author;
                r.ModHub = m.ModHub;
                r.Bytes = ServerPage.ParseSize(m.SizeText);
                r.Status = "inactive";
                plan.Rows.Add(r);
            }
        }
        if (Directory.Exists(target)) AddRemovals(cfg, plan, target, expected);
        MapInfo(plan, active.Where(m => !skipMaps.Contains(m)).ToList(), target);

        lock (Config.Sync) server.LastCheck = plan.Created.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        cfg.Save();
        AppEnv.Log("Geprueft", server.Name + ": " + Summary(plan));
        return plan;
    }

    private static bool SameName(string a, string b)
    {
        if (a == null || b == null) return false;
        string x = System.Text.RegularExpressions.Regex.Replace(a, "[^0-9A-Za-z]", "").ToLowerInvariant();
        string y = System.Text.RegularExpressions.Regex.Replace(b, "[^0-9A-Za-z]", "").ToLowerInvariant();
        return x.Length > 0 && x == y;
    }

    private static void ClearTemp()
    {
        string tmpDir = TempDir;
        if (!Directory.Exists(tmpDir)) return;
        foreach (string f in Directory.GetFiles(tmpDir))
        {
            if (!f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(f); } catch (Exception) { }
        }
    }

    private static PlanRow CompareRow(Config cfg, string baseUrl, ServerMod m, string target, List<LocalFile> cands, AppError probeError, Job job)
    {
        PlanRow row = new PlanRow();
        row.ModName = m.ModName;
        row.FileName = m.FileName;
        row.Title = m.Title;
        row.Version = m.Version;
        row.Author = m.Author;
        row.Url = m.Url;
        row.ModHub = m.ModHub;
        row.Bytes = m.Bytes;
        string tPath = Path.Combine(target, m.FileName);
        row.TargetPath = tPath;
        row.InTarget = File.Exists(tPath);

        if (m.Url == null)
        {
            row.Status = "nodl";
            if (row.InTarget) row.IconPath = tPath;
            return row;
        }
        if (probeError != null || m.Bytes < 0)
        {
            row.Status = "error";
            row.ErrorCode = probeError != null ? probeError.Code : "probeFailed";
            row.ErrorArg = probeError != null ? probeError.Arg : null;
            return row;
        }

        row.ServerKey = ServerKey(baseUrl, m.FileName, m.Bytes, m.Version);
        string serverHash;
        lock (Config.Sync) cfg.ServerHashes.TryGetValue(row.ServerKey, out serverHash);
        row.ServerHash = serverHash;
        List<LocalFile> ordered = cands.OrderBy(c => c.InStore ? 1 : 0).ToList();

        if (serverHash != null)
        {
            if (row.InTarget && SizeOf(tPath) == m.Bytes && GameHash(cfg, tPath, m.ModName, job) == serverHash)
            {
                row.Status = "ok";
                row.Verified = true;
                row.IconPath = tPath;
                return row;
            }
            foreach (LocalFile c in ordered)
            {
                if (c.Length != m.Bytes || GameHash(cfg, c.Path, m.ModName, job) != serverHash) continue;
                row.Status = "link";
                row.Verified = true;
                row.Source = c.Path;
                row.IconPath = c.Path;
                return row;
            }
        }
        else
        {
            if (row.InTarget && SizeOf(tPath) == m.Bytes && VersionEq(ReadZip(tPath).Version, m.Version))
            {
                row.Status = "check";
                row.IconPath = tPath;
                row.CompareWith = tPath;
                return row;
            }
            foreach (LocalFile c in ordered)
            {
                if (c.Length != m.Bytes || !VersionEq(VersionOf(c), m.Version)) continue;
                row.Status = "check";
                row.Source = c.Path;
                row.IconPath = c.Path;
                row.CompareWith = c.Path;
                return row;
            }
        }
        if (ordered.Count > 0)
        {
            row.Status = "update";
            row.LocalVersion = VersionOf(ordered[0]);
            row.IconPath = ordered[0].Path;
            return row;
        }
        row.Status = "download";
        return row;
    }

    private static void AddRemovals(Config cfg, Plan plan, string target, HashSet<string> expected)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(target))
        {
            string name = Path.GetFileName(entry);
            bool isDir = Directory.Exists(entry);
            if (!isDir && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!isDir && expected.Contains(name)) continue;
            PlanRow row = new PlanRow();
            row.Status = "remove";
            row.IsDir = isDir;
            row.TargetPath = entry;
            row.FileName = name;
            row.ModName = isDir ? name : Path.GetFileNameWithoutExtension(name);
            row.Title = row.ModName;
            if (isDir)
            {
                row.RemoveMode = NativeLinks.IsReparsePoint(entry) ? "unlink" : "delete";
            }
            else
            {
                row.Version = ReadZip(entry).Version;
                row.IconPath = entry;
                row.Bytes = SizeOf(entry);
                row.RemoveMode = NativeLinks.LinkCount(entry) > 1 ? "unlink" : "delete";
            }
            plan.Rows.Add(row);
        }
    }

    private static void MapInfo(Plan plan, List<ServerMod> active, string target)
    {
        List<ServerMod> maps = active.Where(m => m.IsMap).ToList();
        plan.MapCount = maps.Count;
        if (maps.Count > 0)
        {
            plan.MapModName = maps[0].ModName;
            plan.MapTitle = maps[0].Title;
            plan.MapTitles = maps.Select(m => m.Title).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        }
        foreach (PlanRow r in plan.Rows)
        {
            if (r.Status != "remove") continue;
            bool isMap = r.IsDir ? DirIsMap(r.TargetPath) : ReadZip(r.TargetPath).IsMap;
            if (!isMap) continue;
            r.IsMap = true;
            plan.TargetOtherMaps.Add(r.ModName);
        }
        string mapsRoot = AppEnv.MapsRoot;
        plan.MapsRootKnown = !string.IsNullOrEmpty(mapsRoot) && Directory.Exists(mapsRoot);
        if (plan.MapModName == null) return;
        plan.TargetHasMap = File.Exists(Path.Combine(target, plan.MapModName + ".zip"));
        if (!plan.MapsRootKnown) return;
        foreach (string dir in Directory.GetDirectories(mapsRoot))
        {
            if (AppEnv.SamePath(dir, target)) continue;
            if (File.Exists(Path.Combine(dir, plan.MapModName + ".zip")) || Directory.Exists(Path.Combine(dir, plan.MapModName))) plan.MapFolders.Add(Path.GetFileName(dir));
        }
    }

    private static bool DirIsMap(string dir)
    {
        string md = Path.Combine(dir, "modDesc.xml");
        if (!File.Exists(md)) return false;
        try
        {
            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            doc.Load(md);
            return doc.SelectSingleNode("/modDesc/maps/map") != null;
        }
        catch (Exception) { return false; }
    }

    public static string Summary(Plan plan)
    {
        return string.Join(", ", plan.Rows.GroupBy(r => r.Status).Select(g => g.Key + "=" + g.Count().ToString(CultureInfo.InvariantCulture)).ToArray());
    }
}
