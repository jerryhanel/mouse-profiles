using System.Net.Http;
using System.Text.Json;

namespace MouseProfiles;

partial class SettingsForm
{
    ToolStripMenuItem loginItem = null!;
    ToolStripMenuItem logoutItem = null!;
    ToolStripMenuItem listCloudItem = null!;
    ToolStripMenuItem uploadItem = null!;

    MenuStrip BuildCloudMenu()
    {
        loginItem = new ToolStripMenuItem("Login...");
        loginItem.Click += async (_, _) => await LoginAsync();

        logoutItem = new ToolStripMenuItem("Logout");
        logoutItem.Click += (_, _) => Logout();

        var register = new ToolStripMenuItem("Register...");
        register.Click += async (_, _) => await RegisterAsync();

        var cloudMenu = new ToolStripMenuItem("Cloud");
        cloudMenu.DropDownItems.AddRange(new ToolStripItem[] { loginItem, logoutItem, new ToolStripSeparator(), register });
        cloudMenu.DropDownOpened += (_, _) =>
        {
            bool loggedIn = CloudTokenStore.Load() != null;
            loginItem.Enabled = !loggedIn;
            logoutItem.Enabled = loggedIn;
        };

        listCloudItem = new ToolStripMenuItem("List Cloud Profiles...");
        listCloudItem.Click += async (_, _) => await ListCloudProfilesAsync();

        uploadItem = new ToolStripMenuItem("Upload Profiles...");
        uploadItem.Click += async (_, _) => await UploadProfilesAsync();

        var syncMenu = new ToolStripMenuItem("Sync");
        syncMenu.DropDownItems.AddRange(new ToolStripItem[] { listCloudItem, uploadItem });
        syncMenu.DropDownOpened += (_, _) =>
        {
            bool loggedIn = CloudTokenStore.Load() != null;
            listCloudItem.Enabled = loggedIn;
            uploadItem.Enabled = loggedIn;
        };

        var menu = new MenuStrip();
        menu.Items.Add(cloudMenu);
        menu.Items.Add(syncMenu);
        return menu;
    }

