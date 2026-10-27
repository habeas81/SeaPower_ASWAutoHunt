using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeapowerUI.ViewModels;
using UnityEngine;

namespace ASWAutoHunt
{
    // Automatic response to a submarine contact for aircraft that are running an ASW pattern:
    //   FIX       drop a tight cluster of active DICASS buoys around the contact's estimated position
    //   ASSESS    wait for the track error to shrink; re-fix once if it does not
    //   PROSECUTE if the contact is classified HOSTILE and the fix is good, queue a torpedo attack
    //   RETURN    when the attack is done (or the contact is lost), resume the interrupted buoy pattern
    // Contacts that are only "unknown" are fixed and reported but never attacked. Only the player's side is automated,
    // and only aircraft that were given a pattern by this mod (so manually flown aircraft are never taken over).
    // Everything works from the side's own plotting table (the estimate and error ellipse the player also sees).
    public static class ContactResponse
    {
        // ---- Tunables -------------------------------------------------------------------------------------------
        private const float ScanSec = 2f;               // how often an idle hunter looks for contacts
        private const float StepSec = 1f;               // how often an active hunt is advanced
        private const float MaxClaimNm = 80f;           // ignore contacts farther than this from the aircraft
        private const int FixBuoys = 3;                 // DICASS buoys per fix cluster (triangle)
        private const float FixRadiusNm = 1.0f;         // cluster radius around the estimate
        private const float MaxFixStdDevM = 1500f;      // "good fix": position error (1 sigma, major axis) at or below this
        private const float DropTimeoutSec = 300f;      // give up on the buoy drops after this long
        private const float AssessTimeoutSec = 90f;     // wait this long for a good fix before re-fixing
        private const int MaxFixAttempts = 2;           // clusters dropped per contact before declaring it lost
        private const float CooldownSec = 300f;         // do not re-engage the same contact for this long
        private const float ReturnTimeoutSec = 240f;    // max wait for the torpedo run before resuming the pattern
        private const float MinFixIntervalSec = 5f;     // log throttle for assessment lines

        private enum Phase { Suspending, Dropping, Assessing, Returning }

        private sealed class Hunt
        {
            public ObjectBase Contact;
            public Phase Phase = Phase.Suspending;
            public float Since;          // time the current phase began
            public float Next;           // next step time
            public float NextLog;
            public int Attempts;
            public string DicassAmmo;
            public int AmmoAtDrop;       // DICASS aboard when the cluster was queued
            public int Queued;           // buoys in the current cluster
        }

        private static readonly Dictionary<ObjectBase, Hunt> Active = new Dictionary<ObjectBase, Hunt>();   // by aircraft
        private static readonly Dictionary<ObjectBase, ObjectBase> Claimed = new Dictionary<ObjectBase, ObjectBase>(); // contact -> aircraft
        private static readonly Dictionary<ObjectBase, float> Cooldown = new Dictionary<ObjectBase, float>();   // contact -> until
        private static readonly Dictionary<ObjectBase, float> NextScan = new Dictionary<ObjectBase, float>();   // by aircraft

        // Called from each aircraft's fixed update; exits immediately for aircraft that are not ASW hunters.
        public static void Tick(ObjectBase o)
        {
            if (!AswPlanner.IsHunter(o))
            {
                return;
            }

            try
            {
                if (o.IsDestroyed)
                {
                    End(o, "aircraft lost", resume: false);
                    AswPlanner.Hunters.Remove(o);
                    NextScan.Remove(o);
                    return;
                }

                if (Active.TryGetValue(o, out Hunt h))
                {
                    if (Time.time >= h.Next)
                    {
                        h.Next = Time.time + StepSec;
                        Step(o, h);
                    }

                    return;
                }

                if (Time.time < (NextScan.TryGetValue(o, out float t) ? t : 0f))
                {
                    return;
                }

                NextScan[o] = Time.time + ScanSec;
                Scan(o);
            }
            catch (Exception ex)
            {
                ModLog.Error("ContactResponse.Tick", ex);
                End(o, "error", resume: false);
            }
        }

