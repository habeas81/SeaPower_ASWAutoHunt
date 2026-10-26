using System;
using HarmonyLib;
using SeaPower;

namespace ASWAutoHunt
{
    // The game drops time compression to its "weapons" limit whenever a PLAYER unit's hardpoint prepares or opens up
    // to release a weapon, and only exempts AI units from that for sonobuoys. Dropping a buoy pattern is routine, so
    // for sonobuoys we put the player's time compression back to what it was. Other weapons are untouched.
    // Applies to both hardpoint spots that apply the cap (the two methods below).
    public static class BuoyTimeCompression
    {
        // Captured before the game's code runs; restored afterwards only if a buoy release caused the drop.
        public static void Before(out float __state) => __state = GameTime.TimeCompression;

        public static void After(WeaponSystemHardpoint __instance, float __state)
        {
            try
            {
                if (__state > 0f && GameTime.TimeCompression < __state && __instance?._ammoForEngage?._ap?._subType == Ammunition.Type.Sonobuoy)
                {
                    GameTime.SetTimeCompression(__state);
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("BuoyTimeCompression", ex);
            }
        }
    }

    [HarmonyPatch(typeof(WeaponSystemHardpoint), "PrepareEngageParameters")]
    public static class BuoyTimeCompressionPreparePatch
    {
        private static void Prefix(out float __state) => BuoyTimeCompression.Before(out __state);

        private static void Postfix(WeaponSystemHardpoint __instance, float __state) => BuoyTimeCompression.After(__instance, __state);
    }

    [HarmonyPatch(typeof(WeaponSystemHardpoint), "CheckMaxRange")]
    public static class BuoyTimeCompressionRangePatch
    {
        private static void Prefix(out float __state) => BuoyTimeCompression.Before(out __state);

        private static void Postfix(WeaponSystemHardpoint __instance, float __state) => BuoyTimeCompression.After(__instance, __state);
    }
}
