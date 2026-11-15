namespace MouseProfiles;

[Flags]
public enum Mods { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

public enum SignalKind { Key, Mouse, Wheel, Hid }

public enum MouseBtn { Middle, X1, X2 }

public static class KeyNames
{
    public static bool IsModifier(uint vk) => ModOf(vk) != Mods.None;

    public static Mods ModOf(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => Mods.Shift,
        0x11 or 0xA2 or 0xA3 => Mods.Ctrl,
        0x12 or 0xA4 or 0xA5 => Mods.Alt,
        0x5B or 0x5C => Mods.Win,
        _ => Mods.None,
    };

    /// <summary>The left-hand virtual keys for each modifier in the flag set, in a stable order.</summary>
    public static uint[] VksFor(Mods m)
    {
        var list = new List<uint>();
        if (m.HasFlag(Mods.Ctrl)) list.Add(0xA2);
        if (m.HasFlag(Mods.Shift)) list.Add(0xA0);
        if (m.HasFlag(Mods.Alt)) list.Add(0xA4);
        if (m.HasFlag(Mods.Win)) list.Add(0x5B);
        return list.ToArray();
    }

    public static string Name(uint vk)
    {
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        var key = (Keys)vk;
        return Enum.IsDefined(key) ? key.ToString() : $"VK_{vk:X2}";
    }

    static readonly Dictionary<string, Keys> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "MediaPlayPause", Keys.MediaPlayPause },
        { "MediaPlay", Keys.MediaPlayPause },
        { "MediaPause", Keys.MediaPlayPause },
        { "PlayPause", Keys.MediaPlayPause },
        { "MediaNext", Keys.MediaNextTrack },
        { "MediaPlayNext", Keys.MediaNextTrack },
        { "MediaNextTrack", Keys.MediaNextTrack },
        { "NextTrack", Keys.MediaNextTrack },
        { "MediaPrev", Keys.MediaPreviousTrack },
        { "MediaPrevious", Keys.MediaPreviousTrack },
        { "MediaPreviousTrack", Keys.MediaPreviousTrack },
        { "PrevTrack", Keys.MediaPreviousTrack },
        { "MediaStop", Keys.MediaStop },
        { "VolumeMute", Keys.VolumeMute },
        { "Mute", Keys.VolumeMute },
        { "VolumeUp", Keys.VolumeUp },
        { "VolumeDown", Keys.VolumeDown },
        { "BrowserBack", Keys.BrowserBack },
        { "BrowserForward", Keys.BrowserForward },
    };

    public static bool TryParse(string name, out uint vk)
    {
        vk = 0;
        name = name.Trim();
        if (name.Length == 0) return false;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z') { vk = c; return true; }
        }
        if (name.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(name.AsSpan(3), System.Globalization.NumberStyles.HexNumber, null, out vk))
            return true;
        if (Aliases.TryGetValue(name, out var aliased)) { vk = (uint)aliased; return true; }
        if (name.Contains(',') || char.IsDigit(name[0])) return false;
        if (Enum.TryParse<Keys>(name, true, out var k) && Enum.IsDefined(k)) { vk = (uint)k; return true; }
        return false;
    }

    public static string ModsText(Mods m)
    {
        var parts = new List<string>();
        if (m.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(Mods.Shift)) parts.Add("Shift");
        if (m.HasFlag(Mods.Alt)) parts.Add("Alt");
        if (m.HasFlag(Mods.Win)) parts.Add("Win");
        return string.Join("+", parts);
    }

    public static bool TryParseMod(string token, out Mods m)
    {
        m = token.Trim().ToLowerInvariant() switch
        {
            "ctrl" or "control" => Mods.Ctrl,
            "shift" => Mods.Shift,
            "alt" => Mods.Alt,
            "win" or "windows" => Mods.Win,
            _ => Mods.None,
        };
        return m != Mods.None;
    }
}

/// <summary>A keyboard combo to emit: modifiers plus zero or more regular keys, e.g. "Ctrl+V".</summary>
public sealed record KeyCombo(Mods Mods, uint[] Keys)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Mods != Mods.None) parts.Add(KeyNames.ModsText(Mods));
        parts.AddRange(Keys.Select(KeyNames.Name));
        return string.Join("+", parts);
    }

    public static KeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = Mods.None;
        var keys = new List<uint>();
        foreach (var token in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (KeyNames.TryParseMod(token, out var m)) mods |= m;
            else if (KeyNames.TryParse(token, out var vk)) keys.Add(vk);
            else return null;
        }
        return mods == Mods.None && keys.Count == 0 ? null : new KeyCombo(mods, keys.ToArray());
    }
}

/// <summary>
/// One input signal the mouse can send. Stored in the config as text:
/// "Key:Ctrl+F13", "Mouse:Ctrl+XButton1", "Wheel:Ctrl+Up", "Hid:&lt;device path&gt;|&lt;hex report&gt;".
/// </summary>
public sealed class Signal
{
    public SignalKind Kind { get; private init; }
    public Mods Mods { get; private init; }
    public uint Vk { get; private init; }
    public MouseBtn Button { get; private init; }
    public bool WheelUp { get; private init; }
    public bool InjectedOnly { get; private init; }
    public string DevicePath { get; private init; } = "";
    public byte[] Report { get; private init; } = Array.Empty<byte>();

