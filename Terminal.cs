// Modelo do ecrã e parser VT: recebe a saída do ConPTY e mantém a grelha de células.
// O estado só se lê/escreve com lock(Sync): o parser corre na thread de leitura, o desenho na UI.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

[Flags]
enum CellAttr : ushort
{
    None = 0, Bold = 1, Dim = 2, Italic = 4, Underline = 8, Blink = 16, Inverse = 32, Hidden = 64, Strike = 128,
    Wide = 256,     // primeira metade de um caractere largo
    WideTail = 512, // segunda metade: não se desenha
}

// Cores: 0 = por omissão, 1..256 = índice da paleta + 1, 0x1000000|rgb = truecolor.
// Assim default(Cell) é um espaço com as cores por omissão.
struct Cell
{
    public string Ch; // null = espaço
    public int Fg, Bg;
    public CellAttr Attr;
}

sealed class Line
{
    public Cell[] Cells;
    public bool Wrapped; // continua na linha seguinte (quebra automática)
    public int Version;  // muda a cada alteração, para a vista saber o que redesenhar

    public Line(int cols, int bg)
    {
        Cells = new Cell[cols];
        if (bg != 0) for (int i = 0; i < cols; i++) Cells[i].Bg = bg;
    }
}

struct SavedCursor
{
    public int X, Y, Fg, Bg;
    public CellAttr Attr;
    public bool Origin, Wrap, LineDrawing;
}

sealed class Terminal
{
    const string ESC = "\u001b", CSI = "\u001b[", ST = "\u001b\\";
    public const int MaxScrollback = 3000;
    public const int DefaultFgRgb = 0xE6E6E6, DefaultBgRgb = 0x202020;
    public static readonly int[] Palette = BuildPalette();

    public readonly object Sync = new object();
    public event Action<string> Reply; // respostas a consultas (DA, DSR...), a escrever no agente

    public int Cols, Rows;
    public Line[] Screen;
    public readonly List<Line> Scrollback = new List<Line>();
    public long Trimmed;        // linhas já descartadas do início do histórico
    public int ScrollbackAdded; // linhas que entraram no histórico desde a última leitura da vista
    public int CursorX, CursorY, MouseMode, CursorShape;
    public bool CursorVisible = true, AppCursorKeys, BracketedPaste, FocusEvents, MouseSgr, SyncOutput;
    public DateTime SyncSince;
    public bool Dirty = true;
    public int CellPxW = 9, CellPxH = 19; // atualizados pela vista, para responder a CSI 14/16 t

    Line[] main, alt;
    bool wrapPending, originMode, autoWrap = true, insertMode, lineDrawing;
    int fg, bg, top, bottom, lastChar = ' ';
    CellAttr attr;
    bool[] tabs;
    SavedCursor saved, altSaved;

    enum State { Ground, Esc, EscInter, Csi, Osc, OscEsc, Str, StrEsc }
    State state;
    char escInter, highSurrogate;
    bool csiOverflow;
    readonly StringBuilder seq = new StringBuilder();
    readonly Decoder utf8 = new UTF8Encoding(false, false).GetDecoder();
    char[] chars = new char[0];
    readonly int[] ps = new int[32];
    readonly bool[] psSub = new bool[32]; // true = sub-parâmetro (veio depois de ':')
    int pn;
    char prefix, inter;

    public Terminal(int cols, int rows)
    {
        Cols = Math.Max(2, cols); Rows = Math.Max(1, rows);
        main = NewScreen(); Screen = main;
        bottom = Rows - 1;
        ResetTabs();
    }

    public bool AltScreen { get { return alt != null && Screen == alt; } }

    public void Feed(byte[] data, int count)
    {
        lock (Sync)
        {
            if (chars.Length < count + 8) chars = new char[count + 8];
            int n = utf8.GetChars(data, 0, count, chars, 0, false);
            for (int i = 0; i < n; i++) Process(chars[i]);
            Dirty = true;
        }
    }

    // ---- Vista -------------------------------------------------------------------------

    // Linha visível na posição 'row' quando a vista está 'offset' linhas acima do fundo.
    public Line ViewLine(int row, int offset)
    {
        if (offset <= 0) return Screen[row];
        int idx = Scrollback.Count - offset + row;
        if (idx < 0) return null;
        return idx < Scrollback.Count ? Scrollback[idx] : Screen[idx - Scrollback.Count];
    }

    public long AbsRow(int row, int offset) { return Trimmed + Scrollback.Count - offset + row; }

