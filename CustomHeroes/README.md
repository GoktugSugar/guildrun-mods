# Custom heroes

New heroes for Guildrun, one folder each, all made by one BepInEx plugin, **CustomHeroes.dll**. It reads
`hero.json`, copies an existing hero (the *template*) inside the game's balancing database and replaces names, lore,
classes, guild, stats, the active ability and the specializations. The new hero joins every shop/starting hero pool
its template is in. Its look is its own: the copied prefab gets the hero's 3D model built in (the template's
renderers are switched off), the portraits are composed from the hero's render, and its voice events play the
hero's own clips.

Saved runs that contain a custom hero need the mod to load.

## hero.json

Comments (`//`) and trailing commas are allowed.

| Key | What |
|---|---|
| `Id` | new hero id (the game uses 1-27) |
| `Key` | internal name: visual config, sprites and voice events use it (`Freddy_VisualConfig`, `event:/.../Freddy/Freddy_skill_vo`) |
| `Name` | display name |
| `Template` | hero id to copy: prefab, animation timings and specialization layout come from it (12 = Skorn) |
| `Guild` | `The Hunt`, `Frontline`, `Gaia Project`, `A Temple`, `L'Héritage`, `Aegis Global` |
| `Classes` | one or two of `Warrior`, `Tank`, `Vanguard`, `Assassin`, `Duelist`, `Mystic`, `Mage` |
| `Gender`, `Tags` | `Masculine`/`Feminine`/`Neutral`; keyword tags shown on the hero (`Shields`, `Stall`, `Stun`...) |
| `Stats` | `MaxHealth`, `MaxMana`, `StartingMana`, `ManaRegen`, `Defense`, `BaseAttackDamage`, `Attack`, `Magic`, `AttackRange`, `BaseAttackSpeed`, `AttackSpeed`, `Crit`, `MoveSpeed`, `StartWithActiveAbility` (missing = template's) |
| `Lore` | `Title`, `Quote`, `CurrentWork`, `Motivation` |
| `Active` | `Id`, `Template` (ability whose *actions* give the cast its shape), `Name`, `Description`, `Icon`, `Attacks`, `ActionFields`, `PreActions`, `ExtraActions`, `Effects` |
| `Specializations` | three of: `Id` (hero id × 100 + n), `Template`, `PassiveTemplate`, `Name`, `Class` (+ `ClassDescription`), `Tags`, `PassiveName`, `Description`, `Effects` |

`Attacks` replaces the hits of the template ability's attack action: `Delay` (seconds after the cast starts; line it
up with the animation's hit), `Damage`, and `Scalings` (`Stat` + `Factors` for ranks C, B, A, S).
`ActionFields` sets fields on the template's actions (`{ "_stunDuration": 2 }` on Ming's `MingAbilityAction`).
`PreActions` / `ExtraActions` add ability actions before / after the template's; they run one after another, so
give pre-actions no delay. Freddy's growth is an `EffectAbilityAction` whose blocks (`Delay`, `Duration`, `Effects`)
hold normal effects; a block lasting 999 s stays for the whole fight and each cast adds one (Rip's Rampage works so).

A specialization copies `Template` (another hero's specialization id) and its passive (`PassiveTemplate` to pick a
different one; the passive's icon comes along). `Tags` replaces the specialization's keyword tags.

Descriptions use the game's text tags: `[Stun]<stun>`, `[Shields]<shield>`, `[Stall (15),]<stall>`, and
`[150+20_Defense]<statcalc_damage>` for a number the game computes from the hero's stats. Stat factors are whole
percents (`20_MaxHealth` = 20% of Max HP; `0.2_MaxHealth` shows as "+0%"), per rank `20|25|30|35_MaxHealth`,
and `40+40_Rank` for a value that grows with rank.

### Effects

Each effect: when it fires (`Trigger`), who it hits (`Target`), whose events count (`Attach`), an optional
`Condition` and a list of `Actions`. Example (stun whatever Freddy's ability damages):

```json
{ "Trigger": "OnAbilityDamageDealt", "Target": "TriggerTarget", "Attach": "OwnerIsSource",
  "Actions": [ { "Type": "ApplyStatusWithDurationAction", "Status": "Stun", "Amount": 1, "Duration": 1.5 } ] }
```

- Triggers: `Constant` (a stat bonus while active), `OnCombatStart`, `OnAutoAttack`, `OnAbilityCast`, `OnDamageReceived`,
  `OnAbilityDamageDealt`, `OnAttackDamageDealt`, `OnDamageDealt`, `OnGlobalCrit`, `OnCharacterKilled`, `OnStallStart`, `OnInterval`...
- `Rush: 8` / `Stall: 15` make it "for Rush (8)" / "after Stall (15)" (a `Constant` effect with `Stall` fires once at that time).
- Targets: `Owner`, `AllHeroes`, `AllEnemies`, `HeroesWithClass` (+ `Class`), `HeroesOfGuild` (+ `Guild`), `TriggerTarget`, `TriggerSource`.
- Attach: `OwnerIsSource` (the hero's own casts/hits), `Global`, `SourceIsHeroWithClass` (+ `AttachClass`)...
- Actions and conditions are the game's classes, by name, with their fields: `ModifyStatAction` (`StatType`, `Value`),
  `ModifyStatPerRankAction` (`Value` + `PerRank`), `ModifySizeAction` (`Delta`), `ApplyStatusAction`,
  `ApplyShieldWithScalingAction` / `DealDamageWithScalingAction` (`BaseShield` / `BaseDamage` + `Scalings` with
  `Stat` and `Factors`), `ApplyShieldFromStatWithBaseAction` (`Base` + `Percent` × `StatType`, `Duration` 999 = lasting),
  `GiveManaAction`, `TauntAdjacentEnemiesAction`, `MingInnerFlameBurnAction` (1 Burn per `HpIncrement` Max HP),
  `IsAdjacentToOwnerCondition`, `IsEffectTargetLowestPercentHealthHeroCondition`,
  `IsEffectTargetBelowPercentHPCondition` (`PercentHealth` 0.15 = 15%)...
- Hero abilities' own effects use `Attach: OwnerIsSource`; class bonuses are `Constant` + `Owner` + `ModifyStatAction`.
- A stat action on a triggered effect (`OnAbilityCast`, `OnAbilityDamageDealt`...) adds up once per trigger for the
  rest of the fight (Karsu, Rowan). `ModifyStatFromStatAction` (`StatTypeSource`, `StatTypeTarget`, `Percent`) gives a
  share of a stat (Freddy's 8% Max HP per cast).
- Run-long counters live in the hero's permanent data: `OwnerIntervalCountCondition` (`Interval`: every Nth trigger
  of that effect, counted across fights) and `LoganGainShardsAndScaleAction` (`StartingAmount` Shards, then
  `Increment` more each time). `DealInstantBurnDamageAndReduceStacksAction` (`StacksToReduceAndDeal`)
  takes Burn off the target; `DoesTriggerTargetHaveAmountOfStatusCondition` (`Status`, `Amount`) checks it first.
- `"Backup": true` makes an effect work in play and in reserve (the game's [Backup.] keyword): the plugin adds the
  same effect again as a `ReserveOnlyModularEffect`, which is how the game's own Backup abilities are built.
- `"Priority": N` (or `"Type": "PriorityModularEffect"`) runs the effect in priority order, lowest first. The Rush duration
  bonus needs `0` so it applies before any Rush starts (Ming's The Enlightened). A Backup copy keeps the same priority (10 if none is given).
- Class and guild ids in conditions can be names: `"ClassId1": "Vanguard"`.
- Rush pieces: `OnRushStart` = "on Rush trigger" (any Rush, Rip's The Glutton); `IncreaseRushDurationAction`
  (`Duration`, target `Player`) = "Rush abilities last N seconds longer"; "whenever a Vanguard is auto attacked" is
  `OnAttackDamageReceived` + `Attach: TargetIsHero` + `TriggerTargetHasOneOfTwoClassesCondition`, aimed at
  `TriggerSource` (Niklas's The Hoarder). `GainShardsAction` (`Amount`) gives Shards; shard actions target `Player`.

`BepInEx\CustomHeroes_dump.json` (written at every start) lists every hero, ability and specialization with the
game's own effect JSON; copy patterns from there. Effects in that full format are accepted as they are.

## Look: Model, Portraits, Voice

```json
"Model": {
  "File": "Assets/freddy.glb",       // skinned mesh + textures + animation clips
  "HipHeight": 0.83,                 // template's hip height to match (missing = measured in the template prefab)
  "Scale": 1, "Yaw": 0,              // on top of the hip match; extra turn if the model faces the wrong way
  "Clips": { "Idle": "Idle", "Move": "Walk", "BasicAttack": "Attack", "SkillAttack": "Skill", "Die": "Death" },
  "Hits": { "BasicAttack": 0.6, "SkillAttack": 0.85 }   // clip time of the hit, lined up with the game's hit timing
},
"Portraits": {
  "Source": "Assets/Portraits/_source.png",   // transparent full-body cut-out, composed into every portrait frame
  "Folder": "Assets/Portraits",               // <sprite name>.png here is used as is (Freddy_round.png)
  "Scale": 0.74,                              // size relative to the template hero
  "Drop": 0.10                                // full-body art moves down so the card's name banner cuts the feet
},
"Voice": "Assets/Voice"              // one subfolder per slot: SKILL_VO, PURCHASE_VO, DEATH_VO, RANKUP_B/A/S_VO
```

The model's own clips play (no retargeting): the game's `AnimationHandler` says when an attack, skill or death
starts, idle and walk follow the movement. The game builds voice events from the `Key`
(`event:/SFX/COMB/HEROES/Freddy/Freddy_skill_vo`); a random clip of that slot plays instead.
`Mods\Tools\Voice\build_voice.py` fills the Voice folders from `voice_map.json` and normalizes loudness.

Freddy's model, animations, portraits and sounds are made by the scripts in `Mods\Tools\CustomHeroes`
(Blender 5.x, headless); the sources are in `Mods\SourceAssets\Heroes\Freddy`.
