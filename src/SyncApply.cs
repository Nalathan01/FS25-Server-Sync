using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

internal static partial class Sync
{
    public static void Apply(Config cfg, ServerEntry server, Plan plan, Job job)
    {
        ValidateFolders(cfg.Library, server.Target);
        if (!AppEnv.SamePath(plan.Target, server.Target) || !AppEnv.SamePath(plan.Library, cfg.Library)) throw new AppError("planOutdated", null);
        string target = plan.Target;
        if (AppEnv.GameRunning() && AppEnv.SamePath(AppEnv.ActiveModsDirectoryOverride(), target)) throw new AppError("gameRunningActive", null);
        Directory.CreateDirectory(target);
        bool sameVolume = NativeLinks.SameVolume(plan.Library, target);

        List<PlanRow> removes = plan.Rows.Where(r => r.Status == "remove").ToList();
        job.Step("remove", removes.Count);
        foreach (PlanRow r in removes)
        {
            job.ThrowIfCancelled();
            job.Current = r.Title;
            OnFile(Path.GetFileName(r.TargetPath), () => Retire(cfg, r.TargetPath));
            job.Done++;
        }

        List<PlanRow> downloads = plan.Rows.Where(r => r.Status == "download" || r.Status == "update" || r.Status == "check").ToList();
        Dictionary<PlanRow, string> fetched = new Dictionary<PlanRow, string>();
        Dictionary<PlanRow, string> fetchedHash = new Dictionary<PlanRow, string>();
        job.Step("download", downloads.Count);
        job.BytesDone = 0;
        job.BytesTotal = downloads.Sum(r => Math.Max(0, r.Bytes));
        string tmpDir = TempDir;
        Directory.CreateDirectory(tmpDir);
        System.Threading.Tasks.ParallelOptions po = new System.Threading.Tasks.ParallelOptions();
        po.MaxDegreeOfParallelism = 3;
        Exception firstError = null;
        System.Threading.Tasks.Parallel.ForEach(downloads.OrderByDescending(r => r.Bytes), po, (r, loop) =>
        {
            if (job.Cancel || firstError != null) { loop.Stop(); return; }
            job.Current = r.Title;
            try
            {
                string tmp = Path.Combine(tmpDir, r.FileName);
                bool identical;
                string h = ServerPage.DownloadCompare(r.Url, r.Bytes, r.CompareWith, tmp, r.ModName, n => Interlocked.Add(ref job.BytesDone, n), () => job.Cancel, out identical);
                lock (fetched)
                {
                    fetched[r] = identical ? null : tmp;
                    fetchedHash[r] = h;
                    job.Compared++;
                }
                lock (Config.Sync) cfg.ServerHashes[r.ServerKey] = h;
            }
            catch (Exception ex)
            {
                lock (fetched) { if (firstError == null) firstError = ex; }
                loop.Stop();
            }
            Interlocked.Increment(ref job.Done);
        });
        cfg.Save();
        job.ThrowIfCancelled();
        if (firstError != null) throw firstError;

        Dictionary<string, List<LocalFile>> index = IndexLibrary(cfg, plan.Library, target);
        List<PlanRow> places = plan.Rows.Where(r => (r.Status == "link" && r.Source != null) || fetched.ContainsKey(r)).ToList();
        job.Step("link", places.Count);
        foreach (PlanRow r in places)
        {
            job.ThrowIfCancelled();
            job.Current = r.Title;
            OnFile(r.FileName, () => Place(cfg, r, target, fetched, fetchedHash, index, sameVolume, job));
            job.Done++;
        }
        try { if (Directory.Exists(tmpDir) && !Directory.EnumerateFileSystemEntries(tmpDir).Any()) Directory.Delete(tmpDir); } catch (Exception) { }

        Dictionary<string, object> marker = new Dictionary<string, object>();
        marker["Server"] = server.Name;
        marker["Url"] = plan.BaseUrl;
        marker["Synced"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(target, MarkerName), AppEnv.Json.Serialize(marker), new UTF8Encoding(false));
        try { File.Delete(Path.Combine(target, LegacyMarkerName)); } catch (Exception) { }
        AppEnv.Log("Abgeglichen", server.Name + " nach " + target + " (" + Summary(plan) + ")");
        RetireStore(plan.Library);
    }

    private static void Place(Config cfg, PlanRow r, string target, Dictionary<PlanRow, string> fetched, Dictionary<PlanRow, string> fetchedHash, Dictionary<string, List<LocalFile>> index, bool sameVolume, Job job)
    {
        string t = Path.Combine(target, r.FileName);
        if (r.Status == "link")
        {
            LinkInto(cfg, r.Source, t, sameVolume);
            return;
        }
        string file = fetched[r];
        string h = fetchedHash[r];
        if (file == null)
        {
            if (!AppEnv.SamePath(r.CompareWith, t)) LinkInto(cfg, r.CompareWith, t, sameVolume);
            job.Identical++;
            return;
        }
        if (File.Exists(t) && SizeOf(t) == r.Bytes && GameHash(cfg, t, r.ModName, job) == h)
        {
            File.Delete(file);
            job.Identical++;
            return;
        }
        List<LocalFile> cands;
        if (!index.TryGetValue(r.ModName, out cands)) cands = new List<LocalFile>();
        LocalFile same = cands.OrderBy(c => c.InStore ? 1 : 0).FirstOrDefault(c => c.Length == r.Bytes && GameHash(cfg, c.Path, r.ModName, job) == h);
        if (same != null)
        {
            File.Delete(file);
            LinkInto(cfg, same.Path, t, sameVolume);
            job.Identical++;
        }
        else
        {
            job.Saved++;
            job.SavedBytes += Math.Max(0, r.Bytes);
            Retire(cfg, t);
            MoveInto(file, t);
            MarkPlaced(cfg, t);
        }
    }

    private static void OnFile(string name, Action action)
    {
        try { action(); }
        catch (AppError) { throw; }
        catch (Exception ex) { throw new AppError("fileFailed", name + ": " + ex.Message); }
    }

    private static void MoveInto(string file, string t)
    {
        if (NativeLinks.SameVolume(file, t)) { File.Move(file, t); return; }
        NativeLinks.CopyFile(file, t);
        File.Delete(file);
    }

    private static void LinkInto(Config cfg, string source, string t, bool sameVolume)
    {
        if (File.Exists(t) && NativeLinks.SameFile(t, source)) return;
        Retire(cfg, t);
        if (sameVolume)
        {
            try
            {
                NativeLinks.CreateHardLink(t, source);
                return;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                AppEnv.Log("Verlinken", Path.GetFileName(t) + ": " + ex.Message + ", wird kopiert");
            }
        }
        NativeLinks.CopyFile(source, t);
        MarkPlaced(cfg, t);
    }

    private static void Retire(Config cfg, string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, !NativeLinks.IsReparsePoint(path));
            AppEnv.Log("Entfernt", path);
            return;
        }
        if (!File.Exists(path)) return;
        File.Delete(path);
        UnmarkPlaced(cfg, path);
        AppEnv.Log("Entfernt", path);
    }
}
