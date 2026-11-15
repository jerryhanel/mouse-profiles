using System.Collections.Concurrent;

namespace MouseProfiles;

/// <summary>Receives raw events while a capture dialog is open. Return true to swallow the event.</summary>
interface ICaptureSink
{
    bool OnKey(uint vk, bool down);
    bool OnMouseButton(MouseBtn button, bool down);
    bool OnWheel(bool up, bool injected);
    void OnHid(string devicePath, byte[] report);
    /// <summary>A left or right click arrived: never usable as a trigger, and always passed through.</summary>
    void OnIgnoredClick(string name);
}

enum TriggerKind { Button, ScrollUp, ScrollDown }

sealed record Trigger(Signal Signal, TriggerKind Kind, int Button);

/// <summary>
/// Decides what each incoming signal means: swallow it and emit the active preset's combo, or flip presets.
/// All On* methods run on the UI thread inside the low-level hooks, so they only match and queue.
/// </summary>
sealed class Engine : IDisposable
{
    readonly object gate = new();
    readonly HashSet<uint> down = new();               // keys currently held (modifier tracking)
    readonly HashSet<uint> swallowedKeys = new();      // keys whose down we swallowed, so we swallow the up too
    readonly Dictionary<uint, int> keyButton = new();  // swallowed key -> MK index that is holding an emission
    readonly HashSet<MouseBtn> swallowedMouse = new();
    readonly Dictionary<MouseBtn, int> mouseButton = new();
    readonly Emitter emitter;
    List<Trigger> triggers = new();

    public AppConfig Config { get; private set; } = new();
    public int Current { get; private set; }
    public bool Enabled { get; set; } = true;
    /// <summary>True while the settings window is open: events are logged but nothing is acted on.</summary>
    public bool Suspended { get; set; }
    public ICaptureSink? Capture { get; set; }

    public bool FeedOn { get; set; }
    public ConcurrentQueue<string> FeedQueue { get; } = new();

    /// <summary>Raised on the UI thread when the active preset changes (profile, index, count).</summary>
    public event Action<Profile, int, int>? ProfileChanged;

    /// <summary>Raised when a trigger fires and the button's preview flag is on. Args: signal description, output combo description.</summary>
    public event Action<string, string>? TriggerPreview;

    public Engine()
    {
        emitter = new Emitter(HeldModifierVks);
    }

    public void Apply(AppConfig cfg)
    {
        cfg.Normalize();
        emitter.ReleaseAll();
        lock (gate)
        {
            Config = cfg;
            emitter.UseScanCodes = cfg.UseScanCodes;
            var list = new List<Trigger>();
            for (int i = 0; i < cfg.Buttons.Count; i++)
                if (Signal.Parse(cfg.Buttons[i]) is { } s) list.Add(new Trigger(s, TriggerKind.Button, i));
            if (Signal.Parse(cfg.ScrollUp) is { } up) list.Add(new Trigger(up, TriggerKind.ScrollUp, -1));
            if (Signal.Parse(cfg.ScrollDown) is { } dn) list.Add(new Trigger(dn, TriggerKind.ScrollDown, -1));
            triggers = list;
            Current = cfg.Profiles.Count == 0 ? 0 : Math.Clamp(cfg.RememberLast ? cfg.LastProfile : 0, 0, cfg.Profiles.Count - 1);
            swallowedKeys.Clear(); keyButton.Clear(); swallowedMouse.Clear(); mouseButton.Clear();
        }
    }

    public bool WantsHid => triggers.Any(t => t.Signal.Kind == SignalKind.Hid);

    public Profile? CurrentProfile => Config.Profiles.Count == 0 ? null : Config.Profiles[Current];

    public void Select(int index, bool announce = true)
    {
        if (Config.Profiles.Count == 0) return;
        emitter.ReleaseAll(); // never leave an emitted key stuck across a preset change
        Current = Math.Clamp(index, 0, Config.Profiles.Count - 1);
        Config.LastProfile = Current;
        if (announce) ProfileChanged?.Invoke(Config.Profiles[Current], Current, Config.Profiles.Count);
    }

    public void Step(int delta) =>
        Select(ProfileCycler.Step(Current, Config.Profiles.Count, delta, Config.WrapAround));

    public void Log(string line)
    {
        if (!FeedOn) return;
        FeedQueue.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (FeedQueue.Count > 2000) FeedQueue.TryDequeue(out _);
    }

    uint[] HeldModifierVks()
    {
        lock (gate) return down.Where(KeyNames.IsModifier).ToArray();
    }