    /// <summary>Hid signals arrive as raw reports, never as input messages, so they can't be blocked.</summary>
    public bool CanSwallow => Kind != SignalKind.Hid;

    public static Signal ForKey(Mods mods, uint vk) => new() { Kind = SignalKind.Key, Mods = mods, Vk = vk };
    public static Signal ForMouse(Mods mods, MouseBtn b) => new() { Kind = SignalKind.Mouse, Mods = mods, Button = b };
    /// <summary>
    /// injectedOnly: matches only wheel ticks generated by software (a mouse driver), never the physical wheel,
    /// which is what lets a button that sends a bare wheel tick be used without breaking normal scrolling.
    /// </summary>
    public static Signal ForWheel(Mods mods, bool up, bool injectedOnly = false) =>
        new() { Kind = SignalKind.Wheel, Mods = mods, WheelUp = up, InjectedOnly = injectedOnly };
    public static Signal ForHid(string path, byte[] report) => new() { Kind = SignalKind.Hid, DevicePath = path, Report = report };

    public bool HidMatches(string path, byte[] report) =>
        Kind == SignalKind.Hid && string.Equals(DevicePath, path, StringComparison.OrdinalIgnoreCase) &&
        Report.AsSpan().SequenceEqual(report);

    public override string ToString()
    {
        string m = Mods == Mods.None ? "" : KeyNames.ModsText(Mods) + "+";
        return Kind switch
        {
            SignalKind.Key => $"Key:{m}{KeyNames.Name(Vk)}",
            SignalKind.Mouse => $"Mouse:{m}{ButtonName(Button)}",
            SignalKind.Wheel => $"Wheel:{m}{(InjectedOnly ? "Injected+" : "")}{(WheelUp ? "Up" : "Down")}",
            _ => $"Hid:{DevicePath}|{Convert.ToHexString(Report)}",
        };
    }

    /// <summary>Friendly text for the UI.</summary>
    public string Describe()
    {
        string m = Mods == Mods.None ? "" : KeyNames.ModsText(Mods) + "+";
        return Kind switch
        {
            SignalKind.Key => m + KeyNames.Name(Vk),
            SignalKind.Mouse => $"{m}Mouse {ButtonName(Button)}",
            SignalKind.Wheel => InjectedOnly
                ? $"{m}Wheel {(WheelUp ? "up" : "down")} sent by mouse software (not your physical wheel)"
                : $"{m}Wheel {(WheelUp ? "up" : "down")}",
            _ => $"HID report {Convert.ToHexString(Report)} ({ShortDevice(DevicePath)}) - cannot be blocked",
        };
    }

    public static string ShortDevice(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(path, @"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Value : path;
    }

    static string ButtonName(MouseBtn b) => b switch { MouseBtn.X1 => "XButton1", MouseBtn.X2 => "XButton2", _ => "Middle" };

    static bool TryButton(string s, out MouseBtn b)
    {
        switch (s.ToLowerInvariant())
        {
            case "middle": b = MouseBtn.Middle; return true;
            case "xbutton1": b = MouseBtn.X1; return true;
            case "xbutton2": b = MouseBtn.X2; return true;
            default: b = default; return false;
        }
    }

    public static Signal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int colon = text.IndexOf(':');
        if (colon < 0) return null;
        string kind = text[..colon].Trim(), rest = text[(colon + 1)..];

        if (kind.Equals("Hid", StringComparison.OrdinalIgnoreCase))
        {
            int bar = rest.LastIndexOf('|');
            if (bar <= 0) return null;
            try { return ForHid(rest[..bar], Convert.FromHexString(rest[(bar + 1)..])); }
            catch (FormatException) { return null; }
        }

        var tokens = rest.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        var mods = Mods.None;
        bool injectedOnly = false;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (tokens[i].Equals("injected", StringComparison.OrdinalIgnoreCase)) { injectedOnly = true; continue; }
            if (!KeyNames.TryParseMod(tokens[i], out var m)) return null;
            mods |= m;
        }
        string last = tokens[^1];
        switch (kind.ToLowerInvariant())
        {
            case "key": return KeyNames.TryParse(last, out var vk) && !KeyNames.IsModifier(vk) ? ForKey(mods, vk) : null;
            case "mouse": return TryButton(last, out var b) ? ForMouse(mods, b) : null;
            case "wheel":
                if (last.Equals("up", StringComparison.OrdinalIgnoreCase)) return ForWheel(mods, true, injectedOnly);
                if (last.Equals("down", StringComparison.OrdinalIgnoreCase)) return ForWheel(mods, false, injectedOnly);
                return null;
            default: return null;
        }
    }
}

/// <summary>Moves through the preset list; scrolling past either end wraps (or stops, if wrapping is off).</summary>
public static class ProfileCycler
{
    public static int Step(int current, int count, int delta, bool wrap)
    {
        if (count <= 0) return 0;
        int next = current + delta;
        if (wrap) return ((next % count) + count) % count;
        return Math.Clamp(next, 0, count - 1);
    }
}