        // ---- Detection ------------------------------------------------------------------------------------------
        private static void Scan(ObjectBase o)
        {
            if (o._taskforce == null || o._taskforce.Side != Taskforce.TfType.Player || DicassCount(o, out _) < FixBuoys)
            {
                return;
            }

            Dictionary<ObjectBase, Vehicle> vehicles = o._taskforce.PlottingTable?.LocalVehicles;
            if (vehicles == null || vehicles.Count == 0)
            {
                return;
            }

            Vehicle best = null;
            float bestNm = MaxClaimNm;
            foreach (KeyValuePair<ObjectBase, Vehicle> kv in vehicles)
            {
                ObjectBase c = kv.Key;
                Vehicle v = kv.Value;
                if (!(c is Submarine) || c.IsDestroyed || v == null || !v.HasLiveTrack || Claimed.ContainsKey(c))
                {
                    continue;
                }

                if (Cooldown.TryGetValue(c, out float until) && Time.time < until)
                {
                    continue;
                }

                RelationsState rel = v.CurrentRelationship();
                if (rel != RelationsState.Hostile && rel != RelationsState.Unknown)
                {
                    continue;
                }

                if (!v.PositionEstimate.HasValue)
                {
                    continue;
                }

                float nm = PatternPlanner.DistNm(o._geoPosition, v.PositionEstimate.Value.Item1);
                if (nm < bestNm)
                {
                    best = v;
                    bestNm = nm;
                }
            }

            // Leave it to a closer idle hunter on the same side (it claims on its own tick).
            if (best == null || CloserIdleHunter(o, best.PositionEstimate.Value.Item1, bestNm))
            {
                return;
            }

            Begin(o, best);
        }

