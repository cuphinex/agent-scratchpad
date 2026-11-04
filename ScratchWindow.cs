// A janela flutuante: acrylic do Windows 11, sem bordas; desliza de uma borda e escurece o resto do ecrã.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

sealed class ScratchWindow : Window
{
    const int MaxPanes = 4;
    // Painéis lado a lado, como as janelas em mosaico do Omarchy; cada um tem o seu agente.
    readonly System.Collections.Generic.List<Pane> panes = new System.Collections.Generic.List<Pane>();
    readonly Grid panesGrid = new Grid();
    readonly AgentPicker picker = new AgentPicker();
    int active;
    readonly Border tint = new Border();
    readonly Grid root = new Grid();
    readonly TranslateTransform slide = new TranslateTransform();
    // backdrop=blur: fotografia reduzida do ecrã por trás, desfocada e presa ao ambiente de trabalho.
    // O acrylic do Windows fica mais claro enquanto a janela se move (o "flash" ao deslizar); isto não.
    readonly Canvas backdropLayer = new Canvas();
    readonly Image snapshot = new Image { Stretch = Stretch.Fill };
    readonly TranslateTransform snapshotOffset = new TranslateTransform();
    const int SnapshotPad = 3;  // píxeis (da imagem reduzida) repetidos à volta: o desfoque esbate as pontas
    const int SnapshotScale = 24;
    double snapshotLeft, snapshotTop; // canto da imagem no ecrã, em píxeis
    double scale = 1;
    bool cloaked, holdCloak; // holdCloak: camuflada até o WPF ter desenhado (evita o flash branco)
    int warmFrames;
    readonly DimOverlay dim = new DimOverlay();
    readonly Stopwatch clock = new Stopwatch();
    Config cfg;
    IntPtr hwnd, previous, monitorHandle;
    double progress, fromProgress; // 0 = escondida, 1 = no sítio
    int direction;                 // +1 a entrar, -1 a sair, 0 parada
    Win32.RECT finalRect, hiddenRect, monitor;
    bool clip, rendering, restoreFocus, quitting;

