# Findings

Investigation log. Each entry: what we saw, the confirmed root cause (with the exact code
location), and the fix status. "Confirmed" means traced through decompiled/source code and,
where possible, verified against server log evidence from an actual test run - not a guess.

---

## 1. Spear tinkerable at all (baseline feature)

Toolsmith builds its `TinkerableTools`/`ToolHeads`/etc. regex config at startup by aggregating
**every** mod's JSON files found at the asset path `config/toolsmith/regex/tinkerabletools/*.json`
(and `toolheads/`, `singleparttools/`, etc.) via `api.Assets.GetMany<List<string>>(...)` in
`ToolsmithModSystem.cs`. This is Toolsmith's own built-in cross-mod compatibility hook - other
compat mods for other item categories already use it. We ship
`ToolsmithWeapons/assets/toolsmithweapons/config/toolsmith/regex/tinkerabletools/tinkerabletools-weapons.json`,
which gets merged into Toolsmith's `TinkerableTools` regex automatically. See finding #4 for what's
in that file and why it's `"spear-"` rather than bare `"spear"`.

This only takes effect while `EnableEditsForRegex: false` in `ModConfig/Toolsmith.json` (the
default) - that flag, when true, skips the asset-file scan entirely in favor of whatever the user
manually typed into the config, so don't recommend enabling it alongside this addon.

Status: **working**, no code required for this part, content-only.

---

## 2. Duplicate spear when a thrown, tinkered spear breaks

**Symptom:** throw a tinkered spear, it breaks on impact (handle or binding worn out - not
necessarily the head). Toolsmith correctly hands you the salvaged head/handle/binding parts. But
a second, seemingly-intact copy of the spear is *also* left behind as a pickable item where it
landed.

**Root cause (confirmed via decompiling `VSEssentials.dll`/`VSSurvivalMod.dll` and reading
Toolsmith's source, then verified against a real playtest log):**

1. `EntityProjectile`/`EntityProjectileBase` (the flying spear entity) calls
   `ProjectileStack.Collectible.DamageItem(world, this, new DummySlot(ProjectileStack), 1, true)`
   on impact - passing a throwaway `DummySlot` *wrapping* the projectile's real `ItemStack`, not
   the real slot.
2. That routes into `CollectibleBehaviorTinkeredTools.OnDamageItem`, which (correctly) computes
   new head/handle/binding durabilities and, if the tool "fell apart" (handle or binding at/under
   0, head still intact - the *common* way Toolsmith tools degrade, not the rare case), calls
   `TinkeringUtility.HandleBrokenTinkeredTool(...)`. That function hands out the loose parts and,
   for the "fell apart, head still fine" case, does `itemslot.Itemstack = null` to clear the
   *slot it was given*.
3. Because that slot was the disposable `DummySlot`, nulling it does nothing to the entity's real
   `ProjectileStack` field - the actual carried item is completely untouched. It was never the
   head that broke, and the flat vanilla `durability` attribute Toolsmith keeps in sync mirrors
   **head** durability only - so the untouched stack still reads as fully intact.
4. The vanilla projectile-impact code (`EntityProjectile.IsColliding` for terrain hits,
   `EntityProjectileBase.DamageProjectile` for entity hits) then asks "is this item still good?"
   by reading `GetRemainingDurability()` on that same untouched stack - sees it's fine - and lets
   the projectile entity survive instead of despawning. That surviving entity is the duplicate.

This only happens on **throw**, never on melee use, because melee damage is applied through the
player's real hotbar slot, where nulling the slot actually works as Toolsmith intended.

**Log evidence** (from a real test throw, `game:spear-generic-flint`):
```
[OnDamageItem-IN]  head=158 handle=16 binding=0
[OnDamageItem-OUT] slotItemNowNull=True        <- the DummySlot got nulled...
[TerrainImpact-OUT] remainingDurIfAny=158      <- ...but the real stack is untouched
```

**Fix:** `ToolsmithWeapons/Fixes/ProjectileBrokenToolCleanupPatch.cs`. Harmony postfix on both
`EntityProjectile.IsColliding` and `EntityProjectileBase.DamageProjectile`. After the vanilla
impact code runs, checks the projectile's *actual* carried stack via Toolsmith's own
`GetToolheadCurrentDurability()` / `GetToolhandleCurrentDurability()` / `GetToolbindingCurrentDurability()`
(not the flat `GetRemainingDurability()`, which is exactly what's blind to a handle/binding
break). If the tool has genuinely broken/fallen apart, force-despawns the projectile
(`EnumDespawnReason.Removed` - no drops) since Toolsmith already handed out the real parts. This
is a cleanup-after-the-fact fix, not a prevention - deliberately, since the actual break/give-parts
logic already ran correctly by that point; we're just removing the leftover husk vanilla didn't
know to remove.

Status: **fixed and confirmed working** - `[Cleanup]` log line fired on every break in a real
test session (both terrain and entity impacts), no leftover duplicate afterward.

---

## 3. Spear auto-throws (or jumps straight to aim) right when it's assembled

**Symptom:** finishing assembly of a tinkered spear (the hold-right-click-with-binding-in-offhand
step) sometimes throws the spear immediately, as if you'd already aimed and released; other times
it just enters the aiming pose, as if you'd only just pressed the button. Inconsistent between
attempts. Does not depend on how the spear is assembled - it's a timing/state issue, not a
technique issue.

**Root cause (confirmed via Toolsmith source, and now confirmed in a real playtest log too -
see "Log evidence" below):**

`Toolsmith/ToolTinkering/Items/ItemTinkerToolParts.cs`, `OnHeldInteractStop` (and
`OnHeldInteractCancel`, same pattern):

```csharp
public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, ...) {
    if (slot.Itemstack.PartBeingCrafted() && secondsUsed >= (TimeToCraftTinkerTool - 0.1)) {
        slot.Itemstack.ClearPartBeingCrafted();
        if (byEntity.World.Side.IsServer() && TinkeringUtility.ValidBindingInOffhand(byEntity)) {
            TinkeringUtility.AssembleFullTool(slot, byEntity, blockSel);   // swaps the item HERE
        }
        ...
    }
}
```

Assembling a tool is itself a hold-right-click-and-release interaction - structurally identical
to `ItemSpear`'s own aim-and-throw interaction (`ItemSpear.OnHeldInteractStart/Step/Stop` in
`VSSurvivalMod.dll`). `AssembleFullTool` replaces `slot.Itemstack` with the finished `ItemSpear`
*synchronously, inside the same `OnHeldInteractStop` call* that's still processing the
release of the right mouse button on the *previous* item (the tool bundle).

