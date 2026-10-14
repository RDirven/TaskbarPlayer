# Taskbar Player

Media controls that live **directly inside the Windows 11 taskbar**: album art, title/artist, a seekable
timeline and buttons for previous, play/pause, next and volume. No separate window.

Works with any app that registers as a media player with Windows: Spotify, YouTube Music (browser or
installed app), Apple Music, iTunes, Deezer, TIDAL, VLC, Media Player, Sonos, YouTube in Chrome/Edge/Firefox/Brave, etc.

English by default; Dutch (Nederlands) can be selected from the menu.

## Usage

| Where | Action |
|-------|--------|
| ⏮ ⏯ ⏭ | Previous / play-pause / next |
| 🔊 click | Volume slider with mute button |
| Mouse wheel over the player | Volume up/down |
| Timeline (thin bar under the title) | Click or drag to seek; the second line shows the time (`1:23 / 3:45`) |
| Album art / title | Choose which app to control, or start a music app |
| Nothing playing? Click the player or ⏯ | Start one of your installed music apps |
| Right-click | Menu: source, display, position, show art/title, start with Windows, language, exit |

The player finds free room in the taskbar by itself (left, between Widgets and Start, or right, between your
app icons and the system tray) and follows along when apps open or close. When room is tight the title goes
first, then only the buttons remain. Use *Position* to pin it to one side.

**Multiple displays**: pick the taskbar via *Display*. This requires *Show my taskbar on all displays* in
Windows Settings. If that display is disconnected, the player moves to the main taskbar and returns when the
display comes back.

## Build and run

Requires Windows 10 (2004) or later and the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run -c Release
```

Exit: right-click the player → *Exit*.
Settings are stored in `%AppData%\TaskbarPlayer\settings.json`, a diagnostic log in
`%LocalAppData%\TaskbarPlayer\log.txt`.

## How it works

- **Inside the taskbar**: a *layered child window* of `Shell_TrayWnd` (or `Shell_SecondaryTrayWnd` on other
  displays), drawn with per-pixel alpha so it blends with the taskbar background. It re-attaches after an
  Explorer restart (`TaskbarCreated`).
- **Free space**: the Windows 11 taskbar draws its icons in XAML, so their positions are read through
  UI Automation on a background thread.
- **Media**: `GlobalSystemMediaTransportControlsSessionManager`, the same API behind the Windows media flyout.
  Without a session, plain media keys are sent.
- **Volume**: Core Audio `IAudioEndpointVolume` on the default playback device.
- **Music apps**: discovered from the Start menu app list (`shell:AppsFolder`).

Windows 11 has no official API for taskbar widgets, so a major Windows update could break the embedding.

## License

[MIT](LICENSE)
