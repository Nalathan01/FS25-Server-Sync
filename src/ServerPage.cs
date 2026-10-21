using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

internal sealed class ServerMod
{
    public string Title;
    public string Version;
    public string Author;
    public string FileName;
    public string ModName;
    public string SizeText;
    public string Url;
    public bool ModHub;
    public bool Active;
    public int Index = -1;
    public bool IsMap;
    public long Bytes = -1;
}

internal static class ServerPage
{
    private const string UserAgent = "FS25-Server-Sync";
    private static readonly string[] YesWords = new string[] { "yes", "ja", "oui" };

    public static string BaseUrl(string input)
    {
        string s = (input ?? "").Trim();
        if (s.Length == 0) throw new AppError("urlEmpty", null);
        if (!Regex.IsMatch(s, "^[a-zA-Z]+://")) s = "http://" + s;
        Uri u;
        if (!Uri.TryCreate(s, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) throw new AppError("urlInvalid", input);
        return u.GetLeftPart(UriPartial.Authority) + "/";
    }

    private sealed class Login
    {
        public string User;
        public string Pass;
        public CookieContainer Cookies = new CookieContainer();
    }

    private static readonly Dictionary<string, Login> Logins = new Dictionary<string, Login>(StringComparer.OrdinalIgnoreCase);

    public static void SetLogin(string url, string user, string pass)
    {
        string baseUrl;
        try { baseUrl = BaseUrl(url); }
        catch (AppError) { return; }
        lock (Logins)
        {
            Login old;
            if (Logins.TryGetValue(baseUrl, out old) && old.User == user && old.Pass == pass) return;
            if (string.IsNullOrEmpty(user)) { Logins.Remove(baseUrl); return; }
            Login l = new Login();
            l.User = user;
            l.Pass = pass ?? "";
            Logins[baseUrl] = l;
        }
    }

    private static Login LoginFor(string url)
    {
        Uri u;
        if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return null;
        lock (Logins)
        {
            Login l;
            return Logins.TryGetValue(u.GetLeftPart(UriPartial.Authority) + "/", out l) ? l : null;
        }
    }

    private static bool IsLoginPage(string html)
    {
        return Regex.IsMatch(html, "<title>[^<]*Login", RegexOptions.IgnoreCase) || html.IndexOf("name=\"password\"", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetText(string url)
    {
        HttpWebRequest req = Request(url, 30000);
        using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
        using (StreamReader reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    private static bool DoLogin(string baseUrl)
    {
        Login l = LoginFor(baseUrl);
        if (l == null) return false;
        HttpWebRequest req = Request(baseUrl + "index.html?lang=en", 30000);
        req.Method = "POST";
        req.ContentType = "application/x-www-form-urlencoded";
        byte[] body = Encoding.UTF8.GetBytes("username=" + Uri.EscapeDataString(l.User) + "&password=" + Uri.EscapeDataString(l.Pass) + "&login=Login");
        req.ContentLength = body.Length;
        using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
        using (StreamReader reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
        {
            return !IsLoginPage(reader.ReadToEnd());
        }
    }

    private static void EnsureNotLogin(HttpWebResponse res, string url)
    {
        if (res.ContentType == null || res.ContentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) < 0) return;
        throw new AppError(LoginFor(url) != null ? "loginFailed" : "modsPageLocked", null);
    }

    private static void Relogin(string url)
    {
        Uri u = new Uri(url);
        DoLogin(u.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static HttpWebRequest Request(string url, int timeoutMs)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        Login l = LoginFor(url);
        if (l != null) req.CookieContainer = l.Cookies;
        req.UserAgent = UserAgent;
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = 60000;
        req.AllowAutoRedirect = true;
        return req;
    }

    public static List<ServerMod> Fetch(string baseUrl)
    {
        bool canManage;
        return Fetch(baseUrl, out canManage);
    }

    public static long ParseSize(string text)
    {
        if (string.IsNullOrEmpty(text)) return -1;
        Match m = Regex.Match(text, @"([0-9]+(?:[.,][0-9]+)?)\s*(KB|MB|GB|B)", RegexOptions.IgnoreCase);
        if (!m.Success) return -1;
        double v = double.Parse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
        string u = m.Groups[2].Value.ToUpperInvariant();
        double f = u == "GB" ? 1073741824.0 : u == "MB" ? 1048576.0 : u == "KB" ? 1024.0 : 1.0;
        return (long)(v * f);
    }

    public static List<ServerMod> Fetch(string baseUrl, out bool canManage)
    {
        List<ServerMod> dlcs;
        return Fetch(baseUrl, out canManage, out dlcs);
    }

    public static List<ServerMod> ParseDlcs(string html)
    {
        List<ServerMod> result = new List<ServerMod>();
        string[] blocks = html.Split(new string[] { "<div class=\"container-row grid-row\">" }, StringSplitOptions.None);
        for (int i = 1; i < blocks.Length; i++)
        {
            string b = blocks[i];
            string file = TitleAttr(b, "Filename");
            if (file == null || !file.EndsWith(".dlc", StringComparison.OrdinalIgnoreCase)) continue;
            ServerMod m = new ServerMod();
            m.FileName = file;
            m.ModName = Path.GetFileNameWithoutExtension(file);
            m.Title = TitleAttr(b, "Name") ?? Cell(b, "Name") ?? m.ModName;
            m.Version = TitleAttr(b, "Version") ?? Cell(b, "Version");
            m.Active = IsYes(Cell(b, "Active"));
            result.Add(m);
        }
        return result;
    }

    public static List<ServerMod> Fetch(string baseUrl, out bool canManage, out List<ServerMod> dlcs)
    {
        canManage = false;
        dlcs = new List<ServerMod>();
        string html;
        try
        {
            html = GetText(baseUrl + "mods.html?lang=en");
            if (IsLoginPage(html) && LoginFor(baseUrl) != null)
            {
                if (!DoLogin(baseUrl)) throw new AppError("loginFailed", null);
                html = GetText(baseUrl + "mods.html?lang=en");
            }
        }
        catch (WebException ex)
        {
            throw new AppError("serverUnreachable", ex.Message);
        }
        if (html.IndexOf("Dedicated Server", StringComparison.OrdinalIgnoreCase) < 0) throw new AppError("noServerPage", null);
        canManage = html.IndexOf("name=\"modUpload\"", StringComparison.OrdinalIgnoreCase) >= 0;
        dlcs = ParseDlcs(html);
        List<ServerMod> mods = Parse(html, baseUrl);
        if (mods.Count == 0)
        {
            if (IsLoginPage(html)) throw new AppError(LoginFor(baseUrl) != null ? "loginFailed" : "modsPageLocked", null);
            throw new AppError("noModsFound", null);
        }
        return mods;
    }

    public static List<ServerMod> Parse(string html, string baseUrl)
    {
        List<ServerMod> result = new List<ServerMod>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] blocks = html.Split(new string[] { "<div class=\"container-row grid-row\">" }, StringSplitOptions.None);
        for (int i = 1; i < blocks.Length; i++)
        {
            string b = blocks[i];
            ServerMod m = new ServerMod();
            m.Title = TitleAttr(b, "Name") ?? Cell(b, "Name");
            m.Version = TitleAttr(b, "Version") ?? Cell(b, "Version");
            m.Author = TitleAttr(b, "Author") ?? Cell(b, "Author");
            m.SizeText = TitleAttr(b, "Size") ?? Cell(b, "Size");
            m.ModHub = IsYes(Cell(b, "ModHub") ?? Cell(b, "Hub"));
            m.Active = IsYes(Cell(b, "Active"));
            Match idx = Regex.Match(b, @"mod_index=(\d+)");
            if (idx.Success) m.Index = int.Parse(idx.Groups[1].Value);
            Match dl = Regex.Match(b, "href=\"(mods/[^\"]+\\.zip)\"", RegexOptions.IgnoreCase);
            if (dl.Success)
            {
                string rel = WebUtility.HtmlDecode(dl.Groups[1].Value);
                m.Url = baseUrl + rel;
                m.FileName = Uri.UnescapeDataString(rel.Substring("mods/".Length));
            }
            else
            {
                m.FileName = TitleAttr(b, "Filename");
            }
            if (string.IsNullOrEmpty(m.FileName) || !m.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            m.FileName = Path.GetFileName(m.FileName);
            m.ModName = Path.GetFileNameWithoutExtension(m.FileName);
            if (!seen.Add(m.ModName)) continue;
            if (string.IsNullOrEmpty(m.Title)) m.Title = m.ModName;
            result.Add(m);
        }
        return result;
    }

    private static string TitleAttr(string block, string label)
    {
        Match m = Regex.Match(block, "title=\"([^\"]*)\">\\s*<div[^>]*><b>" + label + "</b>");
        if (!m.Success) return null;
        string v = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        return v.Length == 0 ? null : v;
    }

    private static string Cell(string block, string label)
    {
        Match m = Regex.Match(block, "<b>" + label + "</b></div>\\s*<div[^>]*>(.*?)</div>", RegexOptions.Singleline);
        if (!m.Success) return null;
        string v = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", "")).Trim();
        return v.Length == 0 ? null : v;
    }

    private static bool IsYes(string v)
    {
        if (v == null) return false;
        foreach (string y in YesWords) if (string.Equals(v, y, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly Dictionary<string, string> MapTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static string FindMapTitle(string input, string user, string pass)
    {
        SetLogin(input, user, pass);
        string baseUrl;
        try { baseUrl = BaseUrl(input); }
        catch (AppError) { return null; }
        lock (MapTitles)
        {
            string cached;
            if (MapTitles.TryGetValue(baseUrl, out cached)) return cached;
        }
        List<ServerMod> mods;
        try { mods = Fetch(baseUrl); }
        catch (AppError) { return null; }
        string found = null;
        List<string> titles = new List<string>();
        System.Threading.Tasks.ParallelOptions po = new System.Threading.Tasks.ParallelOptions();
        po.MaxDegreeOfParallelism = 8;
        System.Threading.Tasks.Parallel.ForEach(mods.FindAll(m => m.Active), po, (m, loop) =>
        {
            if (IsMapMod(baseUrl, m.Index)) lock (titles) titles.Add(m.Title);
        });
        titles.Sort(StringComparer.OrdinalIgnoreCase);
        found = titles.Count == 1 ? titles[0] : null;
        lock (MapTitles) MapTitles[baseUrl] = found;
        return found;
    }

    public static void FetchRunning(string baseUrl, out string slot, out string map)
    {
        slot = null;
        map = null;
        if (LoginFor(baseUrl) == null) return;
        try
        {
            string html = GetText(baseUrl + "index.html?lang=en");
            if (IsLoginPage(html))
            {
                if (!DoLogin(baseUrl)) return;
                html = GetText(baseUrl + "index.html?lang=en");
            }
            slot = ConfigValue(html, "Savegame Slot");
            map = ConfigValue(html, "Map");
        }
        catch (WebException) { }
    }

    private static string ConfigValue(string html, string label)
    {
        Match m = Regex.Match(html, "column-label\">" + Regex.Escape(label) + "</div>\\s*<div class=\"medium-9 columns\">([^<]+)</div>");
        if (!m.Success) return null;
        string v = Regex.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), "\\s+", " ").Trim();
        return v.Length == 0 ? null : v;
    }

    public static bool IsMapMod(string baseUrl, int index)
    {
        if (index < 0) return false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                HttpWebRequest req = Request(baseUrl + "mod.html?lang=en&mod_index=" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), 30000);
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                {
                    Match m = Regex.Match(reader.ReadToEnd(), @"<b>Map</b></td><td>\s*(\w+)");
                    return m.Success && IsYes(m.Groups[1].Value);
                }
            }
            catch (WebException) { }
        }
        return false;
    }

    public static long ProbeSize(string url)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            HttpWebRequest req = Request(url, 30000);
            try
            {
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                {
                    bool html = res.ContentType != null && res.ContentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0;
                    long len = res.ContentLength;
                    req.Abort();
                    if (html)
                    {
                        if (attempt == 0 && LoginFor(url) != null) { Relogin(url); continue; }
                        throw new AppError(LoginFor(url) != null ? "loginFailed" : "modsPageLocked", null);
                    }
                    if (len >= 0) return len;
                }
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.RequestCanceled) continue;
                if (attempt >= 1) throw new AppError("probeFailed", ex.Message);
            }
        }
        return -1;
    }

    public static void Download(string url, string dest, long expected, Action<long> progress, Func<bool> cancelled)
    {
        string part = dest + ".part";
        if (File.Exists(part)) File.Delete(part);
        HttpWebRequest req = Request(url, 60000);
        try
        {
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            using (Stream input = res.GetResponseStream())
            using (FileStream output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                if (expected >= 0 && res.ContentLength >= 0 && res.ContentLength != expected) throw new AppError("sizeChanged", Path.GetFileName(dest));
                byte[] buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (cancelled()) { req.Abort(); throw new AppError("cancelled", null); }
                    output.Write(buffer, 0, read);
                    total += read;
                    progress(read);
                }
                if (expected >= 0 && total != expected) throw new AppError("downloadIncomplete", Path.GetFileName(dest));
            }
            File.Move(part, dest);
        }
        catch (WebException ex)
        {
            TryDelete(part);
            throw new AppError("downloadFailed", Path.GetFileName(dest) + ": " + ex.Message);
        }
        catch (Exception)
        {
            TryDelete(part);
            throw;
        }
    }