    public Mods HeldMods()
    {
        lock (gate)
        {
            // Drop modifiers the OS says are up, in case we missed a key-up (lock screen, Alt+Tab, ...).
            // Skip this cleanup during capture: swallowed keys don't update GetAsyncKeyState.
            if (Capture == null)
                down.RemoveWhere(v => KeyNames.IsModifier(v) && (Native.GetAsyncKeyState((int)v) & 0x8000) == 0);
            var mods = down.Aggregate(Mods.None, (a, v) => a | KeyNames.ModOf(v));
            // Fallback: if the keyboard hook is dead, modifiers aren't in `down` but GetAsyncKeyState
            // still sees them (since they weren't swallowed). Safe to OR in all cases.
            if ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0) mods |= Mods.Ctrl;
            if ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0) mods |= Mods.Shift;
            if ((Native.GetAsyncKeyState(0x12) & 0x8000) != 0) mods |= Mods.Alt;
            if ((Native.GetAsyncKeyState(0x5B) & 0x8000) != 0) mods |= Mods.Win;
            return mods;
        }
    }

    // ---- hook entry points -------------------------------------------------------------------

    public bool OnKey(uint vk, uint scan, uint flags, bool isDown, ulong extra)
    {
        if (extra == Emitter.OwnTag) return false;
        bool injected = (flags & 0x10) != 0;
        if (FeedOn) Log($"HOOK key   {KeyNames.Name(vk),-12} {(isDown ? "down" : "up  ")} scan=0x{scan:X2}{(injected ? "  [injected by software]" : "")}");

        lock (gate) { if (isDown) down.Add(vk); else down.Remove(vk); }

        if (Capture != null) return Capture.OnKey(vk, isDown);
        if (!Enabled) return false;

        if (!isDown)
        {
            if (!swallowedKeys.Remove(vk)) return false;
            if (!Suspended && keyButton.Remove(vk, out int mk)) emitter.Release(mk);
            return true;
        }
        if (swallowedKeys.Contains(vk)) return true; // auto-repeat of a swallowed key
        if (KeyNames.IsModifier(vk)) return false;

        var mods = HeldMods();
        foreach (var t in triggers)
        {
            if (t.Signal.Kind != SignalKind.Key || t.Signal.Vk != vk || t.Signal.Mods != mods) continue;
            swallowedKeys.Add(vk);
            if (!Suspended && Fire(t, hold: true)) keyButton[vk] = t.Button;
            return true;
        }
        return false;
    }

    public bool OnMouse(int msg, uint mouseData, uint flags, ulong extra)
    {
        if (extra == Emitter.OwnTag) return false;
        bool injected = (flags & 0x1) != 0;
        string inj = injected ? "  [injected by software]" : "";

        MouseBtn? button = null; bool isDown = false;
        switch (msg)
        {
            case Native.WM_MBUTTONDOWN: case Native.WM_MBUTTONUP:
                button = MouseBtn.Middle; isDown = msg == Native.WM_MBUTTONDOWN; break;
            case Native.WM_XBUTTONDOWN: case Native.WM_XBUTTONUP:
                button = (mouseData >> 16) == 2 ? MouseBtn.X2 : MouseBtn.X1; isDown = msg == Native.WM_XBUTTONDOWN; break;
            case Native.WM_MOUSEWHEEL:
                break;
            default:
                if (Capture != null && msg is 0x201 or 0x204) Capture.OnIgnoredClick(msg == 0x201 ? "left" : "right");
                return false; // moves and left/right clicks are never triggers
        }

        if (button is { } b)
        {
            if (FeedOn) Log($"HOOK mouse  {b,-12} {(isDown ? "down" : "up  ")}{inj}");
            if (Capture != null) return Capture.OnMouseButton(b, isDown);
            if (!Enabled) return false;
            if (!isDown)
            {
                if (!swallowedMouse.Remove(b)) return false;
                if (!Suspended && mouseButton.Remove(b, out int mk)) emitter.Release(mk);
                return true;
            }
            if (swallowedMouse.Contains(b)) return true;
            var mods = HeldMods();
            foreach (var t in triggers)
            {
                if (t.Signal.Kind != SignalKind.Mouse || t.Signal.Button != b || t.Signal.Mods != mods) continue;
                swallowedMouse.Add(b);
                if (!Suspended && Fire(t, hold: true)) mouseButton[b] = t.Button;
                return true;
            }
            return false;
        }

        bool up = (short)(mouseData >> 16) > 0;
        if (FeedOn) Log($"HOOK wheel  {(up ? "up" : "down"),-12}{inj}");
        if (Capture != null) return Capture.OnWheel(up, injected);
        if (!Enabled) return false;
        var held = HeldMods();
        foreach (var t in triggers)
        {
            if (t.Signal.Kind != SignalKind.Wheel || t.Signal.WheelUp != up || t.Signal.Mods != held) continue;
            if (t.Signal.InjectedOnly && !injected) continue;
            if (!Suspended) Fire(t, hold: false);
            return true;
        }
        return false;
    }

    /// <summary>A vendor HID report from Raw Input. These can be acted on but never blocked.</summary>
    public void OnHid(string devicePath, byte[] report)
    {
        if (Capture != null) { Capture.OnHid(devicePath, report); return; }
        if (!Enabled || Suspended) return;
        foreach (var t in triggers)
            if (t.Signal.HidMatches(devicePath, report)) Fire(t, hold: false);
    }

    // ---- actions ------------------------------------------------------------------------------

    /// <summary>Runs a trigger. Returns true if a combo is being held (so the caller must release it later).</summary>
    bool Fire(Trigger t, bool hold)
    {
        switch (t.Kind)
        {
            case TriggerKind.ScrollUp: Step(-1); return false;
            case TriggerKind.ScrollDown: Step(+1); return false;
        }
        var p = CurrentProfile;
        var combo = p != null && t.Button < p.Outputs.Count ? KeyCombo.Parse(p.Outputs[t.Button]) : null;
        if (p != null && t.Button < p.Preview.Count && p.Preview[t.Button])
            TriggerPreview?.Invoke(t.Signal.Describe(), combo?.ToString() ?? "(nothing)");
        if (combo == null) return false;
        if (hold) { emitter.Press(t.Button, combo); return true; }
        emitter.Tap(t.Button, combo);
        return false;
    }

    public void Dispose() => emitter.Dispose();
}
