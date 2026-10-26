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
    public enum PatternKind { GridDatum, GridRoute, Corridor, Ring, Line }

    // Builds a pattern, draws it on the map's drawing layer (one marker per buoy) and queues the drops on the aircraft
    // with the game's own "attack at waypoint" tasks, so flight, release and bookkeeping are all native behaviour.
    public static class AswPlanner
    {
        public const float ProgressCheckSec = 1f;
        public const int MaxBuoysPerPattern = 120;

        private sealed class Plan
        {
            public string Ammo;
            public int Total;
            public readonly List<MarkerDrawing> Markers = new List<MarkerDrawing>();
            public readonly List<GeoPosition> Points = new List<GeoPosition>(); // undropped points, in drop order
            public readonly List<string> Tags = new List<string>();             // parallel to Points (L/R/C/E/G...)
            public int Dropped;                                                 // buoys logged as dropped so far
            public GeoPosition LastPos;                                         // previous drop, for leg length
            public int StartAmmo;       // buoys aboard when the (re)queue happened
            public bool Interrupted;    // queue was wiped (e.g. manual waypoint) before the pattern finished
            public float NextCheck;
        }

        public static bool CanResume(ObjectBase o) => Plans.TryGetValue(o, out Plan p) && p.Interrupted && p.Points.Count > 0;

        // Same preparation the game's own sonobuoy-line mode does before queuing drops.
        private static void QueueDrops(ObjectBase src, Plan plan, int count)
        {
            if (src.CurrentOrder.Value.OrderType != Order.Type.None)
            {
                src.setOrder(Order.Type.None, (ObjectBase)null, displayOrderText: false);
            }

            if (src.Formation != null && src.Formation.LeaderStation.UnitObject != src)
            {
                src.Formation.MakeUnitActIndependently(src);
            }

            src.RemoveWaypoints();
            Ammunition ammoObj = src.getAmmunitionByName(plan.Ammo);
            VisualActionTask last = null;
            for (int i = 0; i < count; i++)
            {
                last = src.SetAttackAtWaypointTask(ammoObj._ap._ammunitionFileName, null, plan.Points[i], plan.Points[i], 1, last);
            }

            plan.StartAmmo = src.getAmmunitionAmountByName(plan.Ammo);
            plan.Interrupted = false;
            plan.NextCheck = Time.time + ProgressCheckSec;
        }

        // Re-queue the buoys still undropped after the queue was cleared (e.g. by an accidental manual waypoint).
        public static void Resume(ObjectBase src)
        {
            try
            {
                if (!CanResume(src))
                {
                    src.ShowOrderFeedback("No interrupted ASW pattern to resume");
                    return;
                }

                Plan plan = Plans[src];
                int have = src.getAmmunitionAmountByName(plan.Ammo);
                int count = Math.Min(plan.Points.Count, have);
                if (count < 1)
                {
                    src.ShowOrderFeedback("No sonobuoys left to resume pattern");
                    Cancel(src, silent: true);
                    return;
                }

                // Drop surplus points (and their markers) if we no longer carry enough buoys.
                while (plan.Points.Count > count)
                {
                    plan.Points.RemoveAt(plan.Points.Count - 1);
                    plan.Tags.RemoveAt(plan.Tags.Count - 1);
                    DrawingLayerOf()?.Graphics.Remove(plan.Markers[plan.Markers.Count - 1]);
                    plan.Markers.RemoveAt(plan.Markers.Count - 1);
                }

                plan.Total = count;
                QueueDrops(src, plan, count);
                src.ShowOrderFeedback($"Resuming ASW pattern: {count} buoys remaining");
                ModLog.Event($"ASW pattern resumed: {src.getUIDAndName()} buoys={count}");
            }
            catch (Exception ex)
            {
                ModLog.Error("Resume", ex);
            }
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

        // What the player asked for, kept while any participant still has drops queued so that sharing the pattern with
        // another aircraft afterwards can recompute the whole layout.
        private sealed class Request
        {
            public PatternKind Kind;
            public float WidthNm;
            public GeoPosition Datum;
            public ObjectBase Fleet;
            public float Heading;
            public GeoPosition? Second;
            public int Dropped;                                              // buoys already placed by earlier versions of this layout
            public readonly HashSet<ObjectBase> Members = new HashSet<ObjectBase>();
        }

        private static readonly Dictionary<ObjectBase, Request> Requests = new Dictionary<ObjectBase, Request>();

        public static void Assign(ObjectBase src, PatternKind kind, float widthNm, GeoPosition datum, ObjectBase fleet, float gridHeading = 90f, GeoPosition? second = null)
        {
            Request r = new Request { Kind = kind, WidthNm = widthNm, Datum = datum, Fleet = fleet, Heading = gridHeading, Second = second };
            Requests[src] = r;
            Run(src, r);
        }

        // Recompute the active layout for src with its current partner set (called when a partner is added or removed).
        private static void Replan(ObjectBase src)
        {
            if (!Requests.TryGetValue(src, out Request r)) return;
            foreach (ObjectBase m in r.Members)
            {
                if (Plans.TryGetValue(m, out Plan p)) r.Dropped += p.Dropped;
            }

            ModLog.Event($"ASW replan: {src.getUIDAndName()} partners={PartnerCount(src)} already_placed={r.Dropped}");
            Run(src, r);
        }

        // Forget finished requests: nobody involved has a plan any more.
        private static void ReleaseIfIdle()
        {
            foreach (var kv in Requests.ToList())
            {
                if (kv.Value.Members.Any(m => Plans.ContainsKey(m))) continue;
                Requests.Remove(kv.Key);
                Partners.Remove(kv.Key);
            }
        }

        private static void Run(ObjectBase src, Request req)
        {
            PatternKind kind = req.Kind;
            float widthNm = req.WidthNm;
            GeoPosition datum = req.Datum;
            ObjectBase fleet = req.Fleet;
            float gridHeading = req.Heading;
            GeoPosition? second = req.Second;
            try
            {
                int have = PickAmmo(src, out string ammo);
                if (have < 1)
                {
                    src.ShowOrderFeedback("No sonobuoys aboard");
                    return;
                }

                int budget = Math.Min(have, MaxBuoysPerPattern);
                List<ObjectBase> group = new List<ObjectBase> { src };
                List<string> ammos = new List<string> { ammo };
                foreach (ObjectBase p in PartnersOf(src))
                {
                    int n = PickAmmo(p, out string pa);
                    if (n < 1) continue;
                    group.Add(p);
                    ammos.Add(pa);
                    budget += n;
                }

                foreach (ObjectBase g in group) req.Members.Add(g);
                // Buoys placed by an earlier version of this layout count toward its size; deconfliction then skips them.
                budget = Math.Min(budget + req.Dropped, MaxBuoysPerPattern * group.Count);
                // Lay the pattern out for the shortest-range buoy in the group so coverage holds whichever aircraft
                // places each point; longer-range buoys are used to thin points they already cover (see ThinForRange).
                foreach (string a in ammos)
                {
                    if (PatternPlanner.EffectiveRadiusNm(a) < PatternPlanner.EffectiveRadiusNm(ammo)) ammo = a;
                }
                List<PlannedBuoy> pattern;
                if (kind == PatternKind.GridDatum)
                {
                    pattern = PatternPlanner.GridAtDatum(datum, ammo, budget, gridHeading);
                }
                else if (kind == PatternKind.Ring)
                {
                    pattern = PatternPlanner.Ring(datum, PatternPlanner.DistNm(datum, second ?? datum), ammo, budget);
                }
                else if (kind == PatternKind.Line)
                {
                    pattern = PatternPlanner.Line(datum, second ?? datum, ammo, budget);
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

                // Orient from the group's centroid so the slowest aircraft (given the first points) start near the near end.
                double cLat = 0, cLon = 0;
                foreach (ObjectBase g in group)
                {
                    cLat += g._geoPosition._latitude;
                    cLon += g._geoPosition._longitude;
                }

                GeoPosition centroid = group.Count == 1 ? src._geoPosition : new GeoPosition(cLat / group.Count, cLon / group.Count);
                PatternPlanner.OrientFor(pattern, centroid);
                int before = pattern.Count;
                List<CoverageSource> existing = CoverageSources(src, group, out int nWater, out int nQueued);
                pattern = Deconflict(pattern, existing, ammo);
                int skipped = before - pattern.Count;
                if (pattern.Count == 0)
                {
                    src.ShowOrderFeedback("Area already covered by existing or queued buoys");
                    ModLog.Event($"ASW pattern skipped: {src.getUIDAndName()} fully covered (water={nWater} queued={nQueued})");
                    return;
                }

                string desc = kind == PatternKind.Corridor ? $"corridor {widthNm:0} nm wide" : kind == PatternKind.GridRoute ? "grid along route"
                    : kind == PatternKind.Ring ? "ring with infill" : kind == PatternKind.Line ? "straight line" : "grid at datum";
                bool barrier = kind == PatternKind.Corridor || kind == PatternKind.Line;
                ModLog.Event($"ASW pattern assigned: {src.getUIDAndName()} {desc} buoys={pattern.Count}/{budget} skipped={skipped} (water={nWater} queued={nQueued}) ammo={ammo} aircraft={group.Count}");

                List<List<PlannedBuoy>> shares;
                if (group.Count == 1)
                {
                    shares = new List<List<PlannedBuoy>> { pattern };
                }
                else
                {
                    List<PatternPlanner.Carrier> carriers = new List<PatternPlanner.Carrier>();
                    for (int i = 0; i < group.Count; i++)
                    {
                        carriers.Add(new PatternPlanner.Carrier { Pos = group[i]._geoPosition, SpeedKts = CruiseKnots(group[i]), Buoys = PickAmmo(group[i], out _) });
                    }

                    shares = PatternPlanner.Split(pattern, carriers);
                    int thinned = ThinForRange(shares, ammos);
                    if (thinned > 0)
                    {
                        ModLog.Event($"  coordination: {thinned} points thinned (covered by longer-range buoys in the group)");
                        skipped += thinned;
                    }
                }

                for (int i = 0; i < group.Count; i++)
                {
                    Cancel(group[i], silent: true);
                    if (shares[i].Count == 0)
                    {
                        if (group.Count > 1) ModLog.Event($"  coordination: {group[i].getUIDAndName()} given no buoys");
                        continue;
                    }

                    string extra = i == 0 && skipped > 0 ? $" ({skipped} skipped, already covered)" : "";
                    string who = group.Count > 1 ? $" [share {shares[i].Count}/{pattern.Count}, {group.Count} aircraft]" : "";
                    Launch(group[i], ammos[i], shares[i]);
                    group[i].ShowOrderFeedback($"ASW pattern: {desc}, {shares[i].Count} buoys{extra}{who}, spacing {(barrier ? PatternPlanner.BarrierSpacingNm(ammo) : PatternPlanner.HexSpacingNm(ammo)):0.0} nm");
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("Assign", ex);
            }
        }

        // Queues one aircraft's share, draws its markers and logs the planned order with leg lengths.
        private static void Launch(ObjectBase acft, string ammo, List<PlannedBuoy> pts)
        {
            PatternPlanner.PullOnPath(pts, acft._geoPosition);
            Plan plan = new Plan { Ammo = ammo, Total = pts.Count, LastPos = acft._geoPosition };
            foreach (PlannedBuoy b in pts)
            {
                plan.Points.Add(b.Pos);
                plan.Tags.Add(b.Tag);
            }

            QueueDrops(acft, plan, plan.Points.Count);
            GeoPosition prev = acft._geoPosition;
            float total = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                float leg = PatternPlanner.DistNm(prev, pts[i].Pos);
                total += leg;
                ModLog.Event($"  plan {acft.getUIDAndName()} #{i + 1} {pts[i].Tag} leg={leg:0.0}nm cum={total:0.0}nm lat={pts[i].Pos._latitude:0.000} lon={pts[i].Pos._longitude:0.000}");
                prev = pts[i].Pos;
            }

            foreach (PlannedBuoy b in pts)
            {
                MarkerDrawing m = new MarkerDrawing { Location = b.Pos };
                DrawingLayerOf()?.Graphics.Add(m);
                plan.Markers.Add(m);
            }

            Plans[acft] = plan;
        }

        // ---- Coordination: aircraft the player has chosen to share the next pattern with ----
        private static readonly Dictionary<ObjectBase, HashSet<ObjectBase>> Partners = new Dictionary<ObjectBase, HashSet<ObjectBase>>();
        public const int MaxListedPartners = 8;

        // Assumed patrol speed: a fraction of max forward speed, never below a hover-ish floor.
        public static float CruiseKnots(ObjectBase o) => Math.Max(60f, o.MaxForwardSpeedInKnots * 0.7f);

        // Formation mates the player has explicitly opted out of the shared pattern.
        private static readonly Dictionary<ObjectBase, HashSet<ObjectBase>> Excluded = new Dictionary<ObjectBase, HashSet<ObjectBase>>();

        // Every member of src's formation (other than src) that currently carries buoys.
        private static IEnumerable<ObjectBase> FormationMates(ObjectBase src)
        {
            UnitFormation f = src.Formation;
            if (f?.Stations == null) yield break;
            foreach (Station st in f.Stations.ToList())
            {
                ObjectBase m = st?.UnitObject;
                if (m != null && m != src) yield return m;
            }
        }

        public static bool IsPartner(ObjectBase src, ObjectBase o)
        {
            if (Excluded.TryGetValue(src, out var ex) && ex.Contains(o)) return false;
            return (Partners.TryGetValue(src, out var s) && s.Contains(o)) || FormationMates(src).Contains(o);
        }

        // Sharing joins the partner to src's formation when the game allows it (same aircraft class); mixed
        // fixed-wing/helicopter pairs stay in their own formations and are linked here instead. Each member
        // is still given its own queued drops by Run/Launch.
        public static void TogglePartner(ObjectBase src, ObjectBase o)
        {
            if (!Partners.TryGetValue(src, out var s)) Partners[src] = s = new HashSet<ObjectBase>();
            if (!Excluded.TryGetValue(src, out var ex)) Excluded[src] = ex = new HashSet<ObjectBase>();
            bool wasOn = IsPartner(src, o);
            if (wasOn)
            {
                s.Remove(o);
                ex.Add(o);
                Cancel(o, silent: false);
            }
            else
            {
                ex.Remove(o);
                s.Add(o);
                JoinFormation(src, o);
            }

            src.ShowOrderFeedback($"ASW coordination: {PartnersOf(src).Count()} other aircraft share the pattern");
            if (Requests.ContainsKey(src)) Replan(src);
        }

        private static void JoinFormation(ObjectBase src, ObjectBase o)
        {
            try
            {
                if (o.Formation != null && o.Formation == src.Formation) return;
                if (!UnitFormation.CheckFormationJoin(src, o)) return;
                if (src.Formation == null)
                {
                    src.Formation = new UnitFormation(new UnitFormationParameters(src._type == ObjectBase.ObjectType.Aircraft) { _leaderObject = src });
                }

                src.Formation.AddUnit(o);
                ModLog.Event($"ASW: {o.getUIDAndName()} added to formation of {src.getUIDAndName()}");
            }
            catch (Exception ex)
            {
                ModLog.Error("AswPlanner.JoinFormation", ex);
            }
        }

        public static void ClearPartners(ObjectBase src) { Partners.Remove(src); Excluded.Remove(src); }
        public static int PartnerCount(ObjectBase src) => PartnersOf(src).Count();

        private static IEnumerable<ObjectBase> PartnersOf(ObjectBase src)
        {
            HashSet<ObjectBase> seen = new HashSet<ObjectBase>();
            IEnumerable<ObjectBase> all = FormationMates(src);
            if (Partners.TryGetValue(src, out var s)) all = all.Concat(s.ToList());
            Excluded.TryGetValue(src, out var ex);
            foreach (ObjectBase o in all)
            {
                if (o == null || o == src || o.IsDestroyed || (ex != null && ex.Contains(o)) || !IsBuoyCarrier(o) || !seen.Add(o)) continue;
                yield return o;
            }
        }

        // Other friendly buoy carriers, nearest first, for the coordination menu.
        public static List<ObjectBase> CandidatePartners(ObjectBase src)
        {
            List<ObjectBase> res = new List<ObjectBase>();
            ObjectsManager mgr = Singleton<ObjectsManager>.Instance;
            if (mgr?._listOfAllObjects == null || src?._taskforce == null) return res;
            foreach (ObjectBase o in mgr._listOfAllObjects.ToList())
            {
                if (o == null || o == src || o._taskforce == null || o._taskforce.Side != src._taskforce.Side || !o.IsPlayerObject) continue;
                if (IsBuoyCarrier(o)) res.Add(o);
            }

            return res.OrderBy(o => PatternPlanner.DistNm(src._geoPosition, o._geoPosition)).Take(MaxListedPartners).ToList();
        }

        private struct CoverageSource
        {
            public readonly GeoPosition Pos;
            public readonly float RadiusNm;
            public CoverageSource(GeoPosition pos, float radiusNm) { Pos = pos; RadiusNm = radiusNm; }
        }

        // Mixed buoy types: the pattern was laid out for the shortest-range buoy. Going from the longest-range owner down,
        // drop any point already covered by a kept point (same overlap rule as deconfliction, using each buoy's own
        // radius), so long-range buoys space out naturally and short-range ones fill only what is left uncovered.
        private static int ThinForRange(List<List<PlannedBuoy>> shares, List<string> ammos)
        {
            float rMin = ammos.Min(PatternPlanner.EffectiveRadiusNm), rMax = ammos.Max(PatternPlanner.EffectiveRadiusNm);
            if (rMax - rMin < 0.01f) return 0;

            List<CoverageSource> kept = new List<CoverageSource>();
            int removed = 0;
            foreach (int i in Enumerable.Range(0, shares.Count).OrderByDescending(k => PatternPlanner.EffectiveRadiusNm(ammos[k])))
            {
                float r = PatternPlanner.EffectiveRadiusNm(ammos[i]);
                List<PlannedBuoy> keep = new List<PlannedBuoy>();
                foreach (PlannedBuoy p in shares[i])
                {
                    if (kept.Any(q => PatternPlanner.DistNm(p.Pos, q.Pos) < OverlapFactor * (r + q.RadiusNm)))
                    {
                        removed++;
                        continue;
                    }

                    keep.Add(p);
                    kept.Add(new CoverageSource(p.Pos, r));
                }

                shares[i] = keep;
            }

            return removed;
        }

        // ---- Deconfliction ----
        // A planned buoy is dropped when an existing/queued friendly buoy is closer than OverlapFactor x (new radius + that
        // buoy's own effective radius). For equal buoys 0.75 x 2r = 1.5r, just inside the hex (1.73r) and barrier (1.8r)
        // spacings, so a pattern's own neighbours never trigger it, but a buoy mostly inside another's coverage does.
        public const float OverlapFactor = 0.75f;
        public const float MinRemainingLifeSec = 900f; // buoys about to expire do not count as coverage

        // Positions of friendly buoys in the water or in flight, and drops queued by any friendly aircraft/helicopter
        // (this includes manual shift-queued drops). The unit being tasked is skipped: its old plan is being replaced.
        private static List<CoverageSource> CoverageSources(ObjectBase src, ICollection<ObjectBase> replaced, out int inWater, out int queued)
        {
            List<CoverageSource> res = new List<CoverageSource>();
            inWater = 0;
            queued = 0;
            ObjectsManager mgr = Singleton<ObjectsManager>.Instance;
            if (mgr?._listOfAllObjects == null || src?._taskforce == null) return res;
            foreach (ObjectBase o in mgr._listOfAllObjects.ToList())
            {
                if (o == null || o.IsDestroyed || o._taskforce == null || o._taskforce.Side != src._taskforce.Side) continue;
                if (o is Bomb b)
                {
                    if (b._ap == null || b._ap._subType != Ammunition.Type.Sonobuoy) continue;
                    if (b.Deployed.Value && b.RemainingLife.Value < MinRemainingLifeSec) continue;
                    res.Add(new CoverageSource(b._geoPosition, PatternPlanner.EffectiveRadiusNm(b._ap._ammunitionFileName)));
                    inWater++;
                }
                else if ((o is Aircraft || o is Helicopter) && !replaced.Contains(o) && o._userRoot != null)
                {
                    foreach (ITaskViewModel tvm in o._userRoot.TaskViewModels)
                    {
                        if (tvm?.Task is AttackAtWaypoint a && o.getAmmunitionByName(a._ammunitionName)?._ap?._subType == Ammunition.Type.Sonobuoy)
                        {
                            float qr = PatternPlanner.EffectiveRadiusNm(a._ammunitionName);
                            for (int i = 0; i < Math.Max(1, a._salvo); i++) res.Add(new CoverageSource(a.WaypointPosition, qr));
                            queued += Math.Max(1, a._salvo);
                        }
                    }
                }
            }

            return res;
        }

        // Keeps pattern order; removes points already covered by an existing source. Neighbours in the new pattern are not
        // compared with each other (the planner already spaces them correctly).
        private static List<PlannedBuoy> Deconflict(List<PlannedBuoy> pattern, List<CoverageSource> existing, string ammo)
        {
            if (existing.Count == 0) return pattern;
            float rNew = PatternPlanner.EffectiveRadiusNm(ammo);
            List<PlannedBuoy> kept = new List<PlannedBuoy>(pattern.Count);
            foreach (PlannedBuoy p in pattern)
            {
                bool covered = false;
                foreach (CoverageSource q in existing)
                {
                    // Covered when the two detection circles overlap by more than the allowed fraction (existing buoy's own radius).
                    if (PatternPlanner.DistNm(p.Pos, q.Pos) < OverlapFactor * (rNew + q.RadiusNm))
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered) kept.Add(p);
            }

            return kept;
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

            // A manual cancel on the requester ends the whole shared pattern.
            if (!silent && Requests.TryGetValue(src, out Request req))
            {
                    Requests.Remove(src);
                    foreach (ObjectBase m in req.Members.ToList())
                    {
                        if (m != src && Plans.ContainsKey(m)) Cancel(m, silent: false);
                    }

                    ClearPartners(src);
            }
        }

        // Logs and removes the first n points as dropped (detected within ProgressCheckSec of the release).
        private static void LogDrops(ObjectBase o, Plan plan, int n)
        {
            n = Math.Min(n, plan.Points.Count);
            for (int i = 0; i < n; i++)
            {
                GeoPosition p = plan.Points[i];
                float leg = PatternPlanner.DistNm(plan.LastPos, p);
                plan.Dropped++;
                ModLog.Event($"  drop {o.getUIDAndName()} #{plan.Dropped} {plan.Tags[i]} leg={leg:0.0}nm acft_to_point={PatternPlanner.DistNm(o._geoPosition, p):0.0}nm lat={p._latitude:0.000} lon={p._longitude:0.000}");
                plan.LastPos = p;
            }

            plan.Points.RemoveRange(0, n);
            plan.Tags.RemoveRange(0, n);
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
                if (plan.Interrupted)
                {
                    if (o.IsDestroyed) Cancel(o, silent: true);
                    return;
                }

                int remaining = 0;
                if (o._userRoot != null)
                {
                    foreach (ITaskViewModel tvm in o._userRoot.TaskViewModels)
                    {
                        if (tvm?.Task is AttackAtWaypoint a && a._ammunitionName == plan.Ammo) remaining += a._salvo;
                    }
                }

                if (o.IsDestroyed)
                {
                    ClearMarkers(plan, plan.Markers.Count);
                    Plans.Remove(o);
                    ReleaseIfIdle();
                    return;
                }

                if (remaining == 0)
                {
                    // Queue empty: finished, or wiped by another order. Ammo used tells which.
                    int used = plan.StartAmmo - o.getAmmunitionAmountByName(plan.Ammo);
                    if (used >= plan.Points.Count)
                    {
                        LogDrops(o, plan, plan.Points.Count);
                        ModLog.Event($"ASW pattern complete: {o.getUIDAndName()} dropped={plan.Dropped}");
                        ClearMarkers(plan, plan.Markers.Count);
                        Plans.Remove(o);
                        ReleaseIfIdle();
                        return;
                    }

                    LogDrops(o, plan, Math.Max(0, used));
                    ClearMarkers(plan, Math.Max(0, plan.Markers.Count - plan.Points.Count));
                    plan.Interrupted = true;
                    o.ShowOrderFeedback($"ASW pattern interrupted, {plan.Points.Count} buoys unplaced (ASW Pattern > Resume)");
                    ModLog.Event($"ASW pattern interrupted: {o.getUIDAndName()} remaining={plan.Points.Count}");
                    return;
                }

                // Markers track the queue: one per point still queued.
                int toClear = plan.Markers.Count - remaining;
                if (toClear > 0)
                {
                    int done = plan.Points.Count - remaining;
                    LogDrops(o, plan, done);
                    ClearMarkers(plan, toClear);
                }
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

        private bool TwoClick => _kind == PatternKind.GridDatum || _kind == PatternKind.Ring || _kind == PatternKind.Line;
        private bool NeedsShip => !TwoClick;

        // Datum grid is two clicks: centre, then a second point that sets the row direction (rubber-band line shown between).
        private GeoPosition? _centre;
        private MarkerDrawing _centreMarker;
        private MarkerDrawing _endMarker;
        private RingDrawing _ring;
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
            if (_ring != null) layer.Graphics.Remove(_ring);
            if (_ruler != null) layer.Graphics.Remove(_ruler);
            _ruler = null;
            if (_centreMarker != null) layer.Graphics.Remove(_centreMarker);
            if (_endMarker != null) layer.Graphics.Remove(_endMarker);
            _ring = null;
            _centreMarker = null;
            _endMarker = null;
        }

        private void StartPreview(GeoPosition centre)
        {
            MapDrawingLayer layer = Globals._mainGameViewModel?.Map?.DrawingLayer;
            if (layer == null) return;
            _centreMarker = new MarkerDrawing { Location = centre };
            _endMarker = new MarkerDrawing { Location = centre };
            layer.Graphics.Add(_centreMarker);
            layer.Graphics.Add(_endMarker);
            if (_kind == PatternKind.Line)
            {
                _ruler = new RulerDrawing { Start = _centreMarker, End = _endMarker };
                layer.Graphics.Add(_ruler);
            }
            else
            {
                _ring = new RingDrawing { Center = _centreMarker, Extremis = _endMarker };
                layer.Graphics.Add(_ring);
            }
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
                    if (TwoClick)
                    {
                        if (_centre == null)
                        {
                            _centre = mousePosition;
                            StartPreview(mousePosition);
                            _source?.ShowOrderFeedback(_kind == PatternKind.GridDatum ? "Right-click again to set the grid direction (middle-click cancels)"
                                : _kind == PatternKind.Ring ? "Right-click again to set the ring radius (middle-click cancels)"
                                : "Right-click the end of the line (middle-click cancels)");
                        }
                        else
                        {
                            float heading = _centre.Value.HeadingToPosition(mousePosition);
                            AswPlanner.Assign(_source, _kind, _width, _centre.Value, null, heading, mousePosition);
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
                _ring?.Update();
                _ruler?.Update();
            }

            return base.MouseAction(v, args, mousePosition, map, ref handled);
        }

        public static PatternKind LastKind = PatternKind.GridDatum;
        public static float LastWidth = 0f;

        public static void Begin(ObjectBase src, PatternKind kind, float widthNm)
        {
            if (src == null || src.IsDestroyed) return;
            LastKind = kind;
            LastWidth = widthNm;
            src.ShowOrderFeedback(kind == PatternKind.GridDatum || kind == PatternKind.Ring ? "Right-click the pattern centre"
                : kind == PatternKind.Line ? "Right-click the start of the line" : "Right-click the fleet ship whose route to screen");
            InteractionStateManager mgr = Globals._mainGameViewModel.StateManager;
            mgr.PushSingleState(new AswPickState(mgr, src, kind, widthNm));
        }
    }

    // Level-1 right-click entry "ASW Pattern" (only for flying units carrying buoys) and the bottom-bar button.
    [HarmonyPatch(typeof(ObjectBaseViewModel), "GetContextMenuItems")]
    public static class AswMenuPatch
    {
        private static readonly float[] CorridorWidths = { 20f, 40f, 60f, 80f, 100f };

        // Shared by the context menu and the bottom bar. Partner entries relabel themselves so a static bar menu stays current.
        public static TrulyObservableCollection<ContextMenuItem> BuildMenu(ObjectBase o, bool alwaysResume)
        {
            TrulyObservableCollection<ContextMenuItem> sub = new TrulyObservableCollection<ContextMenuItem>
            {
                new ContextMenuItem("Grid at datum", null, new DelegateCommand(_ => AswPickState.Begin(o, PatternKind.GridDatum, 0f))),
                new ContextMenuItem("Grid along fleet route", null, new DelegateCommand(_ => AswPickState.Begin(o, PatternKind.GridRoute, 0f))),
                new ContextMenuItem("Ring with infill", null, new DelegateCommand(_ => AswPickState.Begin(o, PatternKind.Ring, 0f))),
                new ContextMenuItem("Straight line", null, new DelegateCommand(_ => AswPickState.Begin(o, PatternKind.Line, 0f)))
            };
            foreach (float w in CorridorWidths)
            {
                float width = w;
                sub.Add(new ContextMenuItem($"Corridor {width:0} nm wide", null, new DelegateCommand(_ => AswPickState.Begin(o, PatternKind.Corridor, width))));
            }

            if (alwaysResume || AswPlanner.CanResume(o))
            {
                sub.Insert(0, new ContextMenuItem("Resume buoy deployment", null, new DelegateCommand(_ => { if (AswPlanner.CanResume(o)) AswPlanner.Resume(o); })));
            }

            foreach (ObjectBase partner in AswPlanner.CandidatePartners(o))
            {
                ObjectBase pp = partner;
                AswPlanner.PickAmmo(pp, out string pAmmo);
                int buoys = pp.getAmmunitionAmountByName(pAmmo);
                ContextMenuItem item = null;
                string Label() => $"{(AswPlanner.IsPartner(o, pp) ? "[x]" : "[ ]")} Share with {pp.getUIDAndName()} ({buoys} buoys)";
                item = new ContextMenuItem(Label(), null, new DelegateCommand(_ =>
                {
                    AswPlanner.TogglePartner(o, pp);
                    item.Label = Label();
                }));
                sub.Add(item);
            }

            sub.Add(new ContextMenuItem("Cancel ASW pattern", null, new DelegateCommand(_ => AswPlanner.Cancel(o))));
            return sub;
        }

        private static void Postfix(ObjectBase ____objectBase, TrulyObservableCollection<ContextMenuItem> __result)
        {
            try
            {
                ObjectBase o = ____objectBase;
                if (o == null || __result == null || !(o.IsPlayerObject || Globals.AllowControlOfEnemyUnits.Value) || !AswPlanner.IsBuoyCarrier(o))
                {
                    return;
                }

                __result.Add(new ContextMenuItem("ASW Pattern", BuildMenu(o, false)));
            }
            catch (Exception ex)
            {
                ModLog.Error("AswMenuPatch", ex);
            }
        }
    }

    // Bottom bar: left-click starts the last-used pattern, the flyout holds the full menu.
    [HarmonyPatch(typeof(ObjectEffectorsViewModel), MethodType.Constructor, new[] { typeof(ObjectBase), typeof(bool) })]
    public static class AswBottomBarPatch
    {
        private static void Postfix(ObjectEffectorsViewModel __instance, ObjectBase objectBase, bool isWaypointEdit)
        {
            try
            {
                if (isWaypointEdit || objectBase == null || !(objectBase.IsPlayerObject || Globals.AllowControlOfEnemyUnits.Value) || !AswPlanner.IsBuoyCarrier(objectBase))
                {
                    return;
                }

                int count = AswPlanner.PickAmmo(objectBase, out _);
                var entry = new WeaponEntry(objectBase, BaseSystem.SystemType.Generic)
                {
                    Label = "ASW",
                    Command = new DelegateCommand((object _) => AswPickState.Begin(objectBase, AswPickState.LastKind, AswPickState.LastWidth)),
                    Items = AswMenuPatch.BuildMenu(objectBase, true)
                };
                entry.AmmoAmount = count;
                __instance.WeaponEntries.Add(entry);
            }
            catch (Exception ex)
            {
                ModLog.Error("AswBottomBarPatch", ex);
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
