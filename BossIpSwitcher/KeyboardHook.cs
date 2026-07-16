using System.Runtime.InteropServices;

namespace BossIpSwitcher;

internal sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13, WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    private const int VkControl = 0x11, VkMenu = 0x12, VkLwin = 0x5B, VkRwin = 0x5C, VkF10 = 0x79;
    private readonly HashSet<int> down = [];
    private readonly HookProc callback;
    private nint hook;
    private bool toggleLatched, menuLatched;
    public event Action? TogglePressed;
    public event Action? MenuPressed;

    public KeyboardHook() => callback = Handle;
    public void Start() => hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);

    private nint Handle(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var key = Marshal.ReadInt32(data);
            if (message == WmKeyDown || message == WmSysKeyDown) down.Add(key);
            else if (message == WmKeyUp || message == WmSysKeyUp) down.Remove(key);

            var ctrl = down.Contains(VkControl) || IsDown(0xA2) || IsDown(0xA3);
            var alt = down.Contains(VkMenu) || IsDown(0xA4) || IsDown(0xA5);
            var win = down.Contains(VkLwin) || down.Contains(VkRwin);
            var toggle = ctrl && alt && win;
            var menu = ctrl && alt && down.Contains(VkF10);
            if (toggle && !toggleLatched) { toggleLatched = true; TogglePressed?.Invoke(); }
            if (menu && !menuLatched) { menuLatched = true; MenuPressed?.Invoke(); }
            if (!toggle) toggleLatched = false;
            if (!menu) menuLatched = false;
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public void Dispose() { if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; } }
    private delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
