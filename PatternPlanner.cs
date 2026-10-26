using System;
using System.Collections.Generic;
using SeaPower;

namespace ASWAutoHunt
{
    // One planned buoy drop, in flight order.
    public struct PlannedBuoy
    {
        public GeoPosition Pos;
        public string Tag; // "L" / "R" wall, "C" centreline, "E" end cap, "G" grid
    }

    // Pure pattern geometry (no game orders here). Doctrine notes behind the numbers:
    //  * A passive buoy's *nominal* range is for a loud target; a quiet diesel/SSN is detected well inside it, so spacing is
    //    planned on EffectiveRangeFactor x nominal. Hex spacing sqrt(3) x r gives gap-free area coverage for detection circles r;
    //    a barrier (single line) can be thinner, ~1.8 r, because only a crossing has to be caught, not a loiterer.
    //  * A corridor is a screen on each flank of the planned track, set out at the threat's weapon stand-off so a submarine
    //    is held (and prosecuted) before it can shoot at the formation, with a sparse centreline to clear the lane itself
    //    and an optional end cap closing the far end of the screen.
    public static class PatternPlanner
    {
        // ---- Tunables ----
        public const float EffectiveRangeFactor = 0.6f;   // fraction of nominal buoy range assumed against a quiet target
        public const float HexSpacingFactor = 1.732f;     // sqrt(3): gap-free hexagonal area coverage
        public const float BarrierSpacingFactor = 1.8f;   // spacing along a barrier line, in effective radii
        public const float MinSpacingNm = 2.5f;
        public const float MaxSpacingNm = 12f;
        public const float DefaultNominalKm = 10f;        // used for buoys not in the table below

        public const float GridRouteHalfWidthNm = 12f;    // swath half-width for "grid along route"
        public const float GridLeadNm = 15f;              // start the swath this far ahead of the fleet so buoys are down before it arrives
        public const float CorridorLeadNm = 15f;
        public const float CorridorLengthNm = 80f;        // maximum length of the screen; shortened to fit the buoys carried
        public const int CorridorCentrelineEvery = 2;     // centreline buoy at every Nth station (sparser than the walls)
        public const bool CorridorEndCap = true;
        public const float CorridorMinWidthNm = 20f;
        public const float CorridorMaxWidthNm = 100f;
        public const float MaxLegSearchNm = 400f;         // safety cap on route length scanned

        private const float NmPerUnit = 0.036285132f;     // 1 unity unit in nautical miles

        // Nominal passive/active range of the common buoys (km, from sensors.ini).
        public static float NominalKm(string ammoFile)
        {
            string n = (ammoFile ?? "").ToLowerInvariant();
            if (n.Contains("ssq-77")) return 20f;
            if (n.Contains("ssq-62")) return 8f;
            if (n.Contains("ssq-53")) return 10f;
            return DefaultNominalKm;
        }

        public static float EffectiveRadiusNm(string ammoFile) => NominalKm(ammoFile) / 1.852f * EffectiveRangeFactor;

        public static float HexSpacingNm(string ammoFile) => Clamp(EffectiveRadiusNm(ammoFile) * HexSpacingFactor);

        public static float BarrierSpacingNm(string ammoFile) => Clamp(EffectiveRadiusNm(ammoFile) * BarrierSpacingFactor);

        private static float Clamp(float v) => Math.Max(MinSpacingNm, Math.Min(MaxSpacingNm, v));

        public static float DistNm(GeoPosition a, GeoPosition b) => (float)a.GetDistance(b) * NmPerUnit;

        // ---- Route helper: a polyline with distance-along-track lookup, extended straight past its last leg. ----
        public sealed class Route
        {
            private readonly List<GeoPosition> _pts = new List<GeoPosition>();
            private readonly List<float> _cum = new List<float>();
            public float LengthNm => _cum.Count == 0 ? 0f : _cum[_cum.Count - 1];

            public Route(IEnumerable<GeoPosition> pts)
            {
                float total = 0f;
                foreach (GeoPosition p in pts)
                {
                    if (_pts.Count > 0)
                    {
                        float d = DistNm(_pts[_pts.Count - 1], p);
                        if (d < 0.05f)
                        {
                            continue; // drop duplicate points
                        }

                        total += d;
                    }

                    _pts.Add(p);
                    _cum.Add(total);
                    if (total > MaxLegSearchNm)
                    {
                        break;
                    }
                }
            }

            public bool Valid => _pts.Count >= 2;

            // Position and track heading at s nm along the route (clamped before the start, extended past the end).
            public void At(float s, out GeoPosition pos, out float heading)
            {
                if (s < 0f) s = 0f;
                int i = 1;
                while (i < _pts.Count - 1 && _cum[i] < s)
                {
                    i++;
                }

                GeoPosition a = _pts[i - 1], b = _pts[i];
                heading = a.HeadingToPosition(b);
                pos = a.GeoOffsetByDistanceOnHeading(s - _cum[i - 1], heading);
            }
        }

        private static GeoPosition Lateral(GeoPosition p, float heading, float offsetNm)
        {
            return offsetNm >= 0f
                ? p.GeoOffsetByDistanceOnHeading(offsetNm, heading + 90f)
                : p.GeoOffsetByDistanceOnHeading(-offsetNm, heading - 90f);
        }

