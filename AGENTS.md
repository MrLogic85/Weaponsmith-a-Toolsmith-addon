# Weaponsmith - a Toolsmith addon

Unofficial addon for the Vintage Story mod [Toolsmith](https://mods.vintagestory.at/toolsmith) (by JonR / Mario90900).
Toolsmith gives tools (axes, pickaxes, etc.) a head/handle/binding system with independent
durability, sharpening, and reforging. It was never designed with weapons in mind. This addon
extends Toolsmith's own tinkering system to cover the vanilla spear, and fixes bugs that only
show up because a spear is a thrown, single-use-per-throw item - a category Toolsmith's other
supported tools never belonged to.

Not published to mods.vintagestory.at. Local/personal project for now.

## Environment

- Game install: `C:\Users\metca\AppData\Roaming\Vintagestory` (contains `VintagestoryAPI.dll`,
  `VintagestoryLib.dll`, and `Mods\VSSurvivalMod.dll` / `Mods\VSEssentials.dll` - referenced
  directly by absolute path in the csproj).
- Game data / save / installed mods: `C:\Users\metca\AppData\Roaming\VintagestoryData`
  (mods go in `VintagestoryData\Mods`, per-mod config in `VintagestoryData\ModConfig`, logs in
  `VintagestoryData\Logs`).
- That VintagestoryData folder is itself a separate git repo (a curated mod list, see its own
  AGENTS.md there) - unrelated to this repo except that Toolsmith and this addon both end up
  installed in its `Mods\` folder for testing. Never write into that repo from here.
- dotnet SDK is installed (`dotnet --version` -> 10.x). Target framework: `net10.0`, matching
  the game's own runtime (check `Vintagestory.runtimeconfig.json`'s `tfm` if the game ever
  updates its target framework - the csproj needs to match).
- `ilspycmd` (ILSpy decompiler CLI) is available via `dotnet tool install -g ilspycmd` - used
  to read Toolsmith's and the game's own compiled logic when no source/docs cover something.
  Toolsmith's own source is public: https://github.com/Mario90900/Toolsmith (branch `master`).

## Build

```
cd ToolsmithWeapons
dotnet build -c Release
```

Output goes to `ToolsmithWeapons/bin/Release/Mod/` (a ready-to-zip mod folder: `modinfo.json`,
`ToolsmithWeapons.dll` + friends, `assets/`).

`refs/Toolsmith.dll` is required to compile (we call Toolsmith's own public extension methods
and behavior classes rather than reimplementing their durability/part logic) but is **not**
committed - it's someone else's compiled mod, not ours to redistribute. It's gitignored.
Regenerate it by extracting `Toolsmith.dll` from the installed Toolsmith zip in
`VintagestoryData\Mods\toolsmith_*.zip` into `refs\Toolsmith.dll` at the repo root (sibling of
`ToolsmithWeapons\`). A PowerShell one-liner:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead("<path to toolsmith_*.zip>")
$entry = $zip.Entries | Where-Object { $_.FullName -match 'Toolsmith\.dll$' }
[System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "refs\Toolsmith.dll", $true)
$zip.Dispose()
```

Whenever you regenerate `refs/Toolsmith.dll` against a newer Toolsmith release, also bump the
`"toolsmith"` version in `ToolsmithWeapons/modinfo.json`'s `dependencies` to match - it's a
minimum-version constraint, and every `Fixes/` patch targets specific internal methods only
verified to exist in the exact version compiled against. Leaving it pointing at an older version
would let the mod load against a Toolsmith release we never actually tested it with.

The same logic applies to `"game"` in that same `dependencies` block: several `Fixes/` patches
target vanilla engine internals decompiled from a specific installed game version (check
`assets/version-*.txt` in the game install directory), not just the public API surface. If you
re-verify or re-decompile against a newer game release, bump `"game"` to match that version too -
don't leave it at whatever old floor happens to still build.

## Packaging and deploying for a test run

Zip the contents of `ToolsmithWeapons/bin/Release/Mod/` (not the folder itself - `modinfo.json`
must be at the zip root) with **forward-slash** entry paths (Windows' `Compress-Archive` writes
backslashes, which the game's asset loader may not resolve correctly cross-platform) - build the
zip manually via `System.IO.Compression.ZipFile` + `ZipFileExtensions.CreateEntryFromFile`,
setting each entry name explicitly with `/`. Stage the result at
`builds\ToolsmithWeapons_<version>.zip` (gitignored - never commit these) before copying it to
`VintagestoryData\Mods\ToolsmithWeapons_<version>.zip`, removing any older version from `Mods\`
first.

**Critical: fully quit and relaunch Vintage Story (not just reload the world/save) between every
test of a rebuilt DLL.** .NET's default AssemblyLoadContext will not let you load a second
assembly with the same simple name once one is already loaded in the running process. Swapping
mod zips without restarting the game produces:
`Assembly with same name is already loaded` - and the whole mod silently fails to load for that
session (no crash, just missing functionality - easy to mistake for a regression in the mod
itself). The game's own error message for this is explicit if you check the log:
"Please restart the game... Most likely cause is switching mod versions after already playing
one world." Bump the version string in `modinfo.json` on every rebuild anyway (good hygiene,
does not avoid the reload issue by itself).

## Harmony patches: safety rules learned the hard way

Every `[HarmonyPrefix]`/`[HarmonyPostfix]` in this project **must**:
- Wrap its body in try/catch and log-and-swallow on failure. A patch that throws can crash the
  whole game (we shipped a `NullReferenceException` in a logging-only patch that did exactly
  that - see `docs/findings.md`).
- Use null-conditional (`?.`) access on every step of a chain reached through an entity/world -
  fields like `Entity.World` are not guaranteed populated at every point in an entity's
  lifecycle (e.g. very early ticks after spawn).
- Never call back into `CollectibleObject.GetRemainingDurability()`/`GetMaxDurability()` from
  inside a `CollectibleBehavior` override of those same methods, even indirectly. Toolsmith's own
  `GetToolheadCurrentDurability()` extension method calls `GetRemainingDurability()` internally -
  overriding that method and calling this helper from inside the override is infinite recursion
  -> `StackOverflowException` -> the process dies with **no exception log at all** (a stack
  overflow can't be caught or logged by managed code). If you see the game vanish silently with
  nothing in `client-crash.log` or `server-main.log`, check Windows Event Viewer's Application
  log for a `CLR20r3`/`Application Error` entry with exception code `0xc00000fd` - that's the
  fingerprint.
- Restrict to server-side logic where the underlying game logic itself is server-authoritative
  (`world.Side == EnumAppSide.Server` / `.IsServer()`), to avoid client-prediction-path edge
  cases and log noise.

## Current status

See `docs/findings.md` for the full investigation log (root causes, confirmed via reading
Toolsmith's and the game's decompiled source, not guesses) and what's fixed vs. still open.

## Don't

- Don't publish to mods.vintagestory.at without being asked.
- Don't push to the `origin` remote without being asked.
- Don't bundle `Toolsmith.dll` (or any other mod's compiled binary) into a released zip or commit
  it to this repo - it's a compile-time reference only.
