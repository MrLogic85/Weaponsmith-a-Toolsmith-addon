using System;
using HarmonyLib;
using Toolsmith.Client.Behaviors;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ToolsmithWeapons.Fixes {

    // Cosmetic only (see docs/findings.md, finding #12). Vanilla Armory's knives
    // (knife-baselard/dagger/khanjar/stiletto-*) and axes (axe-bardiche/battle/bearded/double-*)
    // rendered as Toolsmith's own generic copper knife/axe once finished.
    //
    // Their heads' shapes ("item/tool/blade/baselardhead", ...) have no ".../parts/<tooltype>/..."
    // segment, so Toolsmith marks the part bundle generic and never gives the finished tool a
    // multi-part render tree. With no tree, ModularPartRenderingFromAttributes.GenMesh falls back
    // to a generic part mesh looked up by the item code's FIRST part - "knife"/"axe", for which
    // Toolsmith ships shapes/item/parts/knife|axe - instead of the item's own shape. (VA's spears,
    // swords and maces were spared only because Toolsmith has no parts/spear|blade|club.)
    //
    // Fix: skip that generic fallback for vanillaarmory: items only. Returning null makes
    // Toolsmith's own GenMesh continue to the item's own shape (game:item/tool/knife/baselard,
    // VA's ornate gold/silver shapes, ...). Tinkering itself is untouched; every other item's
    // fallback is untouched.
    [HarmonyPatch(typeof(ModularPartRenderingFromAttributes), nameof(ModularPartRenderingFromAttributes.GenFallbackGenericPartMesh))]
    public static class VanillaArmoryFinishedWeaponRenderPatch {
        private const string VanillaArmoryDomain = "vanillaarmory";

        [HarmonyPrefix]
        public static bool Prefix(ModularPartRenderingFromAttributes __instance, ref MeshData __result) {
            try {
                if (__instance?.collObj?.Code?.Domain == VanillaArmoryDomain) {
                    __result = null;
                    return false;
                }
            } catch (Exception e) {
                ToolsmithWeaponsModSystem.Logger?.Warning("[ToolsmithWeapons] VanillaArmoryFinishedWeaponRenderPatch failed: " + e);
            }
            return true;
        }
    }
}
