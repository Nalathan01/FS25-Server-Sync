using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

internal sealed class SyncServer
{
    private readonly string token = Guid.NewGuid().ToString("N");
    private readonly string uiLang = AppEnv.UiLanguage();
    private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>();
    private Config cfg;
    private Job job;
    private byte[] html;
    private DateTime lastPing = DateTime.Now;
    private bool firstPingSeen;
    private bool running = true;
    private Process window;
    private DateTime windowStart;

    public void Run(int port, bool noBrowser, string urlFile)
    {
        cfg = Config.Load();
        html = ReadResource("index.html");
        if (port == 0) port = FreePort();
        string prefix = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        string url = prefix + "?t=" + token;
        AppEnv.Log("ServerSync", "Gestartet auf " + prefix);

        if (!noBrowser) OpenWindow(url);
        else if (!string.IsNullOrEmpty(urlFile)) File.WriteAllText(urlFile, url, new UTF8Encoding(false));

        while (running)
        {
            Task<HttpListenerContext> task = listener.GetContextAsync();
            while (!task.Wait(1000))
            {
                bool busy = job != null && !job.Finished;
                if (busy) continue;
                if (window != null && window.HasExited)
                {
                    bool handedOff = DateTime.Now.Subtract(windowStart).TotalSeconds < 15;
                    window = null;
                    if (!handedOff) { running = false; break; }
                }
                double idle = DateTime.Now.Subtract(lastPing).TotalSeconds;
                if ((firstPingSeen && idle > 600) || (!firstPingSeen && idle > 120)) { running = false; break; }
            }
            if (!running) break;
            Handle(task.Result);
        }
        if (job != null && !job.Finished) job.Cancel = true;
        listener.Stop();
        listener.Close();
        try { cfg.Save(); } catch (Exception) { }
        AppEnv.Log("ServerSync", "Beendet");
    }

    private static int FreePort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static byte[] ReadResource(string name)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new InvalidOperationException("Resource missing: " + name);
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

