using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace ASWAutoHunt
{
    // Shows what an ASW hunter is doing ("ASW Searching", "ASW Fixing", "ASW Prosecuting", ...) as an extra line in its
    // map label. The game's line 4 (jamming info) is always empty for the player's own units, so the mod fills it there,
    // and the game's label layout places and declutters it with the rest of the label.
    public static class MapStatus
    {
        // Constant strings: the replay recorder compares label lines by reference, so the same instance is reused.
        public const string Searching = "ASW Searching";
        public const string SearchingHold = "ASW Searching (Hold)";
        public const string Ready = "ASW Ready";
        public const string Hold = "ASW Hold";
        public const string Fixing = "ASW Fixing";
        public const string Dipping = "ASW Dipping";
        public const string Prosecuting = "ASW Prosecuting";

        private static readonly Dictionary<ObjectBase, string> Shown = new Dictionary<ObjectBase, string>();
        private static readonly Dictionary<ObjectBase, MapUnitViewModel> Views = new Dictionary<ObjectBase, MapUnitViewModel>();
        private static readonly MethodInfo Notify = AccessTools.Method(typeof(MapUnitViewModel), "OnPropertyChanged", new[] { typeof(string) });

        // Called from each player aircraft's update: when its status changes, the label is told to re-read line 4.
        public static void Refresh(ObjectBase o)
        {
            string now = o.IsDestroyed ? null : ContactResponse.StatusOf(o);
            Shown.TryGetValue(o, out string old);
            if ((object)now == old)
            {
                return;
            }

            if (now == null) Shown.Remove(o);
            else Shown[o] = now;

            if (Views.TryGetValue(o, out MapUnitViewModel vm) && vm != null)
            {
                try
                {
                    Notify?.Invoke(vm, new object[] { "ContactInfoLine4" });
                }
                catch (Exception ex)
                {
                    ModLog.Error("MapStatus.Refresh", ex);
                }
            }

            if (o.IsDestroyed)
            {
                Views.Remove(o);
            }
        }

        internal static string For(ObjectBase o, MapUnitViewModel vm)
        {
            if (!Views.TryGetValue(o, out MapUnitViewModel known) || known != vm)
            {
                Views[o] = vm;
            }

            return Shown.TryGetValue(o, out string s) ? s : null;
        }
    }

    [HarmonyPatch(typeof(MapUnitViewModel), nameof(MapUnitViewModel.ContactInfoLine4), MethodType.Getter)]
    public static class MapStatusLabelPatch
    {
        private static void Postfix(MapUnitViewModel __instance, ref string __result)
        {
            try
            {
                ObjectBase o = __instance.Unit?.BaseObject;
                if (o == null || !o.IsPlayerObject || !string.IsNullOrEmpty(__result))
                {
                    return;
                }

                string s = MapStatus.For(o, __instance);
                if (s != null)
                {
                    __result = s;
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("MapStatusLabelPatch", ex);
            }
        }
    }
}
