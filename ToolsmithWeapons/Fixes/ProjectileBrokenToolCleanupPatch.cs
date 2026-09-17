using System;
using HarmonyLib;
using Toolsmith.ToolTinkering.Behaviors;
using Toolsmith.Utils;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace ToolsmithWeapons.Fixes {

    // Root cause (confirmed via DurabilityTimingLogPatches logs):
    // When a thrown tinkered tool "falls apart" on impact (handle/binding used up, head still
    // intact - the normal, common way Toolsmith tools degrade), Toolsmith's
    // HandleBrokenTinkeredTool correctly hands the player the loose parts and nulls the *slot*
    // it was given. On a projectile impact, that slot is a throwaway DummySlot wrapping the
    // projectile's real ItemStack - nulling it never touches
    // EntityProjectileBase.ProjectileStack. Vanilla's own "did this break, should I despawn"
    // check then reads GetRemainingDurability on the untouched stack (which only mirrors head
    // durability) and sees it's still fine, so the projectile survives as a pickable duplicate
    // even though Toolsmith already gave out the parts.
    //
    // This patch runs after the vanilla impact-damage code on both impact paths (terrain and
    // entity) and, if the carried stack is a Toolsmith-tinkered tool that has actually broken
    // or fallen apart (head, handle or binding at or below 0), force-despawns the projectile
    // without dropping anything - cleaning up the leftover duplicate rather than preventing it.

    public static class ProjectileBrokenToolCleanupLogic {

        public static bool IsBrokenTinkeredTool(ItemStack stack) {
            if (stack?.Collectible == null) {
                return false;
            }
            if (!stack.Collectible.HasBehavior<CollectibleBehaviorTinkeredTools>()) {
                return false;
            }
            return stack.GetToolheadCurrentDurability() <= 0
                || stack.GetToolhandleCurrentDurability() <= 0
                || stack.GetToolbindingCurrentDurability() <= 0;
        }

        public static void CleanupIfBroken(EntityProjectileBase instance) {
            try {
                if (instance?.World == null || instance.World.Side != EnumAppSide.Server) {
                    return;
                }
                if (!instance.Alive) {
                    return;
                }
                ItemStack stack = instance.ProjectileStack;
                if (!IsBrokenTinkeredTool(stack)) {
                    return;
                }

                instance.World.Api.Logger.Notification($"[ToolsmithWeapons][Server] [Cleanup] Removing leftover projectile entity for already-broken tinkered tool {stack.Collectible.Code} (Toolsmith already handed out the parts).");
                instance.ProjectileStack = null; // belt and braces - don't let despawn drop it either
                instance.Die(EnumDespawnReason.Removed, null);
            } catch (Exception e) {
                instance?.World?.Api?.Logger?.Warning($"[ToolsmithWeapons] ProjectileBrokenToolCleanupPatch failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(EntityProjectile), "IsColliding")]
    public static class TerrainImpactCleanupPatch {
        [HarmonyPostfix]
        public static void Postfix(EntityProjectile __instance) {
            ProjectileBrokenToolCleanupLogic.CleanupIfBroken(__instance);
        }
    }

    [HarmonyPatch(typeof(EntityProjectileBase), "DamageProjectile")]
    public static class EntityImpactCleanupPatch {
        [HarmonyPostfix]
        public static void Postfix(EntityProjectileBase __instance) {
            ProjectileBrokenToolCleanupLogic.CleanupIfBroken(__instance);
        }
    }
}
