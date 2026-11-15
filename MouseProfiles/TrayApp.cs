namespace MouseProfiles;

/// <summary>The app's lifetime: tray icon, hooks, Raw Input, overlay and the setup window.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly Engine engine = new();
    readonly InputHooks hooks;
    readonly RawInputWindow raw;
    readonly OverlayForm overlay = new();
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem presetsMenu = new("Presets");
    readonly ToolStripMenuItem enabledItem = new("Enabled") { Checked = true, CheckOnClick = true };
    readonly ToolStripMenuItem currentItem = new("") { Enabled = false };
    readonly SynchronizationContext ui;
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 1500 };
    SettingsForm? settings;

    public TrayApp()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        // TryLoad returns false on a fresh install or parse failure; only save-back when we
        // successfully read a real config, so a broken file is never silently overwritten.
        bool parsed = ConfigStore.TryLoad(out var loaded);
        if (parsed) try { ConfigStore.Save(loaded); } catch { }
        engine.Apply(loaded);
        engine.ProfileChanged += OnProfileChanged;
        engine.TriggerPreview += (sig, combo) => ui.Post(_ => ShowTriggerPreview(sig, combo), null);
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            try { ConfigStore.Save(engine.Config); } catch { /* remembering the last preset is best effort */ }
        };

        Startup.Sync(engine.Config.RunAtStartup);
        hooks = new InputHooks(engine);
        raw = new RawInputWindow(engine);
        raw.Configure(feed: false, hid: engine.WantsHid);

        var menu = new ContextMenuStrip();
        menu.Items.Add(currentItem);
        menu.Items.Add(presetsMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Setup...", null, (_, _) => ShowSettings());
        enabledItem.CheckedChanged += (_, _) => engine.Enabled = enabledItem.Checked;
        menu.Items.Add(enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => RefreshMenu();

        tray = new NotifyIcon
        {
            Icon = LoadEmbeddedIcon(),
            Text = "Mouse Profiles",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowSettings();

        if (!engine.Config.HasAnySignal) ShowSettings(); // first run: go straight to capture
    }

    void RefreshMenu()
    {
        currentItem.Text = engine.CurrentProfile is { } p ? $"Active: {p.Name}" : "No presets";
        presetsMenu.DropDownItems.Clear();
        for (int i = 0; i < engine.Config.Profiles.Count; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem(engine.Config.Profiles[i].Name) { Checked = i == engine.Current };
            item.Click += (_, _) => engine.Select(idx);
            presetsMenu.DropDownItems.Add(item);
        }
    }

    /// <summary>Called from inside the input hook, which must return fast: the banner and the save are posted out of it.</summary>
    void OnProfileChanged(Profile profile, int index, int count) =>
        ui.Post(_ => Announce(profile, index, count), null);

    void Announce(Profile profile, int index, int count)
    {
        overlay.ShowPreset(profile.Name, $"{index + 1} / {count}", engine.Config.Overlay);

        saveTimer.Stop();
        saveTimer.Start(); // debounced; also saved on exit
    }

    void ShowSettings()
    {
        if (settings is { IsDisposed: false }) { settings.Activate(); return; }
        settings = new SettingsForm(engine, hooks, raw, engine.Config.Clone(), Apply, TestOverlay);
        settings.FormClosed += (_, _) => settings = null;
        settings.Show();
    }

    void Apply(AppConfig cfg)
    {
        engine.Apply(cfg);
        Startup.Sync(cfg.RunAtStartup);
        raw.Configure(feed: settings != null, hid: true); // keep the setup window's feed running
    }

    void TestOverlay()
    {
        if (engine.CurrentProfile is { } p) Announce(p, engine.Current, engine.Config.Profiles.Count);
    }

    void ShowTriggerPreview(string signal, string combo)
    {
        var cfg = new OverlayConfig
        {
            DurationMs = engine.Config.Overlay.DurationMs,
            Opacity = engine.Config.Overlay.Opacity,
            Position = "Bottom",
        };
        overlay.ShowPreset($"{signal}  →  {combo}", "", cfg);
    }

    static Icon LoadEmbeddedIcon()
    {
        var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("MouseProfiles.mouse.ico");
        if (stream != null) return new Icon(stream, SystemInformation.SmallIconSize);
        return SystemIcons.Application;
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        saveTimer.Stop();
        try { ConfigStore.Save(engine.Config); } catch { }
        settings?.Close();
        hooks.Dispose();
        raw.Dispose();
        engine.Dispose();
        overlay.Dispose();
        tray.Dispose();
        base.ExitThreadCore();
    }
}
