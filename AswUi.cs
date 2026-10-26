using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MapControl;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;
using UnityEngine;

namespace ASWAutoHunt
{
    public enum PatternKind { GridDatum, GridRoute, Corridor }

    // Builds a pattern, draws it on the map's drawing layer (one marker per buoy) and queues the drops on the aircraft
    // with the game's own "attack at waypoint" tasks, so flight, release and bookkeeping are all native behaviour.
    public static class AswPlanner
    {
        public const float ProgressCheckSec = 3f;
        public const int MaxBuoysPerPattern = 120;

        private sealed class Plan
        {
            public string Ammo;
            public int Total;
            public readonly List<MarkerDrawing> Markers = new List<MarkerDrawing>();
            public float NextCheck;
        }

        private static readonly Dictionary<ObjectBase, Plan> Plans = new Dictionary<ObjectBase, Plan>();

        public static bool IsBuoyCarrier(ObjectBase o) => (o is Aircraft || o is Helicopter) && !o.IsDestroyed && PickAmmo(o, out _) > 0;

        // Prefer the passive buoy we carry most of; fall back to active-only (DICASS) if that is all there is.
        public static int PickAmmo(ObjectBase o, out string ammoFile)
        {
            ammoFile = null;
            int best = 0;
            bool bestPassive = false;
            foreach (Ammunition a in o.AmmunitionNameToAmmunitionDictionary.Values.ToList())
            {
                if (a?._ap == null || a._ap._subType != Ammunition.Type.Sonobuoy) continue;
                string file = a._ap._ammunitionFileName;
                int n = o.getAmmunitionAmountByName(file);
                if (n < 1) continue;
                bool passive = file.IndexOf("ssq-62", StringComparison.OrdinalIgnoreCase) < 0;
                if (ammoFile == null || (passive && !bestPassive) || (passive == bestPassive && n > best))
                {
                    ammoFile = file;
                    best = n;
                    bestPassive = passive;
                }
            }

            return best;
        }

        // Fleet route: current position followed by the unit's player waypoints; else a straight run on its heading.
        public static PatternPlanner.Route RouteOf(ObjectBase ship)
        {
            List<GeoPosition> pts = new List<GeoPosition> { ship._geoPosition };
            try
            {
                foreach (WaypointData w in ship.ExportWaypoints())
                {
                    pts.Add(w._geoposition);
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("RouteOf", ex);
            }

            if (pts.Count < 2)
            {
                pts.Add(ship._geoPosition.GeoOffsetByDistanceOnHeading(150f, ship.getHeading()));
            }

            return new PatternPlanner.Route(pts);
        }

        public static void Assign(ObjectBase src, PatternKind kind, float widthNm, GeoPosition datum, ObjectBase fleet, float gridHeading = 90f)
        {
            try
            {
                int have = PickAmmo(src, out string ammo);
                if (have < 1)
                {
                    src.ShowOrderFeedback("No sonobuoys aboard");
                    return;
                }

                int budget = Math.Min(have, MaxBuoysPerPattern);
                List<PlannedBuoy> pattern;
                if (kind == PatternKind.GridDatum)
                {
                    pattern = PatternPlanner.GridAtDatum(datum, ammo, budget, gridHeading);
                }
                else
                {
                    PatternPlanner.Route route = RouteOf(fleet);
                    pattern = kind == PatternKind.GridRoute
                        ? PatternPlanner.GridAlongRoute(route, ammo, budget)
                        : PatternPlanner.Corridor(route, ammo, widthNm, budget);
                }

                if (pattern.Count == 0)
                {
                    src.ShowOrderFeedback("Pattern does not fit the buoys carried");
                    return;
                }

                PatternPlanner.OrientFor(pattern, src._geoPosition);
                Cancel(src, silent: true);

                // Same preparation the game's own sonobuoy-line mode does before queuing drops.
                if (src.CurrentOrder.Value.OrderType != Order.Type.None)
                {
                    src.setOrder(Order.Type.None, (ObjectBase)null, displayOrderText: false);
                }

                if (src.Formation != null && src.Formation.LeaderStation.UnitObject != src)
                {
                    src.Formation.MakeUnitActIndependently(src);
                }

                src.RemoveWaypoints();

                Ammunition ammoObj = src.getAmmunitionByName(ammo);
                VisualActionTask last = null;
                Plan plan = new Plan { Ammo = ammo, Total = pattern.Count, NextCheck = Time.time + ProgressCheckSec };
                foreach (PlannedBuoy b in pattern)
                {
                    last = src.SetAttackAtWaypointTask(ammoObj._ap._ammunitionFileName, null, b.Pos, b.Pos, 1, last);
                    MarkerDrawing m = new MarkerDrawing { Location = b.Pos };
                    DrawingLayerOf()?.Graphics.Add(m);
                    plan.Markers.Add(m);
                }

                Plans[src] = plan;
                string desc = kind == PatternKind.Corridor ? $"corridor {widthNm:0} nm wide" : kind == PatternKind.GridRoute ? "grid along route" : "grid at datum";
                src.ShowOrderFeedback($"ASW pattern: {desc}, {pattern.Count} buoys, spacing {(kind == PatternKind.Corridor ? PatternPlanner.BarrierSpacingNm(ammo) : PatternPlanner.HexSpacingNm(ammo)):0.0} nm");
                ModLog.Event($"ASW pattern assigned: {src.getUIDAndName()} {desc} buoys={pattern.Count}/{have} ammo={ammo}");
            }
            catch (Exception ex)
            {
                ModLog.Error("Assign", ex);
            }
        }

        public static void Cancel(ObjectBase src, bool silent = false)
        {
            if (Plans.TryGetValue(src, out Plan plan))
            {
                ClearMarkers(plan, plan.Markers.Count);
                Plans.Remove(src);
                if (!silent)
                {
                    src.RemoveWaypoints();
                    src.ShowOrderFeedback("ASW pattern cancelled");
                }
            }
        }

        private static MapDrawingLayer DrawingLayerOf() => Globals._mainGameViewModel?.Map?.DrawingLayer;

        private static void ClearMarkers(Plan plan, int count)
        {
            MapDrawingLayer layer = DrawingLayerOf();
            for (int i = 0; i < count && plan.Markers.Count > 0; i++)
            {
                layer?.Graphics.Remove(plan.Markers[0]);
                plan.Markers.RemoveAt(0);
            }
        }

        // Called from the per-unit fixed update: remove markers for buoys already dropped, then forget the plan when done.
        public static void Tick(ObjectBase o)
        {
            if (Plans.Count == 0 || !Plans.TryGetValue(o, out Plan plan) || Time.time < plan.NextCheck) return;
            plan.NextCheck = Time.time + ProgressCheckSec;
            try
            {
                int remaining = 0;
                if (o._userRoot != null)
                {
                    foreach (ITaskViewModel tvm in o._userRoot.TaskViewModels)
                    {
                        if (tvm?.Task is AttackAtWaypoint a && a._ammunitionName == plan.Ammo) remaining += a._salvo;
                    }
                }

                if (o.IsDestroyed || remaining == 0)
                {
                    ClearMarkers(plan, plan.Markers.Count);
                    Plans.Remove(o);
                    return;
                }

                int dropped = plan.Total - remaining;
                int toClear = dropped - (plan.Total - plan.Markers.Count);
                if (toClear > 0) ClearMarkers(plan, toClear);
            }
            catch (Exception ex)
            {
                ModLog.Error("AswPlanner.Tick", ex);
            }
        }
    }