    async Task LoginAsync()
    {
        var creds = ShowCredentialsDialog("Login", "Login");
        if (creds == null) return;
        try
        {
            var token = await CloudApiClient.LoginAsync(creds.Value.email, creds.Value.password);
            CloudTokenStore.Save(token);
            if (!IsDisposed)
                MessageBox.Show(this, "Logged in successfully.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            ShowCloudError(ex);
        }
    }

    void Logout()
    {
        CloudTokenStore.Delete();
        MessageBox.Show(this, "Logged out.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    async Task RegisterAsync()
    {
        var creds = ShowCredentialsDialog("Register", "Register");
        if (creds == null) return;
        try
        {
            await CloudApiClient.RegisterAsync(creds.Value.email, creds.Value.password);
            if (!IsDisposed)
                MessageBox.Show(this, "Account created. Check your email to verify your address.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            ShowCloudError(ex);
        }
    }

    async Task ListCloudProfilesAsync()
    {
        var token = CloudTokenStore.Load();
        if (token == null) { MessageBox.Show(this, "You are not logged in.", Text); return; }

        List<CloudApiClient.CloudProfileMeta> cloudProfiles;
        try { cloudProfiles = await CloudApiClient.ListProfilesAsync(token); }
        catch (Exception ex) when (ex is SessionExpiredException or ApiException or HttpRequestException or TaskCanceledException)
        {
            ShowCloudError(ex); return;
        }

        if (cloudProfiles.Count == 0) { MessageBox.Show(this, "No cloud profiles found.", Text); return; }

        var selected = ShowCheckListDialog("Cloud Profiles", "Select profiles to download:", cloudProfiles.Select(p => p.Name).ToList());
        if (selected == null || selected.Count == 0) return;

        int added = 0, replaced = 0;
        foreach (int idx in selected)
        {
            var meta = cloudProfiles[idx];
            CloudApiClient.CloudProfileFull full;
            try { full = await CloudApiClient.DownloadProfileAsync(token, meta.Id); }
            catch (Exception ex) when (ex is SessionExpiredException or HttpRequestException or TaskCanceledException)
            {
                ShowCloudError(ex); return;
            }
            catch (ApiException ex)
            {
                ShowCloudError(ex); continue;
            }

            Profile downloaded;
            try { downloaded = JsonSerializer.Deserialize<Profile>(full.ConfigJson) ?? new Profile(); }
            catch (JsonException) { downloaded = new Profile(); }
            downloaded.Id = full.Id;
            downloaded.Name = full.Name;

            int existing = cfg.Profiles.FindIndex(p => p.Id == full.Id);
            if (existing >= 0)
            {
                var answer = MessageBox.Show(this, $"Local profile \"{cfg.Profiles[existing].Name}\" has the same ID. Replace it?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) continue;
                cfg.Profiles[existing] = downloaded;
                replaced++;
            }
            else
            {
                cfg.Profiles.Add(downloaded);
                added++;
            }
        }

        if (added + replaced > 0)
        {
            cfg.Normalize();
            dirty = true;
            RefreshProfileList(cfg.Profiles.Count - 1);
            if (!IsDisposed)
                MessageBox.Show(this, $"Downloaded: {added} added, {replaced} replaced. Click Save && Apply to keep them.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    async Task UploadProfilesAsync()
    {
        var token = CloudTokenStore.Load();
        if (token == null) { MessageBox.Show(this, "You are not logged in.", Text); return; }
        if (cfg.Profiles.Count == 0) { MessageBox.Show(this, "No local profiles to upload.", Text); return; }

        var selected = ShowCheckListDialog("Upload Profiles", "Select profiles to upload:", cfg.Profiles.Select(p => p.Name).ToList());
        if (selected == null || selected.Count == 0) return;

        var upserts = selected.Select(i =>
        {
            var p = cfg.Profiles[i];
            return new CloudApiClient.CloudSyncUpsert(p.Id, p.Name, JsonSerializer.Serialize(p));
        }).ToList();

        try
        {
            var result = await CloudApiClient.SyncAsync(token, upserts, []);
            if (!IsDisposed)
                MessageBox.Show(this, $"Upload complete: {result.Created} created, {result.Updated} updated.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is SessionExpiredException or ApiException or HttpRequestException or TaskCanceledException)
        {
            ShowCloudError(ex);
        }
    }

    // ---- dialogs ------------------------------------------------------------------------------

    (string email, string password)? ShowCredentialsDialog(string title, string verb)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 150), MaximizeBox = false, MinimizeBox = false,
        };
        var emailBox = new TextBox { Left = 110, Top = 16, Width = 210 };
        var passBox = new TextBox { Left = 110, Top = 50, Width = 210, UseSystemPasswordChar = true };
        var ok = new Button { Text = verb, Left = 150, Top = 108, Width = 85, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 243, Top = 108, Width = 85, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[]
        {
            new Label { Text = "Email:", Left = 12, Top = 19, AutoSize = true },
            emailBox,
            new Label { Text = "Password:", Left = 12, Top = 53, AutoSize = true },
            passBox,
            ok, cancel,
        });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return null;
        return (emailBox.Text.Trim(), passBox.Text);
    }

    List<int>? ShowCheckListDialog(string title, string prompt, List<string> items)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 320), MaximizeBox = false, MinimizeBox = false,
        };
        var label = new Label { Left = 12, Top = 12, AutoSize = true, Text = prompt };
        var list = new CheckedListBox { Left = 12, Top = 36, Width = 316, Height = 220, CheckOnClick = true };
        foreach (var item in items) list.Items.Add(item);
        var ok = new Button { Text = "OK", Left = 150, Top = 272, Width = 85, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 243, Top = 272, Width = 85, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { label, list, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return null;
        return list.CheckedIndices.Cast<int>().ToList();
    }

    // ---- error handling -----------------------------------------------------------------------

    void ShowCloudError(Exception ex)
    {
        if (IsDisposed) return;
        if (ex is SessionExpiredException) CloudTokenStore.Delete();
        var (msg, icon) = ex is HttpRequestException or TaskCanceledException
            ? ("Could not connect to the cloud.", MessageBoxIcon.Error)
            : (ex.Message, MessageBoxIcon.Warning);
        MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, icon);
    }
}
