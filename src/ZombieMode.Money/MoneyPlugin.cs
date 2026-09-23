using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using Microsoft.Extensions.Logging;
using ZombieMode.Api;

namespace ZombieMode.Money;

/// <summary>
/// Settings: <c>addons/counterstrikesharp/configs/plugins/ZombieMode.Money/ZombieMode.Money.json</c>.
/// </summary>
public sealed class MoneyConfig : BasePluginConfig
{
    /// <summary>Dollars per point of damage a human deals to a zombie. 1 damage = $1 by default.</summary>
    [JsonPropertyName("perDamage")] public double PerDamage { get; set; } = 1.0;

    /// <summary>Dollars for the zombie that infects a human. The money stays when they are human again next round.</summary>
    [JsonPropertyName("perInfection")] public int PerInfection { get; set; } = 500;

    /// <summary>Dollars for killing a zombie, on top of the damage already paid.</summary>
    [JsonPropertyName("perZombieKill")] public int PerZombieKill { get; set; }

    /// <summary>Dollars for killing the first infected, on top of the damage already paid.</summary>
    [JsonPropertyName("perFirstInfectedKill")] public int PerFirstInfectedKill { get; set; }

    /// <summary>
    /// Balance cap. 0 — use <c>mp_maxmoney</c>. Earnings above the cap are lost at the moment they are earned:
    /// a cap is a cap.
    /// </summary>
    [JsonPropertyName("cap")] public int Cap { get; set; }

    /// <summary>
    /// Console variables for the economy, applied on map start and every round start. The engine's own rewards
    /// (<c>cash_*</c>) are switched off: in a zombie round money comes from damage and infections only.
    /// </summary>
    [JsonPropertyName("cvars")]
    public Dictionary<string, string> Cvars { get; set; } = new()
    {
        ["mp_maxmoney"] = "16000",
        ["mp_startmoney"] = "800",
        ["mp_afterroundmoney"] = "0",
        ["cash_player_killed_enemy_default"] = "0",
        ["cash_player_killed_enemy_factor"] = "0",
        ["cash_player_killed_teammate"] = "0",
        ["cash_player_get_killed"] = "0",
        ["cash_player_respawn_amount"] = "0",
        ["cash_player_bomb_planted"] = "0",
        ["cash_player_bomb_defused"] = "0",
        ["cash_player_damage_hostage"] = "0",
        ["cash_player_killed_hostage"] = "0",
        ["cash_player_rescued_hostage"] = "0",
        ["cash_player_interact_with_hostage"] = "0",
        ["cash_team_elimination_bomb_map"] = "0",
        ["cash_team_elimination_hostage_map_ct"] = "0",
        ["cash_team_elimination_hostage_map_t"] = "0",
        ["cash_team_loser_bonus"] = "0",
        ["cash_team_loser_bonus_consecutive_rounds"] = "0",
        ["cash_team_winner_bonus_consecutive_rounds"] = "0",
        ["cash_team_per_dead_enemy"] = "0",
        ["cash_team_planted_bomb_but_defused"] = "0",
        ["cash_team_terrorist_win_bomb"] = "0",
        ["cash_team_win_by_defusing_bomb"] = "0",
        ["cash_team_win_by_hostage_rescue"] = "0",
        ["cash_team_win_by_time_running_out_bomb"] = "0",
        ["cash_team_win_by_time_running_out_hostage"] = "0",
        ["cash_team_hostage_alive"] = "0",
        ["cash_team_hostage_interaction"] = "0",
        ["cash_team_rescued_hostage"] = "0",
        ["cash_team_bonus_shorthanded"] = "0",
    };
}

/// <summary>
/// Money extension for cs2-zombie-mode: humans earn dollars by damaging zombies, zombies by infecting humans.
/// Money goes to the regular CS2 account, so the stock buy menu and any shop plugin just work.
/// </summary>
public sealed class MoneyPlugin : BasePlugin, IPluginConfig<MoneyConfig>
{
    public override string ModuleName => "cs2-zombie-mode: money";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "Project Zero";
    public override string ModuleDescription => "Dollars for damage to zombies and for infections.";

    public MoneyConfig Config { get; set; } = new();

    private IZombieCore? _core;

    /// <summary>Fractional dollars per player, so 0.5 per damage does not lose every other point.</summary>
    private readonly Dictionary<int, double> _fraction = new();

    public void OnConfigParsed(MoneyConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ => ApplyCvars());
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _core = ZombieModeCapabilities.Core.Get();
        if (_core is null)
        {
            Logger.LogError("cs2-zombie-mode core is not loaded — money is disabled");
            return;
        }

        _core.ZombieDamaged += OnZombieDamaged;
        _core.Infected += OnInfected;
        _core.InfectedKilled += OnInfectedKilled;
    }

    public override void Unload(bool hotReload)
    {
        if (_core is null) return;
        _core.ZombieDamaged -= OnZombieDamaged;
        _core.Infected -= OnInfected;
        _core.InfectedKilled -= OnInfectedKilled;
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Game mode configs run after OnMapStart and put the engine's values back, so apply again here.
        ApplyCvars();
        return HookResult.Continue;
    }

    private void ApplyCvars()
    {
        foreach (var (cvar, value) in Config.Cvars)
            Server.ExecuteCommand($"{cvar} {value}");
    }

    private void OnZombieDamaged(ZombieDamagedEvent e)
    {
        if (Config.PerDamage <= 0) return;
        var slot = e.Attacker.Slot;
        var total = e.Damage * Config.PerDamage + _fraction.GetValueOrDefault(slot);
        var whole = (int)Math.Floor(total);
        _fraction[slot] = total - whole;
        Add(e.Attacker, whole);
    }

    private void OnInfected(InfectedEvent e)
    {
        if (e.Attacker is not null) Add(e.Attacker, Config.PerInfection);
    }

    private void OnInfectedKilled(InfectedKilledEvent e)
    {
        if (e.Attacker is null || !e.Attacker.IsValid || _core?.IsInfected(e.Attacker) == true) return;
        Add(e.Attacker, e.WasFirstInfected ? Config.PerFirstInfectedKill : Config.PerZombieKill);
    }

    /// <summary>Add money up to the cap. Whatever does not fit is lost.</summary>
    private void Add(CCSPlayerController player, int amount)
    {
        if (amount <= 0 || !player.IsValid) return;
        var services = player.InGameMoneyServices;
        if (services is null) return;

        var cap = Config.Cap > 0 ? Config.Cap : ConVar.Find("mp_maxmoney")?.GetPrimitiveValue<int>() ?? 16000;
        var next = Math.Min(cap, services.Account + amount);
        if (next <= services.Account) return;

        services.Account = next;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }
}
