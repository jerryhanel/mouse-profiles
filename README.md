# Mouse Profiles

A Windows system tray app that gives each programmable mouse button a different job depending on which preset is active — no vendor software required.

Map your side buttons to `Ctrl+C` / `Ctrl+V` for office work, then flip to a gaming preset where they fire `Shift+1` and `Shift+7`, then flip again for Photoshop. Switch presets from the tray menu, or bind scroll wheel up/down to cycle through them on the fly. A toast overlay names the active preset each time you switch.

## How it works

Mouse Profiles installs a low-level Windows hook to intercept input before it reaches any application. When a captured button fires, the hook swallows the original event and injects the configured key combo in its place — so to every app, it looks like you pressed a keyboard shortcut, not a mouse button. Modifier keys held at fire time are included automatically, so `Middle + Ctrl` can send something different from `Middle` alone.

Scroll wheel events can also be captured: bind scroll-up and scroll-down to cycle through presets rather than scroll. Raw Input handles exotic HID devices that don't generate standard mouse messages.

## In-app configuration

Double-click the tray icon (or right-click → **Setup...**) to open the setup window.

### Mouse & Scroll tab

1. Set **button count** to match your mouse (1–16).
2. Click **Capture** next to each button and press that button on your mouse. The app shows both the Windows event (HOOK line, blockable) and the raw device report (RAW line) so you can see exactly what your mouse is sending.
3. Optionally capture **Scroll Up** and **Scroll Down** — once captured, the scroll wheel cycles through presets instead of scrolling.

> **Tip:** Some vendor software maps extra buttons to F13–F24 or XButton1/2. That works well here and avoids conflicts with software that listens for standard keys.

### Presets tab

1. Click **Add** to create a preset and give it a name (e.g. *Gaming*, *Work*, *Photoshop*).
2. Select the preset and click **Record** next to each button to capture the key combo it should fire.
3. Tick the **preview** checkbox on any button to show a small toast whenever that button fires — useful for confirming macros in-game.
4. Add as many presets as you like. The active one is shown in the tray menu and in the overlay banner.

### Options tab

| Option | What it does |
|--------|-------------|
| **Run at startup** | Adds / removes the app from the Windows startup registry key |
| **Remember last preset** | Restores the preset you were on when the app closed |
| **Wrap around** | Scroll-wheel cycling wraps from the last preset back to the first |
| **Use scan codes** | Sends hardware scan codes instead of virtual-key codes — necessary for some games that ignore VK events |
| **Overlay** | Configure the preset-change banner: position (Top / Center / Bottom), opacity, and display duration |

### Live Feed tab

Shows every raw keyboard and mouse event in real time, including whether each event was injected by software. Useful for diagnosing which signal your mouse actually sends and confirming that swallowing works correctly.

### Cloud menu (requires account)

The menu bar in Setup exposes cloud sync via the [MouseProfiles Cloud API](https://github.com/jerryhanel/mouse-profiles-cloud).

| Item | What it does |
|------|-------------|
| **Cloud → Register...** | Create a free account (sends a verification email) |
| **Cloud → Login...** | Authenticate and store a local JWT token |
| **Cloud → Logout** | Delete the local token |
| **Sync → Upload Profiles...** | Choose local presets to push to the cloud |
| **Sync → List Cloud Profiles...** | Download presets from the cloud into the local config |

## Config file

Settings are stored at `%APPDATA%\MouseProfiles\config.json`. Override the path with the `MOUSEPROFILES_CONFIG` environment variable (useful for testing).

## Running from source

```powershell
dotnet run --project MouseProfiles/MouseProfiles.csproj
```

Requires .NET 8 and Windows (WinForms + low-level hooks).

## Tests

```powershell
dotnet test MouseProfiles.Tests/MouseProfiles.Tests.csproj
```

49 tests covering the profile cycler, key combo parser, signal parser, and config load/save round-trips.
