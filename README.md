# PlayPollo

A [Playnite](https://playnite.link) plugin that adds games to a [Sunshine](https://github.com/LizardByte/Sunshine)
or [Apollo](https://github.com/ClassicOldSong/Apollo) game-streaming server as apps. It uses the server's admin
REST API, so it needs no elevation and works against a server on another machine.

## Using it

1. Install the `.pext` (double-click it, or drop it into Playnite).
2. Open **Add-ons > Extensions > Generic > PlayPollo** and fill in the server's admin username and password.
   The install folder and web UI port are detected automatically (see [Server detection](#server-detection)).
   Use **Browse...** for a custom location.
3. Press **Test connection**. The first success pins the server's self-signed certificate; later connections
   only trust that certificate. If the certificate changes later, use **Forget certificate** and test again.
4. Right-click one or more games > **Sunshine / Apollo** and pick an action.

### Right-click menu

The menu reflects what's already on the server, so it only offers actions that make sense for each game:

| Game state | Actions shown |
| --- | --- |
| Not added | **Add**, **Add (launch through Playnite)**, **Add (launch the game's executable)** |
| Added | **Update**, **Update (launch through Playnite)**, **Update (launch the game's executable)**, **Update (choose the executable...)**, **Remove** |

With several games selected, each action applies only to the games it fits and shows a count, for example
"Add (3 games)". The main menu (**Extensions > Sunshine / Apollo**) has **Test connection** and
**Sync all exported games**.

### Launch method

- **Through Playnite** (default): the app runs `playnite://playnite/start/<game id>`, so any library or emulator
  works. The server can't track the game process itself. This is the safer choice for games that need their launcher
  (Steam, anti-cheat) to start properly.
- **Executable**: the app runs the game's own executable with that executable's folder as the working directory, so
  the server can track the process. Launchers in front of the game are skipped.

The method is remembered per game. Pick it from the right-click menu, or set the default in the settings.

#### Finding the executable

Library plugins such as Steam supply the play action themselves, so Playnite has no file for the plugin to read.
In Executable mode the plugin therefore:

1. uses the game's own file-based play action if it has one;
2. otherwise scans the install folder (up to four levels deep), ignoring installers, runtimes, crash handlers and
   anti-cheat helpers, and ranks what's left by name match, location (`bin`, `Win64`...), size and 64-bit;
3. uses the best match if it's clearly ahead, and otherwise **shows a picker** so you can choose. For example
   Baldur's Gate 3 offers `bin\bg3.exe` and `bin\bg3_dx11.exe`.

Your choice is remembered per game. Use **Update (choose the executable...)** to pick again. If you cancel the
picker, that game is skipped. If nothing is found (for example the game isn't installed), the game falls back to the
Playnite launch and the summary says so. Auto-sync never shows the picker.

### Cover art

Covers are converted to PNG and set as the app image.

- **Server on this PC** (`localhost`, a loopback or local IP, or this machine's name): the PNG is saved in the plugin's
  data folder (`%AppData%\Playnite\ExtensionsData\<plugin id>\covers`) and the app's image path points at it. This is
  deliberate. Sunshine and Apollo write uploaded covers in text mode on Windows, which turns every `0x0A` byte into
  `0x0D 0x0A` and corrupts the PNG, so uploading to a Windows server can't produce a valid image.
- **Server on another PC**: the cover is uploaded through the API. If that server is also Windows, the same
  corruption applies and the cover won't display.

### Ending the stream when the game closes

With **End the stream when an exported game is closed in Playnite** on (the default), closing an exported game in
Playnite tells the server to close the running app, which ends the stream and returns the client to its app list.

- **Apollo** reports which app is running, so the stream is only ended if it's that game. Closing one exported game
  while streaming a different one leaves the stream alone.
- **Sunshine** doesn't report the running app, so the plugin ends whatever stream is active.
- The call runs in the background. An unreachable server is only logged, since that's normal when the PC is off.
  Bad credentials or a changed certificate raise a Playnite notification.

### Server detection

The install folder is found by checking, in order: the path you entered, the `ApolloService` and `SunshineService`
Windows services, then `Program Files\Apollo` and `Program Files\Sunshine`. Playnite is a 32-bit program, so both the
native and the `(x86)` Program Files folders are checked. The web UI port is read from `sunshine.conf` (base port + 1,
default 47990). Sunshine and Apollo are told apart by their API responses when the server type is set to Auto.

### Other behaviour

- Re-running **Update** updates the existing entry. Apollo apps are matched by uuid. Sunshine apps have no id,
  so they're matched by the name and command that were last written.
- With **auto-sync** on, changing an exported game's name, cover or launch details in Playnite updates the server
  (batched, a couple of seconds after the last edit). Games are never added automatically.
- The admin password is stored encrypted with Windows DPAPI for your user account.
- Apollo only accepts a session login, while Sunshine accepts HTTP Basic. The plugin tries Basic first and falls back
  to logging in.

## Building

```powershell
.\build.ps1
```

This needs Visual Studio (for the WPF XAML build) and a Playnite install (for `Toolbox.exe`). It runs the tests,
builds the plugin and writes `dist\PlayPollo_<id>_<version>.pext`. The tests alone run with
`dotnet test tests\PlayPollo.Tests`.

Keep `Newtonsoft.Json` at **10.0.3**. Playnite ships 10.0.0.0, and Toolbox leaves out any assembly Playnite already
provides when it packs, so a newer version fails to load with "Extension failed to load properly".

If the extension won't load, the reason is in `%AppData%\Playnite\playnite.log` (search for `PlayPollo`).

## Layout

- `src/PlayPollo.Core`: the server clients, detection, executable finder, mapping and export logic (no UI, unit-tested).
- `src/PlayPollo`: the Playnite plugin class, settings view-model and WPF settings view.
- `tests/PlayPollo.Tests`: xunit tests using a fake HTTP handler and fake server client.

## Known limits

- Tested against a live Apollo only for connection and detection behaviour. Sunshine is covered by unit tests that fake the
  server.
- Games with no executable in their install folder (some Microsoft Store games) can only use the Playnite launch.
