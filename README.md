# Guildrun Custom Heroes

A **Guildrun Demo** (Leyline, Steam) mod that adds brand-new heroes to the game, made with BepInEx 6. Each hero
has its own stats, active ability, specializations, 3D model, animations, portraits and voice lines. It joins the
shop and the starting bundles like any other hero.

Ships with **Freddy Fazbear**: a Vanguard of Aegis Global whose *Jumpscare!* stuns everything around him and makes
him bigger and tougher for the rest of the fight.

The repo also includes the full source, the tools and the raw assets behind the mod.

> Unofficial fan mod. Saved runs that contain a custom hero need the mod to load.

> **Disclaimer: partially vibe-coded.** A large part of this mod (plugin code, Blender scripts, tools and docs) was
> written with an AI coding assistant (Claude Code) and then tested by playing the game, not through careful code
> review. Expect rough edges and bugs. It's a hobby project, provided as is with no warranty. Back up your saves
> before using it, and please report problems as issues (with your `BepInEx\LogOutput.log`).

## Installation

### 1. Install BepInEx 6 (IL2CPP)

Guildrun is a Unity 6 IL2CPP game, so it needs the **bleeding-edge BepInEx 6** build. BepInEx 5 and the
Mono builds won't work.

1. Download **BepInEx 6.0.0-be.788, Unity IL2CPP, win-x64** (the build this mod was made with):
   [BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip)
   (all builds: <https://builds.bepinex.dev/projects/bepinex_be>).
2. Open the game folder: Steam → right-click **Guildrun Demo** → *Manage* → *Browse local files*
   (usually `C:\Program Files (x86)\Steam\steamapps\common\Guildrun Demo`).
3. Extract the zip **into that folder**, so `winhttp.dll`, `doorstop_config.ini`, `BepInEx\` and `dotnet\` sit
   next to `Guildrun.exe`.
4. Start the game once and wait. The first start takes a few minutes while BepInEx generates
   `BepInEx\interop`, and the window may look frozen. Close the game when you reach the main menu.

### 2. Install the mod

1. Download `GuildrunCustomHeroes-<version>.zip` from [Releases](../../releases).
2. Extract it into the game folder (the same folder as `Guildrun.exe`). It adds:
   - `BepInEx\plugins\CustomHeroes.dll`
   - `Mods\CustomHeroes\Freddy\` (Freddy's data, model, portraits and voice)
3. Start the game. Freddy shows up in the shop and the starting bundles.

### Uninstall

Delete `BepInEx\plugins\CustomHeroes.dll` and `Mods\CustomHeroes\`. To remove BepInEx too, delete `BepInEx\`,
`dotnet\`, `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version`.

### Troubleshooting

- Check `BepInEx\LogOutput.log`. Lines from this mod start with `Guildrun Custom Heroes`.
- No console or log at all: `winhttp.dll` isn't next to `Guildrun.exe`, or you installed a BepInEx 5 / Mono build.
- After a game update, if BepInEx fails: delete `BepInEx\interop` and `BepInEx\cache` and start the game again.
- Options are in `BepInEx\config\gg.mods.guildrun.customheroes.cfg` (created after the first start).

## Making your own hero

Each hero is one folder in `Mods\CustomHeroes\` with a `hero.json`. It copies an existing hero (the *template*)
and replaces names, lore, classes, guild, stats, the active ability, the specializations and the look. Start
from `CustomHeroes\Freddy\hero.json`; the full format is in [CustomHeroes/README.md](CustomHeroes/README.md).
`BepInEx\CustomHeroes_dump.json`, written at every start, lists every game hero, ability and specialization with
the game's own effect JSON to copy from.

## Repository layout

| Folder | What |
|---|---|
| `CustomHeroes\<Hero>\` | Hero data (`hero.json`, `voice_map.json`) and `Assets\` (model, portraits, voice) |
| `Source\CustomHeroes\` | C# source of the plugin |
| `SourceAssets\Heroes\<Hero>\` | Raw material: Blender files, renders, original sounds |
| `Tools\CustomHeroes\` | Blender (5.x, headless) and Python scripts that made Freddy's model, animations, portraits and sounds |
| `Tools\Voice\` | Voice-line helpers: list a game hero's VO clips, transcribe, build + loudness-normalize Voice folders (`setup_venv.bat` installs the Python packages) |

### Building from source

Clone this repo **as the `Mods` folder inside the game folder** (`Guildrun Demo\Mods`). The project references
`..\..\..\BepInEx\` for the game's interop assemblies, so BepInEx must have run once. With the .NET 8 SDK:

```
cd Source\CustomHeroes
dotnet build -c Release
```

The build copies `CustomHeroes.dll` to `BepInEx\plugins`. Close the game first, or the copy fails.

## Credits

See [CREDITS.md](CREDITS.md). Freddy Fazbear and Five Nights at Freddy's belong to Scott Cawthon; the movie
model is by its original creators, from the pack by **ubuntutu** on DeviantArt. Guildrun belongs to Leyline.
This is a fan project and isn't affiliated with any of them.

## License

The code and tools are under the [MIT License](LICENSE). Third-party assets (the Freddy model, textures, sounds)
stay with their owners; see [CREDITS.md](CREDITS.md).
