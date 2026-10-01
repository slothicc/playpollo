# PlayPollo

PlayPollo is a [Playnite](https://playnite.link) plugin for adding games to a
[Sunshine](https://github.com/LizardByte/Sunshine) or [Apollo](https://github.com/ClassicOldSong/Apollo) streaming
server. It uses the server's admin REST API, requires no elevation, and supports remote servers.

## Features

- Add, update, and remove Playnite games in Sunshine or Apollo from the right-click menu, including bulk actions.
- Launch through Playnite or run the game's executable directly.
- Automatic executable discovery, allowing for choice when multiple .exe are found.
- Playnite cover art added automatically to applications.
- Optional auto-sync of name, cover, and launch changes for exported games.
- Automatically closes the streaming session when Playnite detects the game has closed. (Optional)
- Automatic server detection (Sunshine/Apollo), with ability to specify custom server install path.

## Requirements

- Windows with [Playnite](https://playnite.link).
- A Sunshine or Apollo server with its web UI admin credentials.

## Setup

1. Install the `.pext` by double-clicking it or dropping it into Playnite.
2. In **Add-ons > Extensions > Generic > PlayPollo**, enter the server's admin username and password. The install
   folder and web UI port are detected automatically; use **Browse...** to choose a custom folder.
3. Select **Test connection**. On first success, PlayPollo pins the server's self-signed certificate and trusts only
   that certificate on later connections. If it changes, select **Forget certificate** and test again.
4. Right-click games and choose **Sunshine / Apollo**.

### Authentication

The admin password is encrypted with Windows DPAPI for your user account. Apollo uses session login; Sunshine supports
HTTP Basic. PlayPollo tries Basic first, then falls back to session login.

## Use

The context menu reflects each game's server state:

| State | Actions |
| --- | --- |
| Not added | **Add**, **Add (launch through Playnite)**, **Add (launch the game's executable)** |
| Added | **Update**, **Update (launch through Playnite)**, **Update (launch the game's executable)**, **Update (choose the executable...)**, **Remove** |

For multiple selections, each action applies only to eligible games and shows a count (for example, **Add (3 games)**).
The **Extensions > Sunshine / Apollo** menu provides **Test connection** and **Sync all exported games**.

### Launch options

The launch method is remembered per game. Choose it from the context menu or set the default in settings:

- **Through Playnite** (default) runs `playnite://playnite/start/<game id>`, supporting any library or emulator. Best for games that need a launcher such as Steam or anti-cheat.
- **Executable** runs the game's executable from its own folder.

### Executable discovery

For executable mode, PlayPollo uses a file-based play action when available. Otherwise, it scans up to four install
folder levels, excludes installers, runtimes, crash handlers, and anti-cheat helpers, then ranks candidates by name,
location (such as `bin` or `Win64`), size, and 64-bit status. It selects a clear best match or opens a picker when
candidates are close (for example, Baldur's Gate 3's `bin\bg3.exe` and `bin\bg3_dx11.exe`). The selection is saved per
game; use **Update (choose the executable...)** to change it. Canceling the picker skips that game. If no executable
is found, PlayPollo falls back to Playnite launch and reports this in the summary. Auto-sync never opens the picker.

## How it works

- **Updates and sync:** Update modifies the existing entry. Apollo entries are matched by UUID; Sunshine entries, which
  have no ID, are matched by the last-written name and command. With auto-sync enabled, changes to an exported game's
  name, cover, or launch details are sent in a batch a few seconds after the last edit. Games are never added
  automatically.
- **Cover art:** Covers are converted to PNG. For a local server (`localhost`, a loopback or local IP, or this
  machine's name), the PNG is saved in `%AppData%\Playnite\ExtensionsData\<plugin id>\covers` and referenced by path.
  This avoids a Windows server bug: Sunshine and Apollo upload covers in text mode, changing each `0x0A` byte to
  `0x0D 0x0A` and corrupting the PNG. Remote servers receive the cover through the API; Windows servers have the same
  corruption issue, so covers won't display there.
- **Ending a stream:** By default, closing an exported game in Playnite tells the server to close its running app,
  ending the stream and returning the client to its app list. Apollo reports the running app, so only that game is
  closed. Sunshine does not, so the active stream is ended regardless of which app is running. The request runs in the
  background: unreachable servers are logged, while bad credentials or a changed certificate trigger a Playnite
  notification.
- **Server detection:** PlayPollo checks the configured path, then the `ApolloService` and `SunshineService` Windows
  services, then `Program Files\Apollo` and `Program Files\Sunshine`. It checks both native and `(x86)` Program Files
  locations because Playnite is 32-bit. The web UI port comes from `sunshine.conf` (base port + 1; default `47990`).
  In Auto mode, the server type is identified from API responses.

## Limitations

- Games are never added automatically; auto-sync only updates games you have already exported.
- Games without an executable in their install folder (including some Microsoft Store games) can only launch through
  Playnite.
- Launching through Playnite means the server cannot track the game process.
- Covers don't display on remote Windows servers because of the text-mode upload bug described above.
- With Sunshine, ending a stream closes the active stream regardless of which app is running.
- Live-server testing has covered Apollo connection and detection; Sunshine is covered only by tests using a fake
  server.

## Build and test

```powershell
.\build.ps1
```

Requires Visual Studio for the WPF XAML build and a Playnite installation for `Toolbox.exe`. The script runs tests,
builds the plugin, and writes `dist\PlayPollo_<id>_<version>.pext`. Run tests alone with
`dotnet test tests\PlayPollo.Tests`.

Keep `Newtonsoft.Json` at **10.0.3**: Playnite ships 10.0.0.0, and Toolbox excludes assemblies already provided by
Playnite when packing. A newer version causes "Extension failed to load properly". For load errors, check
`%AppData%\Playnite\playnite.log` and search for `PlayPollo`.

## Project layout

- `src/PlayPollo.Core`: server clients, detection, executable discovery, mapping, and export logic; no UI, unit-tested.
- `src/PlayPollo`: Playnite plugin, settings view-model, and WPF settings view.
- `tests/PlayPollo.Tests`: xUnit tests with a fake HTTP handler and server client.
