using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace ToolsmithWeapons.Fixes {

    // See AssemblyStaleInteractionFixPatch.cs for the full root-cause writeup (also in
    // docs/findings.md, finding #3). Short version: right after TinkeringUtility.AssembleFullTool
    // creates the finished ItemSpear, vanilla's ServerSystemInventory.HandleHandInteraction
    // unconditionally fires one more OnHeldUseStop on "whatever is in the slot now" - an echo of
    // the hold-session that just legitimately ended one call frame earlier. That vanilla call
    // site can't be safely patched (it handles all hand interaction in the game), so we intercept
    // at the only place we fully control: ItemSpear's own interact handlers.
    //
    // AssemblyStaleInteractionFixPatch tags the freshly-assembled ItemStack (only ever set there
    // - i.e. only for a tool finished by holding right-click in hand, never for one crafted at a
    // workbench/grid recipe, since those never call AssembleFullTool at all). Whichever of
    // OnHeldInteractStart/Stop/Cancel receives the very next call on that exact item consumes
    // (removes) the tag and swallows that one call - Start doesn't even start an aim pose (the
    // user asked for this explicitly: since Start is just as much an unwanted echo as Stop is,
    // not "acceptable fallout"), Stop doesn't throw, Cancel doesn't do its cancel bookkeeping.
    // Every legitimate interaction after that (the player's next real click) proceeds completely
    // normally, since the tag is already gone.
    //
    // Deliberately not restricted to server-side: the tag can be present on both the server's and
    // the synced client's copy of the stack, and we want both sides suppressed consistently to
    // avoid any client/server visual mismatch.

    internal static class SpearAssemblyEchoSuppression {
        public static bool TryConsume(ItemSlot slot) {
            var attrs = slot?.Itemstack?.Attributes;
            if (attrs == null || !attrs.HasAttribute(AssemblyStaleInteractionFixPatch.JustAssembledAttribute)) {
                return false;
            }
            attrs.RemoveAttribute(AssemblyStaleInteractionFixPatch.JustAssembledAttribute);
            return true;
        }
    }

    [HarmonyPatch(typeof(ItemSpear), nameof(ItemSpear.OnHeldInteractStart))]
    public static class SpearStartEchoSuppressionPatch {
        [HarmonyPrefix]
        public static bool Prefix(ItemSlot itemslot, EntityAgent byEntity, ref EnumHandHandling handling) {
            try {
                if (SpearAssemblyEchoSuppression.TryConsume(itemslot)) {
                    handling = EnumHandHandling.PreventDefault;
                    byEntity?.World?.Api?.Logger?.Notification($"[ToolsmithWeapons][{byEntity?.World?.Side}] [EchoSuppressed] Swallowed a stray OnHeldInteractStart (would-be aim pose) on a freshly assembled spear.");
                    return false;
                }
            } catch (Exception e) {
                byEntity?.World?.Api?.Logger?.Warning($"[ToolsmithWeapons] SpearStartEchoSuppressionPatch failed: {e}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ItemSpear), nameof(ItemSpear.OnHeldInteractStop))]
    public static class SpearStopEchoSuppressionPatch {
        [HarmonyPrefix]
        public static bool Prefix(ItemSlot slot, EntityAgent byEntity) {
            try {
                if (SpearAssemblyEchoSuppression.TryConsume(slot)) {
                    byEntity?.World?.Api?.Logger?.Notification($"[ToolsmithWeapons][{byEntity?.World?.Side}] [EchoSuppressed] Swallowed a stray OnHeldInteractStop (would-be instant throw) on a freshly assembled spear.");
                    return false;
                }
            } catch (Exception e) {
                byEntity?.World?.Api?.Logger?.Warning($"[ToolsmithWeapons] SpearStopEchoSuppressionPatch failed: {e}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ItemSpear), nameof(ItemSpear.OnHeldInteractCancel))]
    public static class SpearCancelEchoSuppressionPatch {
        [HarmonyPrefix]
        public static bool Prefix(ItemSlot slot, EntityAgent byEntity, ref bool __result) {
            try {
                if (SpearAssemblyEchoSuppression.TryConsume(slot)) {
                    byEntity?.World?.Api?.Logger?.Notification($"[ToolsmithWeapons][{byEntity?.World?.Side}] [EchoSuppressed] Swallowed a stray OnHeldInteractCancel on a freshly assembled spear.");
                    __result = true;
                    return false;
                }
            } catch (Exception e) {
                byEntity?.World?.Api?.Logger?.Warning($"[ToolsmithWeapons] SpearCancelEchoSuppressionPatch failed: {e}");
            }
            return true;
        }
    }
}
