# Sea Power: ASW Find, Fix and Prosecute

Airborne anti-submarine automation for *Sea Power: Naval Combat in the Missile Age*. Pick a pattern, click the map, and your aircraft and helicopters lay out and drop a full sonobuoy field for you, efficiently and without gaps. Share the job with other aircraft and the mod splits the buoys between them.

**Status: beta.** The pattern and coordination features below are playable, and automatic contact response is included.

## Description

Laying a good sonobuoy field by hand means a lot of shift-right-clicking, guesswork on spacing, and aircraft wasting fuel zig-zagging over the same water. ASW Find, Fix and Prosecute does the planning. You choose the pattern and where it goes; the mod works out buoy positions from the actual detection range of the buoys aboard, orders the drops so the aircraft flies an efficient track, and queues them using the game's own sonobuoy drop order.

Need more aircraft on the job? Tick "Share with..." and the mod puts them in one formation, then recomputes the whole layout from where each aircraft really is, so nearer buoys go to the aircraft that can reach them soonest. Fixed-wing aircraft and helicopters can work the same pattern together.

## Features

**Patterns**
- **Grid at datum:** hex grid centred on a clicked point, rotatable with a second click.
- **Grid along fleet route:** a grid laid along a friendly ship's planned route.
- **Corridor:** a screen of a chosen width (20 to 100 nm) along a ship's route. The perimeter is flown first, then the fill.
- **Ring with infill:** a ring of buoys around a datum, with the interior filled in.
- **Straight line:** a barrier line between two clicked points.

**Smart layout**
- Spacing is derived from each buoy type's detection range (DIFAR, DICASS, VLAD), not a fixed number.
- Buoys already in the water, or already queued by another aircraft, are detected and not duplicated.
- Drop order is optimised (nearest-neighbour plus 2-opt) so the aircraft does not fly over a later drop point on its way to an earlier one.
- Mixed buoy loads are handled: the pattern is laid out for the shortest-range buoy, and points that longer-range buoys already cover are thinned out.

**Multi-aircraft coordination**
- Share a pattern with other aircraft from the menu. Sharing adds them to the lead aircraft's formation (same-type aircraft in one formation).
- Fixed-wing and helicopters can work one pattern together; they stay in separate formations but share the layout.
- Buoys are split by each aircraft's real position, speed and buoy count, and a rebalance pass moves points so everyone finishes at about the same time.
- Add or remove an aircraft mid-pattern and the layout is recomputed. Buoys already placed are kept and skipped.

**Convenience**
- "ASW Pattern" on the first level of the right-click menu for flying units that carry sonobuoys.
- "ASW" button on the bottom bar: left-click repeats the last pattern type, and the menu is available from there too.
- **Resume buoy deployment** if an accidental waypoint wipes the queue.
- Map markers for queued buoys, removed as they are dropped.
- A detailed log (`BepInEx\ASWAutoHunt.log`) of every planned and dropped buoy, to help with tuning and bug reports.
- **Automatic contact response:** when a sonobuoy detects a submarine, the closest idle hunter pauses its pattern, drops a tight 3-buoy DICASS cluster on the contact, and assesses the fix. A good fix on a hostile contact is prosecuted automatically with a torpedo or depth charge; unknown contacts are fixed and reported only. The aircraft then resumes its pattern.
- **Land filtering:** buoy positions over land are removed. Patterns that start from ships now begin within 5 nm ahead of them.
- **Circle-sized grid:** the dragged placement circle sets the size of the grid.
- **Width submenus:** grid and corridor widths along a fleet route, and the "Share with..." targets, are submenus.
- Optional performance profiler: set `PerfMonitor = true` in `BepInEx\config\com.habeas81.seapower.aswautohunt.cfg` to log a `PERF` summary every 30 s.

**Planned**
- Ship evasion and torpedo defence.
- Dipping sonar patterns for helicopters.

## Requirements

- Sea Power 0.8.7+, [Anchor Chain](https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757) with BepInEx 5.4.x. Enable both in the in-game Mods menu and restart the game.
- .NET SDK 10 to build from source.

## Known issues

- Beta: please report bugs with `ASWAutoHunt.log` attached.
- Resume counts buoys dropped by hand during an interruption as part of the pattern.

## Building

Edit `GameDir` / `AnchorChainDll` in the `.csproj` if your paths differ, then `dotnet build`. The mod folder is assembled in `dist\ASWAutoHunt\` and mirrored into the game's `StreamingAssets`; enable it in the in-game Mods menu.

## License

MIT, see [LICENSE](LICENSE). Not affiliated with the developers of Sea Power.
