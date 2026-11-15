namespace MouseProfiles;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "MouseProfiles.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Mouse Profiles is already running - look for it in the system tray.", "Mouse Profiles");
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.Run(new TrayApp());
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Mouse Profiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
