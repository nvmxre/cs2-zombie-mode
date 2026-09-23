# AGENTS.md — writing cs2-zombie-mode plugins with an AI assistant

This file is for AI coding assistants (Claude Code, Codex, Cursor, Copilot, Aider…) and for the people who drive
them. Read it before writing any code in this repository or any extension for it. It is short on purpose:
everything here is either the contract you build against or a trap that has already crashed a real server.

## What this is

A zombie infection mode for Counter-Strike 2 built on [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
(C#, .NET 10, CounterStrikeSharp.API 1.0.374).

- `src/ZombieMode.Api` — the public contract (`IZombieCore`). Small, stable, MIT. Deployed to
  `addons/counterstrikesharp/shared/ZombieMode.Api/`.
- `src/ZombieMode` — the core plugin: the round, hidden infected, bites → bleeding → medkit, zombies, leap and
  berserk, knockback, health spoofing, win conditions. GPL-3.0.
- `src/ZombieMode.Money`, `src/ZombieMode.Magazines`, `src/ZombieMode.WeaponDamage` — extensions. Each is a
  separate plugin that talks to the core only through `IZombieCore`. **Use them as reference implementations.**
- `templates/extension` — a minimal extension that builds on the first try. Start new plugins from it.
- `.claude/skills/new-extension/SKILL.md` — the step-by-step procedure for a new extension, with a checklist.
  Other assistants: follow the same file.

## How an extension talks to the core

```csharp
public override void OnAllPluginsLoaded(bool hotReload)
{
    _core = ZombieModeCapabilities.Core.Get();          // NOT in Load(): the core may load after you
    if (_core is null) { Logger.LogError("core not loaded"); return; }
    _core.Infected += OnInfected;                        // unsubscribe in Unload()
}
```

What `IZombieCore` gives you (see `src/ZombieMode.Api/IZombieCore.cs` — it is the documentation):

| Need | Use |
|---|---|
| Is this player a zombie / the first infected / bleeding / in berserk? | `IsInfected`, `IsFirstInfected`, `IsBleeding`, `IsBerserk` |
| Round state | `Phase`, `PhaseChanged`, `FirstInfectionAt`, `RoundOver` |
| React to infections and kills | `Infected`, `InfectedKilled` |
| Pay or count damage dealt to zombies | `ZombieDamaged` (real damage, after modifiers) |
| Change damage humans deal to zombies | `AddDamageModifier(IDamageModifier)` — `Order` decides the sequence |
| Give a weapon (shop, crate, reward) | `using (core.AllowWeaponGive()) player.GiveNamedItem(...)` |
| Real zombie health (the pawn shows at most 999) | `RealHealth`, `SetHealth` — never write `pawn.Health` of a zombie directly |
| Show core messages in your own HUD | `Notices = new MySink()` |
| Build a mode (boss arena, event) | `PickFirst`, `InfectDelay`, `StrikeTarget`, `Infect`, `EndRound` |

Rules for extensions:
- Never read the player's team to decide "is this a zombie". Ask `IsInfected`. The team switch happens a frame
  later than the infection.
- Never hook `TakeDamage` yourself to change zombie damage. Use `IDamageModifier`: the core already owns that
  hook, and two hooks on the same function fight each other.
- Never give weapons to anyone without `AllowWeaponGive()`: zombies are blocked from acquiring weapons, and
  the block does not know that the give is yours.
- Unsubscribe from every event in `Unload`. Hot reload keeps old delegates alive otherwise.

## Build and run

```bash
./build.sh            # builds everything into dist/addons/counterstrikesharp/…
./build.sh --zip      # plus a release zip
```

Copy `dist/addons` over `game/csgo/addons` on the server. Configs are created on first start in
`addons/counterstrikesharp/configs/plugins/<Plugin>/<Plugin>.json`. Player-facing strings live in
`plugins/<Plugin>/lang/<language>.json`; never hardcode text shown to players — add a key to every lang file.

A new extension project must reference the contract without copying it:

```xml
<ProjectReference Include="..\ZombieMode.Api\ZombieMode.Api.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
```

(Outside this repository, reference `ZombieMode.Api.dll` the same way: `Private=false`.) If the contract dll ends
up next to your plugin, CounterStrikeSharp loads a second copy and `ZombieModeCapabilities.Core.Get()` returns null.

## Traps that crash or freeze a CS2 server

These are not theoretical. Each one happened on a live server while this mode was built.

1. **Returning `HookResult.Handled` from a `TakeDamage` hook crashes the server.** To cancel damage, set
   `info.Damage = 0` and return `HookResult.Changed`.
2. **`SetModel` with a model that is not in the map's precache manifest crashes the server (ABRT)**, not just
   shows an error model. Add models in `Listeners.OnServerPrecacheResources`, and only call `SetModel` after that
   listener ran for the current map (or after a hot reload). A plugin loaded mid-map without a hot reload must
   wait for the next map.
3. **Touching entities before the map is up can blind the plugin until a restart.** `OnMapStart` means "loading
   started", not "entities exist". The first `EventRoundStart` is the first safe moment. The core tracks this in
   `World.Ready`; do the same in your plugin.
4. **Hot reload leaves orphans.** Lights, entities attached to weapons, listeners and hooks that are not removed in
   `Unload` survive `css_plugins reload`, pile up over reloads and make the game stutter while server metrics look
   healthy. Remove everything you create. Hooks registered through `VirtualFunctions.*.Hook` are NOT removed for
   you — `Unhook` them in `Unload`. On a production server prefer a full restart over hot reload when it is empty.
5. **An overload with a named argument can silently turn into infinite recursion.** Adding a public
   `SetHealth(player, int hp, int max)` next to a private `SetHealth(player, int health, int? max = null)` made
   `SetHealth(p, x, max: y)` bind to the new method, which called back into the caller. The JIT turned the tail
   call into a loop: no stack overflow, the server simply froze on the first infection. Before adding a public
   method, search the class for the same name.
6. **Game mode configs run after `OnMapStart`** (`server.cfg`, `gamemode_casual.cfg`…) and put their values back.
   Apply your convars on map start AND on every round start.
7. **Win conditions during the hidden phase.** Until the first zombie exists, the Terrorist side is empty and the
   engine would end the round at once. The core sets `mp_ignore_round_win_conditions 1` at round start and
   restores it at the first infection (`cvarsOnInfection`). Do not flip it yourself.
8. **Shared weapon data.** `CCSWeaponBase.VData` is shared by every weapon of that type for the whole process.
   Changing `PrimaryReserveAmmoMax` changes it for everyone, and a raised value never comes back down by itself.
9. **One entity name, two weapons.** M4A4/M4A1-S (`weapon_m4a1`), P2000/USP-S (`weapon_hkp2000`), P250/CZ75-Auto
   (`weapon_p250`), Desert Eagle/R8 (`weapon_deagle`), MP7/MP5-SD (`weapon_mp7`) share an entity name. Tell them
   apart by `AttributeManager.Item.ItemDefinitionIndex` — see `MagazinesPlugin.RealName`.
10. **Bots have SteamID 0.** Key per-player state by `player.Slot`, not by SteamID, or every bot shares one entry.
11. **`SetStateChanged` after writing a networked field.** Schema writes (health, money, ammo, glow) are not sent to
    clients until you call `Utilities.SetStateChanged(entity, "Class", "m_field")`.
12. **RCON over loopback may be refused** on some hosts (`ECONNREFUSED 127.0.0.1`). Use the machine's interface
    address.

## Debugging a frozen server

If the process is alive but the log and RCON are silent, the game thread is stuck in managed code. Take a stack:

```bash
dotnet tool install -g dotnet-stack
dotnet-stack report -p <cs2 process id>
```

The game thread is the one that starts at `FunctionReference.<CreateWrappedCallback>`.

## Style

- C# with nullable enabled. Comments explain **why** (engine behaviour, the bug that forced a choice), not what.
- Everything a player reads goes through the lang files. Logs and comments are English.
- One behaviour per extension. If it needs its own config, it is probably its own plugin.
- No pay-to-win in examples: this mode is played on community servers.
