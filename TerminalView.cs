// Desenha o Terminal sobre o fundo transparente da janela e converte teclado/rato em VT.
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

sealed class TerminalView : FrameworkElement
{
    const string CSI = "\u001b[", SS3 = "\u001bO";

    public Action<string> Send = delegate { };
    // Agentes como o Grok pintam todas as células com uma cor de fundo própria (quase preta), que
    // tapava o fundo da janela. Com isto, a cor que cobre a maior parte do ecrã fica transparente.
    public bool AutoClearBg = true;
    int clearBg; // cor (no formato de Cell.Bg) tratada como fundo por omissão; 0 = nenhuma
    public event Action<int, int> GridChanged;
    public int GridCols = 80, GridRows = 24;

    Terminal term;
    double emSize = 16, ppd = 1, cellW = 9, cellH = 19, baseline = 15, lineSpacing = 1.17;
    int cellWpx = 9, cellHpx = 19;
    GlyphTypeface face, faceBold, faceItalic, faceBoldItalic;
    readonly List<GlyphTypeface> fallbacks = new List<GlyphTypeface>();
    readonly Dictionary<int, GlyphTypeface> fallbackCache = new Dictionary<int, GlyphTypeface>();
    readonly Dictionary<int, Brush> brushes = new Dictionary<int, Brush>();

    readonly VisualCollection visuals;
    readonly DrawingVisual overlay = new DrawingVisual();
    DrawingVisual[] rows = new DrawingVisual[0];
    Line[] drawnLine = new Line[0];
    int[] drawnVersion = new int[0];
    bool needRender = true, focused;

    int scrollOffset;
    bool selecting, hasSelection;
    long selRow0, selRow1;
    int selCol0, selCol1;

    // corrida de glifos em construção
    GlyphTypeface runFace;
    int runColor;
    double runX;
    readonly List<ushort> runGlyphs = new List<ushort>();
    readonly List<double> runAdv = new List<double>();

    public TerminalView()
    {
        visuals = new VisualCollection(this);
        visuals.Add(overlay);
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        foreach (string name in new[] { "Segoe UI Symbol", "Segoe UI Emoji", "Cambria Math", "Segoe UI", "Yu Gothic UI", "Malgun Gothic", "Microsoft YaHei UI", "Nirmala UI" })
        {
            GlyphTypeface g = LoadFace(name, FontWeights.Normal, FontStyles.Normal);
            if (g != null) fallbacks.Add(g);
        }
        SetFont("Consolas", 12);
        Loaded += delegate { UpdateMetrics(); };
    }

    public Terminal Term
    {
        get { return term; }
        set { term = value; scrollOffset = 0; hasSelection = false; selecting = false; Invalidate(); }
    }

