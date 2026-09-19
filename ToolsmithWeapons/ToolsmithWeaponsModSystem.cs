using HarmonyLib;
using Vintagestory.API.Common;

namespace ToolsmithWeapons {
    public class ToolsmithWeaponsModSystem : ModSystem {
        private const string HarmonyId = "toolsmithweapons";
        private Harmony harmony;

        // For patches that have no world/api handle of their own to log through.
        internal static ILogger Logger;

        public override void Start(ICoreAPI api) {
            base.Start(api);
            Logger = api.Logger;

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