    private void OpenWindow(string url)
    {
        string edge = null;
        foreach (string root in new string[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string candidate = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(candidate)) { edge = candidate; break; }
        }
        if (edge != null)
        {
            string profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"FS25ServerSync\edge");
            ProcessStartInfo psi = new ProcessStartInfo(edge, "--app=" + url + " --user-data-dir=\"" + profileDir + "\" --window-size=1440,920 --no-first-run");
            psi.UseShellExecute = false;
            windowStart = DateTime.Now;
            window = Process.Start(psi);
        }
        else
        {
            Process.Start(url);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        string path = req.Url.AbsolutePath;
        try
        {
            if (path == "/" || path == "/index.html")
            {
                Send(ctx, 200, html, "text/html; charset=utf-8");
                return;
            }
            string given = req.Headers["X-Token"];
            if (string.IsNullOrEmpty(given)) given = req.QueryString["t"];
            if (given != token) { SendJson(ctx, 403, Obj("error", "forbidden")); return; }
            lastPing = DateTime.Now;

            if (path == "/api/ping")
            {
                firstPingSeen = true;
                Dictionary<string, object> r = Obj("ok", true);
                r["gameRunning"] = AppEnv.GameRunning();
                r["lang"] = Lang();
                SendJson(ctx, 200, r);
            }
            else if (path == "/api/state") SendJson(ctx, 200, State());
            else if (path == "/api/plan") SendJson(ctx, 200, PlanJson(req.QueryString["id"]));
            else if (path == "/api/job") SendJson(ctx, 200, JobJson());
            else if (path == "/api/suggest")
            {
                Dictionary<string, object> body = ReadBody(req);
                string user = AppEnv.GetString(body, "user");
                string pass = AppEnv.GetString(body, "pass");
                string sid = AppEnv.GetString(body, "id");
                if (string.IsNullOrEmpty(pass) && !string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(user))
                {
                    ServerEntry known = cfg.Servers.FirstOrDefault(x => x.Id == sid);
                    if (known != null) pass = known.PlainPassword();
                }
                string url = AppEnv.GetString(body, "url");
                string name = AppEnv.GetString(body, "name");
                System.Threading.Thread th = new System.Threading.Thread(() =>
                {
                    try
                    {
                        string map = ServerPage.FindMapTitle(url, user, pass);
                        Dictionary<string, object> r = Obj("path", AppEnv.SuggestTarget(name, map));
                        r["map"] = map;
                        SendJson(ctx, 200, r);
                    }
                    catch (Exception ex)
                    {
                        try { SendJson(ctx, 200, Obj("path", AppEnv.SuggestTarget(name, null))); } catch (Exception) { }
                        AppEnv.Log("ServerSync", "Vorschlag: " + ex.Message);
                    }
                });
                th.IsBackground = true;
                th.Start();
            }
            else if (path == "/api/pick")
            {
                Dictionary<string, object> body = ReadBody(req);
                string title = AppEnv.GetString(body, "title");
                string start = AppEnv.GetString(body, "start");
                System.Threading.Thread th = new System.Threading.Thread(() =>
                {
                    try { SendJson(ctx, 200, Obj("path", FolderPicker.Pick(title, start))); }
                    catch (Exception ex)
                    {
                        AppEnv.Log("ServerSync", "Ordnerauswahl: " + ex.Message);
                        try { SendJson(ctx, 500, Obj("error", "unexpected")); } catch (Exception) { }
                    }
                });
                th.SetApartmentState(System.Threading.ApartmentState.STA);
                th.IsBackground = true;
                th.Start();
            }
            else if (path == "/api/library")
            {
                Dictionary<string, object> body = ReadBody(req);
                SetLibrary(AppEnv.GetString(body, "path"));
                SendJson(ctx, 200, State());
            }
            else if (path == "/api/server/save")
            {
                Dictionary<string, object> body = ReadBody(req);
                ServerEntry s = SaveServer(AppEnv.GetString(body, "id"), AppEnv.GetString(body, "name"), AppEnv.GetString(body, "url"), AppEnv.GetString(body, "target"), AppEnv.GetString(body, "user"), body.ContainsKey("pass") ? AppEnv.GetString(body, "pass") : null);
                Dictionary<string, object> r = State();
                r["savedId"] = s.Id;
                SendJson(ctx, 200, r);
            }
            else if (path == "/api/server/delete")
            {
                Dictionary<string, object> body = ReadBody(req);
                EnsureIdle();
                string id = AppEnv.GetString(body, "id");
                ServerEntry s = cfg.Find(id);
                lock (Config.Sync) cfg.Servers.Remove(s);
                plans.Remove(id);
                cfg.Save();
                SendJson(ctx, 200, State());
            }
            else if (path == "/api/check")
            {
                Dictionary<string, object> body = ReadBody(req);
                StartCheck(AppEnv.GetString(body, "id"), AppEnv.GetBool(body, "thorough"));
                SendJson(ctx, 200, JobJson());
            }
            else if (path == "/api/apply")
            {
                Dictionary<string, object> body = ReadBody(req);
                StartApply(AppEnv.GetString(body, "id"));
                SendJson(ctx, 200, JobJson());
            }
            else if (path == "/api/job/cancel")
            {
                if (job != null) job.Cancel = true;
                SendJson(ctx, 200, JobJson());
            }
            else if (path == "/api/activate")
            {
                Dictionary<string, object> body = ReadBody(req);
                Activate(AppEnv.GetString(body, "id"));
                SendJson(ctx, 200, State());
            }
            else if (path == "/api/deactivate")
            {
                AppEnv.SetModsDirectoryOverride(cfg.PreviousOverrideKnown ? cfg.PreviousOverride : null);
                SendJson(ctx, 200, State());
            }
            else if (path == "/api/open")
            {
                Dictionary<string, object> body = ReadBody(req);
                OpenFolder(AppEnv.GetString(body, "path"));
                SendJson(ctx, 200, Obj("ok", true));
            }
            else if (path == "/icon")
            {
                string png = IconPath(req.QueryString["id"], req.QueryString["i"]);
                if (png != null) Send(ctx, 200, File.ReadAllBytes(png), "image/png");
                else SendJson(ctx, 404, Obj("error", "noIcon"));
            }
            else if (path == "/api/forget")
            {
                Dictionary<string, object> body = ReadBody(req);
                EnsureIdle();
                ServerEntry fs = cfg.Find(AppEnv.GetString(body, "id"));
                string prefix = ServerPage.BaseUrl(fs.Url) + "|";
                bool all = AppEnv.GetBool(body, "all");
                HashSet<string> files = new HashSet<string>(AppEnv.GetStrings(body, "mods"), StringComparer.OrdinalIgnoreCase);
                int removed = 0;
                lock (Config.Sync)
                {
                    foreach (string k in cfg.ServerHashes.Keys.ToList())
                    {
                        if (!k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        string file = k.Substring(prefix.Length).Split('|')[0];
                        if (!all && !files.Contains(file)) continue;
                        cfg.ServerHashes.Remove(k);
                        removed++;
                    }
                }
                cfg.Save();
                SendJson(ctx, 200, Obj("removed", removed));
            }
            else if (path == "/api/settings/language")
            {
                Dictionary<string, object> body = ReadBody(req);
                string l = AppEnv.GetString(body, "lang");
                lock (Config.Sync) cfg.Language = (l == "de" || l == "en" || l == "fr") ? l : null;
                cfg.Save();
                SendJson(ctx, 200, State());
            }
            else if (path == "/api/archive/clear")
            {
                EnsureIdle();
                string dir = string.IsNullOrEmpty(cfg.Library) ? null : Sync.ArchiveDir(cfg.Library);
                if (dir != null && Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    AppEnv.Log("Archiv", "Geleert: " + dir);
                }
                SendJson(ctx, 200, State());
            }
            else if (path == "/api/launch")
            {
                try { Process.Start("steam://rungameid/2300320"); }
                catch (Exception ex) { throw new AppError("launchFailed", ex.Message); }
                SendJson(ctx, 200, Obj("ok", true));
            }
            else if (path == "/api/quit")
            {
                SendJson(ctx, 200, Obj("ok", true));
                running = false;
            }
            else SendJson(ctx, 404, Obj("error", "unknown"));
        }
        catch (Exception ex)
        {
            AppError ae = ex as AppError;
            AppEnv.Log("ServerSync", "Fehler bei " + path + ": " + ex.Message);
            Dictionary<string, object> err = Obj("error", ae != null ? ae.Code : "unexpected");
            err["arg"] = ae != null ? ae.Arg : ex.Message;
            try { SendJson(ctx, 500, err); } catch (Exception) { }
        }
    }

    private static Dictionary<string, object> Obj(string key, object value)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d[key] = value;
        return d;
    }

