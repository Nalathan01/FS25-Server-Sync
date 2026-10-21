using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

internal sealed class ServerEntry
{
    public string Id;
    public string Name;
    public string Url;
    public string Target;
    public string LastCheck;
    public string Username;
    public string Password;

    public string PlainPassword()
    {
        if (string.IsNullOrEmpty(Password)) return null;
        try
        {
            byte[] data = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(Password), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception) { return null; }
    }

    public void SetPassword(string plain)
    {
        if (string.IsNullOrEmpty(plain)) { Password = null; return; }
        byte[] data = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        Password = Convert.ToBase64String(data);
    }

    public void ApplyLogin()
    {
        ServerPage.SetLogin(Url, Username, PlainPassword());
    }
}

internal sealed class Config
{
    public string Library;
    public string PreviousOverride;
    public bool PreviousOverrideKnown;
    public List<ServerEntry> Servers = new List<ServerEntry>();
    public Dictionary<string, string> LocalHashes = new Dictionary<string, string>();
    public Dictionary<string, string> ServerHashes = new Dictionary<string, string>();
    public Dictionary<string, string> Placed = new Dictionary<string, string>();

    public static readonly object Sync = new object();

    private static string FilePath
    {
        get { return Path.Combine(AppEnv.DataDir, "serversync.json"); }
    }

    private static void Migrate()
    {
        if (File.Exists(FilePath)) return;
        string[] old = new string[]
        {
            Path.Combine(AppEnv.SettingsDir ?? AppEnv.DataDir, "serverabgleich.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LS25 Server Abgleich", "serverabgleich.json")
        };
        foreach (string o in old)
        {
            if (!File.Exists(o)) continue;
            try { File.Copy(o, FilePath); AppEnv.Log("Config", "Uebernommen aus " + o); } catch (Exception) { }
            return;
        }
    }

    public static Config Load()
    {
        Migrate();
        Config c = null;
        try
        {
            if (File.Exists(FilePath)) c = AppEnv.Json.Deserialize<Config>(File.ReadAllText(FilePath, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            AppEnv.Log("Config", "Nicht lesbar: " + ex.Message);
        }
        if (c == null) c = new Config();
        if (c.Servers == null) c.Servers = new List<ServerEntry>();
        if (c.LocalHashes == null) c.LocalHashes = new Dictionary<string, string>();
        if (c.ServerHashes == null) c.ServerHashes = new Dictionary<string, string>();
        if (c.Placed == null) c.Placed = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(c.Library)) c.Library = AppEnv.DefaultLibrary();
        return c;
    }

    public void Save()
    {
        lock (Sync)
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, AppEnv.Json.Serialize(this), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
    }

    public ServerEntry Find(string id)
    {
        foreach (ServerEntry s in Servers) if (s.Id == id) return s;
        throw new AppError("serverUnknown", id);
    }
}