    public void SetFont(string families, double sizePt)
    {
        GlyphTypeface g = null;
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamily ff in Fonts.SystemFontFamilies) installed.Add(ff.Source);
        string chosen = "Consolas";
        foreach (string raw in (families ?? "").Split(','))
        {
            string name = raw.Trim();
            if (name.Length > 0 && installed.Contains(name) && (g = LoadFace(name, FontWeights.Normal, FontStyles.Normal)) != null) { chosen = name; break; }
        }
        face = g ?? LoadFace("Consolas", FontWeights.Normal, FontStyles.Normal);
        faceBold = LoadFace(chosen, FontWeights.Bold, FontStyles.Normal) ?? face;
        faceItalic = LoadFace(chosen, FontWeights.Normal, FontStyles.Italic) ?? face;
        faceBoldItalic = LoadFace(chosen, FontWeights.Bold, FontStyles.Italic) ?? faceBold;
        lineSpacing = Math.Max(new FontFamily(chosen).LineSpacing, face.Height);
        emSize = Math.Max(6, sizePt) * 96.0 / 72.0;
        fallbackCache.Clear();
        UpdateMetrics();
    }

    static GlyphTypeface LoadFace(string family, FontWeight weight, FontStyle style)
    {
        GlyphTypeface g;
        var tf = new Typeface(new FontFamily(family), style, weight, FontStretches.Normal);
        return tf.TryGetGlyphTypeface(out g) ? g : null;
    }

    // A grelha vive em píxeis inteiros: o texto fica nítido e as caixas ligam sem falhas.
    void UpdateMetrics()
    {
        ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        ushort gi;
        double adv = face.CharacterToGlyphMap.TryGetValue('M', out gi) ? face.AdvanceWidths[gi] : 0.55;
        cellWpx = Math.Max(1, (int)Math.Round(adv * emSize * ppd));
        cellHpx = Math.Max(1, (int)Math.Ceiling(lineSpacing * emSize * ppd));
        cellW = cellWpx / ppd;
        cellH = cellHpx / ppd;
        double ascent = face.Baseline * emSize, textH = face.Height * emSize;
        baseline = Math.Round((ascent + (cellH - textH) / 2) * ppd) / ppd;
        if (term != null) lock (term.Sync) { term.CellPxW = cellWpx; term.CellPxH = cellHpx; }
        UpdateGrid();
        Invalidate();
    }

    public void Invalidate()
    {
        needRender = true;
        for (int i = 0; i < drawnLine.Length; i++) drawnLine[i] = null;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMetrics();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdateGrid();
    }

    void UpdateGrid()
    {
        int cols = Math.Max(2, (int)Math.Floor(ActualWidth / cellW));
        int rowsN = Math.Max(1, (int)Math.Floor(ActualHeight / cellH));
        if (ActualWidth <= 0 || (cols == GridCols && rowsN == GridRows)) return;
        GridCols = cols; GridRows = rowsN;
        var h = GridChanged;
        if (h != null) h(cols, rowsN);
        Invalidate();
    }

    protected override int VisualChildrenCount { get { return visuals.Count; } }
    protected override Visual GetVisualChild(int index) { return visuals[index]; }
    protected override HitTestResult HitTestCore(PointHitTestParameters p) { return new PointHitTestResult(this, p.HitPoint); }

    // ---- Desenho ----------------------------------------------------------------------

    // Chamado a cada frame enquanto a janela está visível; só redesenha o que mudou.
    public void Tick()
    {
        Terminal t = term;
        if (t == null) return;
        lock (t.Sync)
        {
            if (t.SyncOutput && (DateTime.UtcNow - t.SyncSince).TotalMilliseconds < 250) return;
            if (!t.Dirty && !needRender) return;
            if (t.AltScreen) scrollOffset = 0;
            else if (scrollOffset > 0) scrollOffset += t.ScrollbackAdded; // a vista fica parada no histórico
            t.ScrollbackAdded = 0;
            scrollOffset = Math.Min(scrollOffset, t.Scrollback.Count);
            EnsureRows(t.Rows);
            int dominant = AutoClearBg ? DominantBg(t) : 0;
            if (dominant != clearBg) { clearBg = dominant; for (int i = 0; i < drawnLine.Length; i++) drawnLine[i] = null; }
            for (int r = 0; r < t.Rows; r++)
            {
                Line line = t.ViewLine(r, scrollOffset);
                if (line != null && line == drawnLine[r] && line.Version == drawnVersion[r]) continue;
                drawnLine[r] = line;
                drawnVersion[r] = line == null ? 0 : line.Version;
                RenderRow(r, line, t.Cols);
            }
            RenderOverlay(t);
            t.Dirty = false;
            needRender = false;
        }
    }

    // A cor de fundo explícita que cobre pelo menos metade do ecrã (0 se não houver).
    static int DominantBg(Terminal t)
    {
        var counts = new Dictionary<int, int>();
        int total = 0, best = 0, bestCount = 0;
        for (int r = 0; r < t.Rows; r++)
        {
            Cell[] cells = t.Screen[r].Cells;
            for (int x = 0; x < cells.Length; x += 2) // amostra: uma célula em cada duas chega
            {
                total++;
                int bg = cells[x].Bg;
                if (bg == 0) continue;
                int n;
                counts.TryGetValue(bg, out n);
                counts[bg] = ++n;
                if (n > bestCount) { bestCount = n; best = bg; }
            }
        }
        return bestCount * 2 >= total && total > 0 ? best : 0;
    }

    void EnsureRows(int n)
    {
        if (rows.Length == n) return;
        visuals.Clear();
        var old = rows;
        rows = new DrawingVisual[n];
        for (int i = 0; i < n; i++)
        {
            rows[i] = i < old.Length ? old[i] : new DrawingVisual();
            visuals.Add(rows[i]);
        }
        visuals.Add(overlay);
        drawnLine = new Line[n];
        drawnVersion = new int[n];
    }

    void RenderRow(int row, Line line, int cols)
    {
        DrawingVisual v = rows[row];
        v.Offset = new Vector(0, row * cellH);
        using (DrawingContext dc = v.RenderOpen())
        {
            if (line == null) return;
            Cell[] cells = line.Cells;
            int n = Math.Min(cells.Length, cols);

            // Fundos (o fundo por omissão fica transparente para se ver o acrylic).
            for (int x = 0; x < n; )
            {
                int f, b;
                Colors(ref cells[x], out f, out b);
                int start = x;
                for (x++; x < n; x++)
                {
                    int f2, b2;
                    Colors(ref cells[x], out f2, out b2);
                    if (b2 != b) break;
                }
                if (b >= 0) dc.DrawRectangle(BrushFor(b, 255), null, new Rect(start * cellW, 0, (x - start) * cellW, cellH));
            }

            // Texto, caixas e sublinhados.
            for (int x = 0; x < n; x++)
            {
                Cell c = cells[x];
                if ((c.Attr & CellAttr.WideTail) != 0) continue;
                int f, b;
                Colors(ref c, out f, out b);
                int alpha = (c.Attr & CellAttr.Dim) != 0 ? 150 : 255;
                int color = (alpha << 24) | f;
                double cx = x * cellW, w = (c.Attr & CellAttr.Wide) != 0 ? cellW * 2 : cellW;
                if ((c.Attr & (CellAttr.Underline | CellAttr.Strike)) != 0)
                {
                    double th = Math.Max(1, Math.Round(emSize * 0.06 * ppd)) / ppd;
                    if ((c.Attr & CellAttr.Underline) != 0) dc.DrawRectangle(BrushFor(f, alpha), null, new Rect(cx, baseline + th, w, th));
                    if ((c.Attr & CellAttr.Strike) != 0) dc.DrawRectangle(BrushFor(f, alpha), null, new Rect(cx, Math.Round(cellHpx * 0.52) / ppd, w, th));
                }
                string s = c.Ch;
                if (s == null || s == " " || (c.Attr & CellAttr.Hidden) != 0) { ContinueWithSpace(dc, cx); continue; }
                int cp = char.ConvertToUtf32(s, 0);
                if (cp >= 0x2500 && cp <= 0x259F && DrawBox(dc, cp, cx, BrushFor(f, alpha))) { Flush(dc); continue; }
                GlyphTypeface gf = PickFace(c.Attr);
                for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
                {
                    int code = char.ConvertToUtf32(s, i);
                    ushort glyph;
                    GlyphTypeface g = FaceFor(gf, code, out glyph);
                    if (g != runFace || color != runColor || runGlyphs.Count == 0 && i > 0) { Flush(dc); runFace = g; runColor = color; runX = cx; }
                    else if (runGlyphs.Count == 0) runX = cx;
                    runGlyphs.Add(glyph);
                    runAdv.Add(i == 0 ? w : 0); // marcas combinantes ficam por cima do caractere base
                }
            }
            Flush(dc);
        }
    }

    // Espaços entram na corrida atual (menos corridas); sem corrida não há nada a desenhar.
    void ContinueWithSpace(DrawingContext dc, double cx)
    {
        if (runGlyphs.Count == 0) return;
        ushort g;
        if (runFace.CharacterToGlyphMap.TryGetValue(' ', out g)) { runGlyphs.Add(g); runAdv.Add(cellW); }
        else Flush(dc);
    }

    void Flush(DrawingContext dc)
    {
        if (runGlyphs.Count == 0) return;
        var run = new GlyphRun(runFace, 0, false, emSize, (float)ppd, runGlyphs.ToArray(), new Point(runX, baseline),
            runAdv.ToArray(), null, null, null, null, null, null);
        dc.DrawGlyphRun(BrushFor(runColor & 0xFFFFFF, (runColor >> 24) & 0xFF), run);
        runGlyphs.Clear();
        runAdv.Clear();
    }

    GlyphTypeface PickFace(CellAttr a)
    {
        bool bold = (a & CellAttr.Bold) != 0, italic = (a & CellAttr.Italic) != 0;
        return bold ? (italic ? faceBoldItalic : faceBold) : (italic ? faceItalic : face);
    }

    GlyphTypeface FaceFor(GlyphTypeface preferred, int cp, out ushort glyph)
    {
        if (preferred.CharacterToGlyphMap.TryGetValue(cp, out glyph)) return preferred;
        GlyphTypeface g;
        if (!fallbackCache.TryGetValue(cp, out g))
        {
            g = null;
            foreach (GlyphTypeface f in fallbacks)
                if (f.CharacterToGlyphMap.ContainsKey(cp)) { g = f; break; }
            fallbackCache[cp] = g;
        }
        if (g != null && g.CharacterToGlyphMap.TryGetValue(cp, out glyph)) return g;
        face.CharacterToGlyphMap.TryGetValue('?', out glyph);
        return face;
    }

    // Cores finais de uma célula (RGB); fundo -1 = transparente.
    void Colors(ref Cell c, out int fg, out int bg)
    {
        int f = c.Fg;
        if ((c.Attr & CellAttr.Bold) != 0 && f >= 1 && f <= 8) f += 8; // negrito com cor básica = versão clara
        fg = Terminal.ToRgb(f, true);
        bg = c.Bg == 0 || c.Bg == clearBg ? -1 : Terminal.ToRgb(c.Bg, false);
        if ((c.Attr & CellAttr.Inverse) != 0)
        {
            int t = fg;
            fg = bg < 0 ? Terminal.DefaultBgRgb : bg;
            bg = t;
        }
    }

    Brush BrushFor(int rgb, int alpha)
    {
        int key = (alpha << 24) | (rgb & 0xFFFFFF);
        Brush b;
        if (!brushes.TryGetValue(key, out b))
        {
            b = new SolidColorBrush(Color.FromArgb((byte)alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            b.Freeze();
            brushes[key] = b;
        }
        return b;
    }

    // Caixas e blocos (U+2500–U+259F) desenhados como geometria, alinhados à grelha.
    bool DrawBox(DrawingContext dc, int cp, double x, Brush brush)
    {
        double px = 1 / ppd, W = cellW, H = cellH;
        int lightPx = Math.Max(1, (int)Math.Round(cellWpx / 9.0)), heavyPx = lightPx * 2;
        if (cp >= 0x2580)
        {
            int hh = (int)Math.Round(cellHpx / 2.0), hw = (int)Math.Round(cellWpx / 2.0);
            switch (cp)
            {
                case 0x2580: Fill(dc, brush, x, 0, W, hh * px); return true;
                case 0x2588: Fill(dc, brush, x, 0, W, H); return true;
                case 0x2590: Fill(dc, brush, x + hw * px, 0, W - hw * px, H); return true;
                case 0x2594: Fill(dc, brush, x, 0, W, Math.Max(1, Math.Round(cellHpx / 8.0)) * px); return true;
                case 0x2595: double ew = Math.Max(1, Math.Round(cellWpx / 8.0)) * px; Fill(dc, brush, x + W - ew, 0, ew, H); return true;
                case 0x2591: case 0x2592: case 0x2593:
                    dc.PushOpacity((cp - 0x2590) * 0.25);
                    Fill(dc, brush, x, 0, W, H);
                    dc.Pop();
                    return true;
            }
            if (cp >= 0x2581 && cp <= 0x2587) { double h = Math.Round(cellHpx * (cp - 0x2580) / 8.0) * px; Fill(dc, brush, x, H - h, W, h); return true; }
            if (cp >= 0x2589 && cp <= 0x258F) { double w = Math.Round(cellWpx * (0x2590 - cp) / 8.0) * px; Fill(dc, brush, x, 0, w, H); return true; }
            if (cp >= 0x2596)
            {
                int q = Quadrants[cp - 0x2596];
                double top = hh * px, left = hw * px;
                if ((q & 1) != 0) Fill(dc, brush, x, 0, left, top);
                if ((q & 2) != 0) Fill(dc, brush, x + left, 0, W - left, top);
                if ((q & 4) != 0) Fill(dc, brush, x, top, left, H - top);
                if ((q & 8) != 0) Fill(dc, brush, x + left, top, W - left, H - top);
                return true;
            }
            return false;
        }

        // Linhas: braços (cima, direita, baixo, esquerda) com peso 1 = fina, 2 = grossa.
        int arms;
        if (!Arms.TryGetValue(cp, out arms)) return false;
        int up = arms >> 6 & 3, right = arms >> 4 & 3, down = arms >> 2 & 3, left2 = arms & 3;
        // Centro da linha em píxeis inteiros, para ficar nítida.
        Func<int, int, double> start = (cellPx, t) => ((cellPx - t) / 2) * px;
        bool rounded = cp >= 0x256D && cp <= 0x2570;
        if (rounded)
        {
            double t = lightPx * px;
            double cx = x + start(cellWpx, lightPx) + t / 2, cy = start(cellHpx, lightPx) + t / 2;
            double r = Math.Min(W, H) / 2;
            var geo = new StreamGeometry();
            using (StreamGeometryContext g = geo.Open())
            {
                switch (cp)
                {
                    case 0x256D: g.BeginFigure(new Point(x + W, cy), false, false); g.LineTo(new Point(cx + r, cy), true, false);
                        g.ArcTo(new Point(cx, cy + r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false); g.LineTo(new Point(cx, H), true, false); break;
                    case 0x256E: g.BeginFigure(new Point(x, cy), false, false); g.LineTo(new Point(cx - r, cy), true, false);
                        g.ArcTo(new Point(cx, cy + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false); g.LineTo(new Point(cx, H), true, false); break;
                    case 0x256F: g.BeginFigure(new Point(cx, 0), false, false); g.LineTo(new Point(cx, cy - r), true, false);
                        g.ArcTo(new Point(cx - r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false); g.LineTo(new Point(x, cy), true, false); break;
                    default: g.BeginFigure(new Point(cx, 0), false, false); g.LineTo(new Point(cx, cy - r), true, false);
                        g.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false); g.LineTo(new Point(x + W, cy), true, false); break;
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(brush, t), geo);
            return true;
        }
        int dash = DashCount(cp);
        for (int side = 0; side < 4; side++)
        {
            int weight = side == 0 ? up : side == 1 ? right : side == 2 ? down : left2;
            if (weight == 0) continue;
            int tPx = weight == 2 ? heavyPx : lightPx;
            double t = tPx * px;
            // o braço vai do bordo da célula até ao outro lado da linha perpendicular mais grossa
            int crossPx = Math.Max(side == 0 || side == 2 ? Math.Max(left2, right) : Math.Max(up, down), 1) == 2 ? heavyPx : lightPx;
            if (side == 0 || side == 2)
            {
                double lx = x + start(cellWpx, tPx), mid0 = start(cellHpx, crossPx), mid1 = mid0 + crossPx * px;
                if (side == 0) FillDashed(dc, brush, lx, 0, t, mid1, dash, true);
                else FillDashed(dc, brush, lx, mid0, t, H - mid0, dash, true);
            }
            else
            {
                double ly = start(cellHpx, tPx), mid0 = x + start(cellWpx, crossPx), mid1 = mid0 + crossPx * px;
                if (side == 3) FillDashed(dc, brush, x, ly, mid1 - x, t, dash, false);
                else FillDashed(dc, brush, mid0, ly, x + W - mid0, t, dash, false);
            }
        }
        return true;
    }

    static int DashCount(int cp)
    {
        if (cp >= 0x2504 && cp <= 0x2507) return 3;
        if (cp >= 0x2508 && cp <= 0x250B) return 4;
        if (cp >= 0x254C && cp <= 0x254F) return 2;
        return 0;
    }

    void FillDashed(DrawingContext dc, Brush b, double x, double y, double w, double h, int dashes, bool vertical)
    {
        if (dashes == 0) { Fill(dc, b, x, y, w, h); return; }
        double len = vertical ? h : w, seg = len / dashes, gap = seg * 0.35;
        for (int i = 0; i < dashes; i++)
        {
            double s0 = i * seg + gap / 2;
            if (vertical) Fill(dc, b, x, y + s0, w, seg - gap);
            else Fill(dc, b, x + s0, y, seg - gap, h);
        }
    }

    static void Fill(DrawingContext dc, Brush b, double x, double y, double w, double h)
    {
        if (w > 0 && h > 0) dc.DrawRectangle(b, null, new Rect(x, y, w, h));
    }

    static readonly int[] Quadrants = { 4, 8, 1, 1 | 4 | 8, 1 | 8, 1 | 2 | 4, 1 | 2 | 8, 2, 2 | 4, 2 | 4 | 8 };

    // cima<<6 | direita<<4 | baixo<<2 | esquerda; 1 = fina, 2 = grossa
    static readonly Dictionary<int, int> Arms = BuildArms();

    static Dictionary<int, int> BuildArms()
    {
        var d = new Dictionary<int, int>();
        Action<int, int, int, int, int> a = (cp, u, r, dn, l) => d[cp] = u << 6 | r << 4 | dn << 2 | l;
        a(0x2500, 0, 1, 0, 1); a(0x2501, 0, 2, 0, 2); a(0x2502, 1, 0, 1, 0); a(0x2503, 2, 0, 2, 0);
        a(0x2504, 0, 1, 0, 1); a(0x2505, 0, 2, 0, 2); a(0x2506, 1, 0, 1, 0); a(0x2507, 2, 0, 2, 0);
        a(0x2508, 0, 1, 0, 1); a(0x2509, 0, 2, 0, 2); a(0x250A, 1, 0, 1, 0); a(0x250B, 2, 0, 2, 0);
        a(0x250C, 0, 1, 1, 0); a(0x250F, 0, 2, 2, 0); a(0x2510, 0, 0, 1, 1); a(0x2513, 0, 0, 2, 2);
        a(0x2514, 1, 1, 0, 0); a(0x2517, 2, 2, 0, 0); a(0x2518, 1, 0, 0, 1); a(0x251B, 2, 0, 0, 2);
        a(0x251C, 1, 1, 1, 0); a(0x2523, 2, 2, 2, 0); a(0x2524, 1, 0, 1, 1); a(0x252B, 2, 0, 2, 2);
        a(0x252C, 0, 1, 1, 1); a(0x2533, 0, 2, 2, 2); a(0x2534, 1, 1, 0, 1); a(0x253B, 2, 2, 0, 2);
        a(0x253C, 1, 1, 1, 1); a(0x254B, 2, 2, 2, 2);
        a(0x254C, 0, 1, 0, 1); a(0x254D, 0, 2, 0, 2); a(0x254E, 1, 0, 1, 0); a(0x254F, 2, 0, 2, 0);
        a(0x256D, 0, 1, 1, 0); a(0x256E, 0, 0, 1, 1); a(0x256F, 1, 0, 0, 1); a(0x2570, 1, 1, 0, 0);
        a(0x2574, 0, 0, 0, 1); a(0x2575, 1, 0, 0, 0); a(0x2576, 0, 1, 0, 0); a(0x2577, 0, 0, 1, 0);
        a(0x2578, 0, 0, 0, 2); a(0x2579, 2, 0, 0, 0); a(0x257A, 0, 2, 0, 0); a(0x257B, 0, 0, 2, 0);
        return d;
    }

    void RenderOverlay(Terminal t)
    {
        using (DrawingContext dc = overlay.RenderOpen())
        {
            if (hasSelection)
            {
                long r0 = selRow0, r1 = selRow1; int c0 = selCol0, c1 = selCol1;
                if (r1 < r0 || r1 == r0 && c1 < c0) { r0 = selRow1; r1 = selRow0; c0 = selCol1; c1 = selCol0; }
                Brush sel = BrushFor(0x3B8EEA, 110);
                for (int r = 0; r < t.Rows; r++)
                {
                    long abs = t.AbsRow(r, scrollOffset);
                    if (abs < r0 || abs > r1) continue;
                    int from = abs == r0 ? c0 : 0, to = abs == r1 ? c1 : t.Cols - 1;
                    if (to >= from) dc.DrawRectangle(sel, null, new Rect(from * cellW, r * cellH, (to - from + 1) * cellW, cellH));
                }
            }
            if (t.CursorVisible && scrollOffset == 0)
            {
                Brush b = BrushFor(Terminal.DefaultFgRgb, focused ? 230 : 140);
                double x = t.CursorX * cellW, y = t.CursorY * cellH, px = 1 / ppd;
                int shape = t.CursorShape;
                if (!focused) dc.DrawRectangle(null, new Pen(b, px), new Rect(x + px / 2, y + px / 2, cellW - px, cellH - px));
                else if (shape == 1 || shape == 2) dc.DrawRectangle(BrushFor(Terminal.DefaultFgRgb, 150), null, new Rect(x, y, cellW, cellH));
                else if (shape == 3 || shape == 4) dc.DrawRectangle(b, null, new Rect(x, y + cellH - 2 * px, cellW, 2 * px));
                else dc.DrawRectangle(b, null, new Rect(x, y, Math.Max(2, Math.Round(ppd * 1.5)) * px, cellH));
            }
        }
    }

    // ---- Teclado ----------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (term == null) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed || key == Key.DeadCharProcessed) return;
        ModifierKeys m = Keyboard.Modifiers;
        bool ctrl = (m & ModifierKeys.Control) != 0, alt = (m & ModifierKeys.Alt) != 0, shift = (m & ModifierKeys.Shift) != 0;
        if (ctrl && alt && Keyboard.IsKeyDown(Key.RightAlt)) return; // AltGr: o texto vem no TextInput
        bool app;
        lock (term.Sync) app = term.AppCursorKeys;
        string s = Translate(key, ctrl, alt, shift, app);
        if (s == null) return;
        e.Handled = true;
        if (s.Length == 0) return; // tratado aqui (copiar, colar, histórico)
        BackToLive();
        Send(s);
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (term == null || string.IsNullOrEmpty(e.Text)) return;
        var sb = new StringBuilder();
        foreach (char ch in e.Text) if (ch >= ' ' && ch != '\u007f') sb.Append(ch);
        if (sb.Length == 0) return;
        e.Handled = true;
        BackToLive();
        Send(sb.ToString());
    }

    string Translate(Key key, bool ctrl, bool alt, bool shift, bool app)
    {
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        switch (key)
        {
            case Key.Enter: return shift || ctrl || alt ? "\u001b\r" : "\r"; // Shift+Enter = nova linha no Claude Code
            case Key.Back: return alt ? "\u001b\u007f" : ctrl ? "\b" : "\u007f";
            case Key.Tab: return shift ? CSI + "Z" : "\t";
            case Key.Escape: return "\u001b";
            case Key.Up: return Arrow('A', mod, app);
            case Key.Down: return Arrow('B', mod, app);
            case Key.Right: return Arrow('C', mod, app);
            case Key.Left: return Arrow('D', mod, app);
            case Key.Home: return Arrow('H', mod, app);
            case Key.End: return Arrow('F', mod, app);
            case Key.Insert: if (shift) { Paste(); return ""; } return Tilde(2, mod);
            case Key.Delete: return Tilde(3, mod);
            case Key.PageUp: if (shift) { ScrollBy(GridRows - 1); return ""; } return Tilde(5, mod);
            case Key.PageDown: if (shift) { ScrollBy(-(GridRows - 1)); return ""; } return Tilde(6, mod);
            case Key.Space: return ctrl ? "\0" : alt ? "\u001b " : null;
        }
        if (key >= Key.F1 && key <= Key.F12)
        {
            int i = key - Key.F1;
            if (i < 4) return mod > 1 ? CSI + "1;" + mod + "PQRS"[i] : SS3 + "PQRS"[i];
            int[] codes = { 15, 17, 18, 19, 20, 21, 23, 24 };
            return Tilde(codes[i - 4], mod);
        }
        if (ctrl && !alt)
        {
            if (key == Key.C && (shift || hasSelection)) { CopySelection(); return ""; }
            if (key == Key.V) { if (shift || ClipboardHasText()) { Paste(); return ""; } return "\u0016"; }
            if (key >= Key.A && key <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
            switch (key)
            {
                case Key.OemOpenBrackets: return "\u001b";
                case Key.OemCloseBrackets: return "\u001d";
                case Key.D2: return "\0";
                case Key.D6: return "\u001e";
                case Key.OemMinus: return "\u001f";
            }
            return null;
        }
        if (alt && !ctrl)
        {
            if (key >= Key.A && key <= Key.Z) { char ch = (char)('a' + (key - Key.A)); return "\u001b" + (shift ? char.ToUpperInvariant(ch) : ch); }
            if (key >= Key.D0 && key <= Key.D9) return "\u001b" + (char)('0' + (key - Key.D0));
        }
        return null;
    }

    static string Arrow(char f, int mod, bool app)
    {
        if (mod > 1) return CSI + "1;" + mod + f;
        return (app ? SS3 : CSI) + f;
    }

    static string Tilde(int n, int mod) { return CSI + n + (mod > 1 ? ";" + mod : "") + "~"; }

    void BackToLive()
    {
        if (scrollOffset != 0 || hasSelection) { scrollOffset = 0; hasSelection = false; needRender = true; }
    }

    void ScrollBy(int lines)
    {
        Terminal t = term;
        if (t == null) return;
        lock (t.Sync) scrollOffset = Math.Max(0, Math.Min(t.AltScreen ? 0 : t.Scrollback.Count, scrollOffset + lines));
        needRender = true;
    }

    static bool ClipboardHasText()
    {
        try { return Clipboard.ContainsText(); } catch (Exception) { return false; }
    }

    void Paste()
    {
        string text = null;
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch (Exception) { }
        if (string.IsNullOrEmpty(text) || term == null) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        bool bracketed;
        lock (term.Sync) bracketed = term.BracketedPaste;
        if (bracketed) text = CSI + "200~" + text.Replace("\u001b", "") + CSI + "201~";
        BackToLive();
        Send(text);
    }

    void CopySelection()
    {
        Terminal t = term;
        if (t == null || !hasSelection) return;
        long r0 = selRow0, r1 = selRow1; int c0 = selCol0, c1 = selCol1;
        if (r1 < r0 || r1 == r0 && c1 < c0) { r0 = selRow1; r1 = selRow0; c0 = selCol1; c1 = selCol0; }
        var sb = new StringBuilder();
        lock (t.Sync)
        {
            for (long r = r0; r <= r1; r++)
            {
                Line line = t.AbsLine(r);
                if (line == null) continue;
                var ls = new StringBuilder();
                int to = r == r1 ? Math.Min(c1, line.Cells.Length - 1) : line.Cells.Length - 1;
                for (int x = r == r0 ? c0 : 0; x <= to; x++)
                {
                    Cell c = line.Cells[x];
                    if ((c.Attr & CellAttr.WideTail) == 0) ls.Append(c.Ch ?? " ");
                }
                bool cont = line.Wrapped && r != r1;
                sb.Append(cont ? ls.ToString() : ls.ToString().TrimEnd());
                if (r != r1 && !cont) sb.Append("\r\n");
            }
        }
        try { Clipboard.SetText(sb.ToString()); } catch (Exception) { }
        hasSelection = false;
        needRender = true;
    }

    // ---- Rato -------------------------------------------------------------------------

    int ColAt(double x) { return Math.Max(0, Math.Min(GridCols - 1, (int)(x / cellW))); }
    int RowAt(double y) { return Math.Max(0, Math.Min(GridRows - 1, (int)(y / cellH))); }

    bool Reporting()
    {
        if (term == null || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return false;
        lock (term.Sync) return term.MouseMode != 0;
    }

    void SendMouse(int button, Point p, bool press, bool motion)
    {
        int cb = button + (motion ? 32 : 0);
        ModifierKeys m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Shift) != 0) cb += 4;
        if ((m & ModifierKeys.Alt) != 0) cb += 8;
        if ((m & ModifierKeys.Control) != 0) cb += 16;
        int col = ColAt(p.X) + 1, row = RowAt(p.Y) + 1;
        bool sgr;
        lock (term.Sync) sgr = term.MouseSgr;
        if (sgr) Send(CSI + "<" + cb + ";" + col + ";" + row + (press ? "M" : "m"));
        else
        {
            if (!press) cb = (cb & ~3) | 3;
            Send(CSI + "M" + (char)(32 + cb) + (char)(32 + Math.Min(col, 223)) + (char)(32 + Math.Min(row, 223)));
        }
    }

    static int ButtonCode(MouseButton b) { return b == MouseButton.Left ? 0 : b == MouseButton.Middle ? 1 : 2; }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Keyboard.Focus(this);
        if (term == null) return;
        Point p = e.GetPosition(this);
        e.Handled = true;
        if (Reporting()) { SendMouse(ButtonCode(e.ChangedButton), p, true, false); CaptureMouse(); return; }
        if (e.ChangedButton == MouseButton.Right)
        {
            if (hasSelection) CopySelection(); else Paste();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        int col = ColAt(p.X);
        long abs;
        lock (term.Sync)
        {
            abs = term.AbsRow(RowAt(p.Y), scrollOffset);
            selRow0 = selRow1 = abs;
            selCol0 = selCol1 = col;
            if (e.ClickCount == 2) SelectWord(term.AbsLine(abs), col);
            else if (e.ClickCount >= 3) { selCol0 = 0; selCol1 = term.Cols - 1; }
        }
        hasSelection = e.ClickCount >= 2;
        selecting = e.ClickCount == 1;
        if (selecting) CaptureMouse();
        needRender = true;
    }

    void SelectWord(Line line, int col)
    {
        if (line == null || col >= line.Cells.Length) return;
        Func<int, bool> word = i => { string s = line.Cells[i].Ch; return s != null && s != " " && "()[]{}<>\"'`|,;".IndexOf(s[0]) < 0; };
        if (!word(col)) return;
        int a = col, b = col;
        while (a > 0 && word(a - 1)) a--;
        while (b < line.Cells.Length - 1 && word(b + 1)) b++;
        selCol0 = a; selCol1 = b;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (term == null) return;
        Point p = e.GetPosition(this);
        if (selecting)
        {
            if (p.Y < 0) ScrollBy(1); else if (p.Y > ActualHeight) ScrollBy(-1);
            lock (term.Sync) { selRow1 = term.AbsRow(RowAt(p.Y), scrollOffset); selCol1 = ColAt(p.X); }
            hasSelection = selRow1 != selRow0 || selCol1 != selCol0;
            needRender = true;
            return;
        }
        int mode;
        lock (term.Sync) mode = term.MouseMode;
        bool pressed = e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed;
        if (Reporting() && (mode == 1003 || mode == 1002 && pressed))
        {
            int b = e.LeftButton == MouseButtonState.Pressed ? 0 : e.MiddleButton == MouseButtonState.Pressed ? 1 : e.RightButton == MouseButtonState.Pressed ? 2 : 3;
            SendMouse(b, p, true, true);
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (selecting) { selecting = false; ReleaseMouseCapture(); }
        else if (term != null && Reporting()) { SendMouse(ButtonCode(e.ChangedButton), e.GetPosition(this), false, false); ReleaseMouseCapture(); }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (term == null) return;
        int notches = e.Delta / 120;
        if (notches == 0) notches = Math.Sign(e.Delta);
        e.Handled = true;
        if (Reporting())
        {
            for (int i = 0; i < Math.Abs(notches); i++) SendMouse(notches > 0 ? 64 : 65, e.GetPosition(this), true, false);
            return;
        }
        bool altScreen, app;
        lock (term.Sync) { altScreen = term.AltScreen; app = term.AppCursorKeys; }
        if (altScreen) // ecrã alternativo sem rato: a roda vira setas
        {
            for (int i = 0; i < Math.Abs(notches) * 3; i++) Send(Arrow(notches > 0 ? 'A' : 'B', 1, app));
            return;
        }
        ScrollBy(notches * 3);
    }

    // ---- Foco -------------------------------------------------------------------------

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SetFocused(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        SetFocused(false);
    }

    void SetFocused(bool on)
    {
        focused = on;
        needRender = true;
        if (term == null) return;
        bool report;
        lock (term.Sync) report = term.FocusEvents;
        if (report) Send(CSI + (on ? "I" : "O"));
    }
}
