# Credits

## Freddy Fazbear (Custom Heroes)

- **Character**: Freddy Fazbear and *Five Nights at Freddy's* © Scott Cawthon. Used here in a non-commercial fan mod.
- **3D model**: the Freddy Fazbear costume model from the *FNaF MOVIE model pack* by **ubuntutu** on DeviantArt:
  <https://www.deviantart.com/ubuntutu/art/FNaF-MOVIE-model-pack-963704048>
  (`SourceAssets/Heroes/Freddy/Movie_FreddyFazbear_COSTUME.blend`). Credit for the original model goes to its
  creators as listed on that page. The game version (`freddy.glb`: cleaned skeleton, baked texture, new
  animations) and the portrait renders were made from it with the scripts in `Tools/CustomHeroes`.
- **Voice lines and sounds**: from the *Five Nights at Freddy's* games (© Scott Cawthon), downloaded from the
  Freddy Fazbear audio page of the FNaF Wiki (<https://freddy-fazbears-pizza.fandom.com>), then trimmed and
  loudness-normalized. `Tools/CustomHeroes/freddy_fetch_audio.py` lists the exact files.

## Yasuo, Malphite and Teemo (Custom Heroes)

- **Characters**: Yasuo, Malphite, Teemo and *League of Legends* © Riot Games. Used here in a non-commercial fan mod
  under Riot's [Legal Jibber Jabber](https://www.riotgames.com/en/legal) policy; not endorsed by Riot Games.
- **3D models and animations**: exported from League of Legends (`yasuo.glb`, `malphite.glb`, `teemo.glb`). The portrait
  renders were made from them with `Tools/CustomHeroes/yasuo_render.py`.
- **Icons**: cut from the champions' League splash art (`SourceAssets/Heroes/<Hero>/splash`) by
  `Tools/CustomHeroes/yasuo_icons.py`, `malphite_icons.py` and `teemo_icons.py` (Teemo's from the Data Dragon splash).
- **Voice lines and sounds**: from League of Legends, downloaded from the champions' audio pages on the League of
  Legends Wiki (<https://leagueoflegends.fandom.com>), then loudness-normalized.
  `Tools/CustomHeroes/yasuo_fetch_audio.py` lists the exact files.

## Game

- **Guildrun** © Leyline. This repo contains no game files; the mods load the game's own data at runtime.

## Tools and libraries

- [BepInEx](https://github.com/BepInEx/BepInEx) 6 (IL2CPP), with Il2CppInterop and HarmonyX. Not bundled; see the README.
- [Blender](https://www.blender.org) for the model, animations and renders.
- [faster-whisper](https://github.com/SYSTRAN/faster-whisper), [PyAV](https://github.com/PyAV-Org/PyAV) and
  NumPy for the voice tools.

If you own any of these assets and want them removed, open an issue.
