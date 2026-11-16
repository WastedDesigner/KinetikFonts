// The font library: scanning folders and the registry, enabling, disabling, installing, favourites and sets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

class DisabledFont { public string Hive { get; set; } public string Val { get; set; } public string Data { get; set; } }

class Config
{
    public List<string> Folders { get; set; } = new();
    public List<string> Enabled { get; set; } = new();
    public List<DisabledFont> Disabled { get; set; } = new();
    public List<string> Favourites { get; set; } = new();
    public Dictionary<string, List<string>> Sets { get; set; } = new();
    public string Text { get; set; } = "The quick brown fox jumps over the lazy dog";
    public int Size { get; set; } = 36;
    public bool Stacked { get; set; }
    public string Sort { get; set; } = "Name";
    public string DownloadDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kinetik Fonts");
    public List<string> Downloaded { get; set; } = new();
    public Dictionary<string, string> Cats { get; set; } = new(); // your own category choices, by file
}

class FontRow
{
    public string Kind, Hive, Val, Data, Path, Name, Fam, Style = "Regular", Folder, Src, Key, Norm, Cat = "Other";
    public int Pop;
    public OnlineFont Web;
    public bool On, Installed, Dup, Ready, Bold, Italic;
    public long Bytes;
    public DateTime Modified;
    public bool IsSys => Kind == "sys";
    public bool IsWeb => Kind == "web";
}

static class Native
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern int AddFontResourceEx(string f, uint flags, IntPtr r);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool RemoveFontResourceEx(string f, uint flags, IntPtr r);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr res);
    [DllImport("shell32.dll")] public static extern bool IsUserAnAdmin();
    public static void Broadcast() => SendMessageTimeout((IntPtr)0xFFFF, 0x1D, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _); // WM_FONTCHANGE
}

class Library
{
    public const string REG = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    public static readonly string SysDir = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts");
    public static readonly string UserDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
    public static readonly string CfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KinetikFonts", "config.json");
    static readonly string[] Exts = { ".ttf", ".otf", ".ttc", ".otc" };
    // Disabling these breaks the Windows UI.
    static readonly string[] Protected = { "segoeui", "segmdl2", "seguisym", "seguiemj", "marlett", "tahoma", "micross", "consola", "arial", "symbol" };
    public static readonly string[] Cats = { "Sans serif", "Serif", "Slab", "Script", "Display", "Monospace", "Symbol", "Other" };

    public Config Cfg;
    public HashSet<string> Fav;
    public readonly HashSet<string> Pub = new();
    public List<FontRow> Rows = new();
    readonly Dictionary<string, (long len, long ticks, Dictionary<int, string> names)> cache = new();

