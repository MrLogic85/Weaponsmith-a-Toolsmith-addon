using HarmonyLib;
using Vintagestory.API.Common;

namespace ToolsmithWeapons {
    public class ToolsmithWeaponsModSystem : ModSystem {
        private const string HarmonyId = "toolsmithweapons";
        private Harmony harmony;

        public override void Start(ICoreAPI api) {
            base.Start(api);

            harmony = new Harmony(HarmonyId);
            // Start() runs once per side (client + server) in this Universal mod, both in the
            // same process/AppDomain in singleplayer - without this guard, PatchAll would add
            // every prefix/postfix twice.
            if (!Harmony.HasAnyPatches(HarmonyId)) {
                harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
            }
            api.Logger.Notification("[ToolsmithWeapons] Patches active.");
        }

        public override void Dispose() {
            harmony?.UnpatchAll(HarmonyId);
            base.Dispose();
        }
    }
}
