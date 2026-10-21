using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Icons
{
    public static string CacheDir
    {
        get { return Path.Combine(AppEnv.DataDir, "icons"); }
    }

    private static List<string> Candidates(string declared)
    {
        List<string> result = new List<string>();
        result.Add(declared);
        string dir = Path.GetDirectoryName(declared);
        string baseName = Path.GetFileNameWithoutExtension(declared);
        foreach (string ext in new string[] { ".dds", ".png" })
        {
            string alt = string.IsNullOrEmpty(dir) ? baseName + ext : (dir + "/" + baseName + ext).Replace('\\', '/');
            if (!result.Contains(alt)) result.Add(alt);
        }
        return result;
    }

    public static string ModIconPng(string modPath, bool isDir, string iconFilename)
    {
        if (string.IsNullOrEmpty(modPath) || string.IsNullOrEmpty(iconFilename)) return null;
        string fingerprint = isDir ? AppEnv.Fingerprint(Path.Combine(modPath, "modDesc.xml")) : AppEnv.Fingerprint(modPath);
        string key = Regex.Replace(Path.GetFileName(modPath), @"\.zip$", "", RegexOptions.IgnoreCase);
        string pngPath = Path.Combine(CacheDir, key + "_" + fingerprint + "_192.png");
        if (File.Exists(pngPath)) return pngPath;
        Directory.CreateDirectory(CacheDir);
        try
        {
            byte[] bytes = null;
            if (isDir)
            {
                foreach (string c in Candidates(iconFilename))
                {
                    string p = Path.Combine(modPath, c);
                    if (File.Exists(p)) { bytes = File.ReadAllBytes(p); break; }
                }
            }
            else
            {
                using (ZipArchive zip = ZipFile.OpenRead(modPath))
                {
                    foreach (string c in Candidates(iconFilename))
                    {
                        string wanted = c.Replace('\\', '/');
                        foreach (ZipArchiveEntry entry in zip.Entries)
                        {
                            if (!string.Equals(entry.FullName.Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                            using (Stream s = entry.Open())
                            using (MemoryStream ms = new MemoryStream())
                            {
                                s.CopyTo(ms);
                                bytes = ms.ToArray();
                            }
                            break;
                        }
                        if (bytes != null) break;
                    }
                }
            }
            if (bytes == null) return null;
            ConvertToPng(bytes, pngPath, 192, false);
            return pngPath;
        }
        catch (Exception ex)
        {
            AppEnv.Log("Icon", "Icon von " + key + " konnte nicht umgewandelt werden: " + ex.Message);
            try { File.Delete(pngPath); } catch (Exception) { }
            return null;
        }
    }

    private static void ConvertToPng(byte[] bytes, string pngPath, int size, bool keepAspect)
    {
        using (MemoryStream input = new MemoryStream(bytes))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            double sx = (double)size / frame.PixelWidth;
            double sy = keepAspect ? sx : (double)size / frame.PixelHeight;
            TransformedBitmap scaled = new TransformedBitmap(frame, new ScaleTransform(sx, sy));
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using (FileStream output = File.Create(pngPath))
            {
                encoder.Save(output);
            }
        }
    }
}
