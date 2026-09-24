using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZombieMode.Api;
using ZombieMode.Combat;
using ZombieMode.Config;
using ZombieMode.Core;
using ZombieMode.Runtime;

namespace ZombieMode;

/// <summary>
/// cs2-zombie-mode core plugin. Owns the round: hidden infected, bites and bleeding, zombies and their
/// abilities, knockback, win conditions. Everything else — money, shops, HUDs, maps — lives in extensions
/// that talk to it through <see cref="IZombieCore"/> (see <see cref="ZombieModeCapabilities.Core"/>).
/// </summary>
public sealed class ZombieModePlugin : BasePlugin, IPluginConfig<ZombieModeConfig>
{
    public override string ModuleName => "cs2-zombie-mode";
    public override string ModuleVersion => "0.1.1";
    public override string ModuleAuthor => "Project Zero";
    public override string ModuleDescription => "Zombie infection mode for CS2: hidden infected, bites that bleed, zombie abilities.";

    public ZombieModeConfig Config { get; set; } = new();

    private ZombieCore? _core;
    private Knockback? _knockback;

    /// <summary>
    /// Whether the current map's manifest declares our models. Setting a model that the manifest does not know
    /// crashes the server, so a custom zombie model is only applied after <c>OnServerPrecacheResources</c> ran
    /// for this map (or after a hot reload, when the map was already loaded with the plugin in place).
    /// </summary>
    private bool _modelsReady;