    public Line AbsLine(long abs)
    {
        long idx = abs - Trimmed;
        if (idx < 0) return null;
        if (idx < Scrollback.Count) return Scrollback[(int)idx];
        idx -= Scrollback.Count;
        return idx < Rows ? Screen[idx] : null;
    }

    public static int ToRgb(int color, bool foreground)
    {
        if (color == 0) return foreground ? DefaultFgRgb : DefaultBgRgb;
        if ((color & 0x1000000) != 0) return color & 0xFFFFFF;
        return Palette[(color - 1) & 0xFF];
    }

    public void Resize(int cols, int rows)
    {
        lock (Sync)
        {
            cols = Math.Max(2, cols); rows = Math.Max(1, rows);
            if (cols == Cols && rows == Rows) return;
            bool onAlt = AltScreen;
            int cy = CursorY, other = altSaved.Y;
            if (onAlt)
            {
                alt = Reshape(alt, cols, rows, false, ref cy);
                main = Reshape(main, cols, rows, true, ref other);
                altSaved.Y = other;
                Screen = alt;
            }
            else
            {
                main = Reshape(main, cols, rows, true, ref cy);
                if (alt != null) alt = Reshape(alt, cols, rows, false, ref other);
                Screen = main;
            }
            Cols = cols; Rows = rows;
            top = 0; bottom = rows - 1;
            CursorY = cy; CursorX = Clamp(CursorX, 0, cols - 1);
            wrapPending = false;
            ResetTabs();
            Dirty = true;
        }
    }

    // Encolher: saem primeiro as linhas abaixo do cursor; depois as de cima vão para o histórico.
    Line[] Reshape(Line[] old, int cols, int rows, bool history, ref int cy)
    {
        var list = new List<Line>(old);
        while (list.Count > rows && list.Count - 1 > cy) list.RemoveAt(list.Count - 1);
        while (list.Count > rows) { if (history) PushScrollback(list[0]); list.RemoveAt(0); cy--; }
        while (list.Count < rows) list.Add(new Line(cols, 0));
        foreach (Line line in list) Fit(line, cols);
        cy = Clamp(cy, 0, rows - 1);
        return list.ToArray();
    }

    static void Fit(Line line, int cols)
    {
        int old = line.Cells.Length;
        if (old == cols) return;
        Array.Resize(ref line.Cells, cols);
        if (cols < old && (line.Cells[cols - 1].Attr & CellAttr.Wide) != 0) line.Cells[cols - 1] = new Cell();
        line.Version++;
    }

    // ---- Parser ------------------------------------------------------------------------

    void Process(char c)
    {
        switch (state)
        {
            case State.Ground: Ground(c); break;
            case State.Esc: Escape(c); break;
            case State.EscInter:
                state = State.Ground;
                if (escInter == '(') lineDrawing = c == '0';
                break;
            case State.Csi: CsiChar(c); break;
            case State.Osc:
                if (c == '\a') { Osc(); state = State.Ground; }
                else if (c == '\u001b') state = State.OscEsc;
                else if (c == '\u0018' || c == '\u001a') state = State.Ground;
                else if (seq.Length < 8192) seq.Append(c);
                break;
            case State.OscEsc:
                Osc();
                state = State.Ground;
                if (c != '\\') Escape(c);
                break;
            case State.Str: // DCS/SOS/PM/APC: ignorados até ao ST
                if (c == '\u001b') state = State.StrEsc;
                else if (c == '\u0018' || c == '\u001a') state = State.Ground;
                break;
            case State.StrEsc:
                state = State.Ground;
                if (c != '\\') Escape(c);
                break;
        }
    }

    void Ground(char c)
    {
        if (c < ' ' || c == '\u007f') { Control(c); return; }
        if (c >= '\u0080' && c < ' ') return; // C1
        if (char.IsHighSurrogate(c)) { highSurrogate = c; return; }
        int cp = c;
        if (char.IsLowSurrogate(c))
        {
            if (highSurrogate == '\0') return;
            cp = char.ConvertToUtf32(highSurrogate, c);
        }
        highSurrogate = '\0';
        Print(cp);
    }

    void Control(char c)
    {
        switch (c)
        {
            case '\u001b': state = State.Esc; break;
            case '\r': CursorX = 0; wrapPending = false; break;
            case '\n': case '\u000b': case '\u000c': LineFeed(); break;
            case '\b': if (CursorX > 0) CursorX--; wrapPending = false; break;
            case '\t': Tab(); break;
        }
    }

