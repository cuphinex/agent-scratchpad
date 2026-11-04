// Agent Scratchpad: Win+S mostra/esconde uma janela flutuante do Windows 11 (acrylic, sem bordas)
// com um agente de linha de comandos (por omissão, Claude Code), ao estilo do scratchpad do Omarchy.
// Compila com o csc.exe do .NET Framework 4.x (C# 5): ver build.ps1.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

// Liga os comportamentos do WPF 4.8 (DPI por monitor), que dependem da versão alvo.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

static class Program
{
    public const string Name = "Agent Scratchpad";
    const string MutexName = @"Local\AgentScratchpad";
    const string QuitEventName = @"Local\AgentScratchpadQuit";

    [STAThread]
    static void Main(string[] args)
    {
        using (var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName))
        {
            // "AgentScratchpad.exe --quit" pede à instância em execução que saia de forma limpa.
            if (args.Length > 0 && args[0] == "--quit") { quitEvent.Set(); return; }

            bool created;
            using (new Mutex(true, MutexName, out created))
            {
                if (!created) return;
                Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
                Forms.Application.EnableVisualStyles();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { Log.Write(e.Exception); e.Handled = true; };
                new ScratchpadHost(app, quitEvent);
                app.Run();
            }
        }
    }
}

sealed class ScratchpadHost
{
    readonly Application app;
    readonly ScratchWindow window;
    readonly Forms.NotifyIcon tray;
    readonly WinSHook hook = new WinSHook();
    readonly DispatcherTimer reload = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
    readonly FileSystemWatcher watcher;

    public ScratchpadHost(Application app, EventWaitHandle quitEvent)
    {
        this.app = app;
        window = new ScratchWindow(Config.Load());
        new WindowInteropHelper(window).EnsureHandle();
        window.Prewarm();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mostrar/esconder (Win+S)", null, delegate { window.Toggle(); });
        menu.Items.Add("Nova sessão (painel ativo)", null, delegate { window.NewSession(); });
        menu.Items.Add("Abrir configuração", null, delegate { OpenConfig(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, delegate { Quit(); });
        tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = Program.Name + " (Win+S)", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) window.Toggle(); };

        hook.RelayKey = window.RelayKey;
        hook.Pressed += () => window.Dispatcher.BeginInvoke(new Action(window.HotkeySeen));
        hook.Scratch = new WindowInteropHelper(window).Handle;
        hook.PaneAction += a => window.Dispatcher.BeginInvoke(new Action(() => window.PaneAction(a)));
        hook.Start();
        if (!hook.Installed) tray.ShowBalloonTip(5000, Program.Name, "Não consegui instalar o atalho Win+S.", Forms.ToolTipIcon.Error);

        // Gravar o config.ini aplica logo as alterações (o comando só na próxima sessão).
        reload.Tick += delegate { reload.Stop(); window.ApplyConfig(Config.Load()); };
        watcher = new FileSystemWatcher(Path.GetDirectoryName(Config.FilePath), "config.ini");
        FileSystemEventHandler changed = (s, e) => window.Dispatcher.BeginInvoke(new Action(() => { reload.Stop(); reload.Start(); }));
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (s, e) => changed(s, null);
        watcher.EnableRaisingEvents = true;

        ThreadPool.RegisterWaitForSingleObject(quitEvent, (s, timedOut) => app.Dispatcher.BeginInvoke(new Action(Quit)), null, -1, true);
    }

    void OpenConfig()
    {
        if (!File.Exists(Config.FilePath)) File.WriteAllText(Config.FilePath, "command=claude\r\nworkdir=C:\\\r\n");
        Process.Start("notepad.exe", "\"" + Config.FilePath + "\"");
    }

    void Quit()
    {
        watcher.EnableRaisingEvents = false;
        hook.Stop();
        window.Shutdown();
        tray.Visible = false;
        tray.Dispose();
        app.Shutdown();
    }

