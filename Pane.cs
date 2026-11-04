// Um painel do scratchpad (um agente no seu próprio terminal) e o menu para escolher o agente.
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

sealed class Pane
{
    public readonly TerminalView View = new TerminalView();
    public readonly Border Host = new Border(); // à volta da vista: linha separadora e opacidade
    public readonly Agent Agent;
    public event Action<Pane> Exited; // na thread da UI, quando o agente termina

    Terminal term;
    PseudoConsole pty;

    public Pane(Agent agent, Config cfg)
    {
        Agent = agent;
        Host.Child = View;
        Host.Background = Brushes.Transparent; // para os cliques em toda a área contarem
        View.Send = s => { PseudoConsole p = pty; if (p != null) p.Write(s); };
        View.GridChanged += (cols, rows) =>
        {
            if (term != null) term.Resize(cols, rows);
            if (pty != null) pty.Resize(cols, rows);
        };
        ApplyConfig(cfg);
    }

    public void ApplyConfig(Config cfg)
    {
        View.SetFont(cfg.Font, cfg.FontSize);
        View.Margin = new Thickness(cfg.Padding);
        View.AutoClearBg = cfg.TransparentBg;
        View.Invalidate();
    }

    public bool Running { get { return pty != null; } }

    public void Start()
    {
        if (pty != null) return;
        var t = new Terminal(View.GridCols, View.GridRows);
        var p = new PseudoConsole();
        t.Reply += s => p.Write(s);
        p.Output += (buf, n) => t.Feed(buf, n);
        p.Exited += () => View.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (p != pty) return;
            Stop();
            var h = Exited;
            if (h != null) h(this);
        }));
        term = t;
        View.Term = t;
        try
        {
            p.Start(Agent.Command, Agent.WorkDir, View.GridCols, View.GridRows);
            pty = p;
        }
        catch (Exception ex)
        {
            Log.Write("start '" + Agent.Command + "': " + ex);
            byte[] msg = Encoding.UTF8.GetBytes("\r\n  Não consegui arrancar " + Agent.Name + " (\"" + Agent.Command + "\"): " + ex.Message +
                "\r\n  Está instalado? Confirma a linha agent= no config.ini. Win+W fecha este painel.\r\n");
            t.Feed(msg, msg.Length);
        }
    }

    public void Stop()
    {
        PseudoConsole p = pty;
        pty = null;
        if (p != null) p.Dispose();
    }

    public void Restart()
    {
        Stop();
        Start();
    }
}

// Lista curta dos agentes do config.ini: 1–9 ou setas + Enter escolhem, Esc cancela.
sealed class AgentPicker : Border
{
    public event Action<Agent> Picked;
    public event Action Cancelled;

    // Com o mesmo aspeto do terminal: a letra e o tamanho dos painéis, uma caixa de linha fina e
    // cantos redondos (como a caixa de input do Claude Code), e a opção escolhida a lilás com "›".
    static readonly Brush Text = Gray(0xE6), Dim = Gray(0x8A), Faint = Gray(0x5C);
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xB1, 0xB9, 0xF9));

    readonly StackPanel list = new StackPanel();
    readonly TextBlock title, footer;
    readonly List<Run[]> rows = new List<Run[]>(); // marcador, número, nome
    List<Agent> agents = new List<Agent>();
    FontFamily font = new FontFamily("Cascadia Mono, Consolas");
    double fontSize = 16;
    int selected;

    public AgentPicker()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Padding = new Thickness(18, 12, 22, 12);
        CornerRadius = new CornerRadius(6);
        Background = new SolidColorBrush(Color.FromArgb(0xEC, 0x17, 0x17, 0x19));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x88, 0x88, 0x88));
        BorderThickness = new Thickness(1);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        title = new TextBlock { Text = "abrir agente ao lado", Foreground = Dim, Margin = new Thickness(0, 0, 0, 8) };
        footer = new TextBlock { Text = "1–9 escolher · ↑↓ enter · esc", Foreground = Faint, Margin = new Thickness(0, 8, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(list);
        stack.Children.Add(footer);
        Child = stack;
    }

    static Brush Gray(byte v)
    {
        var b = new SolidColorBrush(Color.FromRgb(v, v, v));
        b.Freeze();
        return b;
    }

    // A mesma letra dos painéis (a primeira da lista que existir) e o mesmo tamanho.
    public void ApplyConfig(Config cfg)
    {
        font = new FontFamily(cfg.Font);
        fontSize = Math.Max(6, cfg.FontSize) * 96.0 / 72.0;
        TextElement.SetFontFamily(this, font);
        TextElement.SetFontSize(this, fontSize);
    }

    public bool IsOpen { get { return Visibility == Visibility.Visible; } }

    public void Open(List<Agent> available)
    {
        agents = available;
        selected = 0;
        list.Children.Clear();
        rows.Clear();
        for (int i = 0; i < agents.Count && i < 9; i++)
        {
            var row = new Run[] { new Run("  "), new Run((i + 1) + "  ") { Foreground = Faint }, new Run(agents[i].Name) };
            var line = new TextBlock { Margin = new Thickness(0, 1, 0, 1) };
            line.Inlines.AddRange(row);
            rows.Add(row);
            list.Children.Add(line);
        }
        Highlight();
        Visibility = Visibility.Visible;
        Keyboard.Focus(this);
    }

    public void Close() { Visibility = Visibility.Collapsed; }

    void Highlight()
    {
        for (int i = 0; i < rows.Count; i++)
        {
            bool on = i == selected;
            rows[i][0].Text = on ? "› " : "  ";
            rows[i][0].Foreground = Accent;
            rows[i][2].Foreground = on ? Accent : Text;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        e.Handled = true;
        int digit = e.Key >= Key.D1 && e.Key <= Key.D9 ? e.Key - Key.D1 : e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        if (digit >= 0 && digit < rows.Count) { Choose(digit); return; }
        switch (e.Key)
        {
            case Key.Up: selected = (selected + rows.Count - 1) % rows.Count; Highlight(); break;
            case Key.Down: case Key.Tab: selected = (selected + 1) % rows.Count; Highlight(); break;
            case Key.Enter: Choose(selected); break;
            case Key.Escape: Close(); var c = Cancelled; if (c != null) c(); break;
        }
    }

    // Não deixa o texto escrito com o menu aberto ir para o agente por baixo.
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        e.Handled = true;
    }

    void Choose(int i)
    {
        Close();
        var h = Picked;
        if (h != null) h(agents[i]);
    }
}