    public Library()
    {
        try { Cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(CfgPath)) ?? new Config(); }
        catch { Cfg = new Config(); }
        Fav = new HashSet<string>(Cfg.Favourites);
        foreach (var p in Cfg.Enabled)
            if (File.Exists(p) && Native.AddFontResourceEx(p, 0, IntPtr.Zero) > 0) Pub.Add(p);
        Native.Broadcast();
    }

    public void Save()
    {
        Cfg.Favourites = Fav.OrderBy(x => x).ToList();
        Cfg.Enabled = Pub.OrderBy(x => x).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(CfgPath));
        File.WriteAllText(CfgPath, JsonSerializer.Serialize(Cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Norm(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Every name-table string of the first face (name id → text); reads only the table.
    public static Dictionary<int, string> FaceInfo(string path)
    {
        using var f = File.OpenRead(path);
        byte[] Read(int n) { var b = new byte[n]; f.ReadExactly(b); return b; }
        int U16(byte[] b, int o) => b[o] << 8 | b[o + 1];
        uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        long bas = 0;
        var head = Read(12);
        if (Encoding.ASCII.GetString(head, 0, 4) == "ttcf") { f.Position = 12; bas = U32(Read(4), 0); f.Position = bas; head = Read(12); }
        int n = U16(head, 4);
        f.Position = bas + 12;
        var tables = new Dictionary<string, (uint off, uint len)>();
        for (int i = 0; i < n; i++)
        {
            var d = Read(16);
            tables[Encoding.ASCII.GetString(d, 0, 4)] = (U32(d, 8), U32(d, 12));
        }
        if (!tables.TryGetValue("name", out var nt)) throw new InvalidDataException("no name table");
        f.Position = nt.off;
        var t = Read((int)nt.len);
        int cnt = U16(t, 2), so = U16(t, 4);
        var got = new Dictionary<int, string>();
        for (int i = 0; i < cnt; i++)
        {
            int p = 6 + 12 * i, pid = U16(t, p), lang = U16(t, p + 4), nid = U16(t, p + 6), l = U16(t, p + 8), o = U16(t, p + 10);
            string s;
            if (pid == 3) s = Encoding.BigEndianUnicode.GetString(t, so + o, l);
            else if (pid == 1) s = Encoding.Latin1.GetString(t, so + o, l); // approximates MacRoman
            else continue;
            if (!got.ContainsKey(nid) || (pid == 3 && lang == 0x409)) got[nid] = s;
        }
        int cls = 0; byte[] pan = null; bool fixedPitch = false;
        try
        {
            if (tables.TryGetValue("OS/2", out var os) && os.len >= 42) { f.Position = os.off; var o = Read(42); cls = U16(o, 30); pan = o[32..42]; }
            if (tables.TryGetValue("post", out var po) && po.len >= 16) { f.Position = po.off; fixedPitch = U32(Read(16), 12) != 0; }
        }
        catch { } // a damaged OS/2 or post table only costs the category its hints
        int el = -1; bool cff = tables.ContainsKey("CFF ");
        try { el = Elaboration(f, tables, cff); } catch { } // how fancy the capital I is: the serif-or-sans hint for fonts that say nothing about themselves
        got[-3] = el.ToString();
        got[-2] = $"class {cls:x4} panose {(pan == null ? "-" : Convert.ToHexString(pan))} fixed {fixedPitch} elaboration {el} {(cff ? "cff" : "glyf")}"; // kept for diagnosing categories
        got[-1] = Classify(got, cls, pan, fixedPitch, el, cff);
        return got;
    }

    // The category from what the font says about itself (OS/2 family class and PANOSE, post fixed-pitch flag), then its name.
    static string Classify(Dictionary<int, string> n, int cls, byte[] pan, bool mono, int el, bool cff)
    {
        var raw = $"{n.GetValueOrDefault(1)} {n.GetValueOrDefault(4)} {n.GetValueOrDefault(6)}";
        var words = new HashSet<string>(System.Text.RegularExpressions.Regex.Replace(raw, "(?<=[a-z])(?=[A-Z])", " ")
            .ToLowerInvariant().Split(new[] { ' ', '-', '_', '.', '+', '(', ')' }, StringSplitOptions.RemoveEmptyEntries));
        bool Has(params string[] w) => w.Any(words.Contains);
        int hi = cls >> 8, ft = pan?[0] ?? 0, ser = pan?[1] ?? 0, prop = pan?[3] ?? 0;
        if (ft == 5 || hi == 12 || Has("dingbats", "symbols", "icons", "emoji", "wingdings", "webdings")) return "Symbol";
        if (mono || prop == 9 || Has("mono", "monospace", "monospaced", "code", "console")) return "Monospace";
        if (ft == 3 || hi == 10 || Has("script", "hand", "handwriting", "handwritten", "brush", "calligraphy", "signature", "marker", "cursive")) return "Script";
        if (ft == 4 || hi == 9 || Has("display", "decorative", "grunge", "stencil", "poster", "shadow", "outline", "inline", "neon")) return "Display";
        if (hi == 5 || Has("slab")) return "Slab";
        if (hi == 8 || (ser >= 11 && ser <= 13) || ser == 15 || Has("sans", "grotesk", "grotesque", "gothic")) return "Sans serif";
        if ((hi >= 1 && hi <= 7) || (ser >= 2 && ser <= 10) || ser == 14 || Has("serif", "roman", "antiqua", "didone", "garamond")) return "Serif";
        if (!cff && el >= 0) return el >= 14 ? "Serif" : "Sans serif"; // CFF charstrings are subroutinised, so their length says nothing
        return "Other";
    }

    // ---- the capital I: a bare bar (few points / short charstring) is sans serif, serifs make it elaborate

    static byte[] ReadAt(Stream f, long pos, int n) { f.Position = pos; var b = new byte[n]; f.ReadExactly(b); return b; }
    static int BE16(byte[] b, int o) => b[o] << 8 | b[o + 1];
    static uint BE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    static int Elaboration(Stream f, Dictionary<string, (uint off, uint len)> tb, bool cff)
    {
        if (!tb.TryGetValue("cmap", out var cm)) return -1;
        int g = GlyphOf(f, cm, 0x49);
        if (g <= 0) return -1;
        if (cff) return tb.TryGetValue("CFF ", out var c) ? CffLength(f, c, g) : -1;
        return TrueTypePoints(f, tb, g);
    }

    static int GlyphOf(Stream f, (uint off, uint len) cmap, int cp)
    {
        var h = ReadAt(f, cmap.off, 4);
        int num = BE16(h, 2);
        var recs = ReadAt(f, cmap.off + 4, num * 8);
        long sub = -1; int rank = 9;
        for (int i = 0; i < num; i++)
        {
            int pid = BE16(recs, i * 8), eid = BE16(recs, i * 8 + 2), r = pid == 3 && eid == 1 ? 0 : pid == 0 ? 1 : pid == 3 && eid == 10 ? 2 : 9;
            if (r < rank) { rank = r; sub = cmap.off + BE32(recs, i * 8 + 4); }
        }
        if (sub < 0) return 0;
        int fmt = BE16(ReadAt(f, sub, 2), 0);
        if (fmt == 4)
        {
            int len = BE16(ReadAt(f, sub + 2, 2), 0);
            var t = ReadAt(f, sub, len);
            int segs = BE16(t, 6) / 2;
            for (int i = 0; i < segs; i++)
            {
                int end = BE16(t, 14 + i * 2);
                if (cp > end) continue;
                int start = BE16(t, 16 + segs * 2 + i * 2);
                if (cp < start) return 0;
                int delta = BE16(t, 16 + segs * 4 + i * 2), roPos = 16 + segs * 6 + i * 2, ro = BE16(t, roPos);
                if (ro == 0) return (cp + delta) & 0xFFFF;
                int gi = BE16(t, roPos + ro + 2 * (cp - start));
                return gi == 0 ? 0 : (gi + delta) & 0xFFFF;
            }
            return 0;
        }
        if (fmt == 12)
        {
            int groups = (int)BE32(ReadAt(f, sub + 12, 4), 0);
            var t = ReadAt(f, sub + 16, Math.Min(groups, 100000) * 12);
            for (int i = 0; i < t.Length / 12; i++)
            {
                uint s0 = BE32(t, i * 12), e0 = BE32(t, i * 12 + 4);
                if (cp >= s0 && cp <= e0) return (int)(BE32(t, i * 12 + 8) + (uint)cp - s0);
            }
        }
        return 0;
    }

    static int TrueTypePoints(Stream f, Dictionary<string, (uint off, uint len)> tb, int g)
    {
        if (!tb.TryGetValue("head", out var head) || !tb.TryGetValue("loca", out var loca) || !tb.TryGetValue("glyf", out var glyf)) return -1;
        bool wide = BE16(ReadAt(f, head.off + 50, 2), 0) != 0;
        uint a, b;
        if (wide) { var l = ReadAt(f, loca.off + (long)g * 4, 8); a = BE32(l, 0); b = BE32(l, 4); }
        else { var l = ReadAt(f, loca.off + (long)g * 2, 4); a = (uint)BE16(l, 0) * 2; b = (uint)BE16(l, 2) * 2; }
        if (b <= a) return -1;
        int contours = (short)BE16(ReadAt(f, glyf.off + a, 2), 0);
        if (contours <= 0) return -1; // composite glyph: no simple count
        return BE16(ReadAt(f, glyf.off + a + 10 + (contours - 1) * 2, 2), 0) + 1;
    }

    // Length in bytes of glyph g's charstring in a CFF table.
    static int CffLength(Stream f, (uint off, uint len) cff, int g)
    {
        long IndexEnd(long pos)
        {
            int count = BE16(ReadAt(f, pos, 2), 0);
            if (count == 0) return pos + 2;
            int sz = ReadAt(f, pos + 2, 1)[0];
            var last = ReadAt(f, pos + 3 + (long)count * sz, sz);
            long v = 0; foreach (var x in last) v = v << 8 | x;
            return pos + 3 + (long)(count + 1) * sz + v - 1;
        }
        (long start, int len) Entry(long pos, int i)
        {
            int sz = ReadAt(f, pos + 2, 1)[0];
            var o = ReadAt(f, pos + 3 + (long)i * sz, sz * 2);
            long a = 0, b = 0;
            for (int k = 0; k < sz; k++) { a = a << 8 | o[k]; b = b << 8 | o[sz + k]; }
            int count = BE16(ReadAt(f, pos, 2), 0);
            return (pos + 3 + (long)(count + 1) * sz + a - 1, (int)(b - a));
        }
        long p = cff.off + ReadAt(f, cff.off, 4)[2];
        long top = IndexEnd(p);                 // after the Name INDEX comes the Top DICT INDEX
        var (ds, dl) = Entry(top, 0);
        var d = ReadAt(f, ds, Math.Min(dl, 4096));
        var ops = new List<double>();
        long charStrings = 0;
        for (int i = 0; i < d.Length;)
        {
            int b0 = d[i++];
            if (b0 >= 32 && b0 <= 246) ops.Add(b0 - 139);
            else if (b0 >= 247 && b0 <= 250) ops.Add((b0 - 247) * 256 + d[i++] + 108);
            else if (b0 >= 251 && b0 <= 254) ops.Add(-(b0 - 251) * 256 - d[i++] - 108);
            else if (b0 == 28) { ops.Add((short)(d[i] << 8 | d[i + 1])); i += 2; }
            else if (b0 == 29) { ops.Add((int)BE32(d, i)); i += 4; }
            else if (b0 == 30) { while (i < d.Length) { int v = d[i++]; if ((v & 15) == 15 || (v >> 4) == 15) break; } ops.Add(0); }
            else
            {
                if (b0 == 12) i++;
                else if (b0 == 17 && ops.Count > 0) { charStrings = (long)ops[^1]; break; }
                ops.Clear();
            }
        }
        if (charStrings <= 0) return -1;
        return Entry(cff.off + charStrings, g).len;
    }

    static void Describe(FontRow r, Dictionary<int, string> n)
    {
        r.Fam = n.GetValueOrDefault(1) ?? Path.GetFileNameWithoutExtension(r.Path);
        var style = n.GetValueOrDefault(2) ?? "Regular";
        if (!r.IsWeb) { r.Style = style; r.Cat = n.GetValueOrDefault(-1) ?? "Other"; }
        r.Bold = style.Contains("bold", StringComparison.OrdinalIgnoreCase);
        r.Italic = style.Contains("italic", StringComparison.OrdinalIgnoreCase) || style.Contains("oblique", StringComparison.OrdinalIgnoreCase);
        r.Ready = true;
    }

    // Makes a row drawable: reads its names (the drawing itself loads the file privately, see Gdi).
    public static void Prepare(FontRow r)
    {
        if (r.Path == null) return; // an online font that has not been fetched yet
        if (!r.Ready)
        {
            try { Describe(r, FaceInfo(r.Path)); }
            catch { r.Fam ??= "Segoe UI"; r.Ready = true; }
        }
    }

    static FontRow SysRow(string hive, string val, string data, bool on)
    {
        var i = val.LastIndexOf(" (");
        return new FontRow
        {
            Kind = "sys", Hive = hive, Val = val, Data = data, On = on,
            Path = Path.IsPathRooted(data) ? data : Path.Combine(SysDir, data),
            Name = i > 0 ? val[..i] : val, Src = hive == "HKLM" ? "Installed (system)" : "Installed (user)"
        };
    }

    public static List<FontRow> Installed()
    {
        var o = new List<FontRow>();
        foreach (var (h, root) in new[] { ("HKLM", Registry.LocalMachine), ("HKCU", Registry.CurrentUser) })
        {
            using var k = root.OpenSubKey(REG);
            if (k == null) continue;
            foreach (var n in k.GetValueNames())
                if (k.GetValue(n) is string d) o.Add(SysRow(h, n, d, true));
        }
        return o;
    }

    static RegistryKey Hive(string h) => h == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;

    Dictionary<int, string> Names(string p, FileInfo fi)
    {
        if (cache.TryGetValue(p, out var c) && c.len == fi.Length && c.ticks == fi.LastWriteTimeUtc.Ticks) return c.names;
        Dictionary<int, string> n;
        try { n = FaceInfo(p); } catch { n = new(); }
        cache[p] = (fi.Length, fi.LastWriteTimeUtc.Ticks, n);
        return n;
    }

    // Safe to run off the UI thread; the caller assigns the result to Rows.
    public List<FontRow> BuildRows()
    {
        var r = Installed();
        var live = r.Select(x => (x.Hive, x.Val)).ToHashSet();
        r.AddRange(Cfg.Disabled.Where(d => !live.Contains((d.Hive, d.Val))).Select(d => SysRow(d.Hive, d.Val, d.Data, false)));
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var folder in Cfg.Folders.Where(Directory.Exists))
        {
            var src = Path.GetFileName(folder.TrimEnd('\\'));
            foreach (var p in Directory.EnumerateFiles(folder, "*", opt).Where(p => Exts.Contains(Path.GetExtension(p).ToLowerInvariant())))
            {
                var fi = new FileInfo(p);
                var n = Names(p, fi);
                var row = new FontRow { Kind = "dir", Folder = folder, Path = p, On = Pub.Contains(p), Src = src.Length > 0 ? src : folder, Bytes = fi.Length, Modified = fi.LastWriteTime };
                Describe(row, n);
                row.Name = n.GetValueOrDefault(4) ?? (n.Count == 0 ? Path.GetFileNameWithoutExtension(p) : $"{row.Fam} {row.Style}".Trim());
                r.Add(row);
            }
        }
        foreach (var x in r)
        {
            x.Key = x.Path.ToLowerInvariant();
            x.Norm = Norm(x.Name);
            if (x.IsSys)
                try
                {
                    var fi = new FileInfo(x.Path); x.Bytes = fi.Length; x.Modified = fi.LastWriteTime;
                    var n = Names(x.Path, fi);
                    if (n.Count > 0) Describe(x, n);
                }
                catch { }
            if (Cfg.Cats.TryGetValue(x.Key, out var cat)) x.Cat = cat;
        }
        var sysNames = r.Where(x => x.IsSys).Select(x => x.Norm).ToHashSet();
        var sysFiles = r.Where(x => x.IsSys).Select(x => Path.GetFileName(x.Path).ToLowerInvariant()).ToHashSet();
        foreach (var x in r.Where(x => !x.IsSys)) x.Installed = sysNames.Contains(x.Norm) || sysFiles.Contains(Path.GetFileName(x.Path).ToLowerInvariant());
        foreach (var g in r.GroupBy(x => x.Norm).Where(g => g.Count() > 1 && g.Key.Length > 0)) foreach (var x in g) x.Dup = true;
        return Sorted(r);
    }

    public List<FontRow> Sorted(IEnumerable<FontRow> rows)
    {
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        return (Cfg.Sort switch
        {
            "Newest" => rows.OrderByDescending(x => x.Modified).ThenBy(x => x.Name, cmp),
            "Largest" => rows.OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, cmp),
            "Source" => rows.OrderBy(x => x.Src, cmp).ThenBy(x => x.Name, cmp),
            "Popular" => rows.OrderBy(x => x.Pop == 0 ? int.MaxValue : x.Pop).ThenBy(x => x.Name, cmp),
            "State" => rows.OrderByDescending(x => x.On).ThenBy(x => x.Name, cmp),
            _ => rows.OrderBy(x => x.Name, cmp),
        }).ToList();
    }

    public void Enable(FontRow r)
    {
        if (r.Kind == "dir")
        {
            if (Native.AddFontResourceEx(r.Path, 0, IntPtr.Zero) == 0) throw new IOException("Windows rejected this font file");
            Pub.Add(r.Path);
        }
        else
        {
            using (var k = Hive(r.Hive).OpenSubKey(REG, true)) k.SetValue(r.Val, r.Data, RegistryValueKind.String);
            Native.AddFontResourceEx(r.Path, 0, IntPtr.Zero);
            Cfg.Disabled.RemoveAll(d => d.Hive == r.Hive && d.Val == r.Val);
        }
        r.On = true;
    }

    public void Disable(FontRow r)
    {
        if (r.Kind == "dir")
        {
            Native.RemoveFontResourceEx(r.Path, 0, IntPtr.Zero);
            Pub.Remove(r.Path);
        }
        else
        {
            if (Protected.Any(p => Path.GetFileName(r.Path).StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("core Windows font, protected");
            using (var k = Hive(r.Hive).OpenSubKey(REG, true)) k.DeleteValue(r.Val);
            Native.RemoveFontResourceEx(r.Path, 0, IntPtr.Zero);
            Cfg.Disabled.Add(new DisabledFont { Hive = r.Hive, Val = r.Val, Data = r.Data });
        }
        r.On = false;
    }

    // Per-user install, as Windows' own Settings does it: copy the file and register it for the current user. No admin needed.
    public void Install(FontRow r)
    {
        Directory.CreateDirectory(UserDir);
        var dest = Path.Combine(UserDir, Path.GetFileName(r.Path));
        if (!File.Exists(dest)) File.Copy(r.Path, dest);
        var ext = Path.GetExtension(r.Path).ToLowerInvariant();
        using (var k = Registry.CurrentUser.CreateSubKey(REG))
            k.SetValue($"{r.Name} ({(ext == ".otf" || ext == ".otc" ? "OpenType" : "TrueType")})", dest, RegistryValueKind.String);
        Native.AddFontResourceEx(dest, 0, IntPtr.Zero);
    }

    public void Forget(string folder)
    {
        foreach (var r in Rows.Where(x => x.Folder == folder && x.On)) Disable(r);
        Cfg.Folders.Remove(folder);
        Native.Broadcast();
    }
}