    void Escape(char c)
    {
        state = State.Ground;
        if (c < ' ')
        {
            if (c == '\u0018' || c == '\u001a') return; // cancela
            state = State.Esc;                         // continua à espera do resto
            if (c != '\u001b') Control(c);
            return;
        }
        switch (c)
        {
            case '[': state = State.Csi; seq.Length = 0; csiOverflow = false; break;
            case ']': state = State.Osc; seq.Length = 0; break;
            case 'P': case 'X': case '^': case '_': state = State.Str; break;
            case '7': saved = Save(); break;
            case '8': Restore(saved); break;
            case 'D': LineFeed(); break;
            case 'E': CursorX = 0; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'H': if (CursorX < Cols) tabs[CursorX] = true; break;
            case 'c': FullReset(); break;
            default:
                if (c <= '/') { escInter = c; state = State.EscInter; }
                break;
        }
    }

    void CsiChar(char c)
    {
        if (c < ' ')
        {
            if (c == '\u0018' || c == '\u001a') state = State.Ground;
            else Control(c); // ESC aborta e começa outra sequência; CR/LF executam-se
            return;
        }
        if (c >= '@' && c <= '~') { state = State.Ground; if (!csiOverflow) Csi(c); return; }
        if (c > '~') { state = State.Ground; Ground(c); return; }
        if (seq.Length < 128) seq.Append(c); else csiOverflow = true;
    }

    void ParseParams()
    {
        pn = 0; prefix = '\0'; inter = '\0';
        int i = 0, len = seq.Length, cur = -1;
        bool sub = false;
        if (len > 0 && seq[0] >= '<' && seq[0] <= '?') { prefix = seq[0]; i = 1; }
        for (; i < len; i++)
        {
            char ch = seq[i];
            if (ch >= '0' && ch <= '9') cur = Math.Min(99999, (cur < 0 ? 0 : cur) * 10 + (ch - '0'));
            else if (ch == ';' || ch == ':')
            {
                if (pn < ps.Length) { ps[pn] = cur; psSub[pn] = sub; pn++; }
                cur = -1; sub = ch == ':';
            }
            else if (ch >= ' ' && ch <= '/') inter = ch;
        }
        if (pn < ps.Length) { ps[pn] = cur; psSub[pn] = sub; pn++; }
    }

    int Arg(int k, int def) { int v = k < pn ? ps[k] : -1; return v < 0 ? def : v; }
    int Count(int k) { return Math.Max(1, Arg(k, 1)); }

    void Csi(char f)
    {
        ParseParams();
        if (prefix == '?') { CsiPrivate(f); return; }
        if (prefix == '>')
        {
            if (f == 'c') Send(CSI + ">0;10;1c");                 // DA2
            else if (f == 'q') Send(ESC + "P>|AgentScratchpad 3.0" + ST); // XTVERSION
            return;
        }
        if (prefix != '\0') return; // '<' '=' (protocolo de teclado kitty, etc.): não suportado
        if (inter == ' ') { if (f == 'q') CursorShape = Arg(0, 0); return; }
        if (inter == '!') { if (f == 'p') SoftReset(); return; }
        if (inter != '\0') return;

        int upLimit = CursorY >= top ? top : 0, downLimit = CursorY <= bottom ? bottom : Rows - 1;
        switch (f)
        {
            case 'A': CursorY = Math.Max(upLimit, CursorY - Count(0)); wrapPending = false; break;
            case 'B': case 'e': CursorY = Math.Min(downLimit, CursorY + Count(0)); wrapPending = false; break;
            case 'C': case 'a': CursorX = Math.Min(Cols - 1, CursorX + Count(0)); wrapPending = false; break;
            case 'D': CursorX = Math.Max(0, CursorX - Count(0)); wrapPending = false; break;
            case 'E': CursorY = Math.Min(downLimit, CursorY + Count(0)); CursorX = 0; wrapPending = false; break;
            case 'F': CursorY = Math.Max(upLimit, CursorY - Count(0)); CursorX = 0; wrapPending = false; break;
            case 'G': case '`': CursorX = Clamp(Count(0) - 1, 0, Cols - 1); wrapPending = false; break;
            case 'H': case 'f': MoveTo(Count(0) - 1, Count(1) - 1); break;
            case 'd': MoveTo(Count(0) - 1, CursorX); break;
            case 'J': EraseDisplay(Arg(0, 0)); break;
            case 'K': EraseLine(Arg(0, 0)); break;
            case 'X': EraseChars(Count(0)); break;
            case 'P': DeleteChars(Count(0)); break;
            case '@': InsertBlanks(Count(0)); break;
            case 'L': if (CursorY >= top && CursorY <= bottom) { ScrollDown(CursorY, bottom, Count(0)); CursorX = 0; wrapPending = false; } break;
            case 'M': if (CursorY >= top && CursorY <= bottom) { ScrollUp(CursorY, bottom, Count(0), false); CursorX = 0; wrapPending = false; } break;
            case 'S': ScrollUp(top, bottom, Count(0), top == 0 && !AltScreen); break;
            case 'T': if (pn <= 1) ScrollDown(top, bottom, Count(0)); break;
            case 'r': SetRegion(Arg(0, 1), Arg(1, Rows)); break;
            case 'm': Sgr(); break;
            case 'h': case 'l':
                for (int k = 0; k < pn; k++) if (ps[k] == 4) insertMode = f == 'h';
                break;
            case 'n':
                if (Arg(0, 0) == 5) Send(CSI + "0n");
                else if (Arg(0, 0) == 6) Send(CSI + (CursorY - (originMode ? top : 0) + 1) + ";" + (CursorX + 1) + "R");
                break;
            case 'c': if (Arg(0, 0) == 0) Send(CSI + "?62;22c"); break; // DA1: VT220 com cor
            case 's': saved = Save(); break;
            case 'u': Restore(saved); break;
            case 't': WindowOps(); break;
            case 'g':
                if (Arg(0, 0) == 0) { if (CursorX < Cols) tabs[CursorX] = false; }
                else if (Arg(0, 0) == 3) Array.Clear(tabs, 0, tabs.Length);
                break;
            case 'I': for (int k = Count(0); k > 0; k--) Tab(); break;
            case 'Z': for (int k = Count(0); k > 0; k--) BackTab(); break;
            case 'b': for (int k = Math.Min(Count(0), 4096); k > 0; k--) Print(lastChar); break;
        }
    }

