---
name: new-extension
description: Create a new cs2-zombie-mode extension plugin (CounterStrikeSharp, C#) from the template — scaffold, wire it to IZombieCore, build, and check it against the CS2 traps. Use when the user asks to write, add or scaffold a plugin, extension, feature or mod for cs2-zombie-mode or its zombie round.
---

# New cs2-zombie-mode extension

You are adding a separate CounterStrikeSharp plugin that talks to the zombie core only through `IZombieCore`.
Read `AGENTS.md` first if you have not in this session — it is short and the traps in it are real.

## 1. Understand the request in core terms

Map what the user wants onto the contract in `src/ZombieMode.Api/IZombieCore.cs` before writing code:

- "when someone turns / dies / gets hurt" → events `Infected`, `InfectedKilled`, `ZombieDamaged`, `PhaseChanged`
- "is he a zombie / bleeding / first" → `IsInfected`, `IsBleeding`, `IsFirstInfected`, `IsBerserk`
- "more/less damage to zombies" → an `IDamageModifier` (never your own TakeDamage hook)
- "give a weapon or item" → inside `using (core.AllowWeaponGive())`
- "zombie health" → `RealHealth` / `SetHealth` (the pawn only shows up to 999)
- "a special round / boss" → `PickFirst`, `InfectDelay`, `Infect`, `EndRound`
- anything about the round clock → `Phase`, `FirstInfectionAt`, `RoundOver`

If the request needs something the contract does not offer, say so and propose the smallest addition to
`IZombieCore` instead of reaching into the core's internals.

## 2. Scaffold

1. Copy `templates/extension` to `src/ZombieMode.<Name>/`.
2. Rename `MyExtension` → `ZombieMode.<Name>` in the file names, the `.csproj`, the namespace and the class.
3. Inside this repository, replace the `<Reference Include="ZombieMode.Api">` with a project reference:
   ```xml
   <ProjectReference Include="..\ZombieMode.Api\ZombieMode.Api.csproj">
     <Private>false</Private>
     <ExcludeAssets>runtime</ExcludeAssets>
   </ProjectReference>
   ```
   and drop the `PropertyGroup`/`PackageReference` duplicated by `Directory.Build.props`
   (compare with `src/ZombieMode.Money/ZombieMode.Money.csproj`).
4. `dotnet sln ZombieMode.slnx add src/ZombieMode.<Name>/ZombieMode.<Name>.csproj`
5. Add the plugin to the loop in `build.sh`.

## 3. Write it

- `ModuleName` starts with `cs2-zombie-mode: `.
- Resolve the core in `OnAllPluginsLoaded`, log an error and return if it is null.
- Subscribe in `OnAllPluginsLoaded`, unsubscribe everything in `Unload`.
- Per-player state keyed by `player.Slot` (bots share SteamID 0), cleared on `PhaseChanged` → `Hidden`.
- Settings in a `BasePluginConfig` class with `[JsonPropertyName]` and an XML doc on every key.
- Text shown to players goes through `Localizer` and `lang/en.json` + `lang/ru.json`, never string literals.
- Comments explain why, not what.

## 4. Build and check

```bash
./build.sh
```

Then go through this list and fix anything that applies before you say it is done:

- [ ] No `HookResult.Handled` from a damage hook; no own TakeDamage hook at all.
- [ ] No `SetModel` without the model in `OnServerPrecacheResources`.
- [ ] No entity access in `Load` or `OnMapStart` — first safe moment is `EventRoundStart`.
- [ ] Every timer, listener, hook, entity and light you create is removed in `Unload`.
- [ ] Every schema write is followed by `Utilities.SetStateChanged`.
- [ ] Convars that the game mode config could reset are applied on round start too.
- [ ] `ZombieMode.Api.dll` is NOT in your plugin's output folder.
- [ ] No new public method shares a name with an existing private one (named-argument overload trap).

Report to the user: what the extension does, its config keys with defaults, how to install it
(`dist/addons/counterstrikesharp/plugins/ZombieMode.<Name>/`), and what you could not verify without a live server.
