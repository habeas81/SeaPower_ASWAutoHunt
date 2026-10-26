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
        public const float MaxChunkNm = 40f;              // longest single chunk along the track
        public const int Chunks = 3;                      // patterns are flown in thirds (wall, cross, wall, interior)
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

        // Hexagonal area grid centred on a datum, up to maxBuoys points, flown row by row (boustrophedon). Rows run along
        // rowHeading (default 90 = east-west rows, north-aligned grid) and stack to the left of that heading.
        public static List<PlannedBuoy> GridAtDatum(GeoPosition datum, string ammoFile, int maxBuoys, float rowHeading = 90f)
        {
            List<PlannedBuoy> res = new List<PlannedBuoy>();
            if (maxBuoys < 1) return res;
            float d = HexSpacingNm(ammoFile);
            float rowStep = d * 0.866f;
            int cols = (int)Math.Ceiling(Math.Sqrt(maxBuoys));
            int rows = (int)Math.Ceiling(maxBuoys / (double)cols);
            for (int r = 0; r < rows && res.Count < maxBuoys; r++)
            {
                float stack = (r - (rows - 1) / 2f) * rowStep;
                float shift = (r % 2 == 1) ? d / 2f : 0f;
                for (int c = 0; c < cols && res.Count < maxBuoys; c++)
                {
                    int cc = (r % 2 == 0) ? c : cols - 1 - c;
                    float along = (cc - (cols - 1) / 2f) * d + shift - d / 4f;
                    GeoPosition p = Lateral(datum.GeoOffsetByDistanceOnHeading(Math.Abs(along), along >= 0f ? rowHeading : rowHeading + 180f),
                        rowHeading, -stack);
                    res.Add(new PlannedBuoy { Pos = p, Tag = "G" });
                }
            }

            return res;
        }

        // Stations per chunk: thirds of the pattern, but no chunk longer than MaxChunkNm along the track.
        private static int ChunkSize(int stations, float stationSpacingNm)
        {
            int perChunk = (int)Math.Ceiling(stations / (double)Math.Min(Chunks, stations));
            return Math.Max(1, Math.Min(perChunk, (int)Math.Floor(MaxChunkNm / stationSpacingNm)));
        }

        // Hexagonal swath centred on the route, starting GridLeadNm ahead of the fleet. Running out of buoys shortens the
        // far end rather than leaving holes. Flown in chunks like the corridor: up the left edge, across, down the right
        // edge, then back and forth through the interior lines before moving on to the next chunk.
        public static List<PlannedBuoy> GridAlongRoute(Route route, string ammoFile, int maxBuoys)
        {
            List<PlannedBuoy> res = new List<PlannedBuoy>();
            if (!route.Valid || maxBuoys < 1) return res;
            float d = HexSpacingNm(ammoFile);
            float rowStep = d * 0.866f;
            float stepNm = d / 2f; // half-spacing steps so alternate lines stagger
            int lines = Math.Max(2, (int)Math.Floor(2f * GridRouteHalfWidthNm / rowStep) + 1);

            // Lattice cell (station, line) is occupied when (station + line) is even.
            int stations = 0, used = 0;
            for (int st = 0; st < 400 && GridLeadNm + st * stepNm <= route.LengthNm + GridLeadNm + 2f * d; st++)
            {
                int need = 0;
                for (int k = 0; k < lines; k++) if (((k + st) & 1) == 0) need++;
                if (used + need > maxBuoys) break;
                used += need;
                stations++;
            }

            if (stations == 0) return res;

            int perChunk = ChunkSize(stations, stepNm);
            for (int first = 0; first < stations; first += perChunk)
            {
                int last = Math.Min(first + perChunk, stations) - 1;
                AddLine(res, route, first, last, 0, lines, rowStep, stepNm, true);
                AddLine(res, route, first, last, lines - 1, lines, rowStep, stepNm, false);
                bool up = true;
                for (int k = lines - 2; k >= 1; k--)
                {
                    AddLine(res, route, first, last, k, lines, rowStep, stepNm, up);
                    up = !up;
                }
            }

            return res;
        }

        // Adds the occupied cells of one lattice line within a chunk, in ascending or descending station order.
        private static void AddLine(List<PlannedBuoy> res, Route route, int first, int last, int k, int lines, float rowStep, float stepNm, bool ascending)
        {
            for (int i = 0; i <= last - first; i++)
            {
                int st = ascending ? first + i : last - i;
                if (((k + st) & 1) != 0) continue;
                route.At(GridLeadNm + st * stepNm, out GeoPosition c, out float hdg);
                res.Add(new PlannedBuoy { Pos = Lateral(c, hdg, (k - (lines - 1) / 2f) * rowStep), Tag = "G" });
            }
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
            // First decide how many stations the buoy budget allows (walls every station, centreline every Nth).
            int stations = 0, used = 0;
            for (int st = 0; st < maxStations; st++)
            {
                int need = 2 + ((st % CorridorCentrelineEvery) == 0 ? 1 : 0);
                if (used + need + capN > maxBuoys) break;
                used += need;
                stations++;
            }

            if (stations == 0) return res;

            // Fly it in chunks (thirds of the corridor): up one wall, across the lane, back down the other wall, then up the
            // middle laying the centreline buoys; the aircraft ends the chunk at the far end, ready for the next chunk.
            int perChunk = ChunkSize(stations, sp);
            for (int first = 0; first < stations; first += perChunk)
            {
                int last = Math.Min(first + perChunk, stations) - 1;
                for (int st = first; st <= last; st++) res.Add(Station(route, st, sp, -half, "L"));

                // On the final chunk the end cap is the cross-leg of the perimeter (left wall -> cap -> right wall),
                // so the whole perimeter is flown before any infill.
                if (last == stations - 1)
                {
                    for (int i = 1; i <= capN; i++)
                    {
                        float lat = -half + i * (widthNm / (capN + 1));
                        res.Add(Station(route, stations, sp, lat, "E"));
                    }
                }

                for (int st = last; st >= first; st--) res.Add(Station(route, st, sp, half, "R"));
                for (int st = first; st <= last; st++)
                {
                    if ((st % CorridorCentrelineEvery) == 0) res.Add(Station(route, st, sp, 0f, "C"));
                }
            }

            return res;
        }

        private static PlannedBuoy Station(Route route, int st, float sp, float lateralNm, string tag)
        {
            route.At(CorridorLeadNm + st * sp, out GeoPosition c, out float hdg);
            return new PlannedBuoy { Pos = Lateral(c, hdg, lateralNm), Tag = tag };
        }

        // Straight barrier from a to b, buoys at BarrierSpacingNm along it (truncated at the far end if the buoys run out).
        public static List<PlannedBuoy> Line(GeoPosition a, GeoPosition b, string ammoFile, int maxBuoys)
        {
            List<PlannedBuoy> res = new List<PlannedBuoy>();
            float len = DistNm(a, b);
            float sp = BarrierSpacingNm(ammoFile);
            float hdg = a.HeadingToPosition(b);
            int n = Math.Min(maxBuoys, (int)Math.Floor(len / sp) + 1);
            for (int i = 0; i < n; i++)
            {
                res.Add(new PlannedBuoy { Pos = a.GeoOffsetByDistanceOnHeading(i * sp, hdg), Tag = "B" });
            }

            return res;
        }

        // Ring of radius radiusNm around a datum: outer ring at barrier spacing (stretched if the buoys cannot cover it),
        // then concentric infill rings stepped inwards at hex row spacing, then a centre buoy if the middle is still open.
        public static List<PlannedBuoy> Ring(GeoPosition centre, float radiusNm, string ammoFile, int maxBuoys)
        {
            List<PlannedBuoy> res = new List<PlannedBuoy>();
            if (maxBuoys < 3 || radiusNm < 1f) return res;
            float sp = BarrierSpacingNm(ammoFile);
            float step = HexSpacingNm(ammoFile) * 0.866f;
            float r = radiusNm;
            bool outer = true;
            float startAngle = 0f;
            while (r > step * 0.6f && res.Count < maxBuoys)
            {
                int n = Math.Max(3, (int)Math.Ceiling(2f * Math.PI * r / (outer ? sp : HexSpacingNm(ammoFile))));
                if (res.Count + n > maxBuoys)
                {
                    if (outer) n = maxBuoys; // stretch the outer ring rather than leave it open
                    else break;
                }

                for (int i = 0; i < n; i++)
                {
                    float ang = startAngle + i * 360f / n;
                    res.Add(new PlannedBuoy { Pos = centre.GeoOffsetByDistanceOnHeading(r, ang), Tag = outer ? "O" : "I" });
                }

                outer = false;
                startAngle += 180f / Math.Max(3, res.Count); // small stagger between rings
                r -= step;
            }

            if (r >= 0f && r < step * 0.6f && !outer && res.Count < maxBuoys && radiusNm > step)
            {
                res.Add(new PlannedBuoy { Pos = centre, Tag = "C" });
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

        public const float OnPathToleranceNm = 1.0f; // a later buoy this close to a leg is dropped while passing over it

        // Local flat-earth position in nm (fine for pattern-scale distances).
        private static void ToNm(GeoPosition g, double refLat, out double x, out double y)
        {
            y = (g._latitude - refLat) * 60.0;
            x = g._longitude * 60.0 * Math.Cos(refLat * Math.PI / 180.0);
        }

        // Efficiency pass: when a leg A->B flies over a buoy point that is planned later, drop that buoy on the way
        // instead of crossing it now and returning for it. Points are inserted in order along the leg.
        public static void PullOnPath(List<PlannedBuoy> pattern, GeoPosition from)
        {
            if (pattern.Count < 3) return;
            double refLat = from._latitude;
            GeoPosition prev = from;
            for (int i = 0; i < pattern.Count - 1; i++)
            {
                ToNm(prev, refLat, out double ax, out double ay);
                ToNm(pattern[i].Pos, refLat, out double bx, out double by);
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                if (len2 > 1e-6)
                {
                    List<KeyValuePair<double, PlannedBuoy>> found = new List<KeyValuePair<double, PlannedBuoy>>();
                    for (int j = i + 1; j < pattern.Count; j++)
                    {
                        ToNm(pattern[j].Pos, refLat, out double px, out double py);
                        double t = ((px - ax) * dx + (py - ay) * dy) / len2;
                        if (t <= 0.0 || t >= 1.0) continue;
                        double cx = ax + t * dx - px, cy = ay + t * dy - py;
                        if (cx * cx + cy * cy <= OnPathToleranceNm * OnPathToleranceNm)
                        {
                            found.Add(new KeyValuePair<double, PlannedBuoy>(t, pattern[j]));
                        }
                    }

                    if (found.Count > 0)
                    {
                        found.Sort((p, q) => p.Key.CompareTo(q.Key));
                        foreach (var f in found) pattern.Remove(f.Value);
                        pattern.InsertRange(i, found.ConvertAll(f => f.Value));
                        // Continue from the last inserted buoy; the original B follows it on the same line.
                        i += found.Count - 1;
                        prev = pattern[i].Pos;
                        continue;
                    }
                }

                prev = pattern[i].Pos;
            }
        }
    }
}
