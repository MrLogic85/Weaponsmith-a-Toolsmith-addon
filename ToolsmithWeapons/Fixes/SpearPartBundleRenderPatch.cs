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
    // Toolsmith drops a generic stick handle at the origin on top of that full-length model.
    // (Vanilla heads still looked acceptable in playtest, though.) Only the bundle is affected:
    // for generic bundles Toolsmith skips the render tree when the binding step finishes the
    // tool, so the finished spear uses the item's own vanilla shape.
    //
    // Fix (Vanilla Armory heads only - vanilla heads render fine, see below): the head part's shape
    // path is normalised, and the handle part becomes vanilla's whole-spear shape of the same name
    // (game:.../spear/boar for boarhead, if it exists; VA's head shapes are head-only, in the same
    // coordinate frame) with every texture the head shows made transparent, leaving just the shaft
    // (and no string, the binding isn't there yet). Any other head is left exactly as Toolsmith
    // renders it.
    //
    // Learned from the first playtest (finding #11):
    // - This runs on the server, where Item.Textures is evidently not populated (NRE in 1.1.1/1.1.2) (Toolsmith itself uses its
    //   ToolHeadTexturesCache there), so the head's texture keys are read back from the head part
    //   Toolsmith just wrote into the render tree, which is identical on both sides.
    // - Vanilla Armory's item JSON writes its shape bases WITH the "shapes/" prefix
    //   ("vanillaarmory:shapes/item/tool/spear/boarhead"); Toolsmith prepends "shapes/" again,
    //   producing a path that doesn't exist; Toolsmith then (by our reading of its code, not
    //   confirmed in game) falls back to tinkertoolparts' own scrap-weapon-kit model (the dark clump). The head part's path is normalised here too.

    [HarmonyPatch(typeof(MultiPartRenderingHelpers), nameof(MultiPartRenderingHelpers.BuildToolRenderFromHeadAndHandle))]
    public static class SpearPartBundleRenderPatch {
        private const string VanillaSpearShapePrefix = "item/tool/spear/";
        private const string Transparent = "game:block/transparent";

        // "item/x" and "shapes/item/x" are the same asset; the engine adds the prefix if missing.
        private static string WithShapesPrefix(string path) {
            return path.StartsWith("shapes/") ? path : "shapes/" + path;
        }

        [HarmonyPostfix]
        public static void Postfix(ItemStack tool, ItemStack head) {
            try {
                var shape = head?.Item?.Shape?.Base;
                bool generic = tool != null && tool.HasBundleHasGenericParts();
                // Diagnostic (finding #11): a bundle is only made by hand, so this is low-volume.
                ToolsmithWeaponsModSystem.Logger?.Notification($"[ToolsmithWeapons] Part bundle built: head={head?.Collectible?.Code} shape={shape} genericParts={generic}");
                if (tool == null || shape == null || !generic) return;

                string shapePath = WithShapesPrefix(shape.Path);
                if (!shapePath.StartsWith("shapes/" + VanillaSpearShapePrefix)) return;

                // Vanilla heads already render acceptably through Toolsmith's own path (playtested
                // 1.1.2 - the earlier NRE meant this patch never touched them); leave them alone.
                if (shape.Domain == "game" || !shapePath.EndsWith("head")) return;

                string headShape = shape.Domain + ":" + shapePath;
                var full = new AssetLocation("game", shapePath.Substring(0, shapePath.Length - "head".Length));
                if (ToolsmithWeaponsModSystem.Api?.Assets?.Exists(new AssetLocation(full.Domain, full.Path + ".json")) != true) return;
                string handleShape = full.ToString();

                var multiPart = tool.GetMultiPartRenderTree();
                var headPart = multiPart?.GetPartAndTransformRenderTree("head");
                var headRender = headPart?.GetPartRenderTree();
                var handlePart = multiPart?.GetPartAndTransformRenderTree("handle");
                var handleRender = handlePart?.GetPartRenderTree();
                if (headRender == null || handleRender == null) return;

                headRender.SetPartShapePath(headShape);
                handleRender.SetPartShapePath(handleShape);

                var textures = handleRender.GetPartTextureTree();
                foreach (var texture in headRender.GetPartTextureTree()) {
                    if (texture.Key != "handle" && texture.Key != "wood") {
                        textures.SetPartTexturePathFromKey(texture.Key, Transparent);
                    }
                }
                handleRender.SetPartTextureTree(textures);

                headPart.SetPartRenderTree(headRender);
                handlePart.SetPartRenderTree(handleRender);
                multiPart.SetPartAndTransformRenderTree("head", headPart);
                multiPart.SetPartAndTransformRenderTree("handle", handlePart);
                tool.SetMultiPartRenderTree(multiPart);
            } catch (Exception e) {
                ToolsmithWeaponsModSystem.Logger?.Warning("[ToolsmithWeapons] SpearPartBundleRenderPatch failed: " + e);
            }
        }
    }
}
