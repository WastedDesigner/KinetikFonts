// Kinetik Fonts: a font manager. Browse, enable and disable fonts from folders and from Windows, preview and compare them.
// Folder fonts: enable = load for the whole Windows session (other apps see it at once); remembered and re-loaded when this
// app starts. Installed fonts: disable = remove the registry entry (the file stays put, fully reversible); system-wide ones
// need admin. "Install for me" copies a folder font into the user's fonts, no admin needed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KinetikSuite;

class FontsForm : Form
{
    readonly Library lib = new();
    readonly float k;
    Look look = Look.Current;
    readonly Sidebar nav = new();
    readonly FontList list = new();
    readonly Box search, sampleBox;
    readonly Dropdown sort = new();
    readonly Segmented tabs = new(), layout = new();
    readonly Slider slider = new();
    readonly Canvas canvas = new();
    readonly Panel scroll = new() { AutoScroll = true };
    readonly Panel header = new(), ctl = new(), right = new();
    readonly Label status = new(), sizeLabel = new();
    readonly SplitContainer split = new();
    readonly FlowLayoutPanel actions = new();
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 80 }, noteTimer = new() { Interval = 3500 };
    string note;
    readonly Dropdown catDd = new();
    readonly Dictionary<string, List<FontRow>> web = new();
    readonly HashSet<string> loadingSrc = new(), previewFailed = new();
    CancellationTokenSource previewCts;
    string busyText;
    bool scanning, stole, onlineBusy;

    int S(float v) => (int)(v * k);

    public FontsForm()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f);
        Text = "Kinetik Fonts" + (Native.IsUserAnAdmin() ? " (admin)" : "");
        using (var s = typeof(FontsForm).Assembly.GetManifestResourceStream("icon.ico")) if (s != null) Icon = new Icon(s);
        var wa = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(Math.Min(S(1640), wa.Width * 9 / 10), Math.Min(S(900), wa.Height * 9 / 10));
        MinimumSize = new Size(S(1000), S(600));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = look.Bg; ForeColor = look.Text;

        search = new Box(k, "Search fonts…") { Font = Font };
        sampleBox = new Box(k, "Type your own preview text") { Font = Font };

        // sidebar
        nav.K = k; nav.Font = Font; nav.Dock = DockStyle.Left; nav.Width = S(236);
        nav.Picked += NavPicked; nav.Context += NavMenu;

        // centre: header, list, status
        header.Dock = DockStyle.Top; header.Height = S(106); header.BackColor = look.Bg;
        sort.Font = Font; sort.Width = S(130); sort.ItemHeight = S(22);
        sort.Items.AddRange(new object[] { "Name", "Newest", "Largest", "Source", "State", "Popular" });
        sort.SelectedItem = lib.Cfg.Sort;
        if (sort.SelectedIndex < 0) sort.SelectedIndex = 0;
        sort.SelectedIndexChanged += (s, e) => { lib.Cfg.Sort = (string)sort.SelectedItem; lib.Rows = lib.Sorted(lib.Rows); lib.Save(); ApplyFilter(); };
        catDd.Font = Font; catDd.Width = S(150); catDd.ItemHeight = S(22);
        catDd.Items.Add("All categories"); catDd.Items.AddRange(Library.Cats); catDd.SelectedIndex = 0;
        catDd.SelectedIndexChanged += (s, e) => ApplyFilter();
        search.T.TextChanged += (s, e) => ApplyFilter();
        actions.AutoSize = true; actions.WrapContents = false; actions.BackColor = look.Bg;
        foreach (var (t, accent, act) in new (string, bool, Action)[]
        {
            ("Enable", true, () => SetState(Targets(), true)),
            ("Disable", false, () => SetState(Targets(), false)),
            ("Install for me", false, () => Install(Targets())),
            ("★  Favourite", false, () => Favourite(Targets())),
            ("Show file", false, () => Reveal(list.FocusRow)),
        })
        {
            var b = new FlatButton(t, accent, k) { Font = Font, Margin = new Padding(0, 0, S(8), 0) };
            b.Click += (s, e) => act();
            actions.Controls.Add(b);
        }
        var more = new FlatButton("⋯", false, k) { Font = Font, Margin = new Padding(0, 0, S(8), 0) };
        more.Click += (s, e) => MoreMenu(more);
        actions.Controls.Add(more);
        header.Controls.Add(search); header.Controls.Add(catDd); header.Controls.Add(sort); header.Controls.Add(actions);
        header.Resize += (s, e) => LayoutHeader();

        status.Dock = DockStyle.Bottom; status.Height = S(30); status.ForeColor = look.Sub; status.BackColor = look.Bg;
        status.TextAlign = ContentAlignment.MiddleLeft; status.Padding = new Padding(S(20), 0, 0, 0);
        list.K = k; list.Font = Font; list.Dock = DockStyle.Fill; list.IsFav = r => lib.Fav.Contains(r.Key);
        list.SelectionChanged += SelectionChanged;
        list.ToggleRow += r => SetState(list.Sel.Contains(r) ? list.SelectedRows() : new List<FontRow> { r }, !r.On);
        list.FavRow += r => Favourite(list.Sel.Contains(r) ? list.SelectedRows() : new List<FontRow> { r });
        list.ContextRequested += ListMenu;

        // right: preview controls and canvas
        right.Dock = DockStyle.Fill; right.BackColor = look.Card;
        ctl.Dock = DockStyle.Top; ctl.Height = S(152); ctl.BackColor = look.Card;
        tabs.K = k; tabs.Font = Font; tabs.Items = new[] { "Preview", "Compare", "Glyphs", "Info" };
        layout.K = k; layout.Font = Font; layout.Items = new[] { "Side by side", "Stacked" }; layout.Index = lib.Cfg.Stacked ? 1 : 0; layout.Visible = false;
        sizeLabel.ForeColor = look.Sub; sizeLabel.BackColor = look.Card; sizeLabel.TextAlign = ContentAlignment.MiddleLeft;
        slider.K = k; slider.Min = 10; slider.Max = 120; slider.Value = Math.Max(10, Math.Min(120, lib.Cfg.Size));
        sampleBox.T.Text = lib.Cfg.Text;
        ctl.Controls.AddRange(new Control[] { tabs, sampleBox, sizeLabel, slider, layout });
        ctl.Resize += (s, e) => LayoutCtl();

        canvas.K = k; canvas.Font = Font; canvas.Dock = DockStyle.Top; canvas.Height = S(300);
        canvas.Px = slider.Value; canvas.Sample = sampleBox.T.Text; canvas.Stacked = layout.Index == 1;
        canvas.MetaOf = r => Library.FaceInfo(r.Path);
        canvas.Notice += Note;
        scroll.Dock = DockStyle.Fill; scroll.BackColor = look.Card;
        scroll.Controls.Add(canvas);
        right.Controls.Add(scroll); right.Controls.Add(ctl);
        canvas.MouseEnter += (s, e) => { if (ActiveControl == list) { stole = true; scroll.Focus(); } };
        canvas.MouseLeave += (s, e) => { if (stole) { stole = false; list.Focus(); } };

        tabs.Changed += () => { canvas.Mode = tabs.Items[tabs.Index]; layout.Visible = canvas.Mode == "Compare"; LayoutCtl(); scroll.AutoScrollPosition = Point.Empty; RefreshCanvas(); };
        layout.Changed += () => { canvas.Stacked = layout.Index == 1; lib.Cfg.Stacked = canvas.Stacked; canvas.Remeasure(); };
        slider.Changed += () => { sizeLabel.Text = slider.Value + " px"; canvas.Px = slider.Value; lib.Cfg.Size = slider.Value; debounce.Stop(); debounce.Start(); };
        sampleBox.T.TextChanged += (s, e) => { canvas.Sample = sampleBox.T.Text; lib.Cfg.Text = sampleBox.T.Text; debounce.Stop(); debounce.Start(); };
        sizeLabel.Text = slider.Value + " px";
        debounce.Tick += (s, e) => { debounce.Stop(); RefreshCanvas(); };
        noteTimer.Tick += (s, e) => { noteTimer.Stop(); note = null; UpdateStatus(); };

        split.Dock = DockStyle.Fill; split.BackColor = look.Border; split.SplitterWidth = Math.Max(1, S(1));
        split.FixedPanel = FixedPanel.Panel2;
        split.Panel1.BackColor = look.Bg; split.Panel2.BackColor = look.Card;
        split.Panel1.Controls.Add(list); split.Panel1.Controls.Add(status); split.Panel1.Controls.Add(header);
        split.Panel2.Controls.Add(right);
        Controls.Add(split); Controls.Add(nav);

        HandleCreated += (s, e) => { look.Window(Handle); look.ScrollBars(scroll); };
        Shown += (s, e) =>
        {
            split.Panel1MinSize = S(440); split.Panel2MinSize = S(420);
            split.SplitterDistance = Math.Max(S(440), split.Width - S(720));
            LayoutHeader(); LayoutCtl();
            if (lib.Fresh && ShotPath == null && File.Exists(Library.BackupBeside)
                && MessageBox.Show(this, "Found a settings backup next to Kinetik Fonts. Restore your folders, favourites and sets from it?", "Kinetik Fonts", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            { RestoreSettings(Library.BackupBeside); return; }
            Rescan();
        };
        Activated += (s, e) => { if (!SameLook(Look.Current, look)) LookChanged(); };
        FormClosing += (s, e) =>
        {
            lib.Save();
            // A copy beside the exe, so a new Windows can offer it back. Skipped while there's nothing worth keeping, and where the folder is read-only.
            if (lib.Cfg.Folders.Count + lib.Fav.Count + lib.Cfg.Sets.Count > 0) try { lib.Backup(Library.BackupBeside); } catch { }
        };
    }

    static bool SameLook(Look a, Look b) => a.Bg == b.Bg && a.Accent == b.Accent && a.Text == b.Text && a.Card == b.Card;

    void LayoutHeader()
    {
        int pad = S(20), W = header.ClientSize.Width;
        sort.Location = new Point(W - pad - sort.Width, S(16));
        catDd.Location = new Point(sort.Left - S(10) - catDd.Width, S(16));
        search.SetBounds(pad, S(14), Math.Max(S(120), catDd.Left - pad - S(12)), S(34));
        actions.Location = new Point(pad, S(60));
    }

    void LayoutCtl()
    {
        int pad = S(18), W = ctl.ClientSize.Width, y = S(106);
        tabs.SetBounds(pad, S(16), Math.Min(S(400), W - 2 * pad), S(34));
        sampleBox.SetBounds(pad, S(60), W - 2 * pad, S(34));
        sizeLabel.SetBounds(pad, y, S(54), S(30));
        int segW = layout.Visible ? S(220) : 0;
        layout.SetBounds(W - pad - segW, y, segW, S(32));
        slider.SetBounds(pad + S(58), y + S(2), Math.Max(S(60), W - 2 * pad - S(58) - (segW > 0 ? segW + S(10) : 0)), S(28));
    }

    // ---- scanning and filtering

    void Rescan() => _ = RescanAsync();

    async Task RescanAsync()
    {
        if (scanning) return;
        scanning = true; UpdateStatus();
        Cursor = Cursors.AppStarting;
        List<FontRow> rows;
        try { rows = await Task.Run(lib.BuildRows); }
        catch (Exception e) { rows = new(); Note("Scan failed: " + e.Message); }
        lib.Rows = rows;
        scanning = false; Cursor = Cursors.Default;
        RefreshAll();
        if (ShotPath != null) Shoot();
    }

    // Test aid: --shot <png> <tab 0-3> renders the window to a file and exits.
    public static string ShotPath, ShotNav, ShotQuery; public static int ShotTab, ShotSel;
    async void Shoot()
    {
        if (ShotQuery != null) search.T.Text = ShotQuery;
        if (ShotNav != null)
        {
            nav.CurKind = "online"; nav.CurArg = ShotNav; ApplyFilter();
            await LoadOnline(ShotNav, false);
        }
        tabs.Index = ShotTab;
        list.Preselect(ShotSel > 0 ? ShotSel : ShotQuery != null ? 1 : 3);
        await Task.Delay(ShotNav != null ? 6000 : 1200);
        // Whole window, cropped to the client area. DrawToBitmap cannot see the owner-drawn drop-downs, so they are painted in by hand.
        using var full = new Bitmap(Width, Height);
        DrawToBitmap(full, new Rectangle(0, 0, Width, Height));
        var off = new Point(PointToScreen(Point.Empty).X - Left, PointToScreen(Point.Empty).Y - Top);
        using var bmp = full.Clone(new Rectangle(off, ClientSize), full.PixelFormat);
        using (var g = Graphics.FromImage(bmp))
        {
            var l = Look.Current;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            foreach (var dd in new[] { catDd, sort })
            {
                var r = RectangleToClient(dd.Parent.RectangleToScreen(dd.Bounds));
                var rf = new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 2, r.Height - 2);
                Paths.Fill(g, rf, 6 * k, l.Field); Paths.Stroke(g, rf, 6 * k, l.Border);
                TextRenderer.DrawText(g, dd.Text, Font, new Rectangle(r.X + S(8), r.Y, r.Width - S(34), r.Height), l.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                Paths.Chevron(g, new RectangleF(r.Right - S(30), r.Y, S(24), r.Height), l.Sub);
            }
            if (search.T.Text.Length == 0)
            {
                var r = RectangleToClient(search.Parent.RectangleToScreen(search.Bounds));
                TextRenderer.DrawText(g, "Search fonts…", Font, new Rectangle(r.X + S(12), r.Y, r.Width - S(24), r.Height), l.Sub, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }
        bmp.Save(ShotPath);
        Close();
    }

    void RefreshAll() { RefreshNav(); ApplyFilter(); }

    int Count(Func<FontRow, bool> f) => lib.Rows.Count(f);

    void RefreshNav()
    {
        var sets = lib.Cfg.Sets;
        NavItem I(string kind, string arg, string text, int n) => new() { Kind = kind, Arg = arg, Text = text, Count = n };
        NavItem Head(string t) => new() { Head = true, Text = t };
        NavItem Add(string kind, string t) => new() { Kind = kind, Text = t, Add = true };
        var items = new List<NavItem>
        {
            Head("Library"),
            I("all", "", "All fonts", lib.Rows.Count),
            I("installed", "", "Installed", Count(r => r.IsSys)),
            I("new", "", "Not installed", Count(r => !r.IsSys && !r.Installed)),
            I("on", "", "Enabled", Count(r => r.On)),
            I("off", "", "Disabled", Count(r => !r.On)),
            I("fav", "", "Favourites", Count(r => lib.Fav.Contains(r.Key))),
            I("dup", "", "Duplicates", Count(r => r.Dup)),
            Head("Online"),
            I("online", "", "All sources", web.Count == 0 ? -1 : web.Values.Sum(v => v.Count)),
        };
        foreach (var src in Online.Sources) items.Add(I("online", src, src, web.TryGetValue(src, out var w) ? w.Count : -1));
        items.Add(Head("Folders"));
        foreach (var f in lib.Cfg.Folders)
        {
            var name = Path.GetFileName(f.TrimEnd('\\'));
            items.Add(I("src", f, name.Length > 0 ? name : f, Count(r => r.Folder == f)));
        }
        items.Add(Add("addfolder", "＋  Add folder…"));
        items.Add(Head("Sets"));
        foreach (var kv in sets.OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var keys = kv.Value.ToHashSet();
            items.Add(I("set", kv.Key, kv.Key, Count(r => keys.Contains(r.Key))));
        }
        items.Add(Add("newset", "＋  New set…"));
        nav.Items = items;
        if ((nav.CurKind == "src" && !lib.Cfg.Folders.Contains(nav.CurArg)) || (nav.CurKind == "set" && !sets.ContainsKey(nav.CurArg))) { nav.CurKind = "all"; nav.CurArg = ""; }
        nav.Invalidate();
    }

    IEnumerable<FontRow> SetRows(string name)
    {
        var keys = lib.Cfg.Sets.GetValueOrDefault(name)?.ToHashSet();
        return keys == null ? Enumerable.Empty<FontRow>() : lib.Rows.Where(r => keys.Contains(r.Key));
    }

    void ApplyFilter()
    {
        IEnumerable<FontRow> src = nav.CurKind switch
        {
            "installed" => lib.Rows.Where(r => r.IsSys),
            "new" => lib.Rows.Where(r => !r.IsSys && !r.Installed),
            "on" => lib.Rows.Where(r => r.On),
            "off" => lib.Rows.Where(r => !r.On),
            "fav" => lib.Rows.Where(r => lib.Fav.Contains(r.Key)),
            "dup" => lib.Rows.Where(r => r.Dup),
            "src" => lib.Rows.Where(r => r.Folder == nav.CurArg),
            "set" => SetRows(nav.CurArg),
            "online" => lib.Sorted(OnlineRows(nav.CurArg)),
            _ => lib.Rows,
        };
        if (catDd.SelectedIndex > 0) { var c = (string)catDd.SelectedItem; src = src.Where(r => r.Cat == c); }
        var q = search.T.Text.Trim();
        if (q.Length > 0)
            src = src.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Src.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Cat.Contains(q, StringComparison.OrdinalIgnoreCase) || (r.IsWeb && r.Style.Contains(q, StringComparison.OrdinalIgnoreCase)));
        list.SetItems(src.ToList());
        UpdateStatus();
        RefreshCanvas();
    }

    // ---- online catalogues

    IEnumerable<FontRow> OnlineRows(string arg)
    {
        var rows = (arg == "" ? web.Values.SelectMany(x => x) : web.GetValueOrDefault(arg) ?? Enumerable.Empty<FontRow>()).ToList();
        var local = lib.Rows.Select(r => Library.Norm(r.Fam ?? r.Name)).ToHashSet();
        foreach (var r in rows) r.Installed = local.Contains(r.Norm);
        return rows;
    }

    FontRow MakeRow(OnlineFont o)
    {
        var key = $"web:{o.Source}:{o.Id}";
        var row = new FontRow
        {
            Kind = "web", Web = o, Name = o.Family, Fam = o.Family, Style = o.Designer ?? "", Src = o.Source, Cat = o.Category, Key = key,
            Norm = Library.Norm(o.Family), On = lib.Cfg.Downloaded.Contains(key), Pop = o.Pop, Modified = o.Added,
        };
        try
        {
            var dir = Online.PreviewDir(o);
            if (Directory.Exists(dir)) row.Path = Directory.EnumerateFiles(dir).FirstOrDefault(f => !f.EndsWith(".part"));
        }
        catch { }
        return row;
    }

    async Task LoadOnline(string arg, bool force)
    {
        var todo = (arg == "" ? Online.Sources : new[] { arg }).Where(s => (force || !web.ContainsKey(s)) && loadingSrc.Add(s)).ToList();
        if (todo.Count == 0) return;
        busyText = "Loading the online catalogue…"; UpdateStatus();
        list.EmptyText = "Loading the online catalogue…"; list.Invalidate();
        foreach (var s in todo)
        {
            try { web[s] = (await Online.Catalog(s, force, CancellationToken.None)).Select(MakeRow).ToList(); }
            catch (Exception e) { Note($"Could not load {s}: {e.Message}"); }
            loadingSrc.Remove(s);
        }
        busyText = null; list.EmptyText = "Nothing found.";
        RefreshNav();
        if (nav.CurKind == "online") ApplyFilter(); else UpdateStatus();
    }

    // Fetches a small preview file for online fonts that are on screen, a moment after the selection settles.
    async Task FetchPreviews(List<FontRow> rows)
    {
        previewCts?.Cancel();
        var cts = previewCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(400, cts.Token);
            foreach (var r in rows)
            {
                if (r.Path != null || previewFailed.Contains(r.Key)) continue;
                try
                {
                    var paths = await Online.Fetch(r.Web, Online.PreviewDir(r.Web), true, cts.Token);
                    r.Path = paths[0]; r.Ready = false;
                    canvas.Remeasure(); list.Invalidate();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { previewFailed.Add(r.Key); Note($"No preview for {r.Name}: {e.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task GetOnline(List<FontRow> rows, bool install)
    {
        if (onlineBusy) { Note("A download is already running"); return; }
        onlineBusy = true;
        var got = new List<string>(); var errs = new List<string>(); int i = 0;
        var root = Path.GetFullPath(lib.Cfg.DownloadDir);
        try
        {
            foreach (var r in rows)
            {
                busyText = $"Downloading {r.Name} ({++i}/{rows.Count})…"; UpdateStatus();
                try
                {
                    var dir = Path.Combine(root, Online.Safe(r.Src), Online.Safe(r.Name));
                    got.AddRange(await Online.Fetch(r.Web, dir, false, CancellationToken.None));
                    lib.Cfg.Downloaded.Add(r.Key); r.On = true;
                }
                catch (Exception e) { errs.Add($"{r.Name}: {e.Message}"); }
            }
            busyText = null;
            if (got.Count > 0)
            {
                if (!lib.Cfg.Folders.Any(f => string.Equals(f, root, StringComparison.OrdinalIgnoreCase))) lib.Cfg.Folders.Add(root);
                lib.Save();
                await RescanAsync();
                var keys = got.Select(x => x.ToLowerInvariant()).ToHashSet();
                var local = lib.Rows.Where(x => x.Kind == "dir" && keys.Contains(x.Key)).ToList();
                if (install) Install(local); else SetState(local, true);
                Note($"{(install ? "Installed" : "Downloaded and enabled")} {rows.Count - errs.Count:N0} font(s) from {string.Join(", ", rows.Select(r => r.Src).Distinct())}");
            }
        }
        finally { onlineBusy = false; busyText = null; UpdateStatus(); }
        if (errs.Count > 0) MessageBox.Show(this, string.Join("\n", errs.Take(15)), "Some downloads failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static void OpenUrl(string url)
    {
        if (url == null || !url.StartsWith("https://")) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void SelectionChanged() { UpdateStatus(); debounce.Stop(); debounce.Start(); }

    void RefreshCanvas()
    {
        var sel = list.SelectedRows();
        canvas.Primary = list.FocusRow ?? sel.FirstOrDefault();
        canvas.Sel = sel.Count == 0 && canvas.Primary != null ? new List<FontRow> { canvas.Primary } : sel.Take(30).ToList();
        canvas.Remeasure();
        var need = canvas.Sel.Where(r => r.IsWeb && r.Path == null && !previewFailed.Contains(r.Key)).Take(8).ToList();
        if (canvas.Primary is { IsWeb: true, Path: null } p && !previewFailed.Contains(p.Key) && !need.Contains(p)) need.Insert(0, p);
        if (need.Count > 0) _ = FetchPreviews(need);
    }

    void UpdateStatus()
    {
        int sel = list.Sel.Count;
        status.Text = $"{list.Items.Count:N0} shown · {list.Items.Count(r => r.On):N0} enabled" + (sel > 0 ? $" · {sel:N0} selected" : "")
            + (scanning ? "  —  Scanning…" : "") + (busyText != null ? "  —  " + busyText : "") + (note != null ? "  —  " + note : "");
    }

    void Note(string msg) { note = msg; noteTimer.Stop(); noteTimer.Start(); UpdateStatus(); }

    // ---- actions

    List<FontRow> Targets()
    {
        var s = list.SelectedRows();
        if (s.Count == 0 && list.FocusRow != null) s.Add(list.FocusRow);
        return s;
    }

    void SetState(List<FontRow> rows, bool on)
    {
        if (scanning) { Note("Wait for the scan to finish"); return; }
        var fromWeb = rows.Where(r => r.IsWeb).ToList();
        if (fromWeb.Count > 0)
        {
            rows = rows.Where(r => !r.IsWeb).ToList();
            if (on) _ = GetOnline(fromWeb, false); else Note("Downloaded fonts live in your library: disable them there");
        }
        rows = rows.Where(r => r.On != on).ToList();
        if (rows.Count == 0) return;
        if (!on && rows.Any(r => r.IsSys) && MessageBox.Show(this, $"Disable {rows.Count(r => r.IsSys):N0} installed font(s)?\nThe files stay where they are; you can enable them again here.",
                "Disable installed fonts", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (rows.Count > 300 && MessageBox.Show(this, $"{(on ? "Enable" : "Disable")} {rows.Count:N0} fonts at once?", "Many fonts", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Cursor = Cursors.WaitCursor;
        var errs = new List<string>(); int done = 0;
        foreach (var r in rows)
        {
            try { if (on) lib.Enable(r); else lib.Disable(r); done++; }
            catch (Exception e) { errs.Add($"{Path.GetFileName(r.Path)}: {e.Message}"); }
        }
        Native.Broadcast();
        lib.Save();
        Cursor = Cursors.Default;
        RefreshAll();
        Note($"{(on ? "Enabled" : "Disabled")} {done:N0} font(s)");
        if (errs.Count > 0)
            MessageBox.Show(this, string.Join("\n", errs.Take(15)) + (errs.Any(e => e.Contains("denied", StringComparison.OrdinalIgnoreCase))
                ? "\n\nSystem-wide fonts need Kinetik Fonts to run as administrator." : ""), "Some fonts failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void Install(List<FontRow> rows)
    {
        var fromWeb = rows.Where(r => r.IsWeb).ToList();
        if (fromWeb.Count > 0) { _ = GetOnline(fromWeb, true); rows = rows.Where(r => !r.IsWeb).ToList(); if (rows.Count == 0) return; }
        rows = rows.Where(r => !r.IsSys && !r.Installed).ToList();
        if (rows.Count == 0) { Note("Nothing to install: those fonts are installed already"); return; }
        Cursor = Cursors.WaitCursor;
        var errs = new List<string>(); int done = 0;
        foreach (var r in rows)
        {
            try { lib.Install(r); done++; }
            catch (Exception e) { errs.Add($"{Path.GetFileName(r.Path)}: {e.Message}"); }
        }
        Native.Broadcast();
        Cursor = Cursors.Default;
        Note($"Installed {done:N0} font(s) for your account");
        Rescan();
        if (errs.Count > 0) MessageBox.Show(this, string.Join("\n", errs.Take(15)), "Some fonts failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void Favourite(List<FontRow> rows)
    {
        if (rows.Any(r => r.IsWeb)) { Note("Download a font first to add it to your favourites"); rows = rows.Where(r => !r.IsWeb).ToList(); }
        if (rows.Count == 0) return;
        bool all = rows.All(r => lib.Fav.Contains(r.Key));
        foreach (var r in rows) { if (all) lib.Fav.Remove(r.Key); else lib.Fav.Add(r.Key); }
        lib.Save();
        RefreshNav();
        if (nav.CurKind == "fav") ApplyFilter(); else list.Invalidate();
    }

    static void Reveal(FontRow r)
    {
        if (r == null) return;
        if (r.IsWeb) { OpenUrl(r.Web?.Page); return; }
        try { Process.Start("explorer.exe", $"/select,\"{r.Path}\""); } catch { }
    }

    void AddFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Choose a folder of fonts" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var p = Path.GetFullPath(d.SelectedPath);
        if (lib.Cfg.Folders.Contains(p, StringComparer.OrdinalIgnoreCase)) return;
        lib.Cfg.Folders.Add(p);
        lib.Save();
        nav.CurKind = "src"; nav.CurArg = p;
        Rescan();
    }

    void AddToSet(string name, List<FontRow> rows)
    {
        if (!lib.Cfg.Sets.TryGetValue(name, out var keys)) lib.Cfg.Sets[name] = keys = new List<string>();
        int n = 0;
        foreach (var r in rows) if (!keys.Contains(r.Key)) { keys.Add(r.Key); n++; }
        lib.Save();
        RefreshNav();
        Note($"Added {n:N0} font(s) to {name}");
    }

    void NewSet(List<FontRow> rows)
    {
        var name = Prompt.Ask(this, "New set", "Name for the new set", "", k);
        if (name == null) return;
        if (!lib.Cfg.Sets.ContainsKey(name)) lib.Cfg.Sets[name] = new List<string>();
        AddToSet(name, rows);
    }

    // ---- menus

    ContextMenuStrip NewMenu()
    {
        var m = MenuFocus.Attach(new ContextMenuStrip());
        m.Renderer = look.MenuRenderer();
        return m;
    }

    void NavPicked(NavItem n)
    {
        if (n.Kind == "addfolder") { AddFolder(); return; }
        if (n.Kind == "newset") { NewSet(Targets()); return; }
        nav.CurKind = n.Kind; nav.CurArg = n.Arg;
        nav.Invalidate();
        ApplyFilter();
        if (n.Kind == "online") _ = LoadOnline(n.Arg, false);
        else list.EmptyText = "No fonts here yet.";
    }

    void NavMenu(NavItem n, Point p)
    {
        var m = NewMenu();
        if (n.Kind == "online")
            m.Items.Add("Refresh this catalogue", null, (s, e) => _ = LoadOnline(n.Arg, true));
        else if (n.Kind == "src")
        {
            m.Items.Add("Rescan", null, (s, e) => Rescan());
            m.Items.Add("Show in Explorer", null, (s, e) => { try { Process.Start("explorer.exe", $"\"{n.Arg}\""); } catch { } });
            m.Items.Add("Remove folder from the list", null, (s, e) => { lib.Forget(n.Arg); lib.Save(); Rescan(); });
        }
        else if (n.Kind == "set")
        {
            m.Items.Add("Enable every font in this set", null, (s, e) => SetState(SetRows(n.Arg).ToList(), true));
            m.Items.Add("Disable every font in this set", null, (s, e) => SetState(SetRows(n.Arg).ToList(), false));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Rename…", null, (s, e) =>
            {
                var name = Prompt.Ask(this, "Rename set", "New name", n.Arg, k);
                if (name == null || name == n.Arg || lib.Cfg.Sets.ContainsKey(name)) return;
                lib.Cfg.Sets[name] = lib.Cfg.Sets[n.Arg]; lib.Cfg.Sets.Remove(n.Arg);
                if (nav.CurKind == "set" && nav.CurArg == n.Arg) nav.CurArg = name;
                lib.Save(); RefreshNav(); ApplyFilter();
            });
            m.Items.Add("Delete set (the fonts stay)", null, (s, e) => { lib.Cfg.Sets.Remove(n.Arg); lib.Save(); RefreshAll(); });
        }
        if (m.Items.Count > 0) m.Show(nav, p);
    }

    void ListMenu(Point p)
    {
        var rows = Targets();
        if (rows.Count == 0) return;
        var m = NewMenu();
        var c = rows.Count > 1 ? $" ({rows.Count:N0})" : "";
        if (rows.All(r => r.IsWeb))
        {
            m.Items.Add("Download and enable" + c, null, (s, e) => SetState(rows, true));
            m.Items.Add("Download and install for me" + c, null, (s, e) => Install(rows));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Open on the website", null, (s, e) => OpenUrl(rows[0].Web?.Page));
            m.Items.Add("Copy font name", null, (s, e) => { try { Clipboard.SetText(rows[0].Name); } catch { } });
            m.Show(list, p);
            return;
        }
        m.Items.Add("Enable" + c, null, (s, e) => SetState(rows, true));
        m.Items.Add("Disable" + c, null, (s, e) => SetState(rows, false));
        if (rows.Any(r => !r.IsSys && !r.Installed)) m.Items.Add("Install for me" + c, null, (s, e) => Install(rows));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(rows.All(r => lib.Fav.Contains(r.Key)) ? "Remove from favourites" : "Add to favourites", null, (s, e) => Favourite(rows));
        var add = new ToolStripMenuItem("Add to set");
        foreach (var name in lib.Cfg.Sets.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) add.DropDownItems.Add(name, null, (s, e) => AddToSet(name, rows));
        if (add.DropDownItems.Count > 0) add.DropDownItems.Add(new ToolStripSeparator());
        add.DropDownItems.Add("New set…", null, (s, e) => NewSet(rows));
        m.Items.Add(add);
        var cat = new ToolStripMenuItem("Category");
        foreach (var name in Library.Cats) { var cn = name; cat.DropDownItems.Add(new ToolStripMenuItem(cn, null, (s, e) => SetCat(rows, cn)) { Checked = rows.All(r => r.Cat == cn) }); }
        cat.DropDownItems.Add(new ToolStripSeparator());
        cat.DropDownItems.Add("Detect automatically", null, (s, e) => SetCat(rows, null));
        m.Items.Add(cat);
        if (nav.CurKind == "set")
            m.Items.Add("Remove from this set", null, (s, e) =>
            {
                var keys = rows.Select(r => r.Key).ToHashSet();
                lib.Cfg.Sets[nav.CurArg].RemoveAll(keys.Contains);
                lib.Save(); RefreshAll();
            });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Compare", null, (s, e) => tabs.Index = 1);
        m.Items.Add("Show in Explorer", null, (s, e) => Reveal(rows[0]));
        m.Items.Add("Copy font name", null, (s, e) => { try { Clipboard.SetText(rows[0].Name); } catch { } });
        m.Items.Add("Copy file path", null, (s, e) => { try { Clipboard.SetText(rows[0].Path); } catch { } });
        m.Show(list, p);
    }

    void BackupSettings()
    {
        using var d = new SaveFileDialog
        {
            Title = "Back up Kinetik Fonts settings", Filter = "Kinetik Fonts backup (*.json)|*.json", FileName = "KinetikFonts-settings.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), OverwritePrompt = true,
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { lib.Save(); lib.Backup(d.FileName); Note("Settings backed up to " + d.FileName); }
        catch (Exception e) { MessageBox.Show(this, "Could not save the backup: " + e.Message, "Back up settings", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void RestoreSettings(string file)
    {
        if (file == null)
        {
            using var d = new OpenFileDialog
            {
                Title = "Restore Kinetik Fonts settings", Filter = "Kinetik Fonts backup (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            file = d.FileName;
        }
        Config c;
        try { c = Library.Unpack(File.ReadAllText(file)); }
        catch (Exception e) { MessageBox.Show(this, "Could not read the backup: " + e.Message, "Restore settings", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (MessageBox.Show(this, $"This backup has {c.Folders.Count:N0} folder(s), {c.Favourites.Count:N0} favourite(s), {c.Sets.Count:N0} set(s) and {c.Enabled.Count:N0} enabled font(s).\n\nReplace your current settings with it?",
                "Restore settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { lib.Restore(file); }
        catch (Exception e) { MessageBox.Show(this, "Could not restore: " + e.Message, "Restore settings", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        sort.SelectedItem = lib.Cfg.Sort;
        sampleBox.T.Text = lib.Cfg.Text;
        slider.Value = lib.Cfg.Size;
        layout.Index = lib.Cfg.Stacked ? 1 : 0;
        nav.CurKind = "all"; nav.CurArg = "";
        Rescan();
        Note("Settings restored");
    }

    void SetCat(List<FontRow> rows, string cat)
    {
        foreach (var r in rows) { if (cat == null) lib.Cfg.Cats.Remove(r.Key); else { lib.Cfg.Cats[r.Key] = cat; r.Cat = cat; } }
        lib.Save();
        if (cat == null) Rescan(); else { list.Invalidate(); ApplyFilter(); }
    }

    void MoreMenu(Control at)
    {
        var m = NewMenu();
        m.Items.Add("Add folder…", null, (s, e) => AddFolder());
        m.Items.Add("Rescan everything", null, (s, e) => Rescan());
        m.Items.Add("Refresh online catalogues", null, (s, e) => _ = LoadOnline("", true));
        m.Items.Add("Download folder…", null, (s, e) =>
        {
            using var d = new FolderBrowserDialog { Description = "Where downloaded fonts are saved", SelectedPath = lib.Cfg.DownloadDir };
            if (d.ShowDialog(this) == DialogResult.OK) { lib.Cfg.DownloadDir = d.SelectedPath; lib.Save(); Note("Downloads now go to " + d.SelectedPath); }
        });
        m.Items.Add("Clear preview cache", null, (s, e) =>
        {
            int n = 0;
            try { foreach (var f in Directory.EnumerateFiles(Path.Combine(Online.Root, "preview"), "*", SearchOption.AllDirectories)) { try { File.Delete(f); n++; } catch { } } } catch { }
            Note($"Removed {n:N0} cached preview file(s)");
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Back up settings…", null, (s, e) => BackupSettings());
        m.Items.Add("Restore settings…", null, (s, e) => RestoreSettings(null));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Look.MenuItem(LookChanged));
        m.Show(at, new Point(0, at.Height));
    }

    void LookChanged()
    {
        var old = look;
        look = Look.Current;
        ToolStripManager.Renderer = look.MenuRenderer();
        Look.Recolour(this, old);
        look.Window(Handle);
        look.ScrollBars(scroll);
        Invalidate(true);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { search.T.Focus(); search.T.SelectAll(); return true; }
        if (keyData == Keys.Escape && search.T.Focused) { search.T.Clear(); list.Focus(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    static async Task<int> GetFont(string source, string family, bool install, string log)
    {
        var lines = new List<string>();
        try
        {
            var lib = new Library();
            var f = (await Online.Catalog(source, false, CancellationToken.None)).First(x => string.Equals(x.Family, family, StringComparison.OrdinalIgnoreCase));
            var root = Path.GetFullPath(lib.Cfg.DownloadDir);
            var files = await Online.Fetch(f, Path.Combine(root, Online.Safe(f.Source), Online.Safe(f.Family)), false, CancellationToken.None);
            lines.Add($"downloaded {files.Count} file(s): {string.Join(", ", files)}");
            if (!lib.Cfg.Folders.Any(x => string.Equals(x, root, StringComparison.OrdinalIgnoreCase))) lib.Cfg.Folders.Add(root);
            lib.Cfg.Downloaded.Add($"web:{f.Source}:{f.Id}");
            lib.Save();
            if (install)
            {
                var keys = files.Select(x => x.ToLowerInvariant()).ToHashSet();
                foreach (var r in lib.BuildRows().Where(x => x.Kind == "dir" && keys.Contains(x.Key)))
                {
                    lib.Install(r);
                    lines.Add($"installed for current user: {r.Name} -> {Path.Combine(Library.UserDir, Path.GetFileName(r.Path))}");
                }
                Native.Broadcast();
            }
            File.WriteAllLines(log, lines);
            return 0;
        }
        catch (Exception e) { lines.Add("FAILED " + e); File.WriteAllLines(log, lines); return 1; }
    }

    static async Task<int> OnlineTest(string outFile)
    {
        var lines = new List<string>(); int bad = 0;
        var tmp = Path.Combine(Path.GetTempPath(), "kf-online-test-" + Guid.NewGuid().ToString("N"));
        foreach (var src in Online.Sources)
        {
            try
            {
                var cat = await Online.Catalog(src, true, CancellationToken.None);
                var pick = cat.FirstOrDefault(f => f.Family == "Lobster") ?? cat.First();
                var files = await Online.Fetch(pick, Path.Combine(tmp, Online.Safe(src)), true, CancellationToken.None);
                var n = Library.FaceInfo(files[0]);
                lines.Add($"{src}: {cat.Count} fonts; sample {pick.Family} ({pick.Category}, {pick.Designer}) -> {Path.GetFileName(files[0])} family '{n.GetValueOrDefault(1)}' category {n.GetValueOrDefault(-1)}");
            }
            catch (Exception e) { bad++; lines.Add($"{src}: FAILED {e.GetType().Name}: {e.Message}"); }
        }
        File.WriteAllLines(outFile, lines);
        return bad;
    }

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Contains("--test"))
        {
            var t = new Config { Folders = { @"D:\x" }, Favourites = { "a" }, Sets = { ["S"] = new() { "k" } }, Size = 500, Sort = "bogus", Cats = { ["f"] = "Script", ["g"] = "Nonsense" } };
            t.Enabled.Add(@"C:\Windows\Fonts\arial.ttf"); t.Enabled.Add("not-a-font.exe");
            var u = Library.Unpack(Library.Pack(t));
            bool backupOk = u.Folders.Count == 1 && u.Favourites[0] == "a" && u.Sets["S"][0] == "k" && u.Size == 120 && u.Sort == "Name"
                && u.Cats.Count == 1 && u.Enabled.Count == 1 && u.Disabled.Count == 0;
            bool refuses = false;
            try { Library.Unpack("{\"app\":\"other\"}"); } catch (InvalidDataException) { refuses = true; }
            return backupOk && refuses && Library.FaceInfo(Path.Combine(Library.SysDir, "arial.ttf")).GetValueOrDefault(1) == "Arial"
                && Library.Installed().Count > 20 && Library.Norm("Arial Bold!") == "arialbold" ? 0 : 1;
        }
        int di = Array.IndexOf(a, "--dump"); // test aid: every font with its detected category
        if (di >= 0) { File.WriteAllLines(a[di + 1], new Library().BuildRows().Select(r => { var n = r.Ready || !File.Exists(r.Path) ? null : (Dictionary<int, string>)null; Dictionary<int, string> d = null; try { d = Library.FaceInfo(r.Path); } catch { } return $"{r.Cat}	{r.Name}	{r.Src}	{d?.GetValueOrDefault(-2)}"; })); return 0; }
        int gi = Array.IndexOf(a, "--get"); // --get <source> <family> [install] <log>: downloads a font into the library and optionally installs it, headless
        if (gi >= 0) return GetFont(a[gi + 1], a[gi + 2], a.Contains("install"), a[^1]).GetAwaiter().GetResult();
        int oi = Array.IndexOf(a, "--online-test"); // test aid: loads each catalogue and fetches one preview from each
        if (oi >= 0) return OnlineTest(a[oi + 1]).GetAwaiter().GetResult();
        int si = Array.IndexOf(a, "--shot");
        if (si >= 0) { ShotPath = a[si + 1]; ShotTab = si + 2 < a.Length ? int.Parse(a[si + 2]) : 0; if (si + 3 < a.Length && a[si + 3] != "-") ShotNav = a[si + 3]; if (si + 4 < a.Length && a[si + 4] != "-") ShotQuery = a[si + 4]; if (si + 5 < a.Length) ShotSel = int.Parse(a[si + 5]); }
        using var single = new Mutex(true, "KinetikFonts.Single", out var first);
        if (!first && ShotPath != null) first = true;
        if (!first) return 0;
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ToolStripManager.Renderer = Look.Current.MenuRenderer();
        using (new Shutdown("KinetikFonts", () => Application.Exit()))
            Application.Run(new FontsForm());
        return 0;
    }
}
