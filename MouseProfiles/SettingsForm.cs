namespace MouseProfiles;

/// <summary>The setup window: capture mouse buttons and scroll chords, build presets, tune options, watch the live feed.</summary>
sealed partial class SettingsForm : Form
{
    readonly Engine engine;
    readonly InputHooks hooks;
    readonly RawInputWindow raw;
    AppConfig cfg;
    readonly Action<AppConfig> apply;
    readonly Action testOverlay;
    bool dirty;

    readonly NumericUpDown buttonCount = new() { Minimum = 1, Maximum = 16, Width = 60 };
    readonly FlowLayoutPanel signalRows = Column();
    readonly ListBox profileList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly FlowLayoutPanel outputRows = Column();
    readonly Label outputHeader = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 0, 0, 10) };
    readonly TextBox feed = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), WordWrap = false };
    readonly System.Windows.Forms.Timer feedTimer = new() { Interval = 100 };

    public SettingsForm(Engine engine, InputHooks hooks, RawInputWindow raw, AppConfig working, Action<AppConfig> apply, Action testOverlay)
    {
        this.engine = engine;
        this.hooks = hooks;
        this.raw = raw;
        cfg = working;
        this.apply = apply;
        this.testOverlay = testOverlay;

        Text = "Mouse Profiles - Setup";
        ClientSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(700, 480);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildMouseTab());
        tabs.TabPages.Add(BuildPresetsTab());
        tabs.TabPages.Add(BuildOptionsTab());
        tabs.TabPages.Add(BuildImportExportTab());
        tabs.TabPages.Add(BuildFeedTab());

        var save = new Button { Text = "Save && Apply", Width = 110 };
        var close = new Button { Text = "Close", Width = 90 };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        bottom.Controls.AddRange(new Control[] { close, save });
        Controls.Add(tabs);
        Controls.Add(bottom);
        var cloudMenu = BuildCloudMenu();
        MainMenuStrip = cloudMenu;
        Controls.Add(cloudMenu);

        save.Click += (_, _) => Save();
        close.Click += (_, _) => Close();

        buttonCount.Value = cfg.ButtonCount;
        buttonCount.ValueChanged += (_, _) =>
        {
            cfg.ButtonCount = (int)buttonCount.Value;
            cfg.Normalize();
            dirty = true;
            BuildSignalRows();
            BuildOutputRows();
        };
        profileList.SelectedIndexChanged += (_, _) => BuildOutputRows();
        feedTimer.Tick += (_, _) => FlushFeed();

        RefreshProfileList(0);
        BuildSignalRows();
    }

    static FlowLayoutPanel Column() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8),
    };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        engine.Suspended = true;
        engine.FeedOn = true;
        raw.Configure(feed: true, hid: true);
        feedTimer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (dirty)
        {
            var answer = MessageBox.Show(this, "Save your changes before closing?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
            if (answer == DialogResult.Yes) Save();
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        feedTimer.Stop();
        engine.Suspended = false;
        engine.FeedOn = false;
        engine.FeedQueue.Clear();
        raw.Configure(feed: false, hid: engine.WantsHid);
        base.OnFormClosed(e);
    }

    void Save()
    {
        cfg.Normalize();
        try { ConfigStore.Save(cfg); }
        catch (Exception ex) { MessageBox.Show(this, "Could not save the config:\r\n" + ex.Message, Text); return; }
        apply(cfg.Clone());
        dirty = false;
    }

    // ---- Mouse & scroll tab -------------------------------------------------------------------

    TabPage BuildMouseTab()
    {
        var page = new TabPage("Mouse && scroll");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(8), FlowDirection = FlowDirection.TopDown };
        var line = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        line.Controls.Add(new Label { Text = "How many programmable buttons does your mouse have?", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        line.Controls.Add(buttonCount);
        top.Controls.Add(line);
        top.Controls.Add(new Label
        {
            Width = 700, Height = 46, ForeColor = SystemColors.GrayText,
            Text = "Click Capture, then press the button on your mouse. The app records exactly what Windows receives. " +
                   "For the scroll triggers you can press a key chord, a mouse button, or hold Ctrl/Shift/Alt and turn the wheel.",
        });
        page.Controls.Add(signalRows);
        page.Controls.Add(top);
        return page;
    }

    void BuildSignalRows()
    {
        signalRows.SuspendLayout();
        signalRows.Controls.Clear();
        for (int i = 0; i < cfg.ButtonCount; i++)
        {
            int idx = i;
            AddSignalRow($"Mouse button MK{i + 1}", () => cfg.Buttons[idx], v => cfg.Buttons[idx] = v);
        }
        AddSignalRow("Scroll up (previous preset)", () => cfg.ScrollUp, v => cfg.ScrollUp = v);
        AddSignalRow("Scroll down (next preset)", () => cfg.ScrollDown, v => cfg.ScrollDown = v);
        signalRows.ResumeLayout();
    }

    void AddSignalRow(string label, Func<string?> get, Action<string?> set)
    {
        var box = new TextBox { ReadOnly = true, Width = 350 };
        void Refresh() => box.Text = Signal.Parse(get())?.Describe() ?? "(not captured yet)";
        var capture = new Button { Text = "Capture...", Width = 90 };
        var clear = new Button { Text = "Clear", Width = 60 };
        capture.Click += (_, _) =>
        {
            hooks.Reinstall();
            using var dlg = new CaptureDialog(engine, CaptureMode.Signal, $"Capture: {label}",
                $"{label}\r\n\r\nPress the mouse button (or chord) you want to use for this. Nothing you press here will reach other programs.");
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.ResultSignal == null) return;
            string text = dlg.ResultSignal.ToString();
            var others = cfg.Buttons.Append(cfg.ScrollUp).Append(cfg.ScrollDown).Where(s => s != null && s != get());
            if (others.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "That signal is already assigned to something else. Pick a different one.", Text);
                return;
            }
            set(text);
            dirty = true;
            Refresh();
        };
        clear.Click += (_, _) => { set(null); dirty = true; Refresh(); };
        Refresh();
        signalRows.Controls.Add(Row(label, box, capture, clear));
    }

    static FlowLayoutPanel Row(string label, params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        row.Controls.Add(new Label { Text = label, Width = 200, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
        row.Controls.AddRange(controls);
        return row;
    }

    // ---- Presets tab --------------------------------------------------------------------------

    TabPage BuildPresetsTab()
    {
        var page = new TabPage("Presets");
        var left = new Panel { Dock = DockStyle.Left, Width = 250, Padding = new Padding(8) };
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70, FlowDirection = FlowDirection.LeftToRight };
        Button B(string text, int w, Action a) { var b = new Button { Text = text, Width = w }; b.Click += (_, _) => a(); return b; }
        leftButtons.Controls.AddRange(new Control[]
        {
            B("Add", 55, AddProfile), B("Rename", 65, RenameProfile), B("Delete", 55, DeleteProfile),
            B("Up", 40, () => MoveProfile(-1)), B("Down", 50, () => MoveProfile(+1)),
        });
        left.Controls.Add(profileList);
        left.Controls.Add(leftButtons);
        outputRows.Controls.Add(outputHeader);
        page.Controls.Add(outputRows);
        page.Controls.Add(left);
        return page;
    }

    Profile? SelectedProfile => profileList.SelectedIndex >= 0 && profileList.SelectedIndex < cfg.Profiles.Count ? cfg.Profiles[profileList.SelectedIndex] : null;

    void RefreshProfileList(int select)
    {
        profileList.Items.Clear();
        foreach (var p in cfg.Profiles) profileList.Items.Add(p.Name);
        if (cfg.Profiles.Count > 0) profileList.SelectedIndex = Math.Clamp(select, 0, cfg.Profiles.Count - 1);
        else BuildOutputRows();
    }

    void AddProfile()
    {
        var name = Prompt("New preset", "Preset name:", $"Preset {cfg.Profiles.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = new Profile { Name = name.Trim() };
        cfg.Profiles.Add(p);
        cfg.Normalize();
        dirty = true;
        RefreshProfileList(cfg.Profiles.Count - 1);
    }

    void RenameProfile()
    {
        if (SelectedProfile is not { } p) return;
        var name = Prompt("Rename preset", "Preset name:", p.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        p.Name = name.Trim();
        dirty = true;
        RefreshProfileList(profileList.SelectedIndex);
    }

    void DeleteProfile()
    {
        if (SelectedProfile is not { } p) return;
        if (MessageBox.Show(this, $"Delete the preset \"{p.Name}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        int at = profileList.SelectedIndex;
        cfg.Profiles.Remove(p);
        dirty = true;
        RefreshProfileList(at);
    }

    void MoveProfile(int delta)
    {
        int at = profileList.SelectedIndex, to = at + delta;
        if (at < 0 || to < 0 || to >= cfg.Profiles.Count) return;
        (cfg.Profiles[at], cfg.Profiles[to]) = (cfg.Profiles[to], cfg.Profiles[at]);
        dirty = true;
        RefreshProfileList(to);
    }

    void BuildOutputRows()
    {
        outputRows.SuspendLayout();
        outputRows.Controls.Clear();
        outputRows.Controls.Add(outputHeader);
        if (SelectedProfile is not { } p)
        {
            outputHeader.Text = "Add a preset to start.";
            outputRows.ResumeLayout();
            return;
        }
        outputHeader.Text = $"\"{p.Name}\" - what each mouse button sends";
        for (int i = 0; i < cfg.ButtonCount; i++)
        {
            int idx = i;
            var box = new TextBox { ReadOnly = true, Width = 190 };
            void Refresh() => box.Text = p.Outputs[idx] ?? "(nothing)";
            var record = new Button { Text = "Record...", Width = 80 };
            var clear = new Button { Text = "Clear", Width = 55 };
            record.Click += (_, _) =>
            {
                hooks.Reinstall();
                using var dlg = new CaptureDialog(engine, CaptureMode.Combo, $"Record: MK{idx + 1} in \"{p.Name}\"",
                    $"Press the keyboard combo that MK{idx + 1} should send in \"{p.Name}\" (for example Ctrl+V or Shift+7). Release all keys to finish.");
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.ResultCombo == null) return;
                p.Outputs[idx] = dlg.ResultCombo.ToString();
                dirty = true;
                Refresh();
            };
            var extKey = new Button { Text = "Extended...", Width = 80 };
            extKey.Click += (_, _) =>
            {
                var combo = ShowExtendedKeyDialog();
                if (combo == null) return;
                p.Outputs[idx] = combo.ToString();
                dirty = true;
                Refresh();
            };
            var preview = new CheckBox { Text = "Preview", AutoSize = true, Checked = p.Preview[idx] };
            preview.CheckedChanged += (_, _) => { p.Preview[idx] = preview.Checked; dirty = true; };
            clear.Click += (_, _) => { p.Outputs[idx] = null; dirty = true; Refresh(); };
            Refresh();
            string slot = Signal.Parse(cfg.Buttons[idx]) is { } s ? $"MK{idx + 1}  ({s.Describe()})" : $"MK{idx + 1}";
            var row = Row(slot, box, record, clear, extKey, preview);
            ((Label)row.Controls[0]).Width = 170;
            outputRows.Controls.Add(row);
        }
        outputRows.ResumeLayout();
    }

    // ---- Options tab --------------------------------------------------------------------------

    TabPage BuildOptionsTab()
    {
        var page = new TabPage("Options");
        var col = Column();

        var startup = new CheckBox { Text = "Run at Windows startup", AutoSize = true, Checked = cfg.RunAtStartup };
        startup.CheckedChanged += (_, _) => { cfg.RunAtStartup = startup.Checked; dirty = true; };
        var wrap = new CheckBox { Text = "Scrolling wraps around (last preset -> first, and back)", AutoSize = true, Checked = cfg.WrapAround };
        wrap.CheckedChanged += (_, _) => { cfg.WrapAround = wrap.Checked; dirty = true; };
        var remember = new CheckBox { Text = "Remember the last preset between runs", AutoSize = true, Checked = cfg.RememberLast };
        remember.CheckedChanged += (_, _) => { cfg.RememberLast = remember.Checked; dirty = true; };
        var scan = new CheckBox { Text = "Send keys as scan codes (recommended for games)", AutoSize = true, Checked = cfg.UseScanCodes };
        scan.CheckedChanged += (_, _) => { cfg.UseScanCodes = scan.Checked; dirty = true; };

        var duration = new NumericUpDown { Minimum = 200, Maximum = 10000, Increment = 100, Width = 80, Value = Math.Clamp(cfg.Overlay.DurationMs, 200, 10000) };
        duration.ValueChanged += (_, _) => { cfg.Overlay.DurationMs = (int)duration.Value; dirty = true; };
        var opacity = new NumericUpDown { Minimum = 10, Maximum = 100, Increment = 5, Width = 80, Value = (decimal)Math.Clamp(cfg.Overlay.Opacity * 100, 10, 100) };
        opacity.ValueChanged += (_, _) => { cfg.Overlay.Opacity = (double)opacity.Value / 100; dirty = true; };
        var position = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        position.Items.AddRange(new object[] { "Center", "Top", "Bottom" });
        position.SelectedItem = cfg.Overlay.Position is "Top" or "Bottom" ? cfg.Overlay.Position : "Center";
        position.SelectedIndexChanged += (_, _) => { cfg.Overlay.Position = (string)position.SelectedItem!; dirty = true; };
        var test = new Button { Text = "Test overlay", Width = 100 };
        test.Click += (_, _) => testOverlay();

        col.Controls.AddRange(new Control[] { startup, wrap, remember, scan, new Label { Height = 12 } });
        col.Controls.Add(Row("Overlay stays for (ms)", duration));
        col.Controls.Add(Row("Overlay opacity (%)", opacity));
        col.Controls.Add(Row("Overlay position", position));
        col.Controls.Add(test);
        col.Controls.Add(new Label
        {
            Width = 640, Height = 60, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 16, 0, 0),
            Text = "Test overlay shows the preview with your saved settings; use Save && Apply first to see changes.",
        });
        page.Controls.Add(col);
        return page;
    }

    // ---- Import / Export tab ------------------------------------------------------------------

    TabPage BuildImportExportTab()
    {
        var page = new TabPage("Import / Export");
        var col = Column();

        var pathBox = new TextBox { ReadOnly = true, Width = 500, Text = ConfigStore.DefaultPath };
        var openFolder = new Button { Text = "Open folder", Width = 100 };
        openFolder.Click += (_, _) =>
        {
            var dir = Path.GetDirectoryName(ConfigStore.DefaultPath);
            if (dir != null && Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", dir);
        };

        var export = new Button { Text = "Export config...", Width = 130 };
        export.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog { Filter = "JSON files|*.json|All files|*.*", FileName = "MouseProfiles-config.json", Title = "Export config" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.Copy(ConfigStore.DefaultPath, dlg.FileName, overwrite: true);
                MessageBox.Show(this, "Config exported.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, "Export failed:\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };

        var import = new Button { Text = "Import config...", Width = 130 };
        import.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "JSON files|*.json|All files|*.*", Title = "Import config" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var imported = ConfigStore.Load(dlg.FileName);
                ConfigStore.Save(imported);
                apply(imported.Clone());
                cfg = imported;
                dirty = false;
                MessageBox.Show(this, "Config imported and applied. Reopening settings to reflect the changes.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, "Import failed:\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };

        col.Controls.Add(new Label { Text = "Config file location:", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        col.Controls.Add(pathBox);
        col.Controls.Add(openFolder);
        col.Controls.Add(new Label { Height = 20 });
        col.Controls.Add(export);
        col.Controls.Add(new Label { Text = "Save your current config to a file you can copy to another PC.", AutoSize = true, ForeColor = SystemColors.GrayText });
        col.Controls.Add(new Label { Height = 12 });
        col.Controls.Add(import);
        col.Controls.Add(new Label { Text = "Load a config file from another PC. This replaces your current settings.", AutoSize = true, ForeColor = SystemColors.GrayText });
        page.Controls.Add(col);
        return page;
    }

    KeyCombo? ShowExtendedKeyDialog()
    {
        var f = new Form
        {
            Text = "Extended key",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(280, 170),
        };

        var keyLabel = new Label { Text = "Key:", Location = new Point(16, 18), AutoSize = true };
        var keyDrop = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(80, 14), Width = 80 };
        for (int fk = 13; fk <= 24; fk++) keyDrop.Items.Add($"F{fk}");
        keyDrop.SelectedIndex = 0;

        var modLabel = new Label { Text = "Mods:", Location = new Point(16, 56), AutoSize = true };
        var chkCtrl = new CheckBox { Text = "Ctrl", Location = new Point(80, 54), AutoSize = true };
        var chkAlt = new CheckBox { Text = "Alt", Location = new Point(130, 54), AutoSize = true };
        var chkShift = new CheckBox { Text = "Shift", Location = new Point(176, 54), AutoSize = true };
        var chkWin = new CheckBox { Text = "Win", Location = new Point(232, 54), AutoSize = true };

        var ok = new Button { Text = "Assign", Width = 90, Height = 32, Location = new Point(50, 110), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 32, Location = new Point(150, 110), DialogResult = DialogResult.Cancel };
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Controls.AddRange(new Control[] { keyLabel, keyDrop, modLabel, chkCtrl, chkAlt, chkShift, chkWin, ok, cancel });

        if (f.ShowDialog(this) != DialogResult.OK) return null;
        var mods = (chkCtrl.Checked ? Mods.Ctrl : Mods.None) | (chkShift.Checked ? Mods.Shift : Mods.None)
                 | (chkAlt.Checked ? Mods.Alt : Mods.None) | (chkWin.Checked ? Mods.Win : Mods.None);
        return new KeyCombo(mods, new[] { (uint)(0x7C + keyDrop.SelectedIndex) });
    }

    // ---- Live feed tab ------------------------------------------------------------------------

    TabPage BuildFeedTab()
    {
        var page = new TabPage("Live feed");
        var info = new Label
        {
            Dock = DockStyle.Top, Height = 78, Padding = new Padding(8, 6, 8, 0),
            Text = "Press buttons on your mouse and watch what arrives.\r\n" +
                   "HOOK lines are what Windows delivers to programs - this is what the app can block and replace.\r\n" +
                   "RAW lines show which physical device sent it. \"[injected by software]\" means your mouse driver (G Hub, Synapse, ...) is generating the key.\r\n" +
                   "If a button shows nothing at all, it isn't sending anything Windows can see - set it to something like F13 in your mouse software.",
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4) };
        var copy = new Button { Text = "Copy to Clipboard", Width = 130, Height = 26 };
        var clear = new Button { Text = "Clear", Width = 60, Height = 26 };
        copy.Click += (_, _) => { if (feed.TextLength > 0) Clipboard.SetText(feed.Text); };
        clear.Click += (_, _) => feed.Clear();
        buttons.Controls.AddRange(new Control[] { copy, clear });
        page.Controls.Add(feed);
        page.Controls.Add(buttons);
        page.Controls.Add(info);
        return page;
    }

    void FlushFeed()
    {
        if (engine.FeedQueue.IsEmpty) return;
        var sb = new System.Text.StringBuilder();
        while (engine.FeedQueue.TryDequeue(out var line)) sb.AppendLine(line);
        feed.AppendText(sb.ToString());
        if (feed.TextLength > 120_000) feed.Text = feed.Text[^60_000..];
    }

    // ---- helpers ------------------------------------------------------------------------------

    string? Prompt(string title, string label, string initial)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 110), MaximizeBox = false, MinimizeBox = false,
        };
        var box = new TextBox { Left = 12, Top = 34, Width = 316, Text = initial };
        var ok = new Button { Text = "OK", Left = 172, Top = 70, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 253, Top = 70, Width = 75, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { new Label { Text = label, Left = 12, Top = 12, AutoSize = true }, box, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }
}