    // Click-to-pick: right-click the datum (map) or the fleet ship whose route is to be screened; middle-click cancels.
    public class AswPickState : InteractionState
    {
        private readonly InteractionStateManager _manager;
        private readonly ObjectBase _source;
        private readonly PatternKind _kind;
        private readonly float _width;

        public AswPickState(InteractionStateManager manager, ObjectBase source, PatternKind kind, float widthNm)
        {
            CursorName = "default";
            _manager = manager;
            _source = source;
            _kind = kind;
            _width = widthNm;
        }

        private bool NeedsShip => _kind != PatternKind.GridDatum;

        // Datum grid is two clicks: centre, then a second point that sets the row direction (rubber-band line shown between).
        private GeoPosition? _centre;
        private MarkerDrawing _centreMarker;
        private MarkerDrawing _endMarker;
        private RulerDrawing _ruler;

        public override void OnExit()
        {
            ClearPreview();
            base.OnExit();
        }

        private void ClearPreview()
        {
            MapDrawingLayer layer = Globals._mainGameViewModel?.Map?.DrawingLayer;
            if (layer == null) return;
            if (_ruler != null) layer.Graphics.Remove(_ruler);
            if (_centreMarker != null) layer.Graphics.Remove(_centreMarker);
            if (_endMarker != null) layer.Graphics.Remove(_endMarker);
            _ruler = null;
            _centreMarker = null;
            _endMarker = null;
        }

        private void StartPreview(GeoPosition centre)
        {
            MapDrawingLayer layer = Globals._mainGameViewModel?.Map?.DrawingLayer;
            if (layer == null) return;
            _centreMarker = new MarkerDrawing { Location = centre };
            _endMarker = new MarkerDrawing { Location = centre };
            _ruler = new RulerDrawing { Start = _centreMarker, End = _endMarker };
            layer.Graphics.Add(_centreMarker);
            layer.Graphics.Add(_endMarker);
            layer.Graphics.Add(_ruler);
        }

        private static ObjectBase PickShip(object v)
        {
            ObjectBase o = (v as MapUnitViewModel)?.Unit?.BaseObject;
            return o is Vessel && o.IsPlayerObject ? o : null;
        }

