using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    private const int HOTKEY_ID = 1001;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_Q = 0x51;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;
    private static readonly nint HWND_MESSAGE = new(-3);

    private static nint _hwnd;
    private static uint _threadId;
    private static WndProcDelegate? _wndProcDelegate;
    private static string? _className;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private static int Main()
    {
        Console.WriteLine("[HotkeyReceiver] Starting...");

        Thread loopThread = new(MessageLoop)
        {
            IsBackground = true,
            Name = "HotkeyMessageLoop",
        };
        loopThread.SetApartmentState(ApartmentState.STA);
        loopThread.Start();

        while (_threadId == 0)
        {
            Thread.Sleep(10);
        }

        Console.WriteLine("[HotkeyReceiver] Press Enter to quit.");
        Console.WriteLine("[HotkeyReceiver] Waiting for Ctrl+Alt+Q ...");
        Console.ReadLine();

        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, nint.Zero, nint.Zero);
        }

        loopThread.Join();
        Console.WriteLine("[HotkeyReceiver] Stopped.");
        return 0;
    }

    private static void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _wndProcDelegate = WndProc;

        nint hInstance = GetModuleHandle(null);
        _className = $"ImmAiPen_Hotkey_{Environment.ProcessId}";

        WNDCLASSEX wc = new()
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = _className,
        };

        ushort atom = RegisterClassEx(ref wc);
        if (atom == 0)
        {
            Console.WriteLine($"[HotkeyReceiver] RegisterClassEx failed, err={Marshal.GetLastWin32Error()}");
            return;
        }

        _hwnd = CreateWindowEx(
            0,
            _className,
            null,
            0,
            0,
            0,
            0,
            0,
            HWND_MESSAGE,
            nint.Zero,
            hInstance,
            nint.Zero);

        if (_hwnd == 0)
        {
            Console.WriteLine($"[HotkeyReceiver] CreateWindowEx failed, err={Marshal.GetLastWin32Error()}");
            UnregisterClass(_className, hInstance);
            return;
        }

        if (!RegisterHotKey(_hwnd, HOTKEY_ID, MOD_ALT | MOD_CONTROL, VK_Q))
        {
            Console.WriteLine($"[HotkeyReceiver] RegisterHotKey failed, err={Marshal.GetLastWin32Error()}");
            DestroyWindow(_hwnd);
            _hwnd = 0;
            UnregisterClass(_className, hInstance);
            return;
        }

        Console.WriteLine("[HotkeyReceiver] Ctrl+Alt+Q registered.");

        while (GetMessage(out MSG msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnregisterHotKey(_hwnd, HOTKEY_ID);
        DestroyWindow(_hwnd);
        _hwnd = 0;
        UnregisterClass(_className, hInstance);
    }

    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY && wParam == HOTKEY_ID)
        {
            string now = DateTime.Now.ToString("HH:mm:ss.fff");
            Console.WriteLine($"[HotkeyReceiver] {now} Ctrl+Alt+Q fired");
            return 0;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }
}