    void CsiPrivate(char f)
    {
        if (f == 'h' || f == 'l') { for (int k = 0; k < pn; k++) SetMode(ps[k], f == 'h'); return; }
        if (f == 'p' && inter == '$') { int m = Arg(0, 0); Send(CSI + "?" + m + ";" + QueryMode(m) + "$y"); return; }
        if (f == 'n' && Arg(0, 0) == 6) { Send(CSI + "?" + (CursorY + 1) + ";" + (CursorX + 1) + "R"); return; }
        if (f == 'J') EraseDisplay(Arg(0, 0));
        else if (f == 'K') EraseLine(Arg(0, 0));
        // '?u' (consulta do protocolo kitty) fica sem resposta: não suportado
    }

    void SetMode(int m, bool on)
    {
        switch (m)
        {
            case 1: AppCursorKeys = on; break;
            case 6: originMode = on; MoveTo(0, 0); break;
            case 7: autoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 47: case 1047: SwitchScreen(on, false); break;
            case 1049: SwitchScreen(on, true); break;
            case 1048: if (on) saved = Save(); else Restore(saved); break;
            case 1000: case 1002: case 1003: if (on) MouseMode = m; else if (MouseMode == m) MouseMode = 0; break;
            case 1006: MouseSgr = on; break;
            case 1004: FocusEvents = on; break;
            case 2004: BracketedPaste = on; break;
            case 2026: SyncOutput = on; if (on) SyncSince = DateTime.UtcNow; break;
        }
    }

    int QueryMode(int m)
    {
        bool v;
        switch (m)
        {
            case 1: v = AppCursorKeys; break;
            case 6: v = originMode; break;
            case 7: v = autoWrap; break;
            case 25: v = CursorVisible; break;
            case 47: case 1047: case 1049: v = AltScreen; break;
            case 1000: case 1002: case 1003: v = MouseMode == m; break;
            case 1004: v = FocusEvents; break;
            case 1006: v = MouseSgr; break;
            case 2004: v = BracketedPaste; break;
            case 2026: v = SyncOutput; break;
            default: return 0; // não reconhecido
        }
        return v ? 1 : 2;
    }

