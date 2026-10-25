# Sea Power: ASW Auto Hunt

Aerial anti-submarine helper for *Sea Power: Naval Combat in the Missile Age* (aircraft and helicopters).

**Status: scaffold only (v0.1.0).** It loads through Anchor Chain and logs; no features yet.

## Planned

1. **Buoy pattern planner (first release):** a map overlay showing an optimum sonobuoy layout (line barrier, grid, circle/datum) around a point or contact, sized from the buoy type's detection range.
2. **Drop the pattern:** assign the pattern to the selected aircraft/helicopter; it flies the track and releases buoys at each point using the game's own `DropSonobuoy` engage task.
3. **Later:** right-click *Automate ASW Hunting* near friendly ships or a map mark (search, localise, attack).

## Requirements

- Sea Power 0.8.7+, [Anchor Chain](https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757) with BepInEx 5.4.x, .NET SDK 10 to build.

## Building

Edit `GameDir` / `AnchorChainDll` in the `.csproj` if your paths differ, then `dotnet build`. The mod folder is assembled in `dist\ASWAutoHunt\` and mirrored into the game's `StreamingAssets`; enable it in the in-game Mods menu.

## License

MIT, see [LICENSE](LICENSE). Not affiliated with the developers of Sea Power.
