// ConPTY: corre o agente numa consola virtual e liga a entrada/saída dela à app.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

sealed class PseudoConsole : IDisposable
{
    public event Action<byte[], int> Output; // na thread de leitura; o buffer é reutilizado a seguir
    public event Action Exited;              // numa thread própria, quando o agente termina

    IntPtr hpc, process, thread;
    FileStream input, output;
    readonly object writeLock = new object();
    int disposed;

    public int ProcessId { get; private set; }

    public void Start(string commandLine, string workDir, int cols, int rows)
    {
        try { StartCore(commandLine, workDir, cols, rows); }
        catch { Dispose(); throw; }
    }

    void StartCore(string commandLine, string workDir, int cols, int rows)
    {
        SafeFileHandle inRead, inWrite, outRead, outWrite;
        if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        input = new FileStream(inWrite, FileAccess.Write, 4096, false);
        output = new FileStream(outRead, FileAccess.Read, 4096, false);

        int hr = CreatePseudoConsole(new COORD(cols, rows), inRead, outWrite, 0, out hpc);
        // A consola duplicou estes lados dos pipes; os nossos já não fazem falta.
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
        // Handles vazios: sem isto o agente herdaria os nossos (se redirecionados) em vez da consola virtual.
        si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        si.lpAttributeList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(si.lpAttributeList, 0, new IntPtr(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE),
                    hpc, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            // PATH do registo (sistema + utilizador): um agente instalado depois de a app arrancar
            // (ex.: o grok, que se acrescenta ao PATH do utilizador) é encontrado sem a reiniciar.
            string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
            string user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            if (!string.IsNullOrEmpty(machine))
                Environment.SetEnvironmentVariable("Path", machine + (string.IsNullOrEmpty(user) ? "" : ";" + user));

            PROCESS_INFORMATION pi;
            string dir = Directory.Exists(workDir) ? workDir : null;
            if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, dir, ref si, out pi))
                throw new Win32Exception();
            process = pi.hProcess;
            thread = pi.hThread;
            ProcessId = pi.dwProcessId;
        }
        finally
        {
            DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
        }

        new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty-wait" }.Start();
    }

    void ReadLoop()
    {
        var buf = new byte[16384];
        try
        {
            while (true)
            {
                int n = output.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                var h = Output;
                if (h != null) h(buf, n);
            }
        }
        catch (Exception) { } // pipe fechado
    }

    void WaitLoop()
    {
        WaitForSingleObject(process, INFINITE);
        CloseHandle(thread);
        CloseHandle(process);
        var h = Exited;
        if (h != null && disposed == 0) h();
    }

    public void Write(string s) { Write(Encoding.UTF8.GetBytes(s)); }

    public void Write(byte[] data)
    {
        lock (writeLock)
        {
            if (input == null) return;
            try { input.Write(data, 0, data.Length); input.Flush(); }
            catch (Exception) { }
        }
    }

    public void Resize(int cols, int rows)
    {
        if (hpc != IntPtr.Zero) ResizePseudoConsole(hpc, new COORD(cols, rows));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (writeLock)
        {
            if (input != null) { try { input.Dispose(); } catch (Exception) { } input = null; }
        }
        IntPtr h = hpc;
        hpc = IntPtr.Zero;
        FileStream o = output;
        // Fechar a consola termina o agente. Pode bloquear até a saída ser lida, por isso
        // corre fora da UI; a thread de leitura continua a esvaziá-la entretanto.
        ThreadPool.QueueUserWorkItem(delegate
        {
            if (h != IntPtr.Zero) ClosePseudoConsole(h);
            if (o != null) { try { o.Dispose(); } catch (Exception) { } }
        });
    }

    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, INFINITE = 0xFFFFFFFF;
    const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016, STARTF_USESTDHANDLES = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    struct COORD
    {
        public short X, Y;
        public COORD(int x, int y) { X = (short)Math.Max(1, x); Y = (short)Math.Max(1, y); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr sa, int size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr hpc, COORD size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