    void Sgr()
    {
        for (int k = 0; k < pn; k++)
        {
            int p = Math.Max(0, ps[k]);
            switch (p)
            {
                case 0: fg = 0; bg = 0; attr = CellAttr.None; break;
                case 1: attr |= CellAttr.Bold; break;
                case 2: attr |= CellAttr.Dim; break;
                case 3: attr |= CellAttr.Italic; break;
                case 4:
                    if (k + 1 < pn && psSub[k + 1] && ps[k + 1] == 0) attr &= ~CellAttr.Underline;
                    else attr |= CellAttr.Underline;
                    break;
                case 5: case 6: attr |= CellAttr.Blink; break;
                case 7: attr |= CellAttr.Inverse; break;
                case 8: attr |= CellAttr.Hidden; break;
                case 9: attr |= CellAttr.Strike; break;
                case 21: attr |= CellAttr.Underline; break;
                case 22: attr &= ~(CellAttr.Bold | CellAttr.Dim); break;
                case 23: attr &= ~CellAttr.Italic; break;
                case 24: attr &= ~CellAttr.Underline; break;
                case 25: attr &= ~CellAttr.Blink; break;
                case 27: attr &= ~CellAttr.Inverse; break;
                case 28: attr &= ~CellAttr.Hidden; break;
                case 29: attr &= ~CellAttr.Strike; break;
                case 38: fg = ExtColor(ref k, fg); break;
                case 39: fg = 0; break;
                case 48: bg = ExtColor(ref k, bg); break;
                case 49: bg = 0; break;
                case 58: ExtColor(ref k, 0); break; // cor do sublinhado: ignorada
                default:
                    if (p >= 30 && p <= 37) fg = p - 30 + 1;
                    else if (p >= 40 && p <= 47) bg = p - 40 + 1;
                    else if (p >= 90 && p <= 97) fg = p - 90 + 8 + 1;
                    else if (p >= 100 && p <= 107) bg = p - 100 + 8 + 1;
                    break;
            }
            while (k + 1 < pn && psSub[k + 1]) k++; // sub-parâmetros que sobrem
        }
    }

    // 38;5;n, 38;2;r;g;b e as formas com ':' (38:5:n, 38:2::r:g:b, 38:2:r:g:b).
    int ExtColor(ref int k, int current)
    {
        if (k + 1 < pn && psSub[k + 1])
        {
            int s = k + 1, e = s;
            while (e < pn && psSub[e]) e++;
            int cnt = e - s, result = current;
            if (ps[s] == 5 && cnt >= 2) result = Clamp(ps[s + 1], 0, 255) + 1;
            else if (ps[s] == 2 && cnt >= 5) result = Rgb(ps[s + 2], ps[s + 3], ps[s + 4]);
            else if (ps[s] == 2 && cnt == 4) result = Rgb(ps[s + 1], ps[s + 2], ps[s + 3]);
            k = e - 1;
            return result;
        }
        if (k + 2 < pn && ps[k + 1] == 5) { int n = ps[k + 2]; k += 2; return Clamp(n, 0, 255) + 1; }
        if (k + 4 < pn && ps[k + 1] == 2) { int r = ps[k + 2], g = ps[k + 3], b = ps[k + 4]; k += 4; return Rgb(r, g, b); }
        return current;
    }

    static int Rgb(int r, int g, int b)
    {
        return 0x1000000 | (Clamp(r, 0, 255) << 16) | (Clamp(g, 0, 255) << 8) | Clamp(b, 0, 255);
    }

    void Osc()
    {
        string s = seq.ToString();
        int semi = s.IndexOf(';');
        string code = semi < 0 ? s : s.Substring(0, semi), arg = semi < 0 ? "" : s.Substring(semi + 1);
        if (arg != "?") return; // título, hiperligações, progresso...: não precisamos deles
        if (code == "10" || code == "12") Send(ESC + "]" + code + ";" + XColor(DefaultFgRgb) + ST);
        else if (code == "11") Send(ESC + "]11;" + XColor(DefaultBgRgb) + ST);
    }

    static string XColor(int rgb)
    {
        int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        return string.Format("rgb:{0:x2}{0:x2}/{1:x2}{1:x2}/{2:x2}{2:x2}", r, g, b);
    }

    void WindowOps()
    {
        switch (Arg(0, 0))
        {
            case 14: Send(CSI + "4;" + (Rows * CellPxH) + ";" + (Cols * CellPxW) + "t"); break;
            case 16: Send(CSI + "6;" + CellPxH + ";" + CellPxW + "t"); break;
            case 18: Send(CSI + "8;" + Rows + ";" + Cols + "t"); break;
        }
    }

    void Send(string s)
    {
        var h = Reply;
        if (h != null) h(s);
    }

    // ---- Operações no ecrã -------------------------------------------------------------

