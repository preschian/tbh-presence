# TaskBarHero Discord Presence

Shows what you're doing in **TaskBarHero** on your Discord profile, live — the
stage you're on and the heroes you've deployed, updating as you play.

```
Playing TaskBarHero
Act 3 - Stage 3  (HELL, Lv 72)
Ranger Lv80, Sorcerer Lv23, Priest Lv35
23:41 elapsed
```

It only reads the game to see where you are; it never changes the game, your
save, or any files.

## Getting started

1. Download `TbhPresence.exe` from the [Releases page](../../releases).
2. Double-click it. A small helmet icon appears in your system tray (near the
   clock) — that's it running.
3. Play the game with Discord open. Your profile shows your current stage and
   party within a few seconds, and keeps up as you move between stages.

That's all. The first time you run it after a game update it spends about a
minute reading the game; after that it starts instantly.

To stop it, right-click the tray icon and choose **Quit**.

> **First-run warning:** Windows SmartScreen may show "Windows protected your
> PC" because the app isn't code-signed. Click **More info → Run anyway**. Some
> antivirus tools may also flag it, because it reads another program's memory to
> see your progress — that's expected for this kind of tool.

## Start it automatically with Windows

So you never have to remember to launch it:

- Press <kbd>Win</kbd>+<kbd>R</kbd>, type `shell:startup`, press Enter, and put a
  shortcut to `TbhPresence.exe` in the folder that opens.

It will sit quietly and wait for the game whenever you log in.

## One-time Discord setup

The presence works out of the box using a shared app, but the "Playing…" name
and the logo image are controlled by a Discord *application*, which only its
owner can configure. If you want your own name/logo:

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications)
   and create an application named **TaskBarHero** (this name is what shows after
   "Playing…").
2. Open **Rich Presence → Art Assets**, add an image named exactly **`tbh`**
   (you can use `assets/tbh.jpg` from this project) for the large logo.
3. Copy the application's **Application ID** and run the app with it:
   `TbhPresence.exe --client-id <your id>`.

Image and name changes can take a few minutes — and a Discord restart — to show
up.

## Options

Double-clicking is all most people need. From a terminal you can also:

```
TbhPresence.exe                 run in the system tray (default)
TbhPresence.exe --console       run with a visible log window
TbhPresence.exe --once          print the current game state once and exit
  --interval <sec>              how often to update (default 5)
  --client-id <id>              use your own Discord application
```

## Troubleshooting

- **Nothing shows on my profile.** In Discord: **Settings → Activity Privacy →
  "Display current activity as a status message"** must be on. Also make sure
  Streamer Mode isn't hiding it.
- **The logo image is missing.** The `tbh` art asset hasn't been uploaded to the
  Discord application yet (see *One-time Discord setup*), or it's still
  propagating — give it a few minutes and restart Discord.
- **It says the wrong stage after a game update.** The app re-reads the game
  automatically after an update; give the first run a minute. If it's still
  wrong, the game's internals changed — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Building it yourself

You don't need this to use the app — grab the exe from Releases. But if you'd
rather build from source, it's one command and needs nothing installed beyond
what ships with Windows:

```powershell
.\build.ps1
```

Details of how it all works are in [CONTRIBUTING.md](CONTRIBUTING.md).

## Privacy & fair use

Read-only and non-invasive — it never modifies the game. This is a single-player
idle game; still, mind the game's terms if any online leaderboard exists.

## Bonus: auto-synthesis mod

This repo also carries an optional, separate tool: a BepInEx plugin that
automates the Cube synthesis loop in-game. **Unlike the presence app, it is a
game mod** (it clicks the game's UI for you and requires installing BepInEx
into the game folder). See [autosynth/README.md](autosynth/README.md).
