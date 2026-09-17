# AGENTS.md

Unofficial addon for the Vintage Story mod [Toolsmith](https://mods.vintagestory.at/toolsmith),
extending its tinkering system to the vanilla spear. Project background, environment setup,
build/packaging steps, and the full Harmony-patch rationale live in `README.md` - read that
first. This file is the condensed checklist for agents working in this repo.

## Environment quick reference

- Game install: `~\AppData\Roaming\Vintagestory`
- Game data / save / installed mods: `~\AppData\Roaming\VintagestoryData` (mods in `Mods\`,
  config in `ModConfig\`, logs in `Logs\`). Separate git repo - never write into it from here.
- Target framework: `net10.0`, must match the installed game's runtime.
- `refs/Toolsmith.dll` (gitignored) is required to build - see `README.md`'s Build section for
  how to regenerate it, and bump `modinfo.json`'s `toolsmith`/`game` dependency versions
  whenever you do.

## Build

```
cd ToolsmithWeapons
dotnet build -c Release
```

## Testing a rebuilt DLL

**Fully quit and relaunch Vintage Story (not just reload the world/save) between every test.**
.NET won't reload an assembly with the same simple name in a running process - swapping mod zips
without restarting produces silent failure (mod just doesn't load, no crash). See `README.md`
for the full explanation and the zip-packaging steps (forward-slash entry paths required).

## Harmony patches: must-follow safety rules

Every `[HarmonyPrefix]`/`[HarmonyPostfix]` **must**:
- Wrap its body in try/catch and log-and-swallow on failure - an uncaught throw can crash the
  whole game.
- Use null-conditional (`?.`) access on every step of an entity/world chain.
- Never call back into `CollectibleObject.GetRemainingDurability()`/`GetMaxDurability()` from
  inside a `CollectibleBehavior` override of those same methods - infinite recursion causes a
  `StackOverflowException` that kills the process with **no exception log at all**.
- Restrict to server-side logic where the underlying game logic is server-authoritative
  (`world.Side == EnumAppSide.Server` / `.IsServer()`).

Full rationale for each rule is in `README.md`; the incident history is in `docs/findings.md`.

## Don't

- Don't publish to mods.vintagestory.at without being asked.
- Don't push to the `origin` remote without being asked.
- Don't bundle `Toolsmith.dll` (or any other mod's compiled binary) into a released zip or commit
  it to this repo - it's a compile-time reference only.