    public static string DownloadCompare(string url, long expected, string compareFile, string tmp, string modName, Action<long> progress, Func<bool> cancelled, out bool identical)
    {
        identical = false;
        string part = tmp + ".part";
        TryDelete(part);
        HttpWebRequest req = Request(url, 60000);
        FileStream local = null;
        FileStream output = null;
        try
        {
            if (compareFile != null && File.Exists(compareFile) && new FileInfo(compareFile).Length == expected)
                local = new FileStream(compareFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            using (Stream input = res.GetResponseStream())
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                EnsureNotLogin(res, url);
                if (expected >= 0 && res.ContentLength >= 0 && res.ContentLength != expected) throw new AppError("sizeChanged", Path.GetFileName(tmp));
                byte[] buffer = new byte[1 << 16];
                byte[] other = new byte[1 << 16];
                long pos = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (cancelled()) { req.Abort(); throw new AppError("cancelled", null); }
                    md5.TransformBlock(buffer, 0, read, null, 0);
                    if (output == null && local != null)
                    {
                        int got = 0;
                        while (got < read)
                        {
                            int n = local.Read(other, got, read-got);
                            if (n <= 0) break;
                            got += n;
                        }
                        bool same = got == read;
                        for (int i = 0; same && i < read; i++) if (buffer[i] != other[i]) same = false;
                        if (!same)
                        {
                            output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                            local.Position = 0;
                            long left = pos;
                            while (left > 0)
                            {
                                int n = local.Read(other, 0, (int)Math.Min(other.Length, left));
                                if (n <= 0) throw new AppError("downloadFailed", Path.GetFileName(tmp));
                                output.Write(other, 0, n);
                                left -= n;
                            }
                            local.Dispose();
                            local = null;
                            output.Write(buffer, 0, read);
                        }
                    }
                    else
                    {
                        if (output == null) output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                        output.Write(buffer, 0, read);
                    }
                    pos += read;
                    progress(read);
                }
                if (expected >= 0 && pos != expected) throw new AppError("downloadIncomplete", Path.GetFileName(tmp));
                byte[] name = Encoding.ASCII.GetBytes(modName);
                md5.TransformFinalBlock(name, 0, name.Length);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in md5.Hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                identical = output == null && local != null;
                if (output != null)
                {
                    output.Dispose();
                    output = null;
                    if (File.Exists(tmp)) File.Delete(tmp);
                    File.Move(part, tmp);
                }
                return sb.ToString();
            }
        }
        catch (WebException ex)
        {
            throw new AppError("downloadFailed", Path.GetFileName(tmp) + ": " + ex.Message);
        }
        finally
        {
            if (output != null) output.Dispose();
            if (local != null) local.Dispose();
            TryDelete(part);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}
