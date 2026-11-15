namespace MouseProfiles;

enum CaptureMode
{
    /// <summary>Learn what a mouse button (or scroll chord) sends: key chord, extra mouse button, modifier+wheel, or vendor HID report.</summary>
    Signal,
    /// <summary>Record a keyboard combo for a preset to emit.</summary>
    Combo,
}

/// <summary>
/// Modal capture box. While it is waiting, the engine routes every key, extra mouse button and wheel tick
/// here and swallows it, so pressing the button being learned can't type into other apps.
/// Left and right clicks still work so the dialog's own buttons stay usable.
/// </summary>
sealed class CaptureDialog : Form, ICaptureSink
{
    readonly Engine engine;
    readonly CaptureMode mode;
    readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 16f), TextAlign = ContentAlignment.MiddleCenter };
    readonly Label note = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.DarkRed, TextAlign = ContentAlignment.TopLeft };
    readonly Button accept = new() { Text = "Accept", DialogResult = DialogResult.OK, Enabled = false, Width = 90 };
    readonly Button retry = new() { Text = "Try again", Width = 90, Enabled = false };
    readonly Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
    readonly Button manualBtn = new() { Text = "Enter manually...", Width = 120 };
    readonly Button captureBtn = new() { Text = "Capture instead", Width = 120, Visible = false };
    readonly TextBox manualBox = new() { Visible = false, Width = 300, Font = new Font("Segoe UI", 14f) };
    readonly Label manualHint = new() { Visible = false, AutoSize = false, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Text = "Type a combo like Ctrl+V, Alt+Win+H, Shift+F13, MediaNextTrack" };
    readonly System.Windows.Forms.Timer hidTimer = new() { Interval = 350 };

    readonly HashSet<uint> downNow = new();
    readonly HashSet<uint> swallowedDowns = new();
    readonly List<uint> order = new();
    MouseBtn? pressedButton;
    Mods buttonMods;
    bool hookActivity, finished, manualMode;
    string? pendingHidPath;
    byte[]? pendingHid;

    public Signal? ResultSignal { get; private set; }
    public KeyCombo? ResultCombo { get; private set; }

    public CaptureDialog(Engine engine, CaptureMode mode, string title, string prompt)
    {
        this.engine = engine;
        this.mode = mode;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(540, 280);
        AcceptButton = null;
        CancelButton = cancel;
        KeyPreview = true;

        var intro = new Label { Text = prompt, Dock = DockStyle.Top, Height = 70, Padding = new Padding(12, 10, 12, 0) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { cancel, retry, accept });
        if (mode == CaptureMode.Combo)
            buttons.Controls.AddRange(new Control[] { captureBtn, manualBtn });
        var notePanel = new Panel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(12, 0, 12, 0) };
        notePanel.Controls.Add(note);
        Controls.Add(status);
        Controls.Add(notePanel);
        Controls.Add(buttons);
        Controls.Add(intro);

        retry.Click += (_, _) => { if (manualMode) EnterManualMode(); else Start(); };
        hidTimer.Tick += (_, _) => HidTimeout();
        manualBtn.Click += (_, _) => EnterManualMode();
        captureBtn.Click += (_, _) => Start();
        manualBox.TextChanged += (_, _) => ValidateManualInput();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        engine.Capture = this;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        engine.Capture = null;
        hidTimer.Dispose();
        base.OnFormClosed(e);
    }

    void Start()
    {
        manualMode = false;
        manualBox.Visible = manualHint.Visible = captureBtn.Visible = false;
        manualBtn.Visible = mode == CaptureMode.Combo;
        status.Visible = true;

        downNow.Clear(); order.Clear(); swallowedDowns.Clear();
        pressedButton = null; hookActivity = finished = false;
        pendingHid = null; pendingHidPath = null; hidTimer.Stop();
        ResultSignal = null; ResultCombo = null;
        accept.Enabled = retry.Enabled = false;
        note.Text = "";
        status.ForeColor = SystemColors.GrayText;
        status.Text = mode == CaptureMode.Signal ? "Waiting - press the mouse button now..." : "Waiting - press the keys now...";
        engine.Capture = this;
    }

    void EnterManualMode()
    {
        manualMode = true;
        finished = false;
        engine.Capture = null;
        ResultCombo = null;
        accept.Enabled = false;
        retry.Enabled = true;
        note.Text = "";

        status.Visible = false;
        manualBox.Visible = manualHint.Visible = captureBtn.Visible = true;
        manualBtn.Visible = false;

        if (!Controls.Contains(manualBox))
        {
            var manualPanel = new Panel { Dock = DockStyle.Fill };
            manualBox.Anchor = AnchorStyles.None;
            manualPanel.Layout += (_, _) =>
            {
                manualBox.Left = (manualPanel.Width - manualBox.Width) / 2;
                manualBox.Top = (manualPanel.Height - manualBox.Height) / 2 - 10;
            };
            manualPanel.Controls.Add(manualBox);
            var hintPanel = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(12, 0, 12, 0) };
            hintPanel.Controls.Add(manualHint);
            Controls.Add(manualPanel);
            Controls.Add(hintPanel);
        }
        else
        {
            manualBox.Parent!.Visible = true;
        }

        manualBox.Text = "";
        manualBox.Focus();
    }

    void ValidateManualInput()
    {
        var text = manualBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            note.Text = "";
            accept.Enabled = false;
            return;
        }
        var combo = KeyCombo.Parse(text);
        if (combo != null)
        {
            ResultCombo = combo;
            note.ForeColor = Color.DarkGreen;
            note.Text = $"Parsed: {combo}";
            accept.Enabled = true;
        }
        else
        {
            ResultCombo = null;
            note.ForeColor = Color.DarkRed;
            note.Text = "Could not parse. Use format like: Ctrl+V, Alt+Win+H, Shift+F13, MediaNextTrack, MediaPlayPause";
            accept.Enabled = false;
        }
    }

    // Fallback: if the keyboard hook is dead, keys arrive here via the form's own message pump.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!manualMode && engine.Capture == this) { OnKey((uint)e.KeyCode, true); e.Handled = e.SuppressKeyPress = true; }
        else base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!manualMode && engine.Capture == this) { OnKey((uint)e.KeyCode, false); e.Handled = e.SuppressKeyPress = true; }
        else base.OnKeyUp(e);
    }

    // ---- sink ---------------------------------------------------------------------------------

    public bool OnKey(uint vk, bool down)
    {
        if (manualMode) return false;
        if (finished)
        {
            if (down) swallowedDowns.Add(vk);
            else return swallowedDowns.Remove(vk);
            return true;
        }
        hookActivity = true;
        if (down)
        {
            swallowedDowns.Add(vk);
            if (downNow.Add(vk) && !order.Contains(vk)) order.Add(vk);
            status.ForeColor = SystemColors.ControlText;
            status.Text = string.Join("+", order.Select(KeyNames.Name));
        }
        else
        {
            swallowedDowns.Remove(vk);
            downNow.Remove(vk);
            if (downNow.Count == 0 && order.Count > 0 && pressedButton == null) EvaluateKeys();
        }
        return true;
    }

    public bool OnMouseButton(MouseBtn button, bool down)
    {
        if (manualMode) return false;
        if (finished || mode != CaptureMode.Signal) return finished;
        hookActivity = true;
        if (down)
        {
            pressedButton = button;
            buttonMods = HeldMods();
            status.ForeColor = SystemColors.ControlText;
            status.Text = Signal.ForMouse(buttonMods, button).Describe();
        }
        else if (pressedButton == button)
        {
            Finish(Signal.ForMouse(buttonMods, button));
        }
        return true;
    }

    public bool OnWheel(bool up, bool injected)
    {
        if (manualMode) return false;
        if (finished || mode != CaptureMode.Signal) return finished;
        hookActivity = true;
        var mods = HeldMods();
        if (mods != Mods.None) Finish(Signal.ForWheel(mods, up));
        else if (injected) Finish(Signal.ForWheel(Mods.None, up, injectedOnly: true));
        else Reject("That wheel tick came from the physical wheel (or the mouse's own firmware), and a bare wheel can't be a trigger - it would break normal scrolling. " +
                    "Hold Ctrl, Shift or Alt while you scroll, or remap the button in your mouse software.");
        return true;
    }

    public void OnIgnoredClick(string name)
    {
        if (manualMode || finished || mode != CaptureMode.Signal) return;
        note.Text = $"A {name} click was heard, but left and right clicks can never be triggers (they would break normal clicking). " +
                    "Press one of the extra buttons instead: side, middle, or a button you've remapped to a key.";
    }

    public void OnHid(string devicePath, byte[] report)
    {
        if (manualMode || finished || mode != CaptureMode.Signal) return;
        pendingHidPath ??= devicePath;
        pendingHid ??= report;
        hidTimer.Stop();
        hidTimer.Start();
    }

    // ---- results ------------------------------------------------------------------------------

    Mods HeldMods() => engine.HeldMods() | downNow.Aggregate(Mods.None, (a, v) => a | KeyNames.ModOf(v));

    void EvaluateKeys()
    {
        var mods = order.Aggregate(Mods.None, (a, v) => a | KeyNames.ModOf(v));
        var mains = order.Where(v => !KeyNames.IsModifier(v)).ToArray();
        order.Clear();
        if (mode == CaptureMode.Combo)
        {
            Finish(new KeyCombo(mods, mains));
            return;
        }
        if (mains.Length == 0) Reject("Only modifier keys were sent. A trigger needs one non-modifier key (such as F13) with them.");
        else if (mains.Length > 1) Reject("More than one non-modifier key was sent at once. Triggers support modifiers plus a single key.");
        else Finish(Signal.ForKey(mods, mains[0]));
    }

    void HidTimeout()
    {
        hidTimer.Stop();
        if (finished || hookActivity || pendingHid == null || pendingHidPath == null) return;
        Finish(Signal.ForHid(pendingHidPath, pendingHid));
    }

    void Reject(string message)
    {
        status.ForeColor = Color.DarkRed;
        status.Text = "Not usable";
        note.Text = message + "\r\nJust try again, or click Try again.";
        retry.Enabled = true;
    }

    void Finish(Signal sig)
    {
        finished = true;
        ResultSignal = sig;
        status.ForeColor = Color.DarkGreen;
        status.Text = sig.Describe();
        note.Text = Warning(sig) ?? "";
        accept.Enabled = retry.Enabled = true;
    }

    void Finish(KeyCombo combo)
    {
        finished = true;
        ResultCombo = combo;
        status.ForeColor = Color.DarkGreen;
        status.Text = combo.ToString();
        accept.Enabled = retry.Enabled = true;
    }

    /// <summary>
    /// Hooks can't tell which device a key came from, so a trigger that is an ordinary key would also be
    /// swallowed from the real keyboard. F13-F24 and media keys are safe because keyboards don't normally send them.
    /// </summary>
    public static string? Warning(Signal sig)
    {
        switch (sig.Kind)
        {
            case SignalKind.Key:
                bool safe = sig.Vk is >= 0x7C and <= 0x87 or >= 0xA6 and <= 0xB7;
                return safe ? null :
                    "Heads up: this is a normal keyboard chord. The app can't tell the mouse from your keyboard, so it will also be blocked when you type it. " +
                    "Prefer remapping the button to F13-F24 in your mouse software.";
            case SignalKind.Hid:
                return "This button only shows up as a raw HID report. It can trigger presets but can't be blocked, so the original action (if any) still happens.";
            default:
                return null;
        }
    }
}
