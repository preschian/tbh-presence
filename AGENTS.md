# Repository Agent Guidelines

## Project context

- Read `README.md` for product behavior and `CONTRIBUTING.md` for architecture, build, and release details before making substantial changes.
- Keep changes focused. Preserve unrelated work already present in the worktree.
- This is a Windows companion app for TaskBarHero. The main app lives in `src/`; the in-game BepInEx plugin lives in `autosynth/`; `legacy/` is retained for the older PowerShell implementation.

## Development and verification

- Build the companion executables on Windows with `./build.ps1`.
- Build the auto-synthesis plugin with `dotnet build autosynth/TbhAutoSynth.csproj -c Release`; this requires the game-generated BepInEx interop assemblies.
- After changing `autosynth/Plugin.cs`, rebuild the plugin and refresh `autosynth/prebuilt/TbhAutoSynth.dll` when preparing a release.
- When adapting to a TaskBarHero update, keep the memory offsets in `src/Game.cs` and `legacy/Get-TbhStage.ps1` aligned and bump `CACHE_VERSION` so stale address caches are discarded.
- Verify every change with the most relevant available build or runtime check. If a required check cannot run in the current environment, state that clearly in the handoff.
- When a development change requires the companion app and/or TaskBarHero to be relaunched, relaunch the affected app or game directly and continue verification. No additional confirmation is needed.

## Commits and pull requests

- Use Conventional Commits for every commit: `<type>(<optional-scope>): <description>`.
- Use a Conventional Commit-style PR title: `<type>(<optional-scope>): <description>`.
- Start the PR description with the same Conventional Commit-style summary, then include concise `Changes` and `Verification` sections as applicable.
- Prefer standard types such as `feat`, `fix`, `docs`, `refactor`, `test`, `build`, `ci`, and `chore`. Add `!` or a `BREAKING CHANGE:` footer for breaking changes.