    private static Dictionary<string, object> ReadBody(HttpListenerRequest req)
    {
        using (StreamReader reader = new StreamReader(req.InputStream, Encoding.UTF8))
        {
            string text = reader.ReadToEnd();
            if (string.IsNullOrEmpty(text)) return new Dictionary<string, object>();
            return AppEnv.Json.DeserializeObject(text) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }
    }

    private static void Send(HttpListenerContext ctx, int status, byte[] body, string contentType)
    {
        HttpListenerResponse res = ctx.Response;
        res.StatusCode = status;
        res.ContentType = contentType;
        res.Headers.Add("Cache-Control", contentType == "image/png" ? "max-age=86400" : "no-store");
        res.ContentLength64 = body.Length;
        res.OutputStream.Write(body, 0, body.Length);
        res.OutputStream.Close();
    }

    private static void SendJson(HttpListenerContext ctx, int status, object obj)
    {
        Send(ctx, status, Encoding.UTF8.GetBytes(AppEnv.Json.Serialize(obj)), "application/json; charset=utf-8");
    }

    private void EnsureIdle()
    {
        if (job != null && !job.Finished) throw new AppError("busy", null);
    }

    private string Lang()
    {
        string l = cfg.Language;
        return (l == "de" || l == "en" || l == "fr") ? l : uiLang;
    }