The engine's "how long has the player been holding the interact button" tracking is tied to the
player/controls, not to which item happens to be sitting in the slot. It has no way to know
Toolsmith just swapped the item out from under it mid-release. Depending on how client
prediction and server authority each independently notice the item changed relative to the
button-state edge for that same tick:
- sometimes it's treated as a fresh press -> `ItemSpear.OnHeldInteractStart` fires clean -> aim
  pose (matches "goes to start of aiming").
- sometimes it's treated as a continuation/release of the *already-elapsed* hold -> the new
  spear's `OnHeldInteractStop` fires immediately with the old, already-large `secondsUsed`
  (`ToolsmithConstants.TimeToCraftTinkerTool = 2.5f`; the spear's own throw threshold is only
  0.35s) -> instant throw.

**Log evidence (2026-09-17, session 10:22-10:32, ~15 real throws recorded via the existing
`[ThrowStart]` diagnostic from finding #2 - no assembly-specific logging needed to catch this,
it goes through the exact same `ItemSpear.OnHeldInteractStop` code as a normal throw):**

```
10:24:38  secondsUsed=2.53  remainingDur=160/160  head=160 handle=32 binding=16   (fresh spear)
10:25:19  secondsUsed=2.52  remainingDur=160/160  head=160 handle=32 binding=16   (fresh spear)
10:25:46  secondsUsed=2.53  remainingDur=160/160  head=160 handle=32 binding=16   (fresh spear)
10:27:22  secondsUsed=2.53  remainingDur=160/160  head=160 handle=32 binding=16   (fresh spear)
10:27:51  secondsUsed=2.53  remainingDur=160/160  head=160 handle=32 binding=16   (fresh spear)
```

versus the genuine player-controlled throws logged in the same session, which vary naturally
as expected: `0.07, 0.11, 0.23, 0.24, 0.30, 0.35, 0.37, 0.45, 0.49, 1.30`.