        // Hexagonal area grid centred on a datum, north-aligned, up to maxBuoys points, flown row by row (boustrophedon).
        public static List<PlannedBuoy> GridAtDatum(GeoPosition datum, string ammoFile, int maxBuoys)
        {
            List<PlannedBuoy> res = new List<PlannedBuoy>();
            if (maxBuoys < 1) return res;
            float d = HexSpacingNm(ammoFile);
            float rowStep = d * 0.866f;
            int cols = (int)Math.Ceiling(Math.Sqrt(maxBuoys));
            int rows = (int)Math.Ceiling(maxBuoys / (double)cols);
            for (int r = 0; r < rows && res.Count < maxBuoys; r++)
            {
                float north = (r - (rows - 1) / 2f) * rowStep;
                float shift = (r % 2 == 1) ? d / 2f : 0f;
                for (int c = 0; c < cols && res.Count < maxBuoys; c++)
                {
                    int cc = (r % 2 == 0) ? c : cols - 1 - c;
                    float east = (cc - (cols - 1) / 2f) * d + shift - d / 4f;
                    GeoPosition p = datum.GeoOffsetByDistanceOnHeading(Math.Abs(north), north >= 0f ? 0f : 180f)
                        .GeoOffsetByDistanceOnHeading(Math.Abs(east), east >= 0f ? 90f : 270f);
                    res.Add(new PlannedBuoy { Pos = p, Tag = "G" });
                }
            }

            return res;
        }

        // Hexagonal swath centred on the route, starting GridLeadNm ahead of the fleet. Built station by station so running
        // out of buoys shortens the far end rather than leaving holes.
        public static List<PlannedBuoy> GridAlongRoute(Route route, string ammoFile, int maxBuoys)
        {
            List<PlannedBuoy> res = new List<PlannedBuoy>();
            if (!route.Valid || maxBuoys < 1) return res;
            float d = HexSpacingNm(ammoFile);
            float rowStep = d * 0.866f;
            int lines = Math.Max(2, (int)Math.Floor(2f * GridRouteHalfWidthNm / rowStep) + 1);
            for (int st = 0; res.Count + lines <= maxBuoys; st++)
            {
                float s = GridLeadNm + st * d / 2f; // half-spacing steps so alternate lines stagger
                route.At(s, out GeoPosition c, out float hdg);
                bool zig = (st % 2) == 1;
                for (int k = 0; k < lines; k++)
                {
                    int kk = zig ? lines - 1 - k : k;
                    // only every other line is occupied at a given station -> staggered hex lattice
                    if (((kk + st) & 1) != 0) continue;
                    float lat = (kk - (lines - 1) / 2f) * rowStep;
                    res.Add(new PlannedBuoy { Pos = Lateral(c, hdg, lat), Tag = "G" });
                }

                if (s > route.LengthNm + GridLeadNm + 2f * d) break;
                if (st > 400) break;
            }

            return res;
        }

        // Two barrier walls at +/- widthNm/2 from the route, a sparse centreline, and an optional end cap.
        public static List<PlannedBuoy> Corridor(Route route, string ammoFile, float widthNm, int maxBuoys)
        {
            List<PlannedBuoy> res = new List<PlannedBuoy>();
            if (!route.Valid || maxBuoys < 3) return res;
            widthNm = Math.Max(CorridorMinWidthNm, Math.Min(CorridorMaxWidthNm, widthNm));
            float half = widthNm / 2f;
            float sp = BarrierSpacingNm(ammoFile);

            // Interior end-cap points between the walls.
            int capN = CorridorEndCap ? Math.Max(0, (int)Math.Ceiling(widthNm / sp) - 1) : 0;
            int maxStations = (int)Math.Floor(CorridorLengthNm / sp) + 1;
            int used = 0;
            float lastS = CorridorLeadNm;
            for (int st = 0; st < maxStations; st++)
            {
                float s = CorridorLeadNm + st * sp;
                bool centre = (st % CorridorCentrelineEvery) == 0;
                int need = 2 + (centre ? 1 : 0);
                if (used + need + capN > maxBuoys) break;

                route.At(s, out GeoPosition c, out float hdg);
                bool zig = (st % 2) == 1;
                // Zig-zag across the lane so the aircraft never doubles back.
                PlannedBuoy left = new PlannedBuoy { Pos = Lateral(c, hdg, -half), Tag = "L" };
                PlannedBuoy right = new PlannedBuoy { Pos = Lateral(c, hdg, half), Tag = "R" };
                PlannedBuoy mid = new PlannedBuoy { Pos = c, Tag = "C" };
                if (!zig)
                {
                    res.Add(left);
                    if (centre) res.Add(mid);
                    res.Add(right);
                }
                else
                {
                    res.Add(right);
                    if (centre) res.Add(mid);
                    res.Add(left);
                }

                used += need;
                lastS = s;
            }

            if (capN > 0 && used > 0)
            {
                route.At(lastS + sp, out GeoPosition c, out float hdg);
                bool leftToRight = (res[res.Count - 1].Tag == "L");
                for (int i = 1; i <= capN; i++)
                {
                    float lat = -half + i * (widthNm / (capN + 1));
                    if (!leftToRight) lat = -lat;
                    res.Add(new PlannedBuoy { Pos = Lateral(c, hdg, lat), Tag = "E" });
                }
            }

            return res;
        }

        // Fly the pattern from whichever end is closer to the aircraft.
        public static void OrientFor(List<PlannedBuoy> pattern, GeoPosition from)
        {
            if (pattern.Count < 2) return;
            if (DistNm(from, pattern[pattern.Count - 1].Pos) < DistNm(from, pattern[0].Pos))
            {
                pattern.Reverse();
            }
        }
    }
}
