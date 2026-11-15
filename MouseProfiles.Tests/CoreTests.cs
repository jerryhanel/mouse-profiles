using MouseProfiles;

namespace MouseProfiles.Tests;

public class ProfileCyclerTests
{
    [Theory]
    [InlineData(0, 3, -1, true, 2)]   // up from the first wraps to the last
    [InlineData(2, 3, +1, true, 0)]   // down from the last wraps to the first
    [InlineData(1, 3, +1, true, 2)]
    [InlineData(0, 3, -1, false, 0)]
    [InlineData(2, 3, +1, false, 2)]
    [InlineData(0, 0, +1, true, 0)]
    public void Step_moves_and_wraps(int cur, int count, int delta, bool wrap, int expected) =>
        Assert.Equal(expected, ProfileCycler.Step(cur, count, delta, wrap));
}

public class KeyComboTests
{
    [Theory]
    [InlineData("Shift+1")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+Shift+F5")]
    [InlineData("Ctrl+Alt+Delete")]
    public void Combo_round_trips(string text) => Assert.Equal(text, KeyCombo.Parse(text)!.ToString());

    [Fact]
    public void Combo_parse_normalizes_modifier_order_and_case()
    {
        var c = KeyCombo.Parse("shift + control + c")!;
        Assert.Equal(Mods.Ctrl | Mods.Shift, c.Mods);
        Assert.Equal("Ctrl+Shift+C", c.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Banana")]
    public void Combo_parse_rejects_garbage(string text) => Assert.Null(KeyCombo.Parse(text));

    [Fact]
    public void Combo_with_win_modifier()
    {
        var c = KeyCombo.Parse("Alt+Win+H")!;
        Assert.Equal(Mods.Alt | Mods.Win, c.Mods);
        Assert.Single(c.Keys);
        Assert.Equal((uint)0x48, c.Keys[0]); // H
        Assert.Equal("Alt+Win+H", c.ToString());
    }

    [Theory]
    [InlineData("MediaNextTrack", 0xB0)]
    [InlineData("MediaNext", 0xB0)]
    [InlineData("MediaPlayNext", 0xB0)]
    [InlineData("NextTrack", 0xB0)]
    [InlineData("MediaPlayPause", 0xB3)]
    [InlineData("MediaPlay", 0xB3)]
    [InlineData("PlayPause", 0xB3)]
    [InlineData("MediaPreviousTrack", 0xB1)]
    [InlineData("MediaPrev", 0xB1)]
    [InlineData("MediaStop", 0xB2)]
    [InlineData("VolumeMute", 0xAD)]
    [InlineData("Mute", 0xAD)]
    [InlineData("VolumeUp", 0xAF)]
    [InlineData("VolumeDown", 0xAE)]
    [InlineData("BrowserBack", 0xA6)]
    [InlineData("BrowserForward", 0xA7)]
    public void Media_key_aliases_parse(string name, int expectedVk)
    {
        var combo = KeyCombo.Parse(name)!;
        Assert.Single(combo.Keys);
        Assert.Equal((uint)expectedVk, combo.Keys[0]);
    }

    [Fact]
    public void Media_key_combo_round_trips()
    {
        var c = KeyCombo.Parse("Ctrl+MediaNextTrack")!;
        Assert.Equal(Mods.Ctrl, c.Mods);
        Assert.Equal("Ctrl+MediaNextTrack", c.ToString());
    }
}

public class SignalTests
{
    [Theory]
    [InlineData("Key:F13")]
    [InlineData("Key:Ctrl+Shift+F14")]
    [InlineData("Mouse:XButton1")]
    [InlineData("Mouse:Alt+Middle")]
    [InlineData("Wheel:Ctrl+Up")]
    [InlineData("Wheel:Shift+Down")]
    [InlineData("Wheel:Injected+Up")]
    [InlineData("Wheel:Ctrl+Injected+Down")]
    public void Signal_round_trips(string text) => Assert.Equal(text, Signal.Parse(text)!.ToString());

    [Fact]
    public void Hid_signal_round_trips_and_matches()
    {
        var s = Signal.Parse(@"Hid:\\?\HID#VID_046D&PID_C08B#7&1|0105A0")!;
        Assert.Equal(SignalKind.Hid, s.Kind);
        Assert.False(s.CanSwallow);
        Assert.True(s.HidMatches(@"\\?\hid#vid_046d&pid_c08b#7&1", new byte[] { 1, 5, 0xA0 }));
        Assert.False(s.HidMatches(@"\\?\hid#vid_046d&pid_c08b#7&1", new byte[] { 1, 5, 0xA1 }));
        Assert.Equal(s.ToString(), Signal.Parse(s.ToString())!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("F13")]
    [InlineData("Key:Ctrl")]          // a modifier alone is not a trigger
    [InlineData("Mouse:Left")]        // left/right click are never triggers
    [InlineData("Wheel:Sideways")]
    [InlineData("Banana:F13")]
    public void Signal_parse_rejects_garbage(string text) => Assert.Null(Signal.Parse(text));
}

public class ConfigTests
{
    [Fact]
    public void Normalize_sizes_every_preset_to_the_button_count()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.ButtonCount = 4;
        cfg.Normalize();
        Assert.Equal(4, cfg.Buttons.Count);
        Assert.All(cfg.Profiles, p => Assert.Equal(4, p.Outputs.Count));
        Assert.Equal("Shift+1", cfg.Profiles[0].Outputs[0]);

        cfg.ButtonCount = 1;
        cfg.Normalize();
        Assert.All(cfg.Profiles, p => Assert.Single(p.Outputs));
    }

    [Fact]
    public void Config_saves_and_loads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mouseprofiles-{Guid.NewGuid():N}.json");
        try
        {
            var cfg = AppConfig.CreateDefault();
            cfg.Buttons = new() { "Key:F13", "Key:F14" };
            cfg.ScrollUp = "Wheel:Ctrl+Up";
            cfg.Normalize();
            ConfigStore.Save(cfg, path);

            var back = ConfigStore.Load(path);
            Assert.Equal("Key:F13", back.Buttons[0]);
            Assert.Equal("Wheel:Ctrl+Up", back.ScrollUp);
            Assert.Equal("Word Stuff", back.Profiles[1].Name);
            Assert.Equal("Ctrl+V", back.Profiles[1].Outputs[1]);
            Assert.True(back.WrapAround);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Corrupt_config_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mouseprofiles-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Equal(2, ConfigStore.Load(path).Profiles.Count);
        }
        finally { File.Delete(path); }
    }
}
