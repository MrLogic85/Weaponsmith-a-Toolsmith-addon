using System;
using HarmonyLib;
using Toolsmith.Client;
using Vintagestory.API.Common;

namespace ToolsmithWeapons.Fixes {

    // Cosmetic only (see docs/findings.md, finding #11). Toolsmith renders the intermediate
    // "Tool Head and Handle" bundle (toolsmith:tinkertoolparts) by combining the head item's own
    // shape with a handle shape looked up by tool type - a type it derives from a
    // ".../parts/<tooltype>/..." segment in the head's shape path. Vanilla's spearhead shape is
    // item/tool/spear/metal-<material> (or stone): the WHOLE spear model, with the handle/string
    // texture keys made transparent by the head item. No "parts" segment -> tool type unknown ->
    // Toolsmith drops a generic stick handle at the origin on top of that full-length model, which
    // renders as a dark clump. (Only the bundle is affected: for generic bundles Toolsmith skips
    // the render tree when the binding step finishes the tool, so the finished spear uses the
    // item's own vanilla shape.)
    //
    // Fix: for that vanilla spearhead shape only, point the bundle's handle part at the same
    // shape and flip which textures are visible - everything the head item shows goes transparent,
    // so just the shaft (and no string, the binding isn't there yet) is left, already positioned
    // correctly since it's the same model. Deliberately tiny: mod heads with their own head-only
    // shapes (e.g. Vanilla Armory) are left exactly as Toolsmith renders them.

    [HarmonyPatch(typeof(MultiPartRenderingHelpers), nameof(MultiPartRenderingHelpers.BuildToolRenderFromHeadAndHandle))]
    public static class SpearPartBundleRenderPatch {
        private const string VanillaSpearShapePrefix = "item/tool/spear/";
        private const string Transparent = "game:block/transparent";

        [HarmonyPostfix]
        public static void Postfix(ItemStack tool, ItemStack head) {
            try {
                var shape = head?.Item?.Shape?.Base;
                bool generic = tool != null && tool.HasBundleHasGenericParts();
                // Diagnostic (finding #11): a bundle is only made by hand, so this is low-volume.
                ToolsmithWeaponsModSystem.Logger?.Notification($"[ToolsmithWeapons] Part bundle built: head={head?.Collectible?.Code} shape={shape} genericParts={generic}");
                if (tool == null || shape == null || shape.Domain != "game"
                    || !shape.Path.StartsWith(VanillaSpearShapePrefix) || !generic) {
                    return;
                }

                var multiPart = tool.GetMultiPartRenderTree();
                var handlePart = multiPart?.GetPartAndTransformRenderTree("handle");
                var handleRender = handlePart?.GetPartRenderTree();
                if (handleRender == null) return;

                handleRender.SetPartShapePath(shape.Domain + ":shapes/" + shape.Path);

                var textures = handleRender.GetPartTextureTree();
                foreach (var texture in head.Item.Textures) {
                    if (texture.Key != "handle") {
                        textures.SetPartTexturePathFromKey(texture.Key, Transparent);
                    }
                }
                handleRender.SetPartTextureTree(textures);

                handlePart.SetPartRenderTree(handleRender);
                multiPart.SetPartAndTransformRenderTree("handle", handlePart);
                tool.SetMultiPartRenderTree(multiPart);
            } catch (Exception e) {
                ToolsmithWeaponsModSystem.Logger?.Warning("[ToolsmithWeapons] SpearPartBundleRenderPatch failed: " + e.Message);
            }
        }
    }
}