    void Print(int cp)
    {
        if (lineDrawing && cp >= 0x5f && cp <= 0x7e) cp = DecSpecial[cp - 0x5f];
        int w = CharWidth(cp);
        if (w == 0) { AppendCombining(cp); return; }
        lastChar = cp;
        if (wrapPending && autoWrap) { Screen[CursorY].Wrapped = true; CursorX = 0; LineFeed(); }
        wrapPending = false;
        if (w == 2 && CursorX == Cols - 1)
        {
            if (!autoWrap) return;
            Line l = Screen[CursorY];
            ClearWideAt(l, CursorX);
            l.Cells[CursorX] = new Cell { Bg = bg };
            l.Wrapped = true; l.Version++;
            CursorX = 0; LineFeed();
        }
        if (insertMode) InsertBlanks(w);
        Line line = Screen[CursorY];
        ClearWideAt(line, CursorX);
        if (w == 2) ClearWideAt(line, CursorX + 1);
        line.Cells[CursorX] = new Cell { Ch = char.ConvertFromUtf32(cp), Fg = fg, Bg = bg, Attr = w == 2 ? attr | CellAttr.Wide : attr };
        if (w == 2) line.Cells[CursorX + 1] = new Cell { Fg = fg, Bg = bg, Attr = attr | CellAttr.WideTail };
        line.Version++;
        CursorX += w;
        if (CursorX >= Cols) { CursorX = Cols - 1; wrapPending = autoWrap; }
    }

    void AppendCombining(int cp)
    {
        int x = wrapPending ? CursorX : CursorX - 1;
        if (x < 0) return;
        Line line = Screen[CursorY];
        if ((line.Cells[x].Attr & CellAttr.WideTail) != 0 && x > 0) x--;
        line.Cells[x].Ch = (line.Cells[x].Ch ?? " ") + char.ConvertFromUtf32(cp);
        line.Version++;
    }

    // Escrever por cima de metade de um caractere largo apaga a outra metade.
    void ClearWideAt(Line line, int x)
    {
        if (x < 0 || x >= Cols) return;
        CellAttr a = line.Cells[x].Attr;
        if ((a & CellAttr.WideTail) != 0 && x > 0) line.Cells[x - 1] = new Cell { Bg = line.Cells[x - 1].Bg };
        else if ((a & CellAttr.Wide) != 0 && x + 1 < Cols) line.Cells[x + 1] = new Cell { Bg = line.Cells[x + 1].Bg };
    }

    void LineFeed()
    {
        wrapPending = false;
        if (CursorY == bottom) ScrollUp(top, bottom, 1, top == 0 && !AltScreen);
        else if (CursorY < Rows - 1) CursorY++;
    }

    void ReverseIndex()
    {
        wrapPending = false;
        if (CursorY == top) ScrollDown(top, bottom, 1);
        else if (CursorY > 0) CursorY--;
    }

    void ScrollUp(int t, int b, int n, bool toHistory)
    {
        n = Math.Min(n, b - t + 1);
        for (int i = 0; i < n; i++)
        {
            if (toHistory) PushScrollback(Screen[t]);
            Array.Copy(Screen, t + 1, Screen, t, b - t);
            Screen[b] = new Line(Cols, bg);
        }
    }

    void ScrollDown(int t, int b, int n)
    {
        n = Math.Min(n, b - t + 1);
        for (int i = 0; i < n; i++)
        {
            Array.Copy(Screen, t, Screen, t + 1, b - t);
            Screen[t] = new Line(Cols, bg);
        }
    }

    void PushScrollback(Line line)
    {
        Scrollback.Add(line);
        ScrollbackAdded++;
        if (Scrollback.Count > MaxScrollback + 256)
        {
            int k = Scrollback.Count - MaxScrollback;
            Scrollback.RemoveRange(0, k);
            Trimmed += k;
        }
    }

    void MoveTo(int row, int col)
    {
        if (originMode) CursorY = Clamp(row + top, top, bottom);
        else CursorY = Clamp(row, 0, Rows - 1);
        CursorX = Clamp(col, 0, Cols - 1);
        wrapPending = false;
    }

    void SetRegion(int t, int b)
    {
        t = Clamp(t, 1, Rows) - 1; b = Clamp(b, 1, Rows) - 1;
        if (t >= b) return;
        top = t; bottom = b;
        MoveTo(0, 0);
    }

    void Blank(Line line, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(Cols, to);
        if (from >= to) return;
        ClearWideAt(line, from);
        ClearWideAt(line, to - 1);
        for (int i = from; i < to; i++) line.Cells[i] = new Cell { Bg = bg };
        line.Version++;
    }

