# Legacy PowerShell prototype

The original proof-of-concept, superseded by the `TbhCompanion.exe` tray app
(`src/`). Kept for development and inspection — the reader logic mirrors
`src/Memory.cs` / `src/Game.cs` and is handy for poking at the game from a
console without rebuilding the exe.

- `Get-TbhStage.ps1` — resolve + poll the current stage, print or emit JSON.
- `Start-TbhPresence.ps1` — the original presence loop on top of the reader.
- `TbhMemory.cs` — read-only memory reader compiled at runtime via `Add-Type`.
- `cache.json` — resolved-address cache written next to the scripts.

Requires Windows PowerShell 5.1+. Run from this folder:

```powershell
.\Get-TbhStage.ps1 -Once
```
