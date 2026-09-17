using System;
using HarmonyLib;
using Toolsmith.Utils;

namespace ToolsmithWeapons.Fixes {

    // Root cause (see docs/findings.md, finding #5): Toolsmith's own base ToolHeads regex has
    // always contained the bare word "blade" (@.*(head|blade|...).*), meant to recognize LOOSE
    // head parts (e.g. some other mod's "...blade..." named head item) so they don't get treated
    // as a complete, already-assembled tool before being combined with a handle - the same way
    // "pickaxehead-copper" (contains "head") is correctly excluded while "pickaxe-copper" (the
    // finished tool) is not.
    //
    // Vanilla's own sword item type happens to be *named* "blade" itself though
    // ("blade-falx-iron", "blade-longsword-admin", etc.) - the whole, finished weapon, not a
    // loose part. Toolsmith's classification loop
    // (ToolsmithModSystem.AssetsFinalize: `IsTinkerableTool(code) && !IsToolHead(code) && ...`)
    // sees that substring match, wrongly concludes the finished sword is itself a loose head, and
    // never attaches CollectibleBehaviorTinkeredTools to it at all - so it silently never becomes
    // tinkerable despite matching TinkerableTools (see tinkerabletools-weapons.json). This is a
    // pure naming coincidence between vanilla's sword code and Toolsmith's pre-existing "blade"
    // keyword; neither mod is "wrong", they just collide.
    //
    // Fix: force ConfigUtility.IsToolHead to answer false for the specific whole-weapon code
    // prefixes we ourselves added to TinkerableTools (tinkerabletools-weapons.json) - never
    // touching the general case, so other mods'/Toolsmith's own loose-head-part recognition via
    // this same method elsewhere is completely unaffected.
    //
    // Timing note: this only works because Harmony patches are applied in our ModSystem.Start()
    // (a phase common to every mod), while Toolsmith's classification loop runs later, in its own
    // AssetsFinalize - a distinct, later global phase that starts only after every mod's Start()
    // has completed, regardless of per-mod dependency order. If Toolsmith ever moved that loop
    // into Start() itself, this patch would need to move earlier too (e.g. StartPre).

    [HarmonyPatch(typeof(ConfigUtility), nameof(ConfigUtility.IsToolHead))]
    public static class WeaponNotAToolHeadPatch {

        // Keep in sync with tinkerabletools-weapons.json's own hyphenated prefixes.
        // ":club-" deliberately left out - see docs/findings.md finding #4/#5/#6, club has no
        // head part in vanilla at all. A Vanilla Armory compatibility experiment (0.9.2/0.9.3)
        // briefly added it back plus a PartBlacklist domain-collision fix (finding #7) but was
        // shelved before playtesting - see finding #7 for the parked fix if this gets picked
        // back up later.
        internal static readonly string[] WholeWeaponPrefixes = { ":spear-", ":blade-" };

        [HarmonyPrefix]
        public static bool Prefix(string toolHead, ref bool __result) {
            try {
                if (toolHead != null) {
                    foreach (var prefix in WholeWeaponPrefixes) {
                        if (toolHead.Contains(prefix)) {
                            __result = false;
                            return false; // skip Toolsmith's own regex check for these codes only
                        }
                    }
                }
            } catch (Exception) {
                // fall through to the original check on any unexpected failure
            }
            return true;
        }
    }

    // Root cause (see docs/findings.md, finding #7): Toolsmith's PartBlacklist already contains
    // the bare word "armory" (presumably to exclude some unrelated armory/weapon-rack storage
    // block, unrelated to weapons entirely). The Vanilla Armory mod's own domain is literally
    // "vanillaarmory" - which contains "armory" as a substring - so EVERY item that mod adds
    // gets silently blacklisted from ever becoming tinkerable, purely because of its mod's name,
    // regardless of its own item code or our TinkerableTools/ToolHeads work.
    //
    // Deliberately NOT the same "force false for our weapon prefixes" shape as
    // WeaponNotAToolHeadPatch: PartBlacklist also correctly excludes "ruined"/"scrap" variants of
    // these same weapons (e.g. "blade-falx-ruined" should stay excluded), so blindly forcing
    // false for every ":blade-"/":spear-"/":club-" code would wrongly un-blacklist those too.
    // Instead, only strip the "vanillaarmory:" domain prefix specifically - re-running the real
    // blacklist check against just the item's path, so ruined/scrap there are still caught
    // normally, and every other mod's use of IsOnBlacklist (including whatever "armory" was
    // actually meant to exclude) is completely untouched.
    [HarmonyPatch(typeof(ConfigUtility), nameof(ConfigUtility.IsOnBlacklist))]
    public static class WeaponNotBlacklistedPatch {
        private const string VanillaArmoryDomainPrefix = "vanillaarmory:";

        [HarmonyPrefix]
        public static bool Prefix(string tool, ref bool __result) {
            try {
                if (tool != null && tool.StartsWith(VanillaArmoryDomainPrefix)) {
                    string pathOnly = tool.Substring(VanillaArmoryDomainPrefix.Length);
                    __result = ConfigUtility.IsOnBlacklist(pathOnly); // re-check without the colliding domain
                    return false;
                }
            } catch (Exception) {
                // fall through to the original check on any unexpected failure
            }
            return true;
        }
    }
}