    void EraseLine(int mode)
    {
        Line line = Screen[CursorY];
        if (mode == 0) { Blank(line, CursorX, Cols); line.Wrapped = false; }
        else if (mode == 1) Blank(line, 0, CursorX + 1);
        else if (mode == 2) { Blank(line, 0, Cols); line.Wrapped = false; }
        wrapPending = false;
    }

    void EraseDisplay(int mode)
    {
        if (mode == 0)
        {
            EraseLine(0);
            for (int y = CursorY + 1; y < Rows; y++) Screen[y] = new Line(Cols, bg);
        }
        else if (mode == 1)
        {
            for (int y = 0; y < CursorY; y++) Screen[y] = new Line(Cols, bg);
            EraseLine(1);
        }
        else if (mode == 2)
        {
            for (int y = 0; y < Rows; y++) Screen[y] = new Line(Cols, bg);
        }
        else if (mode == 3)
        {
            Trimmed += Scrollback.Count;
            Scrollback.Clear();
        }
        wrapPending = false;
    }

    void EraseChars(int n)
    {
        Blank(Screen[CursorY], CursorX, CursorX + n);
        wrapPending = false;
    }

    void DeleteChars(int n)
    {
        Line line = Screen[CursorY];
        n = Math.Min(n, Cols - CursorX);
        ClearWideAt(line, CursorX);
        ClearWideAt(line, CursorX + n - 1);
        Array.Copy(line.Cells, CursorX + n, line.Cells, CursorX, Cols - CursorX - n);
        for (int i = Cols - n; i < Cols; i++) line.Cells[i] = new Cell { Bg = bg };
        line.Version++;
        wrapPending = false;
    }

    void InsertBlanks(int n)
    {
        Line line = Screen[CursorY];
        n = Math.Min(n, Cols - CursorX);
        ClearWideAt(line, CursorX);
        Array.Copy(line.Cells, CursorX, line.Cells, CursorX + n, Cols - CursorX - n);
        for (int i = CursorX; i < CursorX + n; i++) line.Cells[i] = new Cell { Bg = bg };
        if ((line.Cells[Cols - 1].Attr & CellAttr.Wide) != 0) line.Cells[Cols - 1] = new Cell { Bg = bg };
        line.Version++;
        wrapPending = false;
    }

    void Tab()
    {
        int x = CursorX + 1;
        while (x < Cols - 1 && !tabs[x]) x++;
        CursorX = Math.Min(x, Cols - 1);
        wrapPending = false;
    }

    void BackTab()
    {
        int x = CursorX - 1;
        while (x > 0 && !tabs[x]) x--;
        CursorX = Math.Max(0, x);
        wrapPending = false;
    }

    void ResetTabs()
    {
        tabs = new bool[Cols];
        for (int i = 8; i < Cols; i += 8) tabs[i] = true;
    }

    void SwitchScreen(bool toAlt, bool withCursor)
    {
        if (toAlt == AltScreen) return;
        if (toAlt)
        {
            if (withCursor) altSaved = Save();
            alt = NewScreen();
            Screen = alt;
        }
        else
        {
            Screen = main;
            if (withCursor) Restore(altSaved);
        }
        wrapPending = false;
    }

    SavedCursor Save()
    {
        return new SavedCursor { X = CursorX, Y = CursorY, Fg = fg, Bg = bg, Attr = attr, Origin = originMode, Wrap = wrapPending, LineDrawing = lineDrawing };
    }

    void Restore(SavedCursor s)
    {
        CursorX = Clamp(s.X, 0, Cols - 1); CursorY = Clamp(s.Y, 0, Rows - 1);
        fg = s.Fg; bg = s.Bg; attr = s.Attr; originMode = s.Origin; wrapPending = s.Wrap; lineDrawing = s.LineDrawing;
    }

    void SoftReset()
    {
        CursorVisible = true; originMode = false; autoWrap = true; insertMode = false; AppCursorKeys = false;
        top = 0; bottom = Rows - 1;
        fg = 0; bg = 0; attr = CellAttr.None; lineDrawing = false;
        saved = new SavedCursor();
        wrapPending = false;
    }

    void FullReset()
    {
        SoftReset();
        main = NewScreen(); alt = null; Screen = main;
        CursorX = 0; CursorY = 0;
        MouseMode = 0; MouseSgr = false; BracketedPaste = false; FocusEvents = false; SyncOutput = false;
        ResetTabs();
    }