    public ScratchWindow(Config c)
    {
        Title = Program.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize; // mantém a moldura do DWM (sombra, cantos); o WindowChrome tira as pegas
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        // O conteúdo tem sempre o tamanho final: numa borda partilhada a janela encolhe durante a
        // animação e o conteúdo desliza dentro dela, sem a grelha do terminal mudar.
        snapshot.RenderTransform = snapshotOffset;
        RenderOptions.SetBitmapScalingMode(snapshot, BitmapScalingMode.HighQuality);
        snapshot.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 18, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        backdropLayer.Children.Add(snapshot);
        backdropLayer.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)); // nunca preto por baixo
        root.Children.Add(backdropLayer);
        root.Children.Add(tint);
        root.Children.Add(panesGrid);
        root.Children.Add(picker);
        root.HorizontalAlignment = HorizontalAlignment.Left;
        root.VerticalAlignment = VerticalAlignment.Top;
        root.RenderTransform = slide;
        Content = root;

        picker.Picked += agent => OpenPane(agent);
        picker.Cancelled += () => FocusActive();
        dim.Clicked += () => Dismiss(true);
        SourceInitialized += delegate { OnSourceInitialized(); };
        Deactivated += delegate { Dispatcher.BeginInvoke(new Action(OnBlur), System.Windows.Threading.DispatcherPriority.Background); };
        ApplyConfig(c);
    }

    bool IsOut { get { return direction > 0 || direction == 0 && progress > 0; } }

    // Perder o foco esconde a janela, mas só se o foco foi mesmo para outra app
    // (não para a nossa película escura nem de volta para nós).
    void OnBlur()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (!cfg.HideOnBlur || !IsOut || IsActive) return;
        if (fg == hwnd || fg == dim.Handle) { TakeFocus(); return; }
        Dismiss(false);
    }

    public void ApplyConfig(Config c)
    {
        cfg = c;
        foreach (Pane p in panes) p.ApplyConfig(c);
        picker.ApplyConfig(c);
        // Com blur, a fotografia foi tirada antes de escurecer o ecrã: aplicamos aqui também o dim.
        double darken = c.Backdrop == 0 ? 1 - (1 - c.Dim) * (1 - c.Tint) : c.Tint;
        tint.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(darken * 255), 0, 0, 0));
        backdropLayer.Visibility = c.Backdrop == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hwnd != IntPtr.Zero) ApplyDwm();
        if (IsOut && direction == 0) Relayout();
    }

    // Tamanho/posição de novo no mesmo monitor (painéis abertos ou fechados, config alterada).
    void Relayout()
    {
        ComputeRects(monitorHandle);
        Place(1);
    }

    void OnSourceInitialized()
    {
        hwnd = new WindowInteropHelper(this).Handle;
        HwndSource src = HwndSource.FromHwnd(hwnd);
        src.CompositionTarget.BackgroundColor = Colors.Transparent;
        src.AddHook(WndProc);
        int style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, style & ~(Win32.WS_MAXIMIZEBOX | Win32.WS_MINIMIZEBOX));
        ApplyDwm();
        // Win+<tecla livre>: o hook do Win+S injeta-a, e o WM_HOTKEY dá-nos o direito de tomar o foco.
        // Experimenta F24..F13 (teclas que quase nenhum teclado tem) até uma estar livre.
        for (byte vk = 0x87; vk >= 0x7C && !HotkeyRegistered; vk--)
        {
            if (Win32.RegisterHotKey(hwnd, 1, 0x0008 | 0x4000, vk)) { HotkeyRegistered = true; RelayKey = vk; } // MOD_WIN | MOD_NOREPEAT
            else Log.Write("RegisterHotKey(Win+0x" + vk.ToString("X2") + ") falhou: " + Marshal.GetLastWin32Error());
        }
    }

    public bool HotkeyRegistered { get; private set; }
    public byte RelayKey { get; private set; }

    bool hotkeyPending, hotkeyEarly;

    // O hook viu Win+S. Normalmente o WM_HOTKEY chega logo a seguir e faz o toggle com direito ao foco;
    // se não chegar (Win largado antes do F24, registo falhado), fazemos o toggle na mesma.
    public void HotkeySeen()
    {
        if (!HotkeyRegistered || hotkeyEarly) { hotkeyEarly = false; Toggle(); return; }
        hotkeyPending = true;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += delegate
        {
            timer.Stop();
            if (!hotkeyPending) return;
            hotkeyPending = false;
            Toggle();
        };
        timer.Start();
    }

    void ApplyDwm()
    {
        SetAttr(20, 1);           // modo escuro
        SetAttr(3, 1);            // sem animações do DWM: o deslizar é nosso
        SetAttr(33, cfg.Corners); // cantos
        SetAttr(34, cfg.Border);  // linha de 1 px (por omissão: nenhuma)
        SetAttr(38, cfg.Backdrop == 0 ? 1 : cfg.Backdrop); // acrylic / mica / tabbed / none (blur é nosso)
        var m = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    void SetAttr(int attr, int value)
    {
        int hr = Win32.DwmSetWindowAttribute(hwnd, attr, ref value, 4);
        if (hr != 0) Log.Write("DwmSetWindowAttribute(" + attr + ") = 0x" + hr.ToString("X8"));
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == 1) // WM_HOTKEY (Win+F24 injetado pelo hook)
        {
            handled = true;
            if (hotkeyPending) { hotkeyPending = false; Toggle(); }
            else hotkeyEarly = true; // chegou antes do aviso do hook
        }
        if (msg == Win32.WM_SYSCOMMAND)
        {
            int cmd = wParam.ToInt32() & 0xFFF0;
            // Sem maximizar, mover, redimensionar nem menu de sistema (Alt sozinho fazia "beep").
            if (cmd == 0xF000 || cmd == 0xF010 || cmd == 0xF020 || cmd == 0xF030 || cmd == 0xF100) handled = true;
        }
        return IntPtr.Zero;
    }

    // ---- Mostrar / esconder -----------------------------------------------------------

    public void Toggle()
    {
        if (direction > 0 || direction == 0 && progress >= 1)
        {
            if (direction == 0 && !IsActive) { TakeFocus(); return; } // visível mas atrás: só traz o foco
            Dismiss(true);
        }
        else Summon();
    }

    public void Summon()
    {
        if (direction == 0 && progress <= 0)
        {
            previous = Win32.GetForegroundWindow();
            // Mostrada camuflada: o primeiro frame antes do WPF desenhar sai branco.
            holdCloak = true;
            warmFrames = 0;
            Win32.POINT pt;
            Win32.GetCursorPos(out pt);
            monitorHandle = Win32.MonitorFromPoint(pt, 2); // abre no monitor onde está o rato
            ComputeRects(monitorHandle);
            if (cfg.Backdrop == 0) TakeSnapshot(); // ainda sem janela nem escurecimento por cima
            Place(0);
            if (!IsVisible) Show();
            if (panes.Count == 0) AddPane(cfg.Agents[0]);
            UpdateLayout(); // a grelha fica com o tamanho certo antes de o agente arrancar
            foreach (Pane p in panes) p.Start(); // os que já corriam ficam como estavam
            if (cfg.Dim > 0) dim.ShowOn(monitor, cfg.HideOnBlur);
            Place(0);
        }
        direction = 1;
        fromProgress = progress;
        clock.Restart();
        StartRendering();
        TakeFocus();
    }

    public void Dismiss(bool restore)
    {
        if (!IsOut || direction < 0) return;
        restoreFocus = restore;
        direction = -1;
        fromProgress = progress;
        clock.Restart();
        StartRendering();
        if (cfg.AnimationMs <= 0) Step();
    }

    void StartRendering()
    {
        if (rendering) return;
        CompositionTarget.Rendering += OnFrame;
        rendering = true;
    }

    void StopRendering()
    {
        if (!rendering) return;
        CompositionTarget.Rendering -= OnFrame;
        rendering = false;
    }

    void OnFrame(object sender, EventArgs e)
    {
        if (holdCloak)
        {
            // Espera uns frames camuflada, para o WPF já ter desenhado quando a janela aparece.
            foreach (Pane p in panes) p.View.Tick();
            if (++warmFrames < 3) return;
            holdCloak = false;
            clock.Restart();
        }
        if (direction != 0) Step();
        foreach (Pane p in panes) p.View.Tick();
    }

    void Step()
    {
        double target = direction > 0 ? 1 : 0;
        double total = cfg.AnimationMs * Math.Abs(target - fromProgress);
        double t = total <= 0 ? 1 : Math.Min(1, clock.Elapsed.TotalMilliseconds / total);
        progress = fromProgress + (target - fromProgress) * t;
        Place(progress);
        if (t >= 1) Finish();
    }

    // Ease-out cúbico: a entrar abranda no fim; a sair (progresso ao contrário) acelera.
    void Place(double p)
    {
        double e = 1 - Math.Pow(1 - p, 3);
        int x = (int)Math.Round(hiddenRect.Left + (finalRect.Left - hiddenRect.Left) * e);
        int y = (int)Math.Round(hiddenRect.Top + (finalRect.Top - hiddenRect.Top) * e);
        int w = finalRect.Right - finalRect.Left, h = finalRect.Bottom - finalRect.Top;
        int vx = x, vy = y, vw = w, vh = h;
        if (clip)
        {
            // Borda partilhada com outro monitor: a janela fica só com a parte que cabe neste
            // (o acrylic ignora regiões de recorte, por isso é a própria janela que encolhe).
            vx = Math.Max(x, monitor.Left); vy = Math.Max(y, monitor.Top);
            vw = Math.Min(x + w, monitor.Right) - vx; vh = Math.Min(y + h, monitor.Bottom) - vy;
        }
        bool empty = vw <= 0 || vh <= 0;
        Cloak(empty || holdCloak);
        if (empty) { vx = monitor.Left; vy = monitor.Top; vw = 1; vh = 1; }
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, vx, vy, vw, vh, Win32.SWP_NOACTIVATE);
        slide.X = (x - vx) / scale;
        slide.Y = (y - vy) / scale;
        snapshotOffset.X = (snapshotLeft - x) / scale; // a fotografia fica parada em relação ao ecrã
        snapshotOffset.Y = (snapshotTop - y) / scale;
        dim.SetLevel(cfg.Dim * e);
    }

    // Fotografia só da zona onde a janela vai parar, reduzida 24x (a redução já desfoca), com as
    // bordas repetidas para fora. Assim a janela, ao deslizar, nunca mostra o que está fora dessa
    // zona (ex.: as barras de título escuras no topo do ecrã, que apareciam como uma faixa preta).
    void TakeSnapshot()
    {
        int fw = finalRect.Right - finalRect.Left, fh = finalRect.Bottom - finalRect.Top;
        int sw = Math.Max(1, fw / SnapshotScale), sh = Math.Max(1, fh / SnapshotScale);
        int w = sw + 2 * SnapshotPad, h = sh + 2 * SnapshotPad;
        IntPtr screen = Win32.GetDC(IntPtr.Zero), mem = Win32.CreateCompatibleDC(screen), bmp = Win32.CreateCompatibleBitmap(screen, sw, sh);
        IntPtr old = Win32.SelectObject(mem, bmp);
        Win32.SetStretchBltMode(mem, 4); // HALFTONE: média dos píxeis
        Win32.SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
        Win32.StretchBlt(mem, 0, 0, sw, sh, screen, finalRect.Left, finalRect.Top, fw, fh, 0x00CC0020); // SRCCOPY
        Win32.SelectObject(mem, old);
        // O GDI deixa o alfa a 0: em Bgr32 o alfa é ignorado (senão a imagem ficava transparente).
        BitmapSource raw = new FormatConvertedBitmap(
            Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()),
            PixelFormats.Bgr32, null, 0);
        var small = new byte[sw * sh * 4];
        raw.CopyPixels(small, sw * 4, 0);
        int stride = w * 4;
        var px = new byte[stride * h];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int sx = Math.Max(0, Math.Min(sw - 1, xx - SnapshotPad)), sy = Math.Max(0, Math.Min(sh - 1, yy - SnapshotPad));
                Buffer.BlockCopy(small, (sy * sw + sx) * 4, px, (yy * w + xx) * 4, 4);
            }
        // Desfoque forte e cor dessaturada, como o acrylic: as formas por trás ficam só sugeridas.
        BoxBlur(px, w, h, 2);
        BoxBlur(px, w, h, 2);
        for (int i = 0; i < px.Length; i += 4)
        {
            double luma = 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
            // 60% dessaturado e depois 65% do caminho para #2C2C2C (a "luminosidade" do acrylic escuro):
            // zonas claras e escuras por trás ficam num cinzento parecido, sem faixas pretas.
            for (int k = 0; k < 3; k++) px[i + k] = (byte)((px[i + k] + (luma - px[i + k]) * 0.6) * 0.35 + 0x2C * 0.65);
        }
        BitmapSource src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, stride);
        src.Freeze();
        Win32.DeleteObject(bmp);
        Win32.DeleteDC(mem);
        Win32.ReleaseDC(IntPtr.Zero, screen);
        // Cada píxel da imagem reduzida cobre (fw/sw) x (fh/sh) píxeis do ecrã.
        double cellX = (double)fw / sw, cellY = (double)fh / sh;
        snapshotLeft = finalRect.Left - SnapshotPad * cellX;
        snapshotTop = finalRect.Top - SnapshotPad * cellY;
        snapshot.Source = src;
        snapshot.Width = w * cellX / scale;
        snapshot.Height = h * cellY / scale;
    }

    // Desfoque de caixa separável (horizontal e depois vertical), com as bordas repetidas.
    static void BoxBlur(byte[] px, int w, int h, int r)
    {
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            byte[] from = pass == 0 ? px : tmp, to = pass == 0 ? tmp : px;
            int n = pass == 0 ? w : h, lines = pass == 0 ? h : w;
            for (int line = 0; line < lines; line++)
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < 3; c++)
                    {
                        int sum = 0;
                        for (int d = -r; d <= r; d++)
                        {
                            int j = Math.Max(0, Math.Min(n - 1, i + d));
                            sum += from[pass == 0 ? (line * w + j) * 4 + c : (j * w + line) * 4 + c];
                        }
                        to[pass == 0 ? (line * w + i) * 4 + c : (i * w + line) * 4 + c] = (byte)(sum / (2 * r + 1));
                    }
        }
    }

    void Cloak(bool on)
    {
        if (cloaked == on) return;
        SetAttr(13, on ? 1 : 0); // DWMWA_CLOAK: nada a mostrar ainda
        cloaked = on;
    }

    void Finish()
    {
        int dir = direction;
        direction = 0;
        if (dir > 0) return;
        Park();
        dim.HideNow();
        StopRendering();
        // A janela continua "visível" (só camuflada), por isso o foco tem de sair dela explicitamente.
        bool stillOurs = Win32.GetForegroundWindow() == hwnd;
        if ((restoreFocus || stillOurs) && previous != IntPtr.Zero && Win32.IsWindow(previous) && previous != hwnd)
            Win32.SetForegroundWindow(previous);
        else if (stillOurs)
            Win32.SetForegroundWindow(Win32.GetShellWindow());
    }

    // No arranque: mostra a janela já camuflada e fora do ecrã, para o WPF criar a superfície
    // de desenho antes do primeiro Win+S. Não tira o foco a ninguém.
    public void Prewarm()
    {
        Win32.POINT pt;
        Win32.GetCursorPos(out pt);
        monitorHandle = Win32.MonitorFromPoint(pt, 2);
        ComputeRects(monitorHandle);
        Park();
        ShowActivated = false;
        Show();
        ShowActivated = true;
    }

    // Em vez de esconder (o WPF deitava fora a superfície e o próximo Win+S mostrava um retângulo
    // preto até a redesenhar), a janela fica camuflada e estacionada fora do ecrã, pronta a voltar.
    void Park()
    {
        Cloak(true);
        int w = finalRect.Right - finalRect.Left, h = finalRect.Bottom - finalRect.Top;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, -32000, -32000, w, h, Win32.SWP_NOACTIVATE);
    }

    void ComputeRects(IntPtr mon)
    {
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Win32.MONITORINFO)) };
        Win32.GetMonitorInfo(mon, ref mi);
        double scale = 1;
        uint dpiX, dpiY;
        try { if (Win32.GetDpiForMonitor(mon, 0, out dpiX, out dpiY) == 0) scale = dpiX / 96.0; } catch (Exception) { }

        Win32.RECT work = mi.rcWork, b = mi.rcMonitor;
        int ww = work.Right - work.Left, wh = work.Bottom - work.Top;
        double widthFrac = panes.Count >= 2 ? Math.Max(cfg.Width, cfg.WidthSplit) : cfg.Width; // alarga ao dividir
        int w = (int)(ww * widthFrac), h = (int)(wh * cfg.Height);
        int m = (int)Math.Round(cfg.Margin * scale), shadow = (int)Math.Round(64 * scale);
        int cx = work.Left + (ww - w) / 2, cy = work.Top + (wh - h) / 2;
        int x, y, hx, hy;
        switch (cfg.Edge)
        {
            case "bottom": x = cx; y = work.Bottom - m - h; hx = x; hy = b.Bottom + shadow; break;
            case "left": x = work.Left + m; y = cy; hx = b.Left - w - shadow; hy = y; break;
            case "right": x = work.Right - m - w; y = cy; hx = b.Right + shadow; hy = y; break;
            default: x = cx; y = work.Top + m; hx = x; hy = b.Top - h - shadow; break;
        }
        finalRect = new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        hiddenRect = new Win32.RECT { Left = hx, Top = hy, Right = hx + w, Bottom = hy + h };
        monitor = b;
        this.scale = scale;
        root.Width = w / scale;
        root.Height = h / scale;
        // O ponto de partida cai noutro monitor? Então a janela encolhe ao monitor durante a animação.
        Win32.RECT start = hiddenRect;
        clip = Win32.MonitorFromRect(ref start, 0) != IntPtr.Zero;
    }

    // Chamado a partir do WM_HOTKEY (ou de um clique na bandeja), que dão direito a tomar o foco.
    void TakeFocus()
    {
        if (!Win32.SetForegroundWindow(hwnd))
        {
            // Sem direito ao foco: liga-se por instantes à thread em primeiro plano e tenta outra vez.
            IntPtr fg = Win32.GetForegroundWindow();
            uint pid, me = Win32.GetCurrentThreadId(), other = Win32.GetWindowThreadProcessId(fg, out pid);
            bool attached = other != 0 && other != me && Win32.AttachThreadInput(me, other, true);
            bool ok = Win32.SetForegroundWindow(hwnd);
            if (attached) Win32.AttachThreadInput(me, other, false);
            if (!ok) Log.Write("SetForegroundWindow falhou, mesmo com AttachThreadInput");
        }
        Activate();
        FocusActive();
    }

    // ---- Painéis ----------------------------------------------------------------------

    static readonly Brush SeparatorBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    // Atalhos de painéis vindos do hook (Win+Enter / Win+W / Win+← →), só com o scratchpad à vista.
    public void PaneAction(string action)
    {
        if (!IsOut || direction < 0) return;
        if (picker.IsOpen)
        {
            if (action == "open") { picker.Close(); FocusActive(); }
            return;
        }
        switch (action)
        {
            case "open": if (panes.Count < MaxPanes) picker.Open(cfg.Agents); break;
            case "close": if (panes.Count > 0) ClosePane(panes[active]); break;
            case "left": FocusPane(-1); break;
            case "right": FocusPane(1); break;
        }
    }

    // Novo painel à direita do ativo; a janela alarga e o agente arranca já com o tamanho certo.
    void OpenPane(Agent agent)
    {
        Pane p = AddPane(agent);
        Relayout();
        UpdateLayout();
        p.Start();
        FocusActive();
    }

    Pane AddPane(Agent agent)
    {
        var p = new Pane(agent, cfg);
        p.Exited += pane => ClosePane(pane); // /exit fecha só o painel desse agente
        p.View.GotKeyboardFocus += delegate
        {
            int i = panes.IndexOf(p);
            if (i >= 0 && i != active) { active = i; UpdatePaneLook(); }
        };
        int at = panes.Count == 0 ? 0 : active + 1;
        panes.Insert(at, p);
        active = at;
        RebuildGrid();
        return p;
    }

    void ClosePane(Pane p)
    {
        int i = panes.IndexOf(p);
        if (i < 0) return;
        p.Stop();
        panes.RemoveAt(i);
        if (active >= panes.Count) active = Math.Max(0, panes.Count - 1);
        RebuildGrid();
        if (panes.Count == 0) { Dismiss(true); return; } // era o último: o próximo Win+S abre o primeiro agente
        if (IsOut && direction == 0) Relayout();
        FocusActive();
    }

    void FocusPane(int delta)
    {
        if (panes.Count < 2) return;
        active = (active + delta + panes.Count) % panes.Count;
        UpdatePaneLook();
        FocusActive();
    }

    void FocusActive()
    {
        if (picker.IsOpen) Keyboard.Focus(picker);
        else if (panes.Count > 0) Keyboard.Focus(panes[active].View);
    }

    void RebuildGrid()
    {
        panesGrid.Children.Clear();
        panesGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < panes.Count; i++)
        {
            panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Border host = panes[i].Host;
            host.BorderBrush = SeparatorBrush;
            host.BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
            Grid.SetColumn(host, i);
            panesGrid.Children.Add(host);
        }
        UpdatePaneLook();
    }

    // O painel ativo fica nítido; os outros esbatidos, como a borda ativa do Omarchy.
    void UpdatePaneLook()
    {
        for (int i = 0; i < panes.Count; i++) panes[i].Host.Opacity = panes.Count > 1 && i != active ? 0.6 : 1.0;
    }

    // Bandeja > Nova sessão: recomeça o agente do painel ativo.
    public void NewSession()
    {
        if (panes.Count > 0) panes[active].Restart();
    }

    public void Shutdown()
    {
        quitting = true;
        foreach (Pane p in panes) p.Stop();
        StopRendering();
        dim.Close();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!quitting) { e.Cancel = true; Dismiss(true); } // Alt+F4 esconde
        base.OnClosing(e);
    }
}

