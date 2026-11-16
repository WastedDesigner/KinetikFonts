// Custom-drawn controls in the Kinetik look: the font list (names drawn in their own fonts), sidebar, tabs, slider,
// text box and the preview / compare / glyph / info canvas.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KinetikSuite;

// Draws text in a font taken straight from its file. Each file gets its own private font collection, so two files that share
// a family name (two versions of Lobster, say) never get mixed up, and a font draws without being enabled anywhere.
static class Gdi
{
    public const uint Center = 1, VCenter = 4, Wrap = 0x10, Single = 0x20, Ellipsis = 0x8000;

    sealed class Face { public PrivateFontCollection Pfc; public FontFamily Fam; public FontStyle Style; }
    static readonly Dictionary<string, Face> faces = new();
    static readonly Queue<string> order = new();

    static Face Get(FontRow f)
    {
        Library.Prepare(f);
        if (f.Path == null) return null;
        if (faces.TryGetValue(f.Path, out var hit)) return hit;
        Face face = null;
        try
        {
            var pfc = new PrivateFontCollection();
            pfc.AddFontFile(f.Path);
            var fam = pfc.Families.FirstOrDefault(x => x.Name == f.Fam) ?? pfc.Families[0];
            var want = (f.Bold ? FontStyle.Bold : 0) | (f.Italic ? FontStyle.Italic : 0);
            var style = fam.IsStyleAvailable(want) ? want : new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic }.First(fam.IsStyleAvailable);
            face = new Face { Pfc = pfc, Fam = fam, Style = style };
        }
        catch { } // a file GDI+ cannot read is drawn in the interface font
        if (order.Count >= 160 && faces.Remove(order.Dequeue(), out var old)) old?.Pfc.Dispose();
        faces[f.Path] = face; order.Enqueue(f.Path);
        return face;
    }

    static Font Make(Face face, int px) => face == null ? new Font("Segoe UI", px, GraphicsUnit.Pixel) : new Font(face.Fam, px, face.Style, GraphicsUnit.Pixel);

    static StringFormat Fmt(uint flags)
    {
        var sf = new StringFormat(StringFormat.GenericTypographic);
        sf.FormatFlags &= ~(StringFormatFlags.NoClip | StringFormatFlags.LineLimit); // LineLimit hides a line that is taller than its box, which blanks fonts with tall metrics
        if ((flags & Wrap) == 0 || (flags & Single) != 0) sf.FormatFlags |= StringFormatFlags.NoWrap;
        if ((flags & Ellipsis) != 0) sf.Trimming = StringTrimming.EllipsisCharacter;
        if ((flags & VCenter) != 0) sf.LineAlignment = StringAlignment.Center;
        if ((flags & Center) != 0) sf.Alignment = StringAlignment.Center;
        return sf;
    }

    public static void Draw(Graphics g, string s, FontRow f, int px, Color c, Rectangle r, uint flags)
    {
        using var font = Make(Get(f), px);
        using var sf = Fmt(flags);
        using var b = new SolidBrush(c);
        var st = g.Save();
        g.SetClip(r, CombineMode.Intersect);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.DrawString(s, font, b, r, sf);
        g.Restore(st);
    }

    public static int Height(Graphics g, string s, FontRow f, int px, int width, uint flags)
    {
        using var font = Make(Get(f), px);
        using var sf = Fmt(flags);
        return Math.Max((int)(px * 1.25f), (int)Math.Ceiling(g.MeasureString(string.IsNullOrEmpty(s) ? " " : s, font, width, sf).Height));
    }
}