`secondsUsed ≈ 2.5` (`TimeToCraftTinkerTool`, plus a few ticks of natural overshoot before the
release event is processed) appearing repeatedly, always paired with completely untouched full
durability (i.e. an item that has never been thrown or damaged before - only possible for a
spear the very moment it's created), is conclusive: this is the crafting hold-duration being
fed straight into the new spear's throw-release check, not a player who happened to aim for
exactly 2.5 seconds five separate times.

**This happened 5 times in ~15-20 assemblies in this session - not the "1 in 20" it felt like
subjectively.** Likely explanation: on the other 4 occasions the player was already about to
press-and-hold for a real throw anyway, so the automatic one blended into their own motion and
wasn't consciously registered as separate. Don't trust a player's felt frequency for this bug -
count it from the logs.

This is unique to spear (and would affect any future weapon we add that has its own
hold-to-charge interact behavior) because none of Toolsmith's other supported tools implement
`OnHeldInteractStart/Step/Stop` themselves - mining/attacking is a different interaction category
(`OnHeldAttackStart`/`OnBlockBrokenWith`), so this collision could never happen for an axe or
pickaxe. Toolsmith was never wrong for not handling it.

**Exact mechanism (confirmed by decompiling `VintagestoryLib.dll`):**
`Vintagestory.Client.NoObf.SystemMouseInWorldInteractions.HandleHandInteraction` is what
actually computes `secondsUsed` and dispatches it, every tick the mouse button is held:

```csharp
float num = (game.ElapsedMilliseconds - entityPlayer.Controls.UsingBeginMS) / 1000f;
...
activeHotbarSlot.Itemstack.Collectible.OnHeldUseStop(num, ...);
```

`CollectibleObject.OnHeldUseStop`/`OnHeldUseStep`/`OnHeldUseCancel` (in `VintagestoryAPI.dll`)
then route straight through to `OnHeldInteractStop`/`Step`/`Cancel` (the methods Toolsmith and
`ItemSpear` actually override) whenever the active hand-use isn't `HeldItemAttack`. So
`secondsUsed` is never tracked per-item at all - it's always just
`(now - EntityControls.UsingBeginMS) / 1000`, a single `public long UsingBeginMS` field on the
*player's* controls (`EntityAgent.Controls`), set once when the button first goes down and never
touched again until the hold ends. Toolsmith swapping the item mid-hold has no way to reach or
reset it, so the new spear inherits however long the *previous* item (the bundle) had already
been held.

**Fix:** `ToolsmithWeapons/Fixes/AssemblyStaleInteractionFixPatch.cs`. Harmony postfix on
`TinkeringUtility.AssembleFullTool` - right after it swaps the item, resets
`byEntity.Controls.UsingBeginMS = byEntity.World.ElapsedMilliseconds` ("now"). Any interaction
dispatch that still lands on the new item this tick then computes `secondsUsed ≈ 0`, below any
reasonable hold-to-charge threshold - at worst it starts a fresh aim pose (correct behavior
anyway) instead of completing a phantom throw with 2.5s of borrowed crafting time. This directly
addresses the framing "the spear wasn't the item we had in hand when we pressed the button" -
rather than trying to suppress the dispatch call outright, it corrects the one piece of state
(`UsingBeginMS`) that made the engine misattribute the elapsed hold to the wrong item in the
first place.

**Status: fix applied, playtested, confirmed INSUFFICIENT.** Two assemblies in a row, same
session, same code:

```
10:49:29.881  [AssembleFullTool-IN]  beforeItem=tinkertoolparts
10:49:29.885  [AssembleFullTool-OUT] afterItem=spear  <- UsingBeginMS reset runs here
10:49:29      [ThrowStart] secondsUsed=2.54           <- still stale! (bad - auto-threw)

10:49:51.412  [AssembleFullTool-IN]  beforeItem=tinkertoolparts
10:49:51.412  [AssembleFullTool-OUT] afterItem=spear  <- same reset runs here
10:49:51.687  [SpearInteractStart] firstEvent=False   <- clean fresh start (good)
```

If the reset had taken effect for the first case, `secondsUsed` could not possibly compute as
2.54 moments later - that would require ~2.5 *more* seconds to have passed since the reset,
which the timestamps rule out. Something is either reading a different `UsingBeginMS` (client and
server run genuinely separate `EntityPlayer`/`EntityControls` instances even in singleplayer -
only the *locally controlled player's* client-side copy is explicitly unified via
`EntityPlayer.SetCurrentlyControlledPlayer()`, which does not by itself explain a
*server*-side mismatch) or there's a second, not-yet-found call path into
`OnHeldInteractStop`/`OnHeldUseStop` that bypasses whatever the normal dispatch checks.

**A sharper question raised in review:** does the engine's own dispatch check "is a hand-use
session actually active" (`Controls.HandUse != None`) before allowing a Stop/Cancel to fire on an
item? Answered below - the honest answer is "sort of, but not in a way that helps": it checks
once, too early to matter.

**Root cause, fully confirmed** via a captured `Environment.StackTrace` on the actual bad call
(`0.6.1-diagnostic` build) plus decompiling the two methods it pointed at
(`Vintagestory.Server.ServerSystemInventory`, `Inventory.cs`):

```csharp
// HandleHandInteraction, case EnumHandInteractNw.StopHeldItemUse:
if (controls.HandUse != EnumHandInteract.None) {          // gate checked ONCE, before the loop
    while (controls.HandUse != EnumHandInteract.None && controls.UsingCount < handInteraction.UsingCount && n++ < 5000) {
        callOnUsing(itemSlot, player, blockSelection, entitySelection, ref secondsPassed);
    }
    controls.HandUse = EnumHandInteract.None;
    itemSlot.Itemstack?.Collectible.OnHeldUseStop(secondsPassed, itemSlot, ...);   // UNCONDITIONAL - no re-check
}

// callOnUsing:
controls.HandUse = slot.Itemstack.Collectible.OnHeldUseStep(secondsPassed, slot, ...);
if (callStop && controls.HandUse == EnumHandInteract.None) {
    slot.Itemstack?.Collectible.OnHeldUseStop(secondsPassed, slot, ...);   // the REAL, correct Stop
}
// secondsPassed += 0.02f;  -- NOT read from the clock during this catch-up loop, just simulated per iteration
```

The `while` loop exists to replay `OnHeldUseStep` calls the client already reported (via
`UsingCount`) that the server hasn't caught up to yet - `secondsPassed` inside it is a *simulated*
clock (`+= 0.02f` per iteration), not a live read of `UsingBeginMS`. When
`ItemTinkerToolParts.OnHeldInteractStep`'s crafting timer elapses mid-loop, `HandUse` flips to
`None` right there, and `callOnUsing` immediately fires a **correct** `OnHeldUseStop` on the
bundle - this is what actually finishes the tool (`AssembleFullTool` runs here, item becomes the
spear). Control returns to `HandleHandInteraction`; the `while` condition is now false (`HandUse`
is already `None`) so the loop exits - but the gate at the top was only checked *before* the loop
ever started, so the unconditional `OnHeldUseStop` line after the loop **still runs**, on
`itemSlot` - the same slot, now holding the spear. That's the second, spurious call, and it's
vanilla engine code shared by every held-item interaction in the game, not something Toolsmith or
we can safely patch directly (a Harmony transpiler on `HandleHandInteraction` was considered and
rejected as too high-risk for how central that method is).

This also directly answers the review question: the engine's per-item identity is never checked
at all, only the player-level `HandUse` enum, and that enum's *gate* is evaluated too early (once,
before the loop) to catch a session that completes *during* the loop. There's no code path that
would let us "force stop" our way out of it either - the final call is unconditional once inside
the `case` block, so nothing set during `AssembleFullTool` (not `HandUse`, not `UsingBeginMS`, not
an extra self-triggered Stop) can prevent that specific line from executing.

