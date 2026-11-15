using System.Runtime.InteropServices;

namespace MouseProfiles;

/// <summary>
/// Global low-level keyboard and mouse hooks. Windows silently drops hooks whose callbacks are slow,
/// so the callbacks only hand events to the engine, which matches and queues. The delegates are held in
/// fields so the garbage collector can't free them while Windows still holds the pointers.
/// </summary>
sealed class InputHooks : IDisposable
{
    readonly Engine engine;
    readonly Native.HookProc keyProc;
    readonly Native.HookProc mouseProc;
    readonly System.Windows.Forms.Timer healthTimer = new() { Interval = 2000 };
    IntPtr keyHook, mouseHook;
    int keyCallbackCount;

    public InputHooks(Engine engine)
    {
        this.engine = engine;
        keyProc = KeyCallback;
        mouseProc = MouseCallback;
        Install();
        healthTimer.Tick += (_, _) => CheckHealth();
        healthTimer.Start();
    }

    void Install()
    {
        var module = Native.GetModuleHandle(null);
        if (keyHook == IntPtr.Zero)
            keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, module, 0);
        if (mouseHook == IntPtr.Zero)
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
    }

    void CheckHealth()
    {
        // If any key is physically held but we got zero hook callbacks since last check, the hook is dead.
        bool anyKeyDown = false;
        for (int vk = 1; vk < 256; vk++)
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) { anyKeyDown = true; break; }
        if (anyKeyDown && Interlocked.Exchange(ref keyCallbackCount, 0) == 0)
        {
            engine.Log("HEALTH  keyboard hook appears dead - reinstalling");
            if (keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
            Install();
        }
        else
        {
            Interlocked.Exchange(ref keyCallbackCount, 0);
        }
    }

    /// <summary>Reinstall hooks now (call before opening capture dialogs).</summary>
    public void Reinstall()
    {
        if (keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
        if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        Install();
        engine.Log("HEALTH  hooks reinstalled");
    }

    IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Increment(ref keyCallbackCount);
            try
            {
                var s = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                int msg = wParam.ToInt32();
                bool isDown = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                if (engine.OnKey(s.vkCode, s.scanCode, s.flags, isDown, s.dwExtraInfo)) return (IntPtr)1;
            }
            catch { /* a hook must never throw into Windows */ }
        }
        return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
    }

    IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var s = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                if (engine.OnMouse(wParam.ToInt32(), s.mouseData, s.flags, s.dwExtraInfo)) return (IntPtr)1;
            }
            catch { }
        }
        return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        healthTimer.Stop();
        healthTimer.Dispose();
        if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        keyHook = mouseHook = IntPtr.Zero;
    }
}
