using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MouseProfiles;

/// <summary>
/// Sends keyboard combos with SendInput. Work is queued to one worker thread so the low-level hooks
/// can return immediately. A combo mirrors the mouse button: keys go down when the button goes down
/// and come up when it is released, so holding the button holds the keys.
/// </summary>
sealed class Emitter : IDisposable
{
    /// <summary>Stamped on every injected event so the hooks can ignore their own output.</summary>
    public const ulong OwnTag = 0x4D50524F;

    public bool UseScanCodes { get; set; } = true;

    sealed class Active
    {
        public KeyCombo Combo = null!;
        public List<uint> PressedMods = new();
        public List<uint> ReleasedMods = new();
    }

    readonly BlockingCollection<Action> queue = new();
    readonly Thread worker;
    readonly Func<uint[]> heldModifiers;
    readonly Dictionary<int, Active> active = new(); // worker thread only

    public Emitter(Func<uint[]> heldModifiers)
    {
        this.heldModifiers = heldModifiers;
        worker = new Thread(Run) { IsBackground = true, Name = "MouseProfiles.Emitter" };
        worker.Start();
    }

    void Run()
    {
        foreach (var job in queue.GetConsumingEnumerable())
        {
            try { job(); } catch { /* never let one bad send kill the worker */ }
        }
    }

    public void Press(int id, KeyCombo combo) => queue.Add(() => DoPress(id, combo));
    public void Release(int id) => queue.Add(() => DoRelease(id));
    public void Tap(int id, KeyCombo combo) => queue.Add(() => { DoPress(id, combo); DoRelease(id); });
    public void ReleaseAll() => queue.Add(() => { foreach (var id in active.Keys.ToList()) DoRelease(id); });

    void DoPress(int id, KeyCombo combo)
    {
        DoRelease(id);
        var held = heldModifiers();
        var heldMods = held.Aggregate(Mods.None, (a, v) => a | KeyNames.ModOf(v));

        var a = new Active { Combo = combo };
        // Modifiers the user is physically holding that the combo doesn't want would turn Ctrl+V into
        // Ctrl+Shift+V, so lift them for the duration and put them back afterwards.
        a.ReleasedMods = held.Where(v => (KeyNames.ModOf(v) & ~combo.Mods) != 0).ToList();
        a.PressedMods = KeyNames.VksFor(combo.Mods & ~heldMods).ToList();
        active[id] = a;

        var events = new List<(uint vk, bool up)>();
        events.AddRange(a.ReleasedMods.Select(v => (v, true)));
        events.AddRange(a.PressedMods.Select(v => (v, false)));
        events.AddRange(combo.Keys.Select(v => (v, false)));
        Send(events);
    }

    void DoRelease(int id)
    {
        if (!active.Remove(id, out var a)) return;
        var events = new List<(uint vk, bool up)>();
        events.AddRange(a.Combo.Keys.Reverse().Select(v => (v, true)));
        events.AddRange(a.PressedMods.AsEnumerable().Reverse().Select(v => (v, true)));
        // Modifiers lifted in DoPress are deliberately not pressed again: if the user's real key-up lands
        // in between, a re-press would leave the modifier stuck down, while a lifted one is harmless.
        Send(events);
    }

    void Send(List<(uint vk, bool up)> events)
    {
        if (events.Count == 0) return;
        var inputs = new Native.INPUT[events.Count];
        for (int i = 0; i < events.Count; i++) inputs[i] = Build(events[i].vk, events[i].up);
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    Native.INPUT Build(uint vk, bool up)
    {
        var ki = new Native.KEYBDINPUT { dwExtraInfo = (UIntPtr)OwnTag };
        uint scan = UseScanCodes ? Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC_EX) : 0;
        if (scan != 0)
        {
            ki.wScan = (ushort)(scan & 0xFF);
            ki.dwFlags = Native.KEYEVENTF_SCANCODE | ((scan & 0xFF00) == 0xE000 ? Native.KEYEVENTF_EXTENDEDKEY : 0);
        }
        else
        {
            ki.wVk = (ushort)vk;
            if (IsExtendedVk(vk)) ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;
        }
        if (up) ki.dwFlags |= Native.KEYEVENTF_KEYUP;
        return new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.InputUnion { ki = ki } };
    }

    static bool IsExtendedVk(uint vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90 or 0xA3 or 0xA5;

    public void Dispose()
    {
        ReleaseAll();
        queue.CompleteAdding();
        worker.Join(500);
    }
}