    public void OnConfigParsed(ZombieModeConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        Texts.Localizer = Localizer;
        _modelsReady = hotReload;
        // The map is already up after a hot reload; a normal start waits for the first round (see OnRoundStart).
        if (hotReload) World.MapLoaded();

        _core = new ZombieCore(Config, m => Logger.LogInformation("{Message}", m))
        {
            ModelsReady = () => _modelsReady,
        };
        _knockback = new Knockback(
            Config.Knockback,
            WeaponCategories.Of,
            player => _core.IsFirstInfected(player),
            player => _core.IsGuarding(player),
            m => Logger.LogInformation("{Message}", m));

        Capabilities.RegisterPluginCapability(ZombieModeCapabilities.Core, () => _core);
        _core.Start(this);

        RegisterListener<Listeners.OnServerPrecacheResources>(_ => _modelsReady = true);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            // Loading has only started: entities do not exist yet, and the game mode configs will run after us.
            World.MapGone();
            ApplyCvars();
        });
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            World.MapGone();
            _modelsReady = false;
        });

        if (Config.Sounds.Enabled && !string.IsNullOrWhiteSpace(Config.Sounds.Ambience))
            AddTimer((float)Math.Max(5.0, Config.Sounds.AmbienceSeconds), AmbienceTick, TimerFlags.REPEAT);

        Logger.LogInformation("cs2-zombie-mode {Version} loaded", ModuleVersion);
    }

    public override void Unload(bool hotReload)
    {
        _core?.Stop(this);
    }

    /// <summary>
    /// Console variables from the config. Applied on map start and again on every round start: the engine runs
    /// the game mode configs (<c>gamemode_casual.cfg</c> and friends) after <c>OnMapStart</c> and puts its own
    /// values back.
    /// </summary>
    private void ApplyCvars()
    {
        foreach (var (cvar, value) in Config.Cvars)
            Server.ExecuteCommand($"{cvar} {value}");
    }

    /// <summary>
    /// "X joined Terrorists" is noise here: infection moves players between teams all round long. The event stays,
    /// only its chat line goes.
    /// </summary>
    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerTeamQuiet(EventPlayerTeam @event, GameEventInfo info)
    {
        @event.Silent = true;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // The first round start is the first moment entities certainly exist (OnMapStart is too early).
        World.MapLoaded();
        ApplyCvars();
        RemoveObjectives();
        RoundMusic();
        return HookResult.Continue;
    }

    // ── music ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rounds since the last theme; the theme plays on every <c>themeEveryRounds</c>-th round.</summary>
    private int _roundsSinceTheme;

    /// <summary>Until when the theme plays: round start music and ambience stay quiet meanwhile.</summary>
    private double _themeUntil;

    /// <summary>
    /// Round start music, and every few rounds the theme. Nothing stops a sound once it plays, so two tracks at
    /// once are avoided by timing: while the theme is still playing, round start music is skipped.
    /// </summary>
    private void RoundMusic()
    {
        var snd = Config.Sounds;
        if (!snd.Enabled) return;

        if (!string.IsNullOrWhiteSpace(snd.RoundStart) && Server.CurrentTime >= _themeUntil)
            Sound.MusicToEveryone(_core?.MusicMuted, snd.RoundStart, snd.RoundStartVolume);

        if (snd.ThemeEveryRounds <= 0 || string.IsNullOrWhiteSpace(snd.Theme)) return;
        if (++_roundsSinceTheme < snd.ThemeEveryRounds) return;
        _roundsSinceTheme = 0;
        var delay = Math.Max(0.0, snd.ThemeDelay);
        _themeUntil = Server.CurrentTime + delay + Math.Max(0.0, snd.ThemeSeconds);
        AddTimer((float)delay, () =>
        {
            if (World.Ready) Sound.MusicToEveryone(_core?.MusicMuted, snd.Theme, snd.ThemeVolume);
        });
    }

    /// <summary>Next loop of the ambience for everyone on the server. Quiet while the theme plays.</summary>
    private void AmbienceTick()
    {
        if (!World.Ready || !Config.Sounds.Enabled || Server.CurrentTime < _themeUntil) return;
        Sound.MusicToEveryone(_core?.MusicMuted, Config.Sounds.Ambience, Config.Sounds.AmbienceVolume);
    }

    /// <summary>
    /// Zombie rounds have no bomb and no hostages. With the zombie side empty at round start the engine drops the
    /// C4 on the floor, where it only confuses people.
    /// </summary>
    private void RemoveObjectives()
    {
        foreach (var name in new[] { "weapon_c4", "planted_c4", "hostage_entity", "hostage_carriable_prop", "info_hostage_spawn" })
        {
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name))
            {
                if (entity.IsValid) entity.Remove();
            }
        }
    }

    /// <summary>
    /// Knockback for zombies. Computed from <c>player_hurt</c>, not in the damage hook: the event carries the final
    /// damage with armour and hit group already applied, so we do not argue with the engine about numbers.
    /// </summary>
    [GameEventHandler]
    public HookResult OnPlayerHurtKnockback(EventPlayerHurt @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        if (_knockback is null || _core is null) return HookResult.Continue;
        if (victim is null || !victim.IsValid || attacker is null || !attacker.IsValid) return HookResult.Continue;
        if (victim.Slot == attacker.Slot) return HookResult.Continue;

        // Only zombies are pushed, and only by humans. A bitten human bleeds instead.
        if (!_core.IsInfected(victim) || _core.IsInfected(attacker)) return HookResult.Continue;

        // Berserk also removes the slowdown from being hit — right here, after the engine applied it.
        _core.UnslowIfGuarding(victim);

        _knockback.Apply(victim, attacker, @event.Weapon ?? string.Empty, @event.DmgHealth);
        return HookResult.Continue;
    }

    /// <summary>
    /// Humans play on the Counter-Terrorist side. If a human spawns as a Terrorist (auto-balance, an admin move,
    /// humans returned at round start), switch them back and respawn so the engine picks a CT spawn point and
    /// loadout instead of leaving them on the zombie side with a Terrorist pistol.
    /// </summary>
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is null || !player.IsValid) return HookResult.Continue;

        Server.NextWorldUpdate(() =>
        {
            if (!player.IsValid || _core is null) return;
            if (player.Team == CsTeam.Terrorist && !_core.IsInfected(player))
            {
                player.SwitchTeam(CsTeam.CounterTerrorist);
                player.Respawn();
            }
        });
        return HookResult.Continue;
    }
}