**Fix:** `ToolsmithWeapons/Fixes/SpearAssemblyEchoSuppressionPatch.cs` +
`AssemblyStaleInteractionFixPatch.cs`. `AssembleFullTool`'s postfix tags the freshly-created
`ItemStack` (`toolsmithweapons_justAssembledEcho` attribute - set *only* there, i.e. only for a
tool finished by holding right-click in hand; workbench/grid-recipe crafting never calls
`AssembleFullTool` at all, so it's never tagged). `ItemSpear`'s own
`OnHeldInteractStart`/`OnHeldInteractStop`/`OnHeldInteractCancel` each check for and consume
(remove) that tag on entry via a Harmony prefix; whichever one receives the very next call on that
exact item swallows it completely (prefix returns `false`, skipping the original - no aim pose,
no throw, no cancel bookkeeping) and the tag is gone, so every interaction after that is
completely normal. Applied to `Start` as well as `Stop`/`Cancel` deliberately (not left as
"acceptable fallout") - the echo can surface as either, and both are equally an artifact of the
same vanilla double-dispatch, not a real player action. Not gated behind
`EnableDiagnosticLogging` or restricted to server-side, since the tag can land on both the
server's and the synced client's copy of the stack and both should suppress consistently. The
`UsingBeginMS` reset from the earlier attempt is kept as a harmless second line of defense.

Status: **fix applied (`0.7.0`), not yet playtested.** Also fixed in this version: our own mod
was calling `harmony.PatchAll()` twice (`Start()` runs once per side, both patching the same
process-wide Harmony ID) - every log line all session was a duplicate pair because of this, not
a game-engine effect. Guarded with `Harmony.HasAnyPatches(HarmonyId)` now.

Diagnostic logging for this (`ToolsmithWeapons/Diagnostics/AssemblyTimingLogPatches.cs`) is in
place: `[BundleStop-IN/OUT]`, `[BundleCancel-IN/OUT]`, `[AssembleFullTool-IN/OUT]`,
`[SpearInteractStart]`, all with millisecond timestamps and `Controls.HandUse`/`RightMouseDown`
at each point, plus Client/Server side so the two independent dispatch paths can be told apart.

**Observed frequency (2026-09-17 playtest, ~20 spears assembled):** the "flies away instantly"
variant is rare - happened once, right at the start of the session, not reproduced again over
~20 subsequent assemblies. The "jumps straight to aim pose" variant appears more common but
wasn't separately tallied yet. This matters for reading future logs: expect to need a fair number
of assemblies to catch the instant-throw case in the data, and don't conclude a fix worked from a
handful of clean attempts alone.

## Diagnostic logging toggle

Both `Diagnostics/DurabilityTimingLogPatches.cs` and `Diagnostics/AssemblyTimingLogPatches.cs`
are gated behind `ToolsmithWeaponsModSystem.Config.EnableDiagnosticLogging`
(`VintagestoryData/ModConfig/ToolsmithWeapons.json`, default `true` while this is still being
actively investigated). Flip it to `false` once finding #3 is resolved or when not actively
debugging - `DurabilityTimingLogPatches` in particular logs every tick a projectile is in its
stuck-but-not-yet-dead state, which floods the log fast. The fix in `Fixes/` (finding #2) is
*not* gated - it always runs; only its one low-frequency confirmation log line stays on
regardless of the flag.

---

## 4. Which vanilla weapons exist, and which are safe to add next

Surveyed every `tags: ["weapon", ...]` itemtype under `assets/survival/itemtypes/tool/` in the
actual installed game:

| item code | class | risk profile |
|---|---|---|
| `spear` | `ItemSpear`/`ItemHackingSpear` (custom hold-to-charge throw) | high - the bug we just fixed |
| `blade` (sword: falx, blackguard, longsword, forlorn, gladius, arming, claymore, sabre) | none, plain `Item` | low - no interact-hold behavior at all, just left-click attack via the universal `CollectibleObject.AttackPower` every item has |
| `club` (mace: generic, scrapmace, flanged, morningstar, spiked, warhammer) | none, plain `Item` | **not added - see below, has no head part in vanilla at all** |
| `cleaver` | `ItemCleaver` | none - already in Toolsmith's own base `TinkerableTools` list, already works, nothing to do |
| `knife` (incl. dagger/stiletto/khanjar/baselard variants) | `ItemKnife` | none - `knife` is also already in Toolsmith's base list |
| `sling` | `ItemSling` (has `aimAnimation`) | high - hold-to-charge, same risk shape as spear |
| `bow` | `ItemBow` | high - draw/loose mechanic, same risk shape as spear |

Melee damage needs no special "weapon" mechanism to work at all: `CollectibleObject.AttackPower`
(default `0.5`) exists on every item in the game and is what `OnHeldAttackStart/Step/Stop`
(left-click, a completely separate method family from the `OnHeldInteractStart/Step/Stop` spear
uses) reads to compute melee damage - `blade`/`club` just set it higher per variant via
`attackpowerbytype` in their JSON, same mechanism as `spear`'s own melee stats. The `weapon`/
`weapon-*` tags appear to be classification only (handbook grouping etc.) - nothing we've found
reads them to gate any mechanic.

Throwing, by contrast, has two independent implementations in the game: the bespoke one `ItemSpear`
uses, and a generic, fully data-driven `CollectibleBehaviorThrowable` (`VSEssentials.dll`) that any
item can get via a `"behaviors": [{ "name": "Throwable", ... }]` entry in its JSON - no custom C#
needed. Important for later: that behavior's `OnHeldInteractStart/Step/Stop/Cancel` overrides live
on the *behavior* class with different signatures than `Item`'s own (extra `ref EnumHandling`
params) - our current `SpearAssemblyEchoSuppressionPatch` targets `ItemSpear` specifically and
would **not** automatically cover a future weapon built on `CollectibleBehaviorThrowable` instead.
The underlying vanilla bug (`ServerSystemInventory.HandleHandInteraction`'s unconditional second
`OnHeldUseStop`) is generic to *any* hold-to-charge item though, so `sling`/`bow`/any future
`Throwable`-behavior weapon would need the same kind of tag-and-consume treatment, just patched
onto the behavior class instead of an `Item` subclass.

**Added in `0.8.0`:** `blade` and `club` to `tinkerabletools-weapons.json`. Not added as the bare
words `"blade"`/`"club"` though - checked every installed mod first (same lesson as the
SmithingPlus/Toolsmith `"part"` collision earlier) and found real collisions: SmithingPlus ships
an item literally coded `forlornblade` (a raw smithing blank, not a finished weapon - would have
been wrongly treated as a whole tinkerable tool), and Purposeful Storage ships a `spearrack`
*block* (not an item). Used `"blade-"`, `"club-"`, and retroactively `"spear-"` (all with a
trailing hyphen) instead - vanilla's own codes are always `{code}-{type}-{material}`, so the
hyphen keeps the match precise without losing any real variant (`falx` is just one `type` under
`blade`, e.g. `blade-falx-iron` - no separate entry needed for it).

Status: **deployed (`0.8.0`), not yet playtested.** Expected to be low-risk per the class analysis
above (no interact-hold behavior on either item), but hasn't been confirmed in game yet.

---

## 5. Blade (sword) matched `TinkerableTools` but never actually became tinkerable

**Symptom:** after `0.8.0` added `blade-`/`club-` to `tinkerabletools-weapons.json`, swords
(`blade-falx-iron` etc.) still didn't get any Toolsmith behavior (no sharpness bar, not
reforgeable) - silently, no error.

