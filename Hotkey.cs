// Win+S global: hook de teclado de baixo nível numa thread própria,
// para o hook nunca expirar quando a UI está ocupada a desenhar.
using System;
using System.Runtime.InteropServices;
using System.Threading;

sealed class WinSHook
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_QUIT = 0x12;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_S = 0x53, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const byte VK_MASK = 0xE8; // tecla não atribuída, usada só para "gastar" o Win
    const uint KEYEVENTF_KEYUP = 2;

    // Com RelayKey, o Win+S vira um Win+<RelayKey> injetado, que a janela regista com RegisterHotKey:
    // um WM_HOTKEY dá à app o direito de tomar o foco. Pressed avisa sempre (plano B se o WM_HOTKEY falhar).
    public byte RelayKey;
    public event Action Pressed; // chamado na thread do hook

    // Atalhos dos painéis (Win+Enter / Win+W / Win+← →): só quando o scratchpad está em primeiro plano;
    // fora dele estas teclas continuam a ser do Windows.
    public IntPtr Scratch;
    public event Action<string> PaneAction; // "open", "close", "left", "right" (thread do hook)
    uint swallowedVk;

    HookProc proc; // guardado num campo para o GC não o recolher
    IntPtr hook;
    uint threadId;
    bool swallowingS;

    public bool Installed { get { return hook != IntPtr.Zero; } }

    public void Start()
    {
        var ready = new ManualResetEvent(false);
        var thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            proc = OnKey;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            ready.Set();
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        });
        thread.IsBackground = true;
        thread.Name = "hotkey";
        thread.Start();
        ready.WaitOne();
    }

    public void Stop()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    // Engole o S do Win+S (o Windows Search não chega a vê-lo) e avisa a app.
    IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            string action = PaneActionFor(k.vkCode);
            if (action != null)
            {
                int msg = wParam.ToInt32();
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                if (down && (swallowedVk == k.vkCode || Scratch != IntPtr.Zero && IsBareWinHeld() && GetForegroundWindow() == Scratch))
                {
                    if (swallowedVk != k.vkCode) // ignora a auto-repetição
                    {
                        swallowedVk = k.vkCode;
                        keybd_event(VK_MASK, 0, 0, UIntPtr.Zero); // o Win largado depois não abre o menu Iniciar
                        keybd_event(VK_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        var h = PaneAction;
                        if (h != null) h(action);
                    }
                    return new IntPtr(1);
                }
                if (!down && swallowedVk == k.vkCode)
                {
                    swallowedVk = 0;
                    return new IntPtr(1);
                }
            }
            if (k.vkCode == VK_S)
            {
                int msg = wParam.ToInt32();
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                if (down && (swallowingS || IsBareWinHeld()))
                {
                    if (!swallowingS) // ignora a auto-repetição
                    {
                        swallowingS = true;
                        if (RelayKey != 0)
                        {
                            keybd_event(RelayKey, 0, 0, UIntPtr.Zero);
                            keybd_event(RelayKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        }
                        // A tecla do atalho é consumida pelo RegisterHotKey; sem esta tecla neutra o
                        // Windows achava que o Win foi premido sozinho e abria o menu Iniciar ao largá-lo.
                        keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        var h = Pressed;
                        if (h != null) h(); // em modo relay, a app só usa isto se o WM_HOTKEY não chegar
                    }
                    return new IntPtr(1);
                }
                if (!down && swallowingS)
                {
                    swallowingS = false;
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    static string PaneActionFor(uint vk)
    {
        switch (vk)
        {
            case 0x0D: return "open";  // Enter
            case 0x57: return "close"; // W
            case 0x25: return "left";
            case 0x27: return "right";
        }
        return null;
    }

    static bool IsBareWinHeld()
    {
        return (Held(VK_LWIN) || Held(VK_RWIN)) && !Held(VK_SHIFT) && !Held(VK_CONTROL) && !Held(VK_MENU);
    }

    static bool Held(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y, lPrivate; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
}