    private static void TargetUsage(string dir, out int count, out long total, out long extra)
    {
        count = 0;
        total = 0;
        extra = 0;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (string f in Directory.GetFiles(dir, "*.zip"))
        {
            long len;
            try { len = new FileInfo(f).Length; } catch (Exception) { continue; }
            count++;
            total += len;
            if (NativeLinks.LinkCount(f) <= 1) extra += len;
        }
    }

    private Dictionary<string, object> State()
    {
        string active = AppEnv.ActiveModsDirectoryOverride();
        List<object> servers = new List<object>();
        foreach (ServerEntry s in cfg.Servers)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["Id"] = s.Id;
            d["Name"] = s.Name;
            d["Url"] = s.Url;
            d["Target"] = s.Target;
            d["LastCheck"] = s.LastCheck;
            d["Username"] = s.Username;
            d["HasPassword"] = !string.IsNullOrEmpty(s.Password);
            d["TargetExists"] = !string.IsNullOrEmpty(s.Target) && Directory.Exists(s.Target);
            d["Synced"] = !string.IsNullOrEmpty(s.Target) && Sync.HasMarker(s.Target);
            d["SameVolume"] = NativeLinks.SameVolume(cfg.Library, s.Target);
            d["IsActive"] = AppEnv.SamePath(active, s.Target);
            d["HasPlan"] = plans.ContainsKey(s.Id);
            int uc;
            long ut, ux;
            TargetUsage(s.Target, out uc, out ut, out ux);
            d["UsageCount"] = uc;
            d["UsageTotal"] = ut;
            d["UsageExtra"] = ux;
            servers.Add(d);
        }
        Dictionary<string, object> r = new Dictionary<string, object>();
        r["lang"] = Lang();
        r["gameRunning"] = AppEnv.GameRunning();
        r["library"] = cfg.Library;
        r["dataDir"] = AppEnv.DataDir;
        r["langSetting"] = cfg.Language ?? "";
        long archBytes = 0;
        int archCount = 0;
        string archDir = string.IsNullOrEmpty(cfg.Library) ? null : Sync.ArchiveDir(cfg.Library);
        if (archDir != null && Directory.Exists(archDir))
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(archDir, "*", SearchOption.AllDirectories))
                {
                    archCount++;
                    try { archBytes += new FileInfo(f).Length; } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
        r["archiveDir"] = archDir;
        r["archiveBytes"] = archBytes;
        r["archiveCount"] = archCount;
        r["libraryExists"] = !string.IsNullOrEmpty(cfg.Library) && Directory.Exists(cfg.Library);
        r["store"] = string.IsNullOrEmpty(cfg.Library) ? null : Sync.StoreDir(cfg.Library);
        r["activeOverride"] = active;
        r["gameSettingsFound"] = File.Exists(AppEnv.GameSettingsPath);
        r["canRestore"] = cfg.PreviousOverrideKnown && cfg.Servers.Any(s => AppEnv.SamePath(active, s.Target));
        r["previousOverride"] = cfg.PreviousOverride;
        r["servers"] = servers;
        r["job"] = job == null ? null : JobJson();
        return r;
    }

    private void SetLibrary(string path)
    {
        EnsureIdle();
        string dir = AppEnv.NormalizeDir(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new AppError("libraryMissing", path);
        foreach (ServerEntry s in cfg.Servers)
        {
            if (AppEnv.SamePath(s.Target, dir) || AppEnv.IsInside(dir, s.Target)) throw new AppError("libraryIsTarget", s.Name);
        }
        lock (Config.Sync) cfg.Library = dir;
        plans.Clear();
        cfg.Save();
    }

    private ServerEntry SaveServer(string id, string name, string url, string target, string user, string pass)
    {
        EnsureIdle();
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new AppError("nameEmpty", null);
        string baseUrl = ServerPage.BaseUrl(url);
        string tgt = AppEnv.NormalizeDir(target);
        Sync.ValidateFolders(cfg.Library, tgt);
        foreach (ServerEntry other in cfg.Servers)
        {
            if (other.Id != id && AppEnv.SamePath(other.Target, tgt)) throw new AppError("targetUsed", other.Name);
        }
        ServerEntry s;
        if (string.IsNullOrEmpty(id))
        {
            s = new ServerEntry();
            s.Id = Guid.NewGuid().ToString("N").Substring(0, 10);
            lock (Config.Sync) cfg.Servers.Add(s);
        }
        else
        {
            s = cfg.Find(id);
            if (!AppEnv.SamePath(s.Target, tgt) || s.Url != baseUrl) plans.Remove(s.Id);
        }
        lock (Config.Sync)
        {
            s.Name = name;
            s.Url = baseUrl;
            s.Target = tgt;
            s.Username = string.IsNullOrEmpty(user) ? null : user.Trim();
            if (s.Username == null) s.Password = null;
            else if (pass != null) s.SetPassword(pass);
        }
        cfg.Save();
        return s;
    }

    private void StartCheck(string id, bool thorough)
    {
        EnsureIdle();
        ServerEntry s = cfg.Find(id);
        Job j = new Job();
        j.Kind = thorough ? "verify" : "check";
        j.ServerId = id;
        job = j;
        Task.Factory.StartNew(() =>
        {
            try
            {
                s.ApplyLogin();
                Plan p = Sync.Check(cfg, s, thorough, j);
                lock (plans) plans[id] = p;
            }
            catch (Exception ex) { j.Fail(ex); AppEnv.Log("Pruefen", s.Name + ": " + ex.Message); }
            finally
            {
                try { cfg.Save(); } catch (Exception) { }
                j.Finished = true;
            }
        }, TaskCreationOptions.LongRunning);
    }

    private void StartApply(string id)
    {
        EnsureIdle();
        ServerEntry s = cfg.Find(id);
        Plan p;
        lock (plans) if (!plans.TryGetValue(id, out p)) throw new AppError("noPlan", null);
        Job j = new Job();
        j.Kind = "apply";
        j.ServerId = id;
        job = j;
        Task.Factory.StartNew(() =>
        {
            try
            {
                s.ApplyLogin();
                Sync.Apply(cfg, s, p, j);
                j.Step("recheck", 1);
                Plan fresh = Sync.Check(cfg, s, false, j);
                lock (plans) plans[id] = fresh;
            }
            catch (Exception ex)
            {
                j.Fail(ex);
                AppEnv.Log("Abgleich", s.Name + ": " + ex.Message);
                lock (plans) plans.Remove(id);
            }
            finally
            {
                try { cfg.Save(); } catch (Exception) { }
                j.Finished = true;
            }
        }, TaskCreationOptions.LongRunning);
    }

    private Dictionary<string, object> JobJson()
    {
        Job j = job;
        if (j == null) return null;
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["Kind"] = j.Kind;
        d["ServerId"] = j.ServerId;
        d["Phase"] = j.Phase;
        d["Done"] = j.Done;
        d["Total"] = j.Total;
        d["Current"] = j.Current;
        d["BytesDone"] = j.BytesDone;
        d["BytesTotal"] = j.BytesTotal;
        d["Finished"] = j.Finished;
        d["Cancel"] = j.Cancel;
        d["ErrorCode"] = j.ErrorCode;
        d["ErrorArg"] = j.ErrorArg;
        d["Compared"] = j.Compared;
        d["Identical"] = j.Identical;
        d["Saved"] = j.Saved;
        d["SavedBytes"] = j.SavedBytes;
        return d;
    }

    private Dictionary<string, object> PlanJson(string id)
    {
        ServerEntry s = cfg.Find(id);
        Plan p;
        lock (plans) if (!plans.TryGetValue(id, out p)) return Obj("none", true);
        List<object> rows = new List<object>();
        for (int i = 0; i < p.Rows.Count; i++)
        {
            PlanRow r = p.Rows[i];
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["I"] = i;
            d["ModName"] = r.ModName;
            d["FileName"] = r.FileName;
            d["Title"] = r.Title;
            d["Version"] = r.Version;
            d["Author"] = r.Author;
            d["ModHub"] = r.ModHub;
            d["Bytes"] = r.Bytes;
            d["Status"] = r.Status;
            d["LocalVersion"] = r.LocalVersion;
            d["Verified"] = r.Verified;
            d["InTarget"] = r.InTarget;
            d["IsDir"] = r.IsDir;
            d["RemoveMode"] = r.RemoveMode;
            d["ErrorCode"] = r.ErrorCode;
            d["ErrorArg"] = r.ErrorArg;
            d["HasIcon"] = r.IconPath != null;
            d["SourceFolder"] = r.Status == "link" && r.Source != null ? RelativeFolder(p.Library, r.Source) : null;
            d["IsMap"] = r.IsMap;
            rows.Add(d);
        }
        Dictionary<string, object> res = new Dictionary<string, object>();
        res["ServerId"] = s.Id;
        res["Target"] = p.Target;
        res["Library"] = p.Library;
        res["SameVolume"] = p.SameVolume;
        res["Thorough"] = p.Thorough;
        res["ForeignTarget"] = p.ForeignTarget;
        res["ServerTotal"] = p.ServerTotal;
        res["InactiveOnServer"] = p.InactiveOnServer;
        res["CanManage"] = p.CanManage;
        res["DlcKnown"] = p.DlcKnown;
        res["Dlcs"] = p.Dlcs;
        res["Created"] = p.Created.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        res["DownloadBytes"] = p.Rows.Where(r => r.Status == "download" || r.Status == "update" || r.Status == "check").Sum(r => Math.Max(0, r.Bytes));
        res["MapModName"] = p.MapModName;
        res["MapTitle"] = p.MapTitle;
        res["MapTitles"] = p.MapTitles;
        res["RunningMap"] = p.RunningMap;
        res["RunningSlot"] = p.RunningSlot;
        res["RunningMatched"] = p.RunningMatched;
        res["MapCount"] = p.MapCount;
        res["MapsRootKnown"] = p.MapsRootKnown;
        res["TargetHasMap"] = p.TargetHasMap;
        res["MapsRoot"] = AppEnv.MapsRoot;
        res["MapFolders"] = p.MapFolders;
        res["TargetOtherMaps"] = p.TargetOtherMaps;
        res["Rows"] = rows;
        return res;
    }

    private static string RelativeFolder(string library, string file)
    {
        string dir = Path.GetDirectoryName(file);
        if (AppEnv.SamePath(dir, library)) return ".";
        if (AppEnv.IsInside(dir, library)) return AppEnv.NormalizeDir(dir).Substring(AppEnv.NormalizeDir(library).Length + 1);
        return dir;
    }

    private void Activate(string id)
    {
        ServerEntry s = cfg.Find(id);
        if (string.IsNullOrEmpty(s.Target) || !Sync.HasMarker(s.Target)) throw new AppError("notSynced", null);
        string current = AppEnv.ActiveModsDirectoryOverride();
        bool currentIsServer = cfg.Servers.Any(x => AppEnv.SamePath(current, x.Target));
        AppEnv.SetModsDirectoryOverride(s.Target);
        if (!currentIsServer)
        {
            lock (Config.Sync)
            {
                cfg.PreviousOverride = current;
                cfg.PreviousOverrideKnown = true;
            }
            cfg.Save();
        }
    }

    private void OpenFolder(string path)
    {
        string dir = AppEnv.NormalizeDir(path);
        bool allowed = (cfg.Library != null && AppEnv.SamePath(dir, Sync.ArchiveDir(cfg.Library))) || AppEnv.SamePath(dir, AppEnv.DataDir) || AppEnv.SamePath(dir, cfg.Library) || (cfg.Library != null && AppEnv.SamePath(dir, Sync.StoreDir(cfg.Library))) || cfg.Servers.Any(s => AppEnv.SamePath(dir, s.Target));
        if (!allowed || !Directory.Exists(dir)) throw new AppError("folderMissing", path);
        FolderPicker.OpenFolderInFront(dir);
    }

    private string IconPath(string id, string index)
    {
        Plan p;
        lock (plans) if (id == null || !plans.TryGetValue(id, out p)) return null;
        int i;
        if (!int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) || i < 0 || i >= p.Rows.Count) return null;
        string file = p.Rows[i].IconPath;
        if (file == null || !File.Exists(file)) return null;
        ZipInfo zi = Sync.ReadZip(file);
        return Icons.ModIconPng(file, false, zi.IconFilename);
    }
}