    Line[] NewScreen()
    {
        var rows = new Line[Rows];
        for (int i = 0; i < Rows; i++) rows[i] = new Line(Cols, 0);
        return rows;
    }

    static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

    // ---- Larguras e tabelas ------------------------------------------------------------

    public static int CharWidth(int cp)
    {
        if (cp < 0x300) return 1;
        if (cp == 0x200B || cp == 0x200C || cp == 0x200D || cp == 0x2060 || cp == 0xFEFF) return 0;
        if ((cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF)) return 0;
        UnicodeCategory cat = cp <= 0xFFFF
            ? CharUnicodeInfo.GetUnicodeCategory((char)cp)
            : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
        if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark || cat == UnicodeCategory.Format) return 0;
        if (cp >= 0x1100)
            for (int i = 0; i < WideRanges.Length; i += 2)
            {
                if (cp < WideRanges[i]) break;
                if (cp <= WideRanges[i + 1]) return 2;
            }
        return 1;
    }

    // East Asian Wide/Fullwidth e emoji de apresentação larga (aproximação do Unicode 15).
    static readonly int[] WideRanges = {
        0x1100, 0x115F, 0x231A, 0x231B, 0x2329, 0x232A, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3,
        0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648, 0x2653, 0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1,
        0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA,
        0x26F2, 0x26F3, 0x26F5, 0x26F5, 0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B,
        0x2728, 0x2728, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797,
        0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55,
        0x2E80, 0x303E, 0x3041, 0x33FF, 0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xA000, 0xA4CF, 0xA960, 0xA97F,
        0xAC00, 0xD7A3, 0xF900, 0xFAFF, 0xFE10, 0xFE19, 0xFE30, 0xFE6F, 0xFF00, 0xFF60, 0xFFE0, 0xFFE6,
        0x16FE0, 0x16FE4, 0x17000, 0x18AFF, 0x1B000, 0x1B2FF,
        0x1F004, 0x1F004, 0x1F0CF, 0x1F0CF, 0x1F18E, 0x1F18E, 0x1F191, 0x1F19A, 0x1F200, 0x1F202,
        0x1F210, 0x1F23B, 0x1F240, 0x1F248, 0x1F250, 0x1F251, 0x1F260, 0x1F265,
        0x1F300, 0x1F320, 0x1F32D, 0x1F335, 0x1F337, 0x1F37C, 0x1F37E, 0x1F393, 0x1F3A0, 0x1F3CA,
        0x1F3CF, 0x1F3D3, 0x1F3E0, 0x1F3F0, 0x1F3F4, 0x1F3F4, 0x1F3F8, 0x1F43E, 0x1F440, 0x1F440,
        0x1F442, 0x1F4FC, 0x1F4FF, 0x1F53D, 0x1F54B, 0x1F54E, 0x1F550, 0x1F567, 0x1F57A, 0x1F57A,
        0x1F595, 0x1F596, 0x1F5A4, 0x1F5A4, 0x1F5FB, 0x1F64F, 0x1F680, 0x1F6C5, 0x1F6CC, 0x1F6CC,
        0x1F6D0, 0x1F6D2, 0x1F6D5, 0x1F6D7, 0x1F6DC, 0x1F6DF, 0x1F6EB, 0x1F6EC, 0x1F6F4, 0x1F6FC,
        0x1F7E0, 0x1F7EB, 0x1F7F0, 0x1F7F0, 0x1F90C, 0x1F93A, 0x1F93C, 0x1F945, 0x1F947, 0x1F9FF,
        0x1FA70, 0x1FAFF, 0x20000, 0x2FFFD, 0x30000, 0x3FFFD,
    };

    // DEC Special Graphics (ESC ( 0), de 0x5f a 0x7e.
    static readonly int[] DecSpecial = {
        0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518,
        0x2510, 0x250C, 0x2514, 0x253C, 0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534,
        0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7,
    };

    static int[] BuildPalette()
    {
        var p = new int[256];
        int[] campbell = { 0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
                           0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2 };
        Array.Copy(campbell, p, 16);
        int[] steps = { 0, 95, 135, 175, 215, 255 };
        for (int i = 0; i < 216; i++) p[16 + i] = (steps[i / 36] << 16) | (steps[i / 6 % 6] << 8) | steps[i % 6];
        for (int i = 0; i < 24; i++) { int v = 8 + i * 10; p[232 + i] = (v << 16) | (v << 8) | v; }
        return p;
    }
}
