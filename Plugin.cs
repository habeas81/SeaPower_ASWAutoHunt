using System;
using System.Linq;
using System.Reflection;
using AnchorChain;
using HarmonyLib;

namespace ASWAutoHunt
{
    // Entry point used by AnchorChain (Sea Power's community chainloader), which loads this DLL straight from the
    // enabled mod folder (Mods menu / Steam Workshop) and calls TriggerEntryPoint once at start-up.
    [ACPlugin(Plugin.PLUGIN_GUID, Plugin.PLUGIN_NAME, Plugin.PLUGIN_VERSION)]
    public class AnchorChainEntry : IAnchorChainMod
    {
        public void TriggerEntryPoint() => Plugin.Init();
    }

    public static class Plugin
    {
        public const string PLUGIN_GUID = "com.habeas81.seapower.aswautohunt";
        public const string PLUGIN_NAME = "ASW Auto Hunt";
        public const string PLUGIN_VERSION = "0.1.0";

        // Logger so our patches can print to the BepInEx console/log
        internal static BepInEx.Logging.ManualLogSource Log;

        private static bool _initialised;

        public static void Init()
        {
            if (_initialised)
            {
                return;
            }

            _initialised = true;
            Log = BepInEx.Logging.Logger.CreateLogSource(PLUGIN_NAME);
            ModLog.Init();
            ModLog.Info($"{PLUGIN_NAME} v{PLUGIN_VERSION} starting. Detailed log: {ModLog.Path}");

            try
            {
                ApplyPatches();
            }
            catch (Exception ex)
            {
                ModLog.Error("Patch setup failed", ex);
            }
        }

        // Patch each class on its own so one broken patch is logged and skipped instead of taking the mod down.
        private static void ApplyPatches()
        {
            Harmony harmony = new Harmony(PLUGIN_GUID);
            int ok = 0, failed = 0;
            foreach (Type type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly())
                         .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0))
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                    ModLog.Info($"Patched OK: {type.Name}");
                }
                catch (Exception ex)
                {
                    failed++;
                    ModLog.Error($"Patch FAILED: {type.Name}", ex);
                }
            }

            ModLog.Info($"Patching complete: {ok} ok, {failed} failed.");
        }
    }
}
