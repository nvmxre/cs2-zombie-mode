using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace ZombieMode.Config;

/// <summary>
/// Core settings. CounterStrikeSharp creates the file on first start:
/// <c>addons/counterstrikesharp/configs/plugins/ZombieMode/ZombieMode.json</c>.
/// </summary>
public sealed class ZombieModeConfig : BasePluginConfig
{
    /// <summary>Verbose server log: infection timings, bites, bleeding, health spoofing.</summary>
    [JsonPropertyName("debug")] public bool Debug { get; set; }

    /// <summary>
    /// Console variables applied on every map start. The round is decided by the core, not by the engine:
    /// win conditions stay off during the hidden phase, otherwise the round would end at once because
    /// there are no zombies yet. Economy convars (<c>cash_*</c>) belong to the Money extension.
    /// </summary>
    [JsonPropertyName("cvars")]
    public Dictionary<string, string> Cvars { get; set; } = new()
    {
        ["mp_warmuptime"] = "0",
        ["mp_freezetime"] = "0",
        ["mp_roundtime"] = "7",
        ["mp_roundtime_defuse"] = "7",
        ["mp_roundtime_hostage"] = "7",
        ["mp_ignore_round_win_conditions"] = "1",
        ["mp_solid_teammates"] = "0",
        ["mp_autoteambalance"] = "0",
        ["mp_limitteams"] = "0",
        ["mp_give_player_c4"] = "0",
        ["bot_controllable"] = "0",
        ["healthshot_healthboost_damage_multiplier"] = "1",
    };

    /// <summary>
    /// Console variables applied when the first infected turns: from then on the engine's own win conditions
    /// do the job (zombies are Terrorists, humans are Counter-Terrorists), and zombies stop blocking each other.
    /// </summary>
    [JsonPropertyName("cvarsOnInfection")]
    public Dictionary<string, string> CvarsOnInfection { get; set; } = new()
    {
        ["mp_ignore_round_win_conditions"] = "0",
        ["mp_solid_teammates"] = "2",
    };

    [JsonPropertyName("infection")] public InfectionConfig Infection { get; set; } = new();

    [JsonPropertyName("knockback")] public KnockbackConfig Knockback { get; set; } = new();

    [JsonPropertyName("sounds")] public SoundsConfig Sounds { get; set; } = new();
}
