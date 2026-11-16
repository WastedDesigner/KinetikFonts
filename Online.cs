// Free font sources: catalogues to search and downloads to install. No API keys; each source's public endpoints.
//   Google Fonts  - open source (SIL OFL, Apache, Ubuntu licence). Catalogue from fonts.google.com, files from fonts.gstatic.com.
//   Fontshare     - Indian Type Foundry; free for personal and commercial use.
//   Font Squirrel - hand-picked fonts that are free for commercial use.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

class OnlineFont
{
    public string Source { get; set; }
    public string Id { get; set; }
    public string Family { get; set; }
    public string Category { get; set; }
    public string Designer { get; set; }
    public string License { get; set; }
    public string Page { get; set; }
    public int Pop { get; set; }
    public DateTime Added { get; set; }
}

static class Online
{
    public static readonly string[] Sources = { "Google Fonts", "Fontshare", "Font Squirrel" };
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinetikFonts", "cache");
    const long MaxDownload = 300L << 20;

    static readonly HttpClient Http = Make();

    static HttpClient Make()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) KinetikFonts/1.0"); // Font Squirrel refuses clients without one
        return c;
    }

    public static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s.Length > 80 ? s[..80] : s;
    }

    public static string PreviewDir(OnlineFont f) => Path.Combine(Root, "preview", Safe(f.Source), Safe(f.Id));

    // A provider's own category word → one of Library.Cats.
    public static string MapCat(string raw)
    {
        var s = (raw ?? "").ToLowerInvariant();
        if (s.Contains("mono") || s.Contains("typewriter") || s.Contains("programming")) return "Monospace";
        if (s.Contains("slab")) return "Slab";
        if (s.Contains("sans")) return "Sans serif";
        if (s.Contains("serif")) return "Serif";
        if (s.Contains("script") || s.Contains("hand") || s.Contains("calligraph") || s.Contains("brush")) return "Script";
        if (s.Contains("dingbat") || s.Contains("symbol") || s.Contains("icon")) return "Symbol";
        if (s.Contains("display") || s.Contains("retro") || s.Contains("novelty") || s.Contains("comic") || s.Contains("blackletter") || s.Contains("stencil") || s.Contains("pixel") || s.Contains("grunge") || s.Contains("decor")) return "Display";
        return "Other";
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    // ---- catalogues

    public static async Task<List<OnlineFont>> Catalog(string source, bool force, CancellationToken ct)
    {
        var file = Path.Combine(Root, "catalog-" + Safe(source) + ".json");
        List<OnlineFont> Cached()
        {
            try { return JsonSerializer.Deserialize<List<OnlineFont>>(File.ReadAllText(file)); } catch { return null; }
        }
        if (!force && File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7))
        {
            var c = Cached();
            if (c?.Count > 0) return c;
        }
        try
        {
            var list = source switch
            {
                "Google Fonts" => await Google(ct),
                "Fontshare" => await Fontshare(ct),
                "Font Squirrel" => await Squirrel(ct),
                _ => throw new ArgumentException("Unknown source " + source),
            };
            if (list.Count == 0) throw new InvalidDataException("the catalogue came back empty");
            Directory.CreateDirectory(Root);
            File.WriteAllText(file, JsonSerializer.Serialize(list));
            return list;
        }
        catch when (File.Exists(file) && Cached()?.Count > 0) { return Cached(); } // offline: an older copy beats nothing
    }

    static async Task<List<OnlineFont>> Google(CancellationToken ct)
    {
        var json = await Http.GetStringAsync("https://fonts.google.com/metadata/fonts", ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<OnlineFont>();
        foreach (var e in doc.RootElement.GetProperty("familyMetadataList").EnumerateArray())
        {
            var fam = Str(e, "family");
            if (fam.Length == 0) continue;
            var designers = e.TryGetProperty("designers", out var d) && d.ValueKind == JsonValueKind.Array ? string.Join(", ", d.EnumerateArray().Select(x => x.GetString())) : "";
            list.Add(new OnlineFont
            {
                Source = "Google Fonts", Id = fam, Family = fam, Category = MapCat(Str(e, "category")), Designer = designers,
                License = "Open source (SIL OFL, Apache or Ubuntu licence)",
                Page = "https://fonts.google.com/specimen/" + Uri.EscapeDataString(fam),
                Pop = e.TryGetProperty("popularity", out var p) && p.TryGetInt32(out var pi) ? pi : 0,
                Added = DateTime.TryParse(Str(e, "dateAdded"), out var da) ? da : default,
            });
        }
        return list;
    }

    static async Task<List<OnlineFont>> Fontshare(CancellationToken ct)
    {
        var list = new List<(OnlineFont f, int views)>();
        for (int offset = 0; offset < 2000; offset += 100)
        {
            var json = await Http.GetStringAsync($"https://api.fontshare.com/v2/fonts?limit=100&offset={offset}", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("fonts", out var fonts) || fonts.GetArrayLength() == 0) break;
            foreach (var e in fonts.EnumerateArray())
            {
                var slug = Str(e, "slug");
                if (slug.Length == 0) continue;
                var designers = e.TryGetProperty("designers", out var d) && d.ValueKind == JsonValueKind.Array ? string.Join(", ", d.EnumerateArray().Select(x => Str(x, "name")).Where(x => x.Length > 0)) : "";
                var lic = Str(e, "license_type");
                list.Add((new OnlineFont
                {
                    Source = "Fontshare", Id = slug, Family = Str(e, "name"), Category = MapCat(Str(e, "category")), Designer = designers,
                    License = lic == "itf_ffl" ? "ITF Free Font License (free for personal and commercial use)" : "Fontshare licence: " + lic,
                    Page = "https://www.fontshare.com/fonts/" + slug,
                    Added = DateTime.TryParse(Str(e, "inserted_at"), out var da) ? da : default,
                }, e.TryGetProperty("views", out var v) && v.TryGetInt32(out var vi) ? vi : 0));
            }
            if (!doc.RootElement.TryGetProperty("has_more", out var more) || more.ValueKind != JsonValueKind.True) break;
        }
        int rank = 1;
        foreach (var (f, _) in list.OrderByDescending(x => x.views)) f.Pop = rank++;
        return list.Select(x => x.f).ToList();
    }

    static async Task<List<OnlineFont>> Squirrel(CancellationToken ct)
    {
        var json = await Http.GetStringAsync("https://www.fontsquirrel.com/api/fontlist/all", ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<OnlineFont>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var url = Str(e, "family_urlname");
            if (url.Length == 0) continue;
            int.TryParse(Str(e, "id"), out var id);
            list.Add(new OnlineFont
            {
                Source = "Font Squirrel", Id = url, Family = Str(e, "family_name"), Category = MapCat(Str(e, "classification")), Designer = Str(e, "foundry_name"),
                License = "Free for commercial use (read the licence in the download)",
                Page = "https://www.fontsquirrel.com/fonts/" + url, Added = new DateTime(2009, 1, 1).AddDays(id / 2.0), Pop = 0,
            });
        }
        return list;
    }

    // ---- downloads

    // Downloads a family's desktop font files into dir and returns their paths. previewOnly fetches one regular file, quickly.
    public static async Task<List<string>> Fetch(OnlineFont f, string dir, bool previewOnly, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var have = Directory.GetFiles(dir).Where(IsFont).ToList();
        if (have.Count > 0 && (previewOnly || File.Exists(Path.Combine(dir, ".complete")))) return previewOnly ? have.Take(1).ToList() : have;
        var paths = f.Source switch
        {
            "Google Fonts" => await GoogleFiles(f, dir, previewOnly, ct),
            "Fontshare" => await ZipFiles("https://api.fontshare.com/v2/fonts/download/" + Uri.EscapeDataString(f.Id), dir, previewOnly, ct),
            "Font Squirrel" => await ZipFiles("https://www.fontsquirrel.com/fonts/download/" + Uri.EscapeDataString(f.Id), dir, previewOnly, ct),
            _ => throw new ArgumentException("Unknown source " + f.Source),
        };
        var good = paths.Where(IsFont).ToList();
        foreach (var bad in paths.Except(good)) try { File.Delete(bad); } catch { }
        if (good.Count == 0) throw new InvalidDataException("no usable font files in the download");
        if (!previewOnly) File.WriteAllText(Path.Combine(dir, ".complete"), DateTime.UtcNow.ToString("o"));
        return good;
    }

    // Only real font files are kept: the first bytes of a TrueType, OpenType or collection file.
    static bool IsFont(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            var b = new byte[4];
            if (s.Read(b, 0, 4) < 4) return false;
            var m = System.Text.Encoding.ASCII.GetString(b);
            return m == "OTTO" || m == "true" || m == "ttcf" || (b[0] == 0 && b[1] == 1 && b[2] == 0 && b[3] == 0);
        }
        catch { return false; }
    }

    static async Task<List<string>> GoogleFiles(OnlineFont f, string dir, bool previewOnly, CancellationToken ct)
    {
        var body = await Http.GetStringAsync("https://fonts.google.com/download/list?family=" + Uri.EscapeDataString(f.Id), ct);
        using var doc = JsonDocument.Parse(body[body.IndexOf('{')..]); // the reply starts with a )]}' guard line
        var refs = doc.RootElement.GetProperty("manifest").GetProperty("fileRefs").EnumerateArray()
            .Select(e => (name: Path.GetFileName(Str(e, "filename")), url: Str(e, "url")))
            .Where(x => x.name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || x.name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (previewOnly)
        {
            var pick = refs.FirstOrDefault(x => x.name.Contains("Regular", StringComparison.OrdinalIgnoreCase) && !x.name.Contains("Italic", StringComparison.OrdinalIgnoreCase));
            if (pick.name == null) pick = refs.FirstOrDefault(x => !x.name.Contains("Italic", StringComparison.OrdinalIgnoreCase));
            refs = pick.name == null ? refs.Take(1).ToList() : new() { pick };
        }
        var got = new List<string>();
        foreach (var (name, url) in refs)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !u.Host.EndsWith(".gstatic.com", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(dir, Safe(name));
            if (!File.Exists(dest)) await Download(url, dest, ct);
            got.Add(dest);
        }
        return got;
    }

    static async Task<List<string>> ZipFiles(string url, string dir, bool previewOnly, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "kinetikfonts-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await Download(url, tmp, ct);
            using var zip = ZipFile.OpenRead(tmp);
            bool Is(ZipArchiveEntry e, params string[] ext) => e.Name.Length > 0 && !e.FullName.Contains("__MACOSX") && ext.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase));
            var entries = zip.Entries.Where(e => Is(e, ".ttf", ".ttc")).ToList();
            if (entries.Count == 0) entries = zip.Entries.Where(e => Is(e, ".otf", ".otc")).ToList();
            if (previewOnly && entries.Count > 1)
            {
                var plain = entries.Where(e => e.Name.Contains("Regular", StringComparison.OrdinalIgnoreCase) && !e.Name.Contains("Italic", StringComparison.OrdinalIgnoreCase)).ToList();
                entries = (plain.Count > 0 ? plain : entries).Take(1).ToList();
            }
            var got = new List<string>();
            foreach (var e in entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                if (e.Length > MaxDownload) continue;
                var dest = Path.Combine(dir, Safe(e.Name)); // the file name only, so an archive cannot write outside dir
                if (!File.Exists(dest)) e.ExtractToFile(dest);
                got.Add(dest);
            }
            return got;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static async Task Download(string url, string dest, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxDownload) throw new InvalidDataException("the download is too large");
        var part = dest + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[81920]; long total = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                total += n;
                if (total > MaxDownload) throw new InvalidDataException("the download is too large");
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
            }
        }
        File.Move(part, dest, true);
    }
}
