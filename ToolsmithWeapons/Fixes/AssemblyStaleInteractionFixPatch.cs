using System;
using HarmonyLib;
using Toolsmith.ToolTinkering;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ToolsmithWeapons.Fixes {

    // Root cause (see docs/findings.md, finding #3 - fully confirmed by decompiling
    // ServerSystemInventory.HandleHandInteraction/callOnUsing): when a held-use session ends
    // (StopHeldItemUse), the server first runs a catch-up loop (callOnUsing) to replay any Step
    // calls the client already reported. If a Step call inside that loop itself drives
    // Controls.HandUse back to None - which is exactly what happens when
    // ItemTinkerToolParts.OnHeldInteractStep's crafting timer elapses - callOnUsing immediately
    // fires a first, correct OnHeldUseStop right there (this is what completes the tool: it swaps
    // the item bundle -> finished ItemSpear via TinkeringUtility.AssembleFullTool). Control then
    // returns to HandleHandInteraction's StopHeldItemUse case, whose loop exits (HandUse is
    // already None) - but the code *after* the loop calls OnHeldUseStop a SECOND time,
    // unconditionally, on whatever is now in the slot. That's vanilla game code we can't safely
    // patch (it handles all hand interaction, not just tools/weapons), and the outer
    // `if (controls.HandUse != None)` gate is only checked once, before the loop, so nothing we
    // set on Controls during AssembleFullTool can stop that second call from executing.
    //
    // Real fix lives in SpearAssemblyEchoSuppressionPatch.cs: ItemSpear's own
    // OnHeldInteractStart/Stop/Cancel recognize and swallow that specific echoed call. This patch
    // does the tagging those rely on, plus keeps resetting UsingBeginMS as a harmless second line
    // of defense (correct behavior regardless, and reduces stale-time damage for any other caller
    // we haven't found).

    [HarmonyPatch(typeof(TinkeringUtility), nameof(TinkeringUtility.AssembleFullTool))]
    public static class AssemblyStaleInteractionFixPatch {

        // Only ever set here, i.e. only for a tool finished by holding right-click in hand
        // (AssembleFullTool). Workbench/grid-recipe crafting goes through entirely different
        // TinkeringUtility methods and never touches this attribute.
        public const string JustAssembledAttribute = "toolsmithweapons_justAssembledEcho";

        [HarmonyPostfix]
        public static void Postfix(ItemSlot bundleSlot, EntityAgent byEntity) {
            try {
                if (byEntity?.World == null) {
                    return;
                }
                if (byEntity.Controls != null) {
                    byEntity.Controls.UsingBeginMS = byEntity.World.ElapsedMilliseconds;
                }
                bundleSlot?.Itemstack?.Attributes?.SetBool(JustAssembledAttribute, true);
            } catch (Exception e) {
                byEntity?.World?.Api?.Logger?.Warning($"[ToolsmithWeapons] AssemblyStaleInteractionFixPatch failed: {e}");
            }
        }
    }
}