static class Ui2
{
    public static Color Dim(Color a, Color b, float t) => Look.Mix(a, b, t);
    public static PointF[] Star(float cx, float cy, float R)
    {
        var p = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / 5;
            float r = i % 2 == 0 ? R : R * 0.45f;
            p[i] = new PointF(cx + (float)(r * Math.Cos(a)), cy + (float)(r * Math.Sin(a)));
        }
        return p;
    }
    public static Color CatColour(string cat) => cat switch
    {
        "Serif" => Color.FromArgb(232, 160, 100), "Slab" => Color.FromArgb(214, 170, 70), "Sans serif" => Color.FromArgb(96, 165, 235),
        "Script" => Color.FromArgb(205, 125, 230), "Display" => Color.FromArgb(240, 105, 125), "Monospace" => Color.FromArgb(95, 205, 165),
        "Symbol" => Color.FromArgb(190, 190, 95), _ => Color.FromArgb(150, 150, 165),
    };

    // The small coloured tag with a font's category. Returns its width so the text after it can follow.
    public static int Chip(Graphics g, Look l, Font f, string cat, int x, int y, int h, float k)
    {
        var c = CatColour(cat);
        int w = TextRenderer.MeasureText(g, cat, f, new Size(1000, h), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + (int)(14 * k);
        var r = new RectangleF(x, y, w, h);
        Paths.Fill(g, r, h / 2f, Color.FromArgb(l.Light ? 50 : 46, c));
        TextRenderer.DrawText(g, cat, f, Rectangle.Round(r), l.Light ? Look.Mix(c, Color.Black, .4f) : Look.Mix(c, Color.White, .15f),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return w;
    }

    public static readonly TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
    public static readonly TextFormatFlags Right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
}

// The list of fonts. Virtual: only the visible rows are drawn, so thousands of fonts stay quick.
class FontList : Control
{
    public List<FontRow> Items = new();
    public readonly HashSet<FontRow> Sel = new();
    public Func<FontRow, bool> IsFav = _ => false;
    public float K = 1;
    public string EmptyText = "No fonts here yet.";
    public event Action SelectionChanged;
    public event Action<FontRow> ToggleRow, FavRow;
    public event Action<Point> ContextRequested;

    readonly VScrollBar sb = new() { Dock = DockStyle.Right, Visible = false };
    Font small, big;
    int top, focus = -1, anchor = -1, hover = -1;
    FontRow focusRow;

    public FontList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Controls.Add(sb);
        Look.Current.ScrollBars(sb);
        sb.Scroll += (s, e) => { top = sb.Value; Invalidate(); };
    }

    int S(float v) => (int)(v * K);
    int RowH => S(56);
    bool bar;
    int ViewW => bar ? Width - sb.Width : Width;
    public FontRow FocusRow => focus >= 0 && focus < Items.Count ? Items[focus] : null;
    public List<FontRow> SelectedRows() => Items.Where(Sel.Contains).ToList();

    public void SetItems(List<FontRow> items)
    {
        Items = items;
        var set = new HashSet<FontRow>(items);
        Sel.RemoveWhere(r => !set.Contains(r));
        focus = focusRow == null ? -1 : items.IndexOf(focusRow);
        if (focus < 0) { focusRow = null; anchor = -1; }
        UpdateScroll();
        Invalidate();
    }

    public void Preselect(int n)
    {
        Sel.Clear();
        for (int i = 0; i < Math.Min(n, Items.Count); i++) Sel.Add(Items[i]);
        if (Items.Count > 0) { focus = 0; focusRow = Items[0]; anchor = 0; }
        Invalidate();
        SelectionChanged?.Invoke();
    }

    void UpdateScroll()
    {
        int total = Items.Count * RowH;
        bar = total > Height;
        sb.Visible = bar;
        if (bar)
        {
            sb.Minimum = 0; sb.Maximum = Math.Max(1, total - 1); sb.SmallChange = RowH; sb.LargeChange = Math.Max(1, Math.Min(Height, sb.Maximum));
            top = Math.Max(0, Math.Min(top, total - Height));
            sb.Value = Math.Max(sb.Minimum, Math.Min(sb.Maximum, top));
        }
        else top = 0;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScroll(); }

    void Scroll(int to)
    {
        int max = Math.Max(0, Items.Count * RowH - Height);
        top = Math.Max(0, Math.Min(max, to));
        if (bar) sb.Value = Math.Max(sb.Minimum, Math.Min(sb.Maximum, top));
        Invalidate();
    }

    void EnsureVisible(int i)
    {
        if (i < 0) return;
        if (i * RowH < top) Scroll(i * RowH);
        else if ((i + 1) * RowH > top + Height) Scroll((i + 1) * RowH - Height);
    }

    int At(int y) { int i = (y + top) / RowH; return i >= 0 && i < Items.Count ? i : -1; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var l = Look.Current;
        small ??= new Font(Font.FontFamily, 8f);
        big ??= new Font(Font.FontFamily, 12f);
        g.Clear(l.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Items.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, Font, ClientRectangle, l.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        int first = top / RowH, last = Math.Min(Items.Count - 1, (top + Height) / RowH);
        for (int i = first; i <= last; i++) DrawRow(g, l, i, new Rectangle(0, i * RowH - top, ViewW, RowH));
    }

    void DrawRow(Graphics g, Look l, int i, Rectangle rc)
    {
        var r = Items[i];
        bool sel = Sel.Contains(r), hot = i == hover;
        var card = new RectangleF(rc.X + S(8), rc.Y + S(2), rc.Width - S(16), rc.Height - S(4));
        if (sel) Paths.Fill(g, card, S(8), Color.FromArgb(54, l.Accent));
        else if (hot) Paths.Fill(g, card, S(8), l.Hover);
        if (i == focus && Focused_) Paths.Stroke(g, card, S(8), Color.FromArgb(140, l.Accent));

        float cx = rc.X + S(30), cy = rc.Y + rc.Height / 2f, rad = S(8);
        if (r.On)
        {
            using (var b = new SolidBrush(l.Accent)) g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
            using var p = new Pen(l.OnAccent, Math.Max(1.5f, S(1.8f))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(p, new[] { new PointF(cx - rad * .45f, cy), new PointF(cx - rad * .1f, cy + rad * .42f), new PointF(cx + rad * .5f, cy - rad * .38f) });
        }
        else using (var p = new Pen(Color.FromArgb(160, l.Sub), Math.Max(1.5f, S(1.5f)))) g.DrawEllipse(p, cx - rad, cy - rad, rad * 2, rad * 2);

        int left = rc.X + S(54), right = rc.Right - S(230);
        var nameColour = r.On ? l.Text : Look.Mix(l.Text, l.Bg, .3f);
        if (r.Path == null) // an online font not fetched yet: its name in the interface font until its preview arrives
            TextRenderer.DrawText(g, r.Name, big, new Rectangle(left, rc.Y + S(5), right - left, S(30)), nameColour, Ui2.Left);
        else
            Gdi.Draw(g, r.Name, r, S(22), nameColour, new Rectangle(left, rc.Y + S(5), right - left, S(30)), Gdi.Single | Gdi.Ellipsis | Gdi.VCenter);
        int cw = Ui2.Chip(g, l, small, r.Cat, left, rc.Y + S(35), S(17), K);
        TextRenderer.DrawText(g, r.IsWeb ? (r.Style.Length > 0 ? r.Style + "  ·  " : "") + r.Src : $"{r.Style}  ·  {r.Src}", small,
            new Rectangle(left + cw + S(8), rc.Y + S(34), Math.Max(10, right - left - cw - S(8)), S(19)), l.Sub, Ui2.Left);

        string badge = r.IsWeb ? (r.Installed ? "On this PC" : r.On ? "Downloaded" : "Online") : r.IsSys ? "Installed" : r.Installed ? "Installed copy" : "Folder";
        if (r.Dup) badge = "Duplicate · " + badge;
        TextRenderer.DrawText(g, badge, small, new Rectangle(rc.Right - S(230), rc.Y, S(190), rc.Height), r.Dup ? l.Warn : l.Sub, Ui2.Right);

        bool fav = IsFav(r);
        if (fav || hot || sel)
        {
            var pts = Ui2.Star(rc.Right - S(28), cy, S(8));
            if (fav) using (var b = new SolidBrush(l.Accent)) g.FillPolygon(b, pts);
            else using (var p = new Pen(Color.FromArgb(150, l.Sub), 1.4f)) g.DrawPolygon(p, pts);
        }
    }

    bool Focused_ => FocusRow != null && ContainsFocus;

    void Select(int i, bool ctrl, bool shift)
    {
        if (i < 0) return;
        if (shift && anchor >= 0)
        {
            if (!ctrl) Sel.Clear();
            for (int j = Math.Min(anchor, i); j <= Math.Max(anchor, i); j++) Sel.Add(Items[j]);
        }
        else if (ctrl) { if (!Sel.Remove(Items[i])) Sel.Add(Items[i]); anchor = i; }
        else { Sel.Clear(); Sel.Add(Items[i]); anchor = i; }
        focus = i; focusRow = Items[i];
        EnsureVisible(i);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        int i = At(e.Y);
        if (i < 0) { if (e.Button == MouseButtons.Left && ModifierKeys == 0) { Sel.Clear(); Invalidate(); SelectionChanged?.Invoke(); } return; }
        var r = Items[i];
        if (e.Button == MouseButtons.Left && e.X < S(48)) { ToggleRow?.Invoke(r); return; }
        if (e.Button == MouseButtons.Left && e.X > ViewW - S(50)) { FavRow?.Invoke(r); Invalidate(); return; }
        if (e.Button == MouseButtons.Right && Sel.Contains(r)) { focus = i; focusRow = r; Invalidate(); }
        else Select(i, ModifierKeys.HasFlag(Keys.Control), ModifierKeys.HasFlag(Keys.Shift));
        if (e.Button == MouseButtons.Right) ContextRequested?.Invoke(e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        int i = At(e.Y);
        if (i >= 0 && e.Button == MouseButtons.Left && e.X >= S(48) && e.X <= ViewW - S(50)) ToggleRow?.Invoke(Items[i]);
    }

    protected override void OnMouseMove(MouseEventArgs e) { int i = At(e.Y); if (i != hover) { hover = i; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { Scroll(top - e.Delta / 120 * RowH * 3); base.OnMouseWheel(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys k)
    {
        switch (k & Keys.KeyCode)
        {
            case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: return true;
        }
        return base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int page = Math.Max(1, Height / RowH - 1), cur = focus < 0 ? 0 : focus;
        bool shift = e.Shift;
        switch (e.KeyCode)
        {
            case Keys.Down: Select(Math.Min(Items.Count - 1, cur + (focus < 0 ? 0 : 1)), false, shift); break;
            case Keys.Up: Select(Math.Max(0, cur - 1), false, shift); break;
            case Keys.PageDown: Select(Math.Min(Items.Count - 1, cur + page), false, shift); break;
            case Keys.PageUp: Select(Math.Max(0, cur - page), false, shift); break;
            case Keys.Home: Select(0, false, shift); break;
            case Keys.End: Select(Items.Count - 1, false, shift); break;
            case Keys.A when e.Control: foreach (var r in Items) Sel.Add(r); Invalidate(); SelectionChanged?.Invoke(); break;
            case Keys.Space: if (FocusRow != null) { if (!Sel.Contains(FocusRow)) Sel.Add(FocusRow); ToggleRow?.Invoke(FocusRow); } break;
            case Keys.F: if (FocusRow != null) { FavRow?.Invoke(FocusRow); Invalidate(); } break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }
}

class NavItem { public string Kind, Arg, Text; public int Count = -1; public bool Head, Add; }

class Sidebar : Control
{
    public List<NavItem> Items = new();
    public string CurKind = "all", CurArg = "";
    public float K = 1;
    public event Action<NavItem> Picked;
    public event Action<NavItem, Point> Context;
    int hover = -1, top;
    Font small, brand;

    public Sidebar() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

    int S(float v) => (int)(v * K);
    int Brand => S(66);
    int H(NavItem n) => n.Head ? S(38) : S(32);
    int Total() => Items.Sum(H);

    int At(int y)
    {
        int cy = Brand - top;
        for (int i = 0; i < Items.Count; i++) { int h = H(Items[i]); if (y >= cy && y < cy + h) return i; cy += h; }
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var l = Look.Current;
        small ??= new Font(Font.FontFamily, 7.5f, FontStyle.Bold);
        brand ??= new Font(Font.FontFamily, 12.5f, FontStyle.Bold);
        g.Clear(l.Nav);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int y = Brand - top;
        for (int i = 0; i < Items.Count; i++)
        {
            var n = Items[i]; int h = H(n);
            if (y + h > Brand && y < Height)
            {
                var rc = new Rectangle(S(10), y, Width - S(20), h);
                if (n.Head) TextRenderer.DrawText(g, n.Text.ToUpperInvariant(), small, new Rectangle(S(20), y + S(10), Width - S(30), S(22)), l.Sub, Ui2.Left);
                else
                {
                    bool cur = !n.Add && n.Kind == CurKind && n.Arg == CurArg;
                    var card = new RectangleF(rc.X, rc.Y + 1, rc.Width, rc.Height - 2);
                    if (cur) Paths.Fill(g, card, S(8), Color.FromArgb(54, l.Accent));
                    else if (i == hover) Paths.Fill(g, card, S(8), l.Hover);
                    var col = n.Add ? l.Accent : cur ? l.Text : Look.Mix(l.Text, l.Nav, .12f);
                    TextRenderer.DrawText(g, n.Text, Font, new Rectangle(rc.X + S(12), rc.Y, rc.Width - S(56), rc.Height), col, Ui2.Left);
                    if (n.Count >= 0) TextRenderer.DrawText(g, n.Count.ToString("N0"), Font, new Rectangle(rc.Right - S(54), rc.Y, S(44), rc.Height), l.Sub, Ui2.Right);
                }
            }
            y += h;
        }
        using (var b = new SolidBrush(l.Nav)) g.FillRectangle(b, 0, 0, Width, Brand);
        TextRenderer.DrawText(g, "Kinetik Fonts", brand, new Rectangle(S(20), S(8), Width - S(30), S(48)), l.Text, Ui2.Left);
        using (var p = new Pen(l.Border, 1)) g.DrawLine(p, Width - 1, 0, Width - 1, Height);
    }

    protected override void OnMouseMove(MouseEventArgs e) { int i = At(e.Y); if (i != hover) { hover = i; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { top = Math.Max(0, Math.Min(Math.Max(0, Total() + Brand - Height), top - e.Delta / 120 * S(48))); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = At(e.Y);
        if (i < 0 || Items[i].Head) return;
        if (e.Button == MouseButtons.Left) Picked?.Invoke(Items[i]);
        else if (e.Button == MouseButtons.Right && !Items[i].Add) Context?.Invoke(Items[i], e.Location);
    }
}

class Slider : Control
{
    public int Min = 10, Max = 120;
    int val = 36;
    public float K = 1;
    public event Action Changed;
    public int Value { get => val; set { value = Math.Max(Min, Math.Min(Max, value)); if (value != val) { val = value; Invalidate(); Changed?.Invoke(); } } }
    bool drag;

    public Slider() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Height = 28; Cursor = Cursors.Hand; }

    float X(int v) => 10 * K + (Width - 20 * K) * (v - Min) / (Max - Min);
    void Pick(int x) => Value = Min + (int)Math.Round((x - 10 * K) / (Width - 20 * K) * (Max - Min));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var l = Look.Current;
        g.Clear(Parent?.BackColor ?? l.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cy = Height / 2f, x = X(val), t = 4 * K;
        Paths.Fill(g, new RectangleF(10 * K, cy - t / 2, Width - 20 * K, t), t / 2, l.Border);
        Paths.Fill(g, new RectangleF(10 * K, cy - t / 2, x - 10 * K, t), t / 2, l.Accent);
        float r = 8 * K;
        using var b = new SolidBrush(l.Accent);
        g.FillEllipse(b, x - r, cy - r, r * 2, r * 2);
        using var p = new Pen(l.Card, 2 * K);
        g.DrawEllipse(p, x - r + K, cy - r + K, r * 2 - 2 * K, r * 2 - 2 * K);
    }

    protected override void OnMouseDown(MouseEventArgs e) { drag = true; Pick(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { if (drag) Pick(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { drag = false; }
}

class Segmented : Control
{
    public string[] Items = Array.Empty<string>();
    int index, hot = -1;
    public float K = 1;
    public event Action Changed;
    public int Index { get => index; set { if (value != index) { index = value; Invalidate(); Changed?.Invoke(); } } }

    public Segmented() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; }

    int At(int x) => Items.Length == 0 ? -1 : Math.Min(Items.Length - 1, x * Items.Length / Math.Max(1, Width));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var l = Look.Current;
        g.Clear(Parent?.BackColor ?? l.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var all = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        Paths.Fill(g, all, 8 * K, l.Field);
        Paths.Stroke(g, all, 8 * K, l.Border);
        float w = (Width - 2f) / Math.Max(1, Items.Length);
        for (int i = 0; i < Items.Length; i++)
        {
            var seg = new RectangleF(1 + i * w + 2 * K, 1 + 2 * K, w - 4 * K, Height - 4 - 4 * K);
            if (i == index) Paths.Fill(g, seg, 6 * K, Color.FromArgb(70, l.Accent));
            else if (i == hot) Paths.Fill(g, seg, 6 * K, l.Hover);
            TextRenderer.DrawText(g, Items[i], Font, Rectangle.Round(seg), i == index ? l.Text : l.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e) { int i = At(e.X); if (i != hot) { hot = i; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { int i = At(e.X); if (i >= 0) Index = i; }
}

// A text box with a rounded field behind it.
class Box : Control
{
    public readonly TextBox T = new() { BorderStyle = BorderStyle.None };
    readonly float k;

    public Box(float k, string placeholder = null)
    {
        this.k = k;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = (int)(34 * k);
        if (placeholder != null) T.PlaceholderText = placeholder;
        T.BackColor = Look.Current.Field; T.ForeColor = Look.Current.Text;
        T.GotFocus += (s, e) => Invalidate(); T.LostFocus += (s, e) => Invalidate();
        Controls.Add(T);
    }

    protected override void OnFontChanged(EventArgs e) { T.Font = Font; Layout2(); base.OnFontChanged(e); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Layout2(); }
    void Layout2() => T.SetBounds((int)(12 * k), Math.Max(0, (Height - T.Height) / 2), Math.Max(10, Width - (int)(24 * k)), T.Height);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var l = Look.Current;
        g.Clear(Parent?.BackColor ?? l.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        Paths.Fill(g, r, 7 * k, l.Field);
        Paths.Stroke(g, r, 7 * k, T.Focused ? l.Accent : l.Border);
        T.BackColor = l.Field; T.ForeColor = l.Text;
    }
}

// Preview, compare, glyphs and info: one canvas, laid out and drawn by the same pass so they cannot disagree.
class Canvas : Control
{
    public string Mode = "Preview";
    public List<FontRow> Sel = new();
    public FontRow Primary;
    public string Sample = "";
    public int Px = 36;
    public bool Stacked;
    public float K = 1;
    public Func<FontRow, Dictionary<int, string>> MetaOf;
    public event Action<string> Notice;
    Font small, bold, title;
    readonly List<(Rectangle r, string ch)> hits = new();
    int hover = -1;

    bool measuring;
    int lastW;

    public Canvas() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

    int S(float v) => (int)(v * K);
    string Txt => string.IsNullOrWhiteSpace(Sample) ? "The quick brown fox jumps over the lazy dog" : Sample;

    public void Remeasure()
    {
        if (measuring || !IsHandleCreated) return;
        measuring = true;
        try
        {
            using var g = CreateGraphics();
            Height = Math.Max(S(200), Run(g, false));
        }
        finally { measuring = false; }
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width != lastW) { lastW = Width; Remeasure(); }
    }

    protected override void OnPaint(PaintEventArgs e) => Run(e.Graphics, true);

    int Run(Graphics g, bool paint)
    {
        var l = Look.Current;
        small ??= new Font(Font.FontFamily, 8f);
        bold ??= new Font(Font.FontFamily, 9f, FontStyle.Bold);
        title ??= new Font(Font.FontFamily, 12f, FontStyle.Bold);
        hits.Clear();
        if (paint) { g.Clear(l.Card); g.SmoothingMode = SmoothingMode.AntiAlias; }
        int y = S(18);
        switch (Mode)
        {
            case "Compare": y = Compare(g, l, paint, y); break;
            case "Glyphs": y = Glyphs(g, l, paint, y); break;
            case "Info": y = Info(g, l, paint, y); break;
            default: y = Preview(g, l, paint, y); break;
        }
        return y + S(18);
    }

    int Empty(Graphics g, Look l, bool paint, int y, string msg)
    {
        if (paint) TextRenderer.DrawText(g, msg, Font, new Rectangle(S(18), y, Width - S(36), S(60)), l.Sub, TextFormatFlags.WordBreak);
        return y + S(60);
    }

    void Heading(Graphics g, Look l, bool paint, FontRow f, ref int y)
    {
        int pad = S(18);
        if (paint)
        {
            TextRenderer.DrawText(g, f.Name, title, new Rectangle(pad, y, Width - 2 * pad, S(26)), l.Text, Ui2.Left);
            TextRenderer.DrawText(g, f.IsWeb ? $"{f.Cat}  ·  {f.Src}  ·  {(f.Path == null ? "loading preview…" : "preview")}" : $"{f.Fam}  ·  {f.Style}  ·  {f.Cat}  ·  {f.Src}  ·  {(f.On ? "Enabled" : "Disabled")}", small, new Rectangle(pad, y + S(26), Width - 2 * pad, S(18)), l.Sub, Ui2.Left);
        }
        y += S(54);
    }

    int Preview(Graphics g, Look l, bool paint, int y)
    {
        var f = Primary;
        if (f == null) return Empty(g, l, paint, y, "Select a font in the list to preview it here.");
        int pad = S(18), w = Width - 2 * pad;
        Heading(g, l, paint, f, ref y);
        int h = Gdi.Height(g, Txt, f, S(Px), w, Gdi.Wrap);
        if (paint) Gdi.Draw(g, Txt, f, S(Px), l.Text, new Rectangle(pad, y, w, h), Gdi.Wrap);
        y += h + S(22);
        if (paint) using (var p = new Pen(l.Border)) g.DrawLine(p, pad, y, Width - pad, y);
        y += S(14);
        if (paint) TextRenderer.DrawText(g, "SIZES", small, new Rectangle(pad, y, w, S(18)), l.Sub, Ui2.Left);
        y += S(24);
        var line = Txt.Replace("\r", " ").Replace("\n", " ");
        foreach (var sz in new[] { 10, 12, 14, 18, 24, 36, 48, 60, 72 })
        {
            int rh = (int)(S(sz) * 1.3f);
            if (paint)
            {
                TextRenderer.DrawText(g, sz.ToString(), small, new Rectangle(pad, y, S(30), rh), l.Sub, Ui2.Left);
                Gdi.Draw(g, line, f, S(sz), l.Text, new Rectangle(pad + S(36), y, w - S(36), rh), Gdi.Single | Gdi.Ellipsis | Gdi.VCenter);
            }
            y += rh + S(4);
        }
        return y;
    }

    int Compare(Graphics g, Look l, bool paint, int y)
    {
        if (Sel.Count == 0) return Empty(g, l, paint, y, "Select two or more fonts in the list (Ctrl or Shift-click) to compare them side by side.");
        int pad = S(18), gap = S(12);
        if (paint) TextRenderer.DrawText(g, Sel.Count == 1 ? "Comparing 1 font. Select more in the list." : $"Comparing {Sel.Count} fonts", small, new Rectangle(pad, y, Width - 2 * pad, S(18)), l.Sub, Ui2.Left);
        y += S(26);
        int cols = Stacked ? 1 : Math.Max(1, Math.Min(3, (Width - pad) / S(250)));
        int cw = (Width - 2 * pad - gap * (cols - 1)) / cols;
        for (int i = 0; i < Sel.Count; i += cols)
        {
            var row = Sel.Skip(i).Take(cols).ToList();
            int rh = 0;
            var hs = row.Select(f => Gdi.Height(g, Txt, f, S(Px), cw - S(28), Gdi.Wrap)).ToList();
            rh = hs.Max() + S(56);
            for (int c = 0; c < row.Count; c++)
            {
                var f = row[c]; var card = new RectangleF(pad + c * (cw + gap), y, cw, rh);
                if (paint)
                {
                    Paths.Fill(g, card, S(10), l.Field);
                    Paths.Stroke(g, card, S(10), l.Border);
                    using (var b = new SolidBrush(f.On ? l.Accent : Color.FromArgb(120, l.Sub))) g.FillEllipse(b, card.X + S(14), card.Y + S(15), S(8), S(8));
                    TextRenderer.DrawText(g, f.Name, bold, new Rectangle((int)card.X + S(30), (int)card.Y + S(8), cw - S(44), S(22)), l.Text, Ui2.Left);
                    Gdi.Draw(g, Txt, f, S(Px), l.Text, new Rectangle((int)card.X + S(14), (int)card.Y + S(40), cw - S(28), hs[c]), Gdi.Wrap);
                }
            }
            y += rh + gap;
        }
        return y;
    }

    const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~¡¢£¥§©«®°±µ¶·»¿ÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖ×ØÙÚÛÜÝÞßàáâãäåæçèéêëìíîïðñòóôõö÷øùúûüýþÿŒœŠšŽžƒˆ˜–—‘’‚“”„†‡•…‰‹›€™";

    int Glyphs(Graphics g, Look l, bool paint, int y)
    {
        var f = Primary;
        if (f == null) return Empty(g, l, paint, y, "Select a font to see its characters. Click a character to copy it.");
        int pad = S(18);
        Heading(g, l, paint, f, ref y);
        int cell = Math.Max(S(44), Math.Min(S(150), (int)(S(Px) * 1.7f)));
        int cols = Math.Max(1, (Width - 2 * pad) / cell);
        int x0 = pad + (Width - 2 * pad - cols * cell) / 2;
        for (int i = 0; i < Chars.Length; i++)
        {
            var rc = new Rectangle(x0 + i % cols * cell, y + i / cols * cell, cell, cell);
            var s = Chars[i].ToString();
            hits.Add((rc, s));
            if (!paint) continue;
            var inner = new RectangleF(rc.X + 2, rc.Y + 2, rc.Width - 4, rc.Height - 4);
            Paths.Fill(g, inner, S(6), hits.Count - 1 == hover ? l.Hover : l.Field);
            Paths.Stroke(g, inner, S(6), l.Border);
            Gdi.Draw(g, s, f, (int)(cell * 0.5f), l.Text, rc, Gdi.Center | Gdi.VCenter | Gdi.Single);
        }
        return y + (Chars.Length + cols - 1) / cols * cell;
    }

    static string FileSize(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1024.0:0} KB";

    int Info(Graphics g, Look l, bool paint, int y)
    {
        var f = Primary;
        if (f == null) return Empty(g, l, paint, y, "Select a font to see its details.");
        int pad = S(18), lw = S(110), w = Width - 2 * pad - lw;
        Heading(g, l, paint, f, ref y);
        Dictionary<int, string> m;
        try { m = MetaOf(f) ?? new(); } catch { m = new(); }
        var rows = f.IsWeb ? new (string, string)[]
        {
            ("Status", f.Installed ? "A font with this name is on this PC" : f.On ? "Downloaded to your library" : "Online, not downloaded"),
            ("Source", f.Src), ("Category", f.Cat), ("Designer", f.Web?.Designer), ("License", f.Web?.License), ("Page", f.Web?.Page),
        } : new (string, string)[]
        {
            ("Status", f.On ? "Enabled" : "Disabled"),
            ("Category", f.Cat),
            ("Installed", f.IsSys ? "Yes, " + f.Src.ToLowerInvariant() : f.Installed ? "Yes, a copy is installed in Windows" : "No, it only lives in " + f.Folder),
            ("Duplicate", f.Dup ? "Yes, another font has this name" : "No"),
            ("Family", f.Fam), ("Style", f.Style),
            ("Version", m.GetValueOrDefault(5)), ("PostScript", m.GetValueOrDefault(6)),
            ("Designer", m.GetValueOrDefault(9)), ("Foundry", m.GetValueOrDefault(8)),
            ("Copyright", m.GetValueOrDefault(0)), ("License", m.GetValueOrDefault(13) ?? m.GetValueOrDefault(14)),
            ("File", f.Path), ("Size", FileSize(f.Bytes)), ("Modified", f.Modified.ToString("d MMM yyyy")),
        };
        foreach (var (k, v0) in rows)
        {
            if (string.IsNullOrWhiteSpace(v0)) continue;
            var v = v0.Replace("\\", "\\" + "​"); // lets long paths wrap
            int h = Math.Max(S(20), TextRenderer.MeasureText(g, v, Font, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
            if (paint)
            {
                TextRenderer.DrawText(g, k, small, new Rectangle(pad, y, lw, S(20)), l.Sub, Ui2.Left);
                TextRenderer.DrawText(g, v, Font, new Rectangle(pad + lw, y, w, h), l.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                using var p = new Pen(l.Border); g.DrawLine(p, pad, y + h + S(6), Width - pad, y + h + S(6));
            }
            y += h + S(14);
        }
        return y;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Mode != "Glyphs") return;
        int i = hits.FindIndex(h => h.r.Contains(e.Location));
        if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (Mode != "Glyphs") return;
        int i = hits.FindIndex(h => h.r.Contains(e.Location));
        if (i < 0) return;
        try { Clipboard.SetText(hits[i].ch); Notice?.Invoke($"Copied {hits[i].ch}"); } catch { }
    }
}

static class Prompt
{
    public static string Ask(IWin32Window owner, string title, string label, string initial, float k)
    {
        var l = Look.Current;
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            MaximizeBox = false, MinimizeBox = false, BackColor = l.Bg, ForeColor = l.Text, AutoScaleMode = AutoScaleMode.None,
            ClientSize = new Size((int)(380 * k), (int)(150 * k)), Font = new Font("Segoe UI", 9f)
        };
        var lab = new Label { Text = label, Left = (int)(20 * k), Top = (int)(18 * k), Width = (int)(340 * k), Height = (int)(22 * k), ForeColor = l.Text };
        var box = new Box(k) { Left = (int)(20 * k), Top = (int)(46 * k), Width = (int)(340 * k), Font = f.Font };
        box.T.Text = initial ?? ""; box.T.SelectAll();
        var ok = new FlatButton("OK", true, k) { Font = f.Font, Left = (int)(180 * k), Top = (int)(98 * k) };
        var cancel = new FlatButton("Cancel", false, k) { Font = f.Font, Left = (int)(272 * k), Top = (int)(98 * k) };
        ok.Click += (s, e) => { f.DialogResult = DialogResult.OK; };
        cancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; };
        box.T.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { f.DialogResult = DialogResult.OK; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { f.DialogResult = DialogResult.Cancel; e.SuppressKeyPress = true; }
        };
        f.Controls.AddRange(new Control[] { lab, box, ok, cancel });
        f.HandleCreated += (s, e) => l.Window(f.Handle);
        f.Shown += (s, e) => box.T.Focus();
        return f.ShowDialog(owner) == DialogResult.OK && box.T.Text.Trim().Length > 0 ? box.T.Text.Trim() : null;
    }
}
