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
    //
    // The aircraft's weapons status decides how far the response goes, and is re-checked every step:
    //   FREE   fix, and attack a hostile contact once the fix is good
    //   TIGHT  fix and report only; never attack (a pending automatic attack is cancelled)
    //   HOLD   no automatic response; a running hunt is stopped and the interrupted pattern resumed
    public static class ContactResponse
    {
        // ---- Tunables -------------------------------------------------------------------------------------------
        private const float ScanSec = 2f;               // how often an idle hunter looks for contacts
        private const float StepSec = 1f;               // how often an active hunt is advanced
        private const float MaxClaimNm = 80f;           // ignore contacts farther than this from the aircraft
        private const int FixBuoys = 3;                 // DICASS buoys per fix cluster (triangle)
        private const float FixRadiusNm = 1.0f;         // cluster radius around the estimate
        private const float MaxFixStdDevM = 1500f;      // "good fix": position error (1 sigma, major axis) at or below this
        // A hunter is only sent to fix a contact once the plot's 95% uncertainty ellipse (the one drawn on the map) fits
        // inside what its fix sensor can cover: the DICASS cluster's reach, or its dipping sonar's effective active range.
        // Rougher estimates are re-checked every second as the passive buoys refine them.
        private const float Ellipse95Factor = 2.4477f;  // sqrt(chi-squared(2 dof, 95%)) = the game's map ellipse scale
        private const float FixTimeoutBaseSec = 180f;   // give up on a fix after the flight time to it (x1.5) plus this
        private const float ThreatRangeNm = 60f;        // a contact this close to a friendly ship (clear line of sight) threatens it
        private const float LosStepNm = 2f;             // land-check spacing along the contact-to-ship line
        private const float CandidateCacheSec = 1f;     // contact ranking is shared by all hunters for this long
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
            public EngageTask Attack;    // the automatic attack this hunt queued, if any
            public bool Dip;             // fixing with the dipping sonar instead of DICASS
            public bool Direct;          // the contact was already well fixed: go straight to the attack
            public float FixDeadline;    // give up on the current fix after this time
        }

        // A sub contact on the side's plot that may need a response, ranked by the threat it poses.
        private sealed class Candidate
        {
            public ObjectBase Obj;
            public GeoPosition Pos;
            public bool Hostile;
            public bool Fixed;           // the plot already has a good fix (nothing left to fix)
            public float Ellipse95Nm;    // semi-major axis of the 95% uncertainty ellipse
            public double ThreatNm;      // line-of-sight distance to the nearest friendly ship (MaxValue if none in range)
        }

        // What the hunter is doing, for its map label; null for aircraft the mod is not managing.
        public static string StatusOf(ObjectBase o)
        {
            if (o == null || o.IsDestroyed || !AswPlanner.IsHunter(o))
            {
                return null;
            }

            if (Active.TryGetValue(o, out Hunt h))
            {
                if (h.Direct || h.Phase == Phase.Returning) return MapStatus.Prosecuting;
                return h.Dip && h.Phase == Phase.Dropping ? MapStatus.Dipping : MapStatus.Fixing;
            }

            if (AswPlanner.HasRunningPlan(o))
            {
                return WeaponsHold(o) ? MapStatus.SearchingHold : MapStatus.Searching;
            }

            return WeaponsHold(o) ? MapStatus.Hold : MapStatus.Ready;
        }

        private static bool WeaponsFree(ObjectBase o) => o._weaponStatus == ObjectBase.WeaponStatus.Free;

        private static bool WeaponsHold(ObjectBase o) => o._weaponStatus == ObjectBase.WeaponStatus.Hold;

        // Called when the player ends ASW on an aircraft: stop any hunt without resuming and drop its queued DICASS
        // drops or pending attack, so the aircraft is entirely back under the player's control.
        public static void Release(ObjectBase o, string why)
        {
            NextScan.Remove(o);
            if (!Active.TryGetValue(o, out Hunt h))
            {
                return;
            }

            CancelAttack(o, h);
            if (h.Phase == Phase.Dropping)
            {
                o.RemoveWaypoints();
            }

            End(o, why, resume: false);
        }

        // Removes this hunt's automatic attack if it has not been released yet; true if one was removed.
        private static bool CancelAttack(ObjectBase o, Hunt h)
        {
            if (h.Attack == null || !o._currentEngageTasks.Contains(h.Attack))
            {
                return false;
            }

            o.RemoveEngageTask(h.Attack);
            h.Attack = null;
            return true;
        }

        private static readonly Dictionary<ObjectBase, Hunt> Active = new Dictionary<ObjectBase, Hunt>();   // by aircraft
        private static readonly Dictionary<ObjectBase, ObjectBase> Claimed = new Dictionary<ObjectBase, ObjectBase>(); // contact -> aircraft
        private static readonly Dictionary<ObjectBase, float> Cooldown = new Dictionary<ObjectBase, float>();   // contact -> until
        private static readonly Dictionary<ObjectBase, float> NextScan = new Dictionary<ObjectBase, float>();   // by aircraft

        // Called from each aircraft's fixed update; exits immediately for aircraft that are not ASW hunters.
        public static void Tick(ObjectBase o)
        {
            Monitor(o);
            if (o.IsPlayerObject)
            {
                MapStatus.Refresh(o);
            }

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

        // ---- Monitor: what the log records about contacts and weapons, independent of any hunter --------------------
        // Once a second (driven by any player aircraft's update): sub contacts appearing on the player's plot, changes in
        // classification / track / fix quality, contacts dropping off the plot, why an open contact has no hunter, and
        // every friendly ASW weapon release (by this mod, the game's AI or the player).
        private const float MonitorSec = 1f;
        private static float _nextMonitor;
        private static readonly Dictionary<ObjectBase, string> ContactState = new Dictionary<ObjectBase, string>();
        private static readonly Dictionary<ObjectBase, string> ResponseState = new Dictionary<ObjectBase, string>();
        private static readonly HashSet<ObjectBase> SeenWeapons = new HashSet<ObjectBase>();

        private static void Monitor(ObjectBase o)
        {
            if (Time.unscaledTime < _nextMonitor || o?._taskforce == null || o._taskforce.Side != Taskforce.TfType.Player)
            {
                return;
            }

            _nextMonitor = Time.unscaledTime + MonitorSec; // real time: at high time compression game time would run this every frame
            try
            {
                MonitorContacts(o._taskforce);
                MonitorWeapons(o._taskforce.Side);
            }
            catch (Exception ex)
            {
                ModLog.Error("ContactResponse.Monitor", ex);
            }
        }

        private static string Describe(Vehicle v, out bool live, out bool estimate, out double sigma)
        {
            live = v.HasLiveTrack;
            estimate = v.PositionEstimate.HasValue;
            sigma = estimate ? v.PositionEstimate.Value.Item2.MajorStdDev : 0.0;
            string pos = !estimate ? "no position (bearing only)"
                : sigma > 0.0 && sigma <= MaxFixStdDevM ? "good fix"
                : "position estimate";
            return $"{v.CurrentRelationship()}, {(live ? "live" : "not live")}, {pos}";
        }

        private static void MonitorContacts(Taskforce tf)
        {
            Dictionary<ObjectBase, Vehicle> vehicles = tf.PlottingTable?.LocalVehicles;
            HashSet<ObjectBase> present = new HashSet<ObjectBase>();
            if (vehicles != null)
            {
                foreach (KeyValuePair<ObjectBase, Vehicle> kv in vehicles)
                {
                    ObjectBase c = kv.Key;
                    Vehicle v = kv.Value;
                    if (!(c is Submarine) || c.IsDestroyed || v == null)
                    {
                        continue;
                    }

                    present.Add(c);
                    string state = Describe(v, out bool live, out bool estimate, out double sigma);
                    string where = estimate ? $" at lat={v.PositionEstimate.Value.Item1._latitude:0.000} lon={v.PositionEstimate.Value.Item1._longitude:0.000} ellipse95={Ellipse95Nm(sigma):0.0}nm" : "";
                    if (!ContactState.TryGetValue(c, out string old))
                    {
                        ModLog.Event($"DETECT {c.getUIDAndName()}: {state}{where}");
                    }
                    else if (old != state)
                    {
                        ModLog.Event($"TRACK {c.getUIDAndName()}: {old} -> {state}{where}");
                    }

                    ContactState[c] = state;
                    string why = Claimed.TryGetValue(c, out ObjectBase by) ? $"claimed by {by.getUIDAndName()}" : WhyNoResponse(tf, c, v, live, estimate, sigma);
                    if (!ResponseState.TryGetValue(c, out string oldWhy) || oldWhy != why)
                    {
                        ResponseState[c] = why;
                        if (why != null && !why.StartsWith("claimed"))
                        {
                            ModLog.Event($"NO RESPONSE {c.getUIDAndName()}: {why}");
                        }
                    }
                }
            }

            foreach (ObjectBase c in ContactState.Keys.Where(k => !present.Contains(k)).ToList())
            {
                ModLog.Event($"LOST {c?.getUIDAndName() ?? "contact"}: {(c == null || c.IsDestroyed ? "destroyed" : "no longer on the plot")}");
                ContactState.Remove(c);
                ResponseState.Remove(c);
            }
        }

        // Null when some hunter is free to take the contact (it will claim it on its next scan); otherwise the reason.
        private static string WhyNoResponse(Taskforce tf, ObjectBase c, Vehicle v, bool live, bool estimate, double sigma)
        {
            RelationsState rel = v.CurrentRelationship();
            if (rel != RelationsState.Hostile && rel != RelationsState.Unknown) return $"classified {rel}";
            // Not requiring a "live" track: a passive TMA solution from buoys is an estimate, never a live detection.
            if (!estimate) return "no position estimate yet (bearing only)";
            // Only a hostile contact with a good fix skips straight to the attack; an unknown one still gets an active fix.
            bool fixedNow = rel == RelationsState.Hostile && sigma > 0.0 && sigma <= MaxFixStdDevM;
            if (!fixedNow && Cooldown.TryGetValue(c, out float until) && Time.time < until) return "recently fixed, on cooldown before another fix"; // no countdown: the reason is logged only when it changes

            Candidate cand = new Candidate { Obj = c, Pos = v.PositionEstimate.Value.Item1, Hostile = rel == RelationsState.Hostile, Fixed = fixedNow, Ellipse95Nm = Ellipse95Nm(sigma) };
            int total = 0, hold = 0, busy = 0, range = 0, rough = 0, unable = 0;
            float bestCover = 0f;
            foreach (ObjectBase h in AswPlanner.Hunters)
            {
                if (h == null || h.IsDestroyed || h._taskforce == null || h._taskforce.Side != tf.Side) continue;
                total++;
                float cover = FixCoverageNm(h);
                if (WeaponsHold(h)) hold++;
                else if (Active.ContainsKey(h)) busy++;
                else if (PatternPlanner.DistNm(h._geoPosition, cand.Pos) > MaxClaimNm) range++;
                else if (!fixedNow && cover > 0f && cand.Ellipse95Nm > cover) { rough++; bestCover = Math.Max(bestCover, cover); }
                else if (!CanTake(h, cand)) unable++;
                else return null;
            }

            if (total == 0) return "no ASW hunters (give an aircraft a pattern, or use Enable/Disable ASW Prosecute)";
            string unableWhat = fixedNow ? "not weapons free or no torpedo" : "no DICASS or dipping sonar";
            string roughWhat = rough > 0 ? $", {rough} waiting for the estimate to tighten (95% ellipse must fit their {bestCover:0.0} nm fix coverage)" : "";
            return $"no hunter available ({total} hunters: {hold} weapons hold, {busy} busy, {range} beyond {MaxClaimNm:0} nm{roughWhat}, {unable} {unableWhat})";
        }

        private static void MonitorWeapons(Taskforce.TfType side)
        {
            ObjectsManager mgr = Singleton<ObjectsManager>.Instance;
            if (mgr?._listOfAllObjects == null) return;
            SeenWeapons.RemoveWhere(w => w == null || w.IsDestroyed);
            foreach (ObjectBase obj in mgr._listOfAllObjects.ToList())
            {
                if (!(obj is WeaponBase w) || w.IsDestroyed || w._taskforce == null || w._taskforce.Side != side || SeenWeapons.Contains(w)) continue;
                bool asw = w._type == ObjectBase.ObjectType.Torpedo || w._type == ObjectBase.ObjectType.ASROC || w._type == ObjectBase.ObjectType.RBU
                           || (w._ap != null && w._ap._type == Ammunition.Type.AirDepthCharge);
                SeenWeapons.Add(w);
                if (!asw) continue;

                ObjectBase shooter = w._launchPlatform;
                ObjectBase target = w._initialSetTargetObject;
                string by = shooter != null && Active.TryGetValue(shooter, out Hunt h) && h.Phase == Phase.Returning && h.Contact == target ? "ASW mod" : "game/player";
                ModLog.Event($"WEAPON {shooter?.getUIDAndName() ?? "?"} released {w._ap?._ammunitionFileName ?? w._type.ToString()} at {target?.getUIDAndName() ?? "position"} (ordered by {by}) lat={w._geoPosition._latitude:0.000} lon={w._geoPosition._longitude:0.000}");
            }
        }

        // ---- Detection and assignment --------------------------------------------------------------------------
        // Every idle hunter looks at the side's open contacts in threat order and takes the first one it can work on and
        // would reach soonest of all idle hunters able to; otherwise it leaves that contact to the quicker one and looks
        // at the next. A hunter keeps its contact until the hunt ends.
        private static void Scan(ObjectBase o)
        {
            if (!Eligible(o))
            {
                return;
            }

            foreach (Candidate c in Candidates(o._taskforce))
            {
                if (Claimed.ContainsKey(c.Obj) || !CanTake(o, c) || QuickerHunter(o, c))
                {
                    continue;
                }

                Begin(o, c);
                return;
            }
        }

        private static bool Eligible(ObjectBase o) =>
            o != null && !o.IsDestroyed && !Active.ContainsKey(o) && !WeaponsHold(o)
            && o._taskforce != null && o._taskforce.Side == Taskforce.TfType.Player;

        // A contact without a good fix needs a hunter that can fix it (and is not on cooldown from an earlier fix); a
        // well-fixed contact is only worth taking to attack it, which needs weapons free, a hostile contact and a weapon.
        private static bool CanTake(ObjectBase o, Candidate c)
        {
            if (PatternPlanner.DistNm(o._geoPosition, c.Pos) > MaxClaimNm)
            {
                return false;
            }

            if (c.Fixed)
            {
                return c.Hostile && WeaponsFree(o) && AttackAmmo(o, c.Obj) != null;
            }

            float cover = FixCoverageNm(o);
            return cover > 0f && c.Ellipse95Nm <= cover && !(Cooldown.TryGetValue(c.Obj, out float until) && Time.time < until);
        }

        private static float Ellipse95Nm(double sigmaM) => (float)(Ellipse95Factor * sigmaM / 1852.0);

        // How far from the aimed point this hunter's fix still detects the sub, using the same effective-range factor as
        // pattern spacing: a DICASS cluster reaches its buoys' effective radius beyond half the cluster radius; a dip
        // reaches the dipping sonar's effective active range. The better of the two; 0 if it has neither.
        private static float FixCoverageNm(ObjectBase o)
        {
            float best = 0f;
            if (DicassCount(o, out string dicass) > 0)
            {
                best = PatternPlanner.EffectiveRadiusNm(dicass) + 0.5f * FixRadiusNm;
            }

            float dipKm = DippingSonarRangeKm(o);
            if (dipKm > 0f)
            {
                best = Math.Max(best, dipKm / 1.852f * PatternPlanner.EffectiveRangeFactor);
            }

            return best;
        }

        private static float DippingSonarRangeKm(ObjectBase o)
        {
            if (!(o is Helicopter) || o._obp?._towedSonarSystems == null) return 0f;
            float best = 0f;
            foreach (SensorSystemSonar s in o._obp._towedSonarSystems)
            {
                if (s == null || !s._isDippingSonar) continue;
                SonarParameters sp = s._sonar?._sp;
                float km = sp == null ? 0f : Math.Max(sp._activeRangeInKm, sp._passiveRangeInKm);
                best = Math.Max(best, km > 0f ? km : PatternPlanner.DefaultNominalKm);
            }

            return best;
        }

        private static float EtaHours(ObjectBase o, GeoPosition p) => PatternPlanner.DistNm(o._geoPosition, p) / AswPlanner.CruiseKnots(o);

        private static bool QuickerHunter(ObjectBase me, Candidate c)
        {
            float mine = EtaHours(me, c.Pos);
            foreach (ObjectBase h in AswPlanner.Hunters)
            {
                if (h == me || !Eligible(h) || h._taskforce.Side != me._taskforce.Side || !CanTake(h, c))
                {
                    continue;
                }

                if (EtaHours(h, c.Pos) < mine)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasDippingSonar(ObjectBase o) =>
            o is Helicopter && o._obp?._towedSonarSystems != null && o._obp._towedSonarSystems.Any(s => s != null && s._isDippingSonar);

        private static bool CanFix(ObjectBase o) => DicassCount(o, out _) > 0 || HasDippingSonar(o);

        private static List<Candidate> _candidates = new List<Candidate>();
        private static float _candidatesAt = -1f;
        private static Taskforce.TfType _candidatesSide;

        // Open sub contacts (hostile or unknown, live track, position estimate), most threatening first: by line-of-sight
        // distance to the nearest friendly ship, then hostile before unknown. Shared by all hunters for a second.
        private static List<Candidate> Candidates(Taskforce tf)
        {
            if (Time.time - _candidatesAt < CandidateCacheSec && _candidatesSide == tf.Side)
            {
                return _candidates;
            }

            List<Candidate> res = new List<Candidate>();
            Dictionary<ObjectBase, Vehicle> vehicles = tf.PlottingTable?.LocalVehicles;
            if (vehicles != null && vehicles.Count > 0)
            {
                List<ObjectBase> ships = FriendlyShips(tf);
                foreach (KeyValuePair<ObjectBase, Vehicle> kv in vehicles)
                {
                    ObjectBase c = kv.Key;
                    Vehicle v = kv.Value;
                    if (!(c is Submarine) || c.IsDestroyed || v == null || !v.PositionEstimate.HasValue)
                    {
                        continue;
                    }

                    RelationsState rel = v.CurrentRelationship();
                    if (rel != RelationsState.Hostile && rel != RelationsState.Unknown)
                    {
                        continue;
                    }

                    double sigma = v.PositionEstimate.Value.Item2.MajorStdDev;
                    GeoPosition pos = v.PositionEstimate.Value.Item1;
                    res.Add(new Candidate
                    {
                        Obj = c,
                        Pos = pos,
                        Hostile = rel == RelationsState.Hostile,
                        Fixed = rel == RelationsState.Hostile && sigma > 0.0 && sigma <= MaxFixStdDevM,
                        Ellipse95Nm = Ellipse95Nm(sigma),
                        ThreatNm = ThreatNm(pos, ships),
                    });
                }
            }

            _candidates = res.OrderBy(c => c.ThreatNm).ThenBy(c => c.Hostile ? 0 : 1).ToList();
            _candidatesAt = Time.time;
            _candidatesSide = tf.Side;
            return _candidates;
        }

        private static List<ObjectBase> FriendlyShips(Taskforce tf)
        {
            List<ObjectBase> res = new List<ObjectBase>();
            ObjectsManager mgr = Singleton<ObjectsManager>.Instance;
            if (mgr?._listOfAllObjects == null) return res;
            foreach (ObjectBase s in mgr._listOfAllObjects.ToList())
            {
                if (s != null && !s.IsDestroyed && s._type == ObjectBase.ObjectType.Vessel && s._taskforce != null && s._taskforce.Side == tf.Side)
                {
                    res.Add(s);
                }
            }

            return res;
        }

        // Distance to the nearest friendly ship the contact has a clear (no land in between) line to, within ThreatRangeNm.
        private static double ThreatNm(GeoPosition sub, List<ObjectBase> ships)
        {
            foreach (var s in ships.Select(x => (ship: x, nm: PatternPlanner.DistNm(sub, x._geoPosition))).Where(x => x.nm <= ThreatRangeNm).OrderBy(x => x.nm))
            {
                if (!LineCrossesLand(sub, s.ship._geoPosition, s.nm))
                {
                    return s.nm;
                }
            }

            return double.MaxValue;
        }

        private static bool LineCrossesLand(GeoPosition a, GeoPosition b, float nm)
        {
            int n = (int)(nm / LosStepNm);
            if (n < 2) return false;
            float hdg = a.HeadingToPosition(b);
            for (int i = 1; i < n; i++)
            {
                if (PatternPlanner.IsLand(a.GeoOffsetByDistanceOnHeading(i * LosStepNm, hdg))) return true;
            }

            return false;
        }

        private static void Begin(ObjectBase o, Candidate c)
        {
            Hunt h = new Hunt { Contact = c.Obj, Since = Time.time, Next = Time.time, Direct = c.Fixed };
            Active[o] = h;
            Claimed[c.Obj] = o;
            if (!c.Fixed)
            {
                o.ShowOrderFeedback(!c.Hostile ? "ASW Fix (Unknown)" : WeaponsFree(o) ? "ASW Fix" : "ASW Fix, No Prosecute");
            }

            string threat = c.ThreatNm < double.MaxValue ? $"{c.ThreatNm:0.0}nm" : "none";
            ModLog.Event($"CONTACT {o.getUIDAndName()} claims {c.Obj.getUIDAndName()} hostile={c.Hostile} fixed={c.Fixed} weapons={o._weaponStatus} range={PatternPlanner.DistNm(o._geoPosition, c.Pos):0.0}nm eta={EtaHours(o, c.Pos) * 60f:0}min threat_to_ship={threat} ellipse95={c.Ellipse95Nm:0.0}nm fix_coverage={FixCoverageNm(o):0.0}nm");

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

            if (WeaponsHold(o))
            {
                bool cancelled = CancelAttack(o, h);
                if (h.Phase == Phase.Dropping && !AswPlanner.CanResume(o))
                {
                    o.RemoveWaypoints(); // no pattern to go back to: just drop the queued DICASS
                }

                o.ShowOrderFeedback("ASW No Fix, No Prosecute");
                End(o, cancelled ? "weapons hold: attack cancelled" : "weapons hold", resume: true);
                return;
            }

            switch (h.Phase)
            {
                case Phase.Suspending:
                    // Wait until the pattern bookkeeping has registered the wipe (or there was no pattern running).
                    if (!AswPlanner.HasRunningPlan(o) || Time.time - h.Since > 5f)
                    {
                        if (h.Direct)
                        {
                            h.Phase = Phase.Assessing; // already well fixed: Assess attacks straight away
                            h.Since = Time.time;
                        }
                        else
                        {
                            StartFix(o, h);
                        }
                    }

                    break;

                case Phase.Dropping:
                    if (Time.time > h.FixDeadline)
                    {
                        End(o, h.Dip ? "dip timed out" : "buoy drop timed out", resume: true);
                    }
                    else if (Time.time - h.Since > 3f && (h.Dip ? !AswPlanner.HasDipQueued(o) : QueuedDrops(o, h.DicassAmmo) == 0))
                    {
                        h.Phase = Phase.Assessing;
                        h.Since = Time.time;
                        ModLog.Event(h.Dip
                            ? $"CONTACT {o.getUIDAndName()} dip complete, assessing {c.getUIDAndName()}"
                            : $"CONTACT {o.getUIDAndName()} cluster dropped ({h.Queued} DICASS), assessing {c.getUIDAndName()}");
                    }

                    break;

                case Phase.Assessing:
                    Assess(o, h);
                    break;

                case Phase.Returning:
                    if (!WeaponsFree(o) && CancelAttack(o, h))
                    {
                        o.ShowOrderFeedback("ASW Fix, No Prosecute");
                        End(o, "weapons tight: attack cancelled", resume: true);
                    }
                    else if (o._currentEngageTasks.Count == 0 || Time.time - h.Since > ReturnTimeoutSec)
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

            GeoPosition centre = v.PositionEstimate.Value.Item1;
            int have = DicassCount(o, out string ammo);
            if (have > 0)
            {
                h.Dip = false;
                h.DicassAmmo = ammo;
                h.Queued = Math.Min(FixBuoys, have);
                List<GeoPosition> pts = Cluster(centre, h.Queued, o._geoPosition);
                AswPlanner.QueueExtraDrops(o, ammo, pts);
                h.AmmoAtDrop = have;
            }
            else if (HasDippingSonar(o))
            {
                h.Dip = true;
                h.Queued = 0;
                AswPlanner.QueueDip(o, centre);
            }
            else
            {
                End(o, "no DICASS or dipping sonar to fix with", resume: true);
                return;
            }

            h.Attempts++;
            h.Phase = Phase.Dropping;
            h.Since = Time.time;
            h.FixDeadline = Time.time + FixTimeoutBaseSec + 1.5f * 3600f * EtaHours(o, centre);
            ModLog.Event(h.Dip
                ? $"CONTACT {o.getUIDAndName()} fix #{h.Attempts}: dipping sonar at lat={centre._latitude:0.000} lon={centre._longitude:0.000} (timeout {h.FixDeadline - Time.time:0}s)"
                : $"CONTACT {o.getUIDAndName()} fix #{h.Attempts}: {h.Queued} DICASS around lat={centre._latitude:0.000} lon={centre._longitude:0.000} (timeout {h.FixDeadline - Time.time:0}s)");
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
            bool good = sigma > 0.0 && sigma <= MaxFixStdDevM;
            if (Time.time >= h.NextLog)
            {
                h.NextLog = Time.time + MinFixIntervalSec;
                ModLog.Event($"CONTACT {o.getUIDAndName()} assess {h.Contact.getUIDAndName()} live={v.HasLiveTrack} sigma={sigma:0}m (need <= {MaxFixStdDevM:0}) rel={v.CurrentRelationship()}");
            }

            if (good)
            {
                if (v.CurrentRelationship() != RelationsState.Hostile)
                {
                    o.ShowOrderFeedback("ASW Fixed (Unknown)");
                    End(o, "fixed, not hostile: holding fire", resume: true);
                    return;
                }

                if (!WeaponsFree(o))
                {
                    o.ShowOrderFeedback("ASW Fix, No Prosecute");
                    End(o, "fixed, weapons tight: holding fire", resume: true);
                    return;
                }

                h.Attack = Prosecute(o, h.Contact);
                if (h.Attack != null)
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
                if (h.Attempts < MaxFixAttempts && CanFix(o))
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
        private static EngageTask Prosecute(ObjectBase o, ObjectBase target)
        {
            string best = AttackAmmo(o, target);
            if (best == null)
            {
                return null;
            }

            EngageTask task = o.InsertEngageTask(best, target, Vector3.zero, 1, 66, false);
            o.ShowOrderFeedback("ASW Prosecute");
            ModLog.Event($"CONTACT {o.getUIDAndName()} PROSECUTE {target.getUIDAndName()} with {best}");
            return task;
        }

        // The ASW weapon aboard to attack target with: a torpedo if there is one, else a depth charge; null if none.
        private static string AttackAmmo(ObjectBase o, ObjectBase target)
        {
            if (o._ai == null)
            {
                return null;
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

            return best;
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