        public override Noesis.HitTestResultBehavior MouseAction(object v, Noesis.RoutedEventArgs args, GeoPosition mousePosition, MapBase map, ref bool handled)
        {
            if (args is Noesis.MouseButtonEventArgs e)
            {
                if (e.RightButton == Noesis.MouseButtonState.Pressed)
                {
                    if (_kind == PatternKind.GridDatum)
                    {
                        if (_centre == null)
                        {
                            _centre = mousePosition;
                            StartPreview(mousePosition);
                            _source?.ShowOrderFeedback("Right-click again to set the grid direction (middle-click cancels)");
                        }
                        else
                        {
                            float heading = _centre.Value.HeadingToPosition(mousePosition);
                            AswPlanner.Assign(_source, _kind, _width, _centre.Value, null, heading);
                            _manager.PopState<AswPickState>();
                        }

                        handled = true;
                        return Noesis.HitTestResultBehavior.Stop;
                    }

                    ObjectBase ship = PickShip(v);
                    if (ship == null)
                    {
                        _source?.ShowOrderFeedback("Right-click a friendly ship (its route is screened)");
                    }
                    else
                    {
                        AswPlanner.Assign(_source, _kind, _width, mousePosition, ship);
                    }

                    _manager.PopState<AswPickState>();
                    handled = true;
                    return Noesis.HitTestResultBehavior.Stop;
                }

                if (e.MiddleButton == Noesis.MouseButtonState.Pressed)
                {
                    _manager.PopState<AswPickState>();
                    handled = true;
                    return Noesis.HitTestResultBehavior.Stop;
                }
            }
            else if (NeedsShip)
            {
                CursorName = PickShip(v) != null ? "identify" : "unavailable";
            }
            else if (_endMarker != null)
            {
                _endMarker.Location = mousePosition;
                _ruler?.Update();
            }

            return base.MouseAction(v, args, mousePosition, map, ref handled);
        }

        public static void Begin(ObjectBase src, PatternKind kind, float widthNm)
        {
            if (src == null || src.IsDestroyed) return;
            src.ShowOrderFeedback(kind == PatternKind.GridDatum ? "Right-click the datum (grid centre)" : "Right-click the fleet ship whose route to screen");
            InteractionStateManager mgr = Globals._mainGameViewModel.StateManager;
            mgr.PushSingleState(new AswPickState(mgr, src, kind, widthNm));
        }
    }

    // Orders submenu: "ASW Pattern" with grid / corridor entries (only for units carrying buoys).
    [HarmonyPatch(typeof(ObjectBaseViewModel), "GetContextMenuItems")]
    public static class AswMenuPatch
    {
        private static readonly float[] CorridorWidths = { 20f, 40f, 60f, 80f, 100f };

        private static void Postfix(ObjectBase ____objectBase, TrulyObservableCollection<ContextMenuItem> __result)
        {
            try
            {
                ObjectBase o = ____objectBase;
                if (o == null || __result == null || !(o.IsPlayerObject || Globals.AllowControlOfEnemyUnits.Value) || !AswPlanner.IsBuoyCarrier(o))
                {
                    return;
                }

                string ordersLabel = Singleton<LanguageResourceHandler>.Instance.getText("ContextMenu", "Orders");
                foreach (ContextMenuItem item in __result)
                {
                    if (item?.Items == null || !string.Equals(item.Label as string, ordersLabel, StringComparison.Ordinal)) continue;

                    TrulyObservableCollection<ContextMenuItem> sub = new TrulyObservableCollection<ContextMenuItem>
                    {
                        new ContextMenuItem("Grid at datum", null, new DelegateCommand(_ => AswPickState.Begin(o, PatternKind.GridDatum, 0f))),
                        new ContextMenuItem("Grid along fleet route", null, new DelegateCommand(_ => AswPickState.Begin(o, PatternKind.GridRoute, 0f)))
                    };
                    foreach (float w in CorridorWidths)
                    {
                        float width = w;
                        sub.Add(new ContextMenuItem($"Corridor {width:0} nm wide", null, new DelegateCommand(_ => AswPickState.Begin(o, PatternKind.Corridor, width))));
                    }

                    sub.Add(new ContextMenuItem("Cancel ASW pattern", null, new DelegateCommand(_ => AswPlanner.Cancel(o))));
                    item.Items.Add(new ContextMenuItem("ASW Pattern", sub));
                    return;
                }

                ModLog.Warn("AswMenuPatch: Orders submenu not found");
            }
            catch (Exception ex)
            {
                ModLog.Error("AswMenuPatch", ex);
            }
        }
    }

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.OnFixedUpdate))]
    public static class AswTickAircraftPatch
    {
        private static void Postfix(Aircraft __instance) => AswPlanner.Tick(__instance);
    }

    [HarmonyPatch(typeof(Helicopter), nameof(Helicopter.OnFixedUpdate))]
    public static class AswTickHelicopterPatch
    {
        private static void Postfix(Helicopter __instance) => AswPlanner.Tick(__instance);
    }
}