// Película preta por cima do monitor: escurece o resto e, por omissão, um clique nela esconde o scratchpad.
sealed class DimOverlay : System.Windows.Forms.Form
{
    public event Action Clicked;

    public DimOverlay()
    {
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
        BackColor = System.Drawing.Color.Black;
        Opacity = 0;
        Text = Program.Name;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override System.Windows.Forms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x8; // TOOLWINDOW | NOACTIVATE | TOPMOST
            return cp;
        }
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        // Um clique na película não lhe dá o foco (o scratchpad esconde-se e o foco volta à app anterior).
        if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(System.Windows.Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var h = Clicked;
        if (h != null) h();
    }

    public void ShowOn(Win32.RECT b, bool catchClicks)
    {
        if (!Visible) { Opacity = 0; Show(); }
        int ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        ex = catchClicks ? ex & ~Win32.WS_EX_TRANSPARENT : ex | Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex);
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    public void SetLevel(double a)
    {
        if (Visible) Opacity = Math.Max(0, Math.Min(0.95, a));
    }

    public void HideNow()
    {
        if (Visible) Hide();
    }
}

static class Win32
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, WS_MAXIMIZEBOX = 0x10000, WS_MINIMIZEBOX = 0x20000, WS_EX_TRANSPARENT = 0x20;
    public const int WM_SYSCOMMAND = 0x112;
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, int flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT r, int flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, int rop);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
}