**Root cause:** Toolsmith's classification loop
(`ToolsmithModSystem.AssetsFinalize`, iterating every item once at load time):

```csharp
if (ConfigUtility.IsTinkerableTool(code) && !ConfigUtility.IsToolHead(code) && !ConfigUtility.IsOnBlacklist(code)) {
    t.AddBehavior<CollectibleBehaviorTinkeredTools>();
}
```

Toolsmith's own base `ToolHeads` regex has always included the bare word `blade`:
`@.*(head|blade|toolhead|finishingchiselhead|wedgechiselhead|thorn).*` - intended to recognize
*loose head parts* (so e.g. `pickaxehead-copper`, which contains `head`, is correctly excluded
from being treated as an already-finished tool - only the assembled `pickaxe-copper`, which
doesn't contain `head`, should get the behavior). That convention works for every one of
Toolsmith's own supported tools because no *finished* vanilla tool code happens to contain any of
those keywords.

Vanilla's sword item type is itself literally named `blade` though (`blade-falx-iron`,
`blade-longsword-admin`, ...) - the whole, finished weapon, not a part. So
`IsToolHead("game:blade-falx-iron")` returns `true` by coincidence, and the exclusion silently
skips attaching `CollectibleBehaviorTinkeredTools` to any sword at all. A pure naming collision
between vanilla's own choice of item code and Toolsmith's pre-existing keyword - not a bug in
either mod individually, and not something fixable by changing our own `tinkerabletools-weapons.json`
(the collision is in Toolsmith's *other* list, `ToolHeads`, which we don't want to edit broadly
since it's correctly relied on for every other mod's loose head parts too).

