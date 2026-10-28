# Weathering

Valheim mod (BepInEx + Jotunn) that lets the land heal itself outside player bases: churned dirt and
abandoned fields turn back to grass, and dug holes and raised mounds slump into soft ditches and dips.

Players: install it from a mod manager. What heals, what never does and the configuration are on the
mod page, whose source is [`package/README.md`](package/README.md). The design is in
[`docs/design.md`](docs/design.md).

## Building

Requires the .NET SDK (8+) and a Valheim install. The build locates Valheim through the Steam
registry entry; override with `-p:VALHEIM_INSTALL="<path>"`.

```
dotnet build -c Release                           # build bin/Release/Weathering.dll
dotnet build -c Release -t:Package                # also write dist/Weathering-<version>.zip
```

The first build generates publicized Valheim assemblies in `Valheim_Data/Managed/publicized_assemblies`.

The build touches nothing outside the repo. To test in game, install the zip with a mod manager
(e.g. import it into a [Gale](https://github.com/Kesomannen/gale) profile).

## Project layout

| Path | Purpose |
| --- | --- |
| `src/` | Plugin source |
| `tools/` | PowerShell generator for the mod icon |
| `package/` | Mod page README and icon shipped in the package |
| `docs/` | Design, ideas and implementation plan |

## Releases

CI (`.github/workflows/build.yml`) builds the package on every push and pull request. Pushing a
`vX.Y.Z` tag checks that the tag matches the version in `Weathering.csproj` and
`src/WeatheringPlugin.cs` and has a `CHANGELOG.md` section, then creates a GitHub release and
publishes to Hexium.

## License

[MIT](LICENSE). The mod icon is "Hills" by Delapouite from [game-icons.net](https://game-icons.net),
licensed under [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/), recoloured
(`tools/make_icon.ps1`).