    static Icon MakeIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        using (var bg = new SolidBrush(Color.FromArgb(28, 28, 34)))
        using (var font = new Font("Consolas", 15, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.FillRectangle(bg, 1, 1, 30, 30);
            g.DrawString(">_", font, System.Drawing.Brushes.White, 3, 7);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}

sealed class Agent
{
    public string Name, Command, WorkDir;
}

sealed class Config
{
    public string Command = "claude", WorkDir = @"C:\", Edge = "top", Font = "Cascadia Mono, Consolas";
    public double Width = 0.70, Height = 0.70, WidthSplit = 0.90, Dim = 0.35, Tint = 0.35, FontSize = 12;
    public readonly System.Collections.Generic.List<Agent> Agents = new System.Collections.Generic.List<Agent>();
    public int Margin = 12, AnimationMs = 220, Padding = 14;
    public int Backdrop = 0, Corners = 2, Border = unchecked((int)0xFFFFFFFE); // blur (nosso), round, sem linha
    public bool HideOnBlur = true, TransparentBg = true;

    public static string FilePath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini"); }
    }

    public static Config Load()
    {
        var c = new Config();
        string[] lines;
        try { lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : new string[0]; }
        catch (IOException) { lines = new string[0]; } // ficheiro ainda a ser gravado: o watcher volta a tentar
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string val = line.Substring(eq + 1).Trim();
            int hash = val.IndexOf(" #", StringComparison.Ordinal); // comentário no fim da linha
            if (hash >= 0) val = val.Substring(0, hash).Trim();
            string low = val.ToLowerInvariant();
            switch (key)
            {
                case "command": if (val.Length > 0) c.Command = val; break;
                case "agent": // nome | comando
                    int bar = val.IndexOf('|');
                    string name = bar < 0 ? val : val.Substring(0, bar).Trim(), cmd = bar < 0 ? val : val.Substring(bar + 1).Trim();
                    if (cmd.Length > 0) c.Agents.Add(new Agent { Name = name.Length > 0 ? name : cmd, Command = cmd });
                    break;
                case "width_split": c.WidthSplit = Num(val, c.WidthSplit, 0.1, 1); break;
                case "workdir": if (val.Length > 0) c.WorkDir = Environment.ExpandEnvironmentVariables(val); break;
                case "edge": if (low == "top" || low == "bottom" || low == "left" || low == "right") c.Edge = low; break;
                case "font": if (val.Length > 0) c.Font = val; break;
                case "font_size": c.FontSize = Num(val, c.FontSize, 6, 48); break;
                case "width": c.Width = Num(val, c.Width, 0.1, 1); break;
                case "height": c.Height = Num(val, c.Height, 0.1, 1); break;
                case "dim": c.Dim = Num(val, c.Dim, 0, 0.9); break;
                case "tint": c.Tint = Num(val, c.Tint, 0, 1); break;
                case "margin": c.Margin = (int)Num(val, c.Margin, 0, 400); break;
                case "padding": c.Padding = (int)Num(val, c.Padding, 0, 100); break;
                case "animation_ms": c.AnimationMs = (int)Num(val, c.AnimationMs, 0, 3000); break;
                case "transparent_bg": c.TransparentBg = low != "off" && low != "false" && low != "0"; break;
                case "hide_on_blur": c.HideOnBlur = low == "true" || low == "1" || low == "yes" || low == "sim"; break;
                case "backdrop":
                    if (low == "blur") c.Backdrop = 0; else if (low == "acrylic") c.Backdrop = 3; else if (low == "mica") c.Backdrop = 2;
                    else if (low == "tabbed") c.Backdrop = 4; else if (low == "none") c.Backdrop = 1;
                    break;
                case "corners":
                    if (low == "round") c.Corners = 2; else if (low == "small") c.Corners = 3; else if (low == "square") c.Corners = 1;
                    break;
                case "border":
                    int rgb;
                    if (low == "none") c.Border = unchecked((int)0xFFFFFFFE);
                    else if (low == "default") c.Border = unchecked((int)0xFFFFFFFF);
                    else if (low.StartsWith("#") && int.TryParse(low.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                        c.Border = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF); // COLORREF = 0x00BBGGRR
                    break;
            }
        }
        // Sem linhas agent=, o command= é o único agente (configs antigas continuam a funcionar).
        if (c.Agents.Count == 0) c.Agents.Add(new Agent { Name = c.Command, Command = c.Command });
        foreach (Agent a in c.Agents) a.WorkDir = c.WorkDir;
        return c;
    }

    // Aceita "0.7" e "0,7" (o locale pt usa vírgula).
    static double Num(string s, double fallback, double min, double max)
    {
        double d;
        if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= min && d <= max)
            return d;
        return fallback;
    }
}

static class Log
{
    public static void Write(object what)
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scratchpad.log");
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + what + "\r\n");
        }
        catch (Exception) { }
    }
}