`club` does not collide (no `ToolHeads` keyword is a substring of `club-flanged-iron` etc.) and
`spear` doesn't either (already confirmed working since finding #1) - this is specific to `blade`.

**Fix:** `ToolsmithWeapons/Fixes/WeaponNotAToolHeadPatch.cs`. Harmony prefix on
`ConfigUtility.IsToolHead(string toolHead)` - forces the result to `false` for any code containing
`:spear-` or `:blade-` (kept in sync with `tinkerabletools-weapons.json`'s own prefixes; `club`
was never in either list - see below), before Toolsmith's own regex check ever runs, and only
for those specific codes -
every other call to `IsToolHead` (other mods' real loose head parts, Toolsmith's own tools) is
completely untouched. This is our first patch that changes *Toolsmith's* behavior rather than
vanilla engine behavior or our own item - a deliberately narrow, single-purpose override to avoid
coupling us to more of Toolsmith's internals than necessary.

**Timing dependency worth remembering:** this only works because Harmony patches are applied in
our `ModSystem.Start()` (a phase every mod goes through before any mod's `AssetsFinalize` runs),
while Toolsmith's classification loop runs later, in its *own* `AssetsFinalize`. If a future
Toolsmith version moved that loop earlier (into its own `Start()`), this patch would need to move
into our `StartPre()` instead to still land in time.

Status: **fix applied and confirmed working (`0.9.0`)** - both `blade-falx-*` and
`blade-arming-*` ("shortsword", built from the `bladehead-short-*` head part) became tinkerable
after this patch.

## 6. `club` pulled back out - no head part exists in vanilla at all

Checked how `club-generic-wood` is actually made: a single grid recipe, `game:log-* + a knife
(tool, not consumed) -> club-generic-wood` (`recipes/grid/tool/club.json`). One step, one
material, no smithing, no separate head to combine with a handle - unlike `spear`/`blade`/`knife`/
`cleaver`, which all have a real smithed head part (`spearhead`/`bladehead`/`knifeblade`/
`cleaverhead`) assembled with a handle the same way. `club.json`'s own `material` variantgroup
confirms this too: only `wood`, `scrap`, `ruined` - no metal options exist for it in vanilla at
all.

Toolsmith's tinkered-tool system assumes a real head/handle split exists to have anything
meaningful to track separately or hand back on breakage. Forcing it onto a single-piece item with
no natural head would mean Toolsmith invents placeholder handle/binding values that don't
correspond to anything real in the game - unclear whether that's just inert noise or produces
actively confusing behavior (e.g. handing back parts that were never really there when the club
breaks), and not worth finding out for an item that gets no real benefit from the system either
way. Pulled `club-`/`:club-` back out of both `tinkerabletools-weapons.json` and
`WeaponNotAToolHeadPatch.cs` (`0.9.1`) rather than leave it in "probably harmless."

Worth revisiting if a mod that gives club real metal head variants (Vanilla Armory adds Flanged
Mace/Morningstar/Warhammer - unclear yet whether those ship with an actual smithed head part or
are also single-recipe items like vanilla's club; not installed/checked) is ever added - the
question then isn't about vanilla's plain wooden club at all, but about whatever new item type
that mod defines.

Status: **`club-` removed (`0.9.1`).**

---

## 7. Vanilla Armory compatibility - investigated, shelved

Briefly tested pairing with the [Vanilla Armory](https://mods.vintagestory.at/show/mod/23204)
mod (v2.4.6, temporary/local install only, never added to any curated mod list). Downloaded and
inspected the zip directly rather than going by the mod page's description:

- Reuses vanilla's own item codes and classes throughout for the weapon types this addon cares
  about: `spear-*` variants (boar, fork, ranseur, voulge) are `class: "ItemSpear"` - literally
  the same class our existing fixes already patch, no extra work needed there. `blade-*` variants
  (arming, claymore, gladius, sabre) are plain `Item`, same as vanilla. `axe-*`/`knife-*`
  variants use `ItemAxe`/`ItemKnife`, already covered by Toolsmith's own base `TinkerableTools`.
- Its `club-*` variants (flanged/morningstar/spiked/warhammer - shown in-game as "mace"/
  "morningstar"/"warhammer", not "club") *do* ship real smithed head parts
  (`flangedhead.json` etc.) - unlike vanilla's own headless wooden club. Re-enabling `club-`
  conditionally on this mod being present could make sense later.
- Found a second real collision while testing: Toolsmith's `PartBlacklist` already contains the
  bare word `armory` (presumably for some unrelated armory/rack storage block). Vanilla Armory's
  own mod domain is literally `vanillaarmory` - contains `armory` as a substring - so every
  single item that mod adds was silently blacklisted from ever becoming tinkerable, regardless of
  anything else. A fix for this (`WeaponNotBlacklistedPatch` in `WeaponNotAToolHeadPatch.cs`) was
  written and builds cleanly: strips the `vanillaarmory:` domain prefix specifically before
  re-running the real blacklist check on just the item's path, so legitimate `ruined`/`scrap`
  exclusions on those same items still work correctly (unlike a naive "always false for our
  weapon prefixes" version, which would have wrongly un-blacklisted those too).
- The mod's own bundled game-side compatibility patch (`assets/game/patches/vanillaarmory.json`,
  shipped by the *base game*, not this addon) threw two non-fatal "file not found" errors against
  this specific version (2.4.6) - looks like a version mismatch between what the game's own
  patch expects and what's actually in the current release, unrelated to Toolsmith or this addon.

**Shelved without playtesting** - deprioritized mid-investigation, not because anything was found
broken. The `WeaponNotBlacklistedPatch` fix is written and present in the codebase (dormant -
only ever triggers for the `vanillaarmory:` domain, which does nothing without that mod
installed) but `club-` was reverted out of `tinkerabletools-weapons.json` and
`WeaponNotAToolHeadPatch`'s prefix list back to the pre-experiment state. Pick this up again by:
re-adding `":club-"` to both files, reinstalling Vanilla Armory, and actually playtesting -
everything needed for that is already written, just not exercised.

---

## 8. Diagnostics/ removed - minimizing update-breakage surface

Every `[HarmonyPatch]` is a dependency on a specific method's exact signature in a specific
version of the game or Toolsmith. If a future update renames or reshapes a patched method,
`Harmony.PatchAll()` fails to bind that one patch - which can abort the whole call, taking every
*other* patch in this mod down with it (including the actual fixes), not just the broken one.
More patches = more chances for that to happen for zero reason once a patch has done its job.

Once finding #3's root cause was fully nailed down (finding #5's caller chain, confirmed via a
captured stack trace), the `Diagnostics/` folder's only remaining job was re-confirming things we
already knew and had written down here. It patched 8 methods nothing else in this mod needs:
`ItemTinkerToolParts.OnHeldInteractStop`/`Cancel`, `CollectibleBehaviorTinkeredTools.OnDamageItem`,
all four `CollectibleObject.OnHeldUseStart/Cancel/Step/Stop` wrapper methods (used by *every* item
in the game - the largest single risk surface in the whole mod), and `EntityPlayer.TryStopHandAction`
- plus duplicate patches on methods the `Fixes/` files already cover, which don't add risk but do
add dead weight. Removed the whole folder, `Config/ToolsmithWeaponsConfig.cs` (nothing left to
toggle), and the config-loading code in `ToolsmithWeaponsModSystem.cs` along with it. The full
`0.10.0` build patches only the 8 methods the four `Fixes/` files actually need - roughly a third
smaller than the last diagnostic build. Nothing here changes what the mod *does* - every finding
and piece of log evidence that came from this code is preserved in this document; the code itself
had already done its job.

---

## Tooling notes

- Decompiling: `ilspycmd -t <Namespace.Type> <path\to\Assembly.dll>` for a single type,
  `-m "M:<fully qualified method signature>"` for a single method,
  `-l c <path\to\Assembly.dll>` to list all classes in an assembly (useful for finding which of
  the several game DLLs a type actually lives in - `VintagestoryLib.dll` is mostly
  engine/networking, `VintagestoryData\..\Vintagestory\Mods\VSEssentials.dll` and
  `VSSurvivalMod.dll` hold the actual game-content classes like `ItemSpear`, `EntityProjectile`).
- Toolsmith source (public): individual files fetchable at
  `https://raw.githubusercontent.com/Mario90900/Toolsmith/master/Toolsmith/<path>`; use
  `gh api search/code -f q="<symbol> repo:Mario90900/Toolsmith"` to find which file defines/uses
  a given symbol before guessing a path.