        private static bool CloserIdleHunter(ObjectBase me, GeoPosition where, float myNm)
        {
            foreach (ObjectBase h in AswPlanner.Hunters)
            {
                if (h == me || h == null || h.IsDestroyed || h._taskforce != me._taskforce || Active.ContainsKey(h) || DicassCount(h, out _) < FixBuoys)
                {
                    continue;
                }

                if (PatternPlanner.DistNm(h._geoPosition, where) < myNm)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Begin(ObjectBase o, Vehicle v)
        {
            Hunt h = new Hunt { Contact = v.Object, Since = Time.time, Next = Time.time };
            Active[o] = h;
            Claimed[v.Object] = o;
            bool hostile = v.CurrentRelationship() == RelationsState.Hostile;
            string msg = hostile ? "ASW contact: fixing with DICASS" : "ASW contact (unclassified): fixing with DICASS, will not attack";
            o.ShowOrderFeedback(msg);
            ModLog.Event($"CONTACT {o.getUIDAndName()} claims {v.Object.getUIDAndName()} rel={v.CurrentRelationship()} range={PatternPlanner.DistNm(o._geoPosition, v.PositionEstimate.Value.Item1):0.0}nm");

            // Stop the running pattern first; AswPlanner.Tick marks it interrupted so it can be resumed afterwards.
            AswPlanner.SuspendForContact(o);
        }

        // ---- State machine --------------------------------------------------------------------------------------
        private static void Step(ObjectBase o, Hunt h)
        {
            ObjectBase c = h.Contact;
            if (c == null || c.IsDestroyed)
            {
                End(o, "contact destroyed", resume: true);
                return;
            }

            switch (h.Phase)
            {
                case Phase.Suspending:
                    // Wait until the pattern bookkeeping has registered the wipe (or there was no pattern running).
                    if (!AswPlanner.HasRunningPlan(o) || Time.time - h.Since > 5f)
                    {
                        StartFix(o, h);
                    }

                    break;

                case Phase.Dropping:
                    if (Time.time - h.Since > DropTimeoutSec)
                    {
                        End(o, "buoy drop timed out", resume: true);
                    }
                    else if (Time.time - h.Since > 3f && QueuedDrops(o, h.DicassAmmo) == 0)
                    {
                        h.Phase = Phase.Assessing;
                        h.Since = Time.time;
                        ModLog.Event($"CONTACT {o.getUIDAndName()} cluster dropped ({h.Queued} DICASS), assessing {c.getUIDAndName()}");
                    }

                    break;

                case Phase.Assessing:
                    Assess(o, h);
                    break;

                case Phase.Returning:
                    if (o._currentEngageTasks.Count == 0 || Time.time - h.Since > ReturnTimeoutSec)
                    {
                        End(o, "prosecution finished", resume: true);
                    }

                    break;
            }
        }

        private static void StartFix(ObjectBase o, Hunt h)
        {
            Vehicle v = o._taskforce.PlottingTable?.VehicleForObject(h.Contact);
            if (v == null || !v.PositionEstimate.HasValue)
            {
                End(o, "contact track lost", resume: true);
                return;
            }

            int have = DicassCount(o, out string ammo);
            if (have < 1)
            {
                End(o, "no DICASS buoys left", resume: true);
                return;
            }

            h.DicassAmmo = ammo;
            h.Queued = Math.Min(FixBuoys, have);
            GeoPosition centre = v.PositionEstimate.Value.Item1;
            List<GeoPosition> pts = Cluster(centre, h.Queued, o._geoPosition);
            AswPlanner.QueueExtraDrops(o, ammo, pts);
            h.AmmoAtDrop = have;
            h.Attempts++;
            h.Phase = Phase.Dropping;
            h.Since = Time.time;
            ModLog.Event($"CONTACT {o.getUIDAndName()} fix #{h.Attempts}: {pts.Count} DICASS around lat={centre._latitude:0.000} lon={centre._longitude:0.000}");
        }

        private static void Assess(ObjectBase o, Hunt h)
        {
            Vehicle v = o._taskforce.PlottingTable?.VehicleForObject(h.Contact);
            if (v == null || !v.PositionEstimate.HasValue)
            {
                End(o, "contact track lost", resume: true);
                return;
            }

            double sigma = v.PositionEstimate.Value.Item2.MajorStdDev;
            bool good = v.HasLiveTrack && sigma > 0.0 && sigma <= MaxFixStdDevM;
            if (Time.time >= h.NextLog)
            {
                h.NextLog = Time.time + MinFixIntervalSec;
                ModLog.Event($"CONTACT {o.getUIDAndName()} assess {h.Contact.getUIDAndName()} live={v.HasLiveTrack} sigma={sigma:0}m (need <= {MaxFixStdDevM:0}) rel={v.CurrentRelationship()}");
            }

            if (good)
            {
                if (v.CurrentRelationship() != RelationsState.Hostile)
                {
                    o.ShowOrderFeedback("ASW contact fixed (unclassified); holding fire");
                    End(o, "fixed, not hostile: holding fire", resume: true);
                    return;
                }

                if (Prosecute(o, h.Contact))
                {
                    h.Phase = Phase.Returning;
                    h.Since = Time.time;
                    return;
                }

                End(o, "fixed, no torpedo available", resume: true);
                return;
            }

            if (Time.time - h.Since > AssessTimeoutSec)
            {
                if (h.Attempts < MaxFixAttempts && DicassCount(o, out _) > 0)
                {
                    StartFix(o, h);
                }
                else
                {
                    End(o, "fix not achieved", resume: true);
                }
            }
        }

        // Queues the same engage task the game's own attack order creates; the game handles approach and release.
        private static bool Prosecute(ObjectBase o, ObjectBase target)
        {
            if (o._ai == null)
            {
                return false;
            }

            string best = null;
            bool bestTorpedo = false;
            foreach (string name in o._ai.AmmoForTarget(target, false))
            {
                Ammunition am = o.getAmmunitionByName(name);
                if (am?._ap == null || o.getAmmunitionAmountByName(name) < 1)
                {
                    continue;
                }

                bool torpedo = am._ap._type == Ammunition.Type.Torpedo;
                if (!torpedo && am._ap._type != Ammunition.Type.AirDepthCharge)
                {
                    continue;
                }

                if (best == null || (torpedo && !bestTorpedo))
                {
                    best = name;
                    bestTorpedo = torpedo;
                }
            }

            if (best == null)
            {
                return false;
            }

            o.InsertEngageTask(best, target, Vector3.zero, 1, 66, false);
            o.ShowOrderFeedback("ASW contact fixed: attacking");
            ModLog.Event($"CONTACT {o.getUIDAndName()} PROSECUTE {target.getUIDAndName()} with {best}");
            return true;
        }

        // Ends the hunt, puts the contact on cooldown and (optionally) restarts the interrupted buoy pattern.
        private static void End(ObjectBase o, string why, bool resume)
        {
            if (!Active.TryGetValue(o, out Hunt h))
            {
                return;
            }

            Active.Remove(o);
            if (h.Contact != null)
            {
                Claimed.Remove(h.Contact);
                Cooldown[h.Contact] = Time.time + CooldownSec;
            }

            PruneCooldowns();
            ModLog.Event($"CONTACT {o.getUIDAndName()} hunt ended: {why}");
            if (resume && !o.IsDestroyed && AswPlanner.CanResume(o))
            {
                AswPlanner.Resume(o);
            }
        }

        private static void PruneCooldowns()
        {
            List<ObjectBase> stale = Cooldown.Where(kv => kv.Key == null || kv.Key.IsDestroyed || Time.time > kv.Value).Select(kv => kv.Key).ToList();
            foreach (ObjectBase k in stale)
            {
                if (k != null)
                {
                    Cooldown.Remove(k);
                }
            }
        }

        // ---- Helpers --------------------------------------------------------------------------------------------
        // Active-only (DICASS / SSQ-62) buoys aboard; returns the count and the ammo name.
        private static int DicassCount(ObjectBase o, out string ammoFile)
        {
            ammoFile = null;
            int best = 0;
            foreach (Ammunition a in o.AmmunitionNameToAmmunitionDictionary.Values)
            {
                if (a?._ap == null || a._ap._subType != Ammunition.Type.Sonobuoy)
                {
                    continue;
                }

                string file = a._ap._ammunitionFileName;
                if (file.IndexOf("ssq-62", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                int n = o.getAmmunitionAmountByName(file);
                if (n > best)
                {
                    best = n;
                    ammoFile = file;
                }
            }

            return best;
        }

        // Drop tasks of the given ammo still queued on the aircraft.
        private static int QueuedDrops(ObjectBase o, string ammo)
        {
            int n = 0;
            if (o._userRoot != null)
            {
                foreach (ITaskViewModel tvm in o._userRoot.TaskViewModels)
                {
                    if (tvm?.Task is AttackAtWaypoint a && a._ammunitionName == ammo)
                    {
                        n += a._salvo;
                    }
                }
            }

            return n;
        }

        // Evenly spaced ring of points around the centre, visited nearest-first from the aircraft.
        private static List<GeoPosition> Cluster(GeoPosition centre, int count, GeoPosition from)
        {
            List<GeoPosition> pts = new List<GeoPosition>();
            if (count == 1)
            {
                pts.Add(centre);
                return pts;
            }

            for (int i = 0; i < count; i++)
            {
                pts.Add(centre.GeoOffsetByDistanceOnHeading(FixRadiusNm, 360f * i / count));
            }

            List<GeoPosition> ordered = new List<GeoPosition>();
            GeoPosition cur = from;
            while (pts.Count > 0)
            {
                int k = 0;
                for (int i = 1; i < pts.Count; i++)
                {
                    if (PatternPlanner.DistNm(cur, pts[i]) < PatternPlanner.DistNm(cur, pts[k]))
                    {
                        k = i;
                    }
                }

                cur = pts[k];
                ordered.Add(cur);
                pts.RemoveAt(k);
            }

            return ordered;
        }
    }
}
