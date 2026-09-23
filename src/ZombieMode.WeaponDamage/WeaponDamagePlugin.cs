using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using ZombieMode.Api;

namespace ZombieMode.WeaponDamage;

/// <summary>
/// Settings: <c>addons/counterstrikesharp/configs/plugins/ZombieMode.WeaponDamage/ZombieMode.WeaponDamage.json</c>.
/// Multipliers apply only to damage from humans to zombies; damage between humans is untouched.
/// </summary>
public sealed class WeaponDamageConfig : BasePluginConfig
{
    /// <summary>Multiplier for a firearm missing from <see cref="Weapons"/>.</summary>
    [JsonPropertyName("default")] public double Default { get; set; } = 1.0;

    /// <summary>Human knife. The knife is the last resort when a zombie is already close.</summary>
    [JsonPropertyName("knife")] public double Knife { get; set; } = 10.0;

    /// <summary>Grenades by kind: <c>he</c>, <c>fire</c> (molotov and incendiary), <c>decoy</c>.</summary>
    [JsonPropertyName("grenades")]
    public Dictionary<string, double> Grenades { get; set; } = new()
    {
        ["he"] = 15,
        ["fire"] = 8,
        ["decoy"] = 15,
    };

    /// <summary>
    /// Per-weapon multipliers by entity name. The defaults were tuned on a live server: pistols with small
    /// magazines, the Deagle, the revolver and the bolt-action snipers hit harder; spray weapons stay stock.
    /// </summary>
    [JsonPropertyName("weapons")]
    public Dictionary<string, double> Weapons { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_glock"] = 1.0,
        ["weapon_usp_silencer"] = 1.6,
        ["weapon_hkp2000"] = 1.6,
        ["weapon_p250"] = 1.5,
        ["weapon_elite"] = 1.1,
        ["weapon_fiveseven"] = 1.4,
        ["weapon_tec9"] = 1.2,
        ["weapon_cz75a"] = 1.0,
        ["weapon_revolver"] = 3.0,
        ["weapon_deagle"] = 2.5,
        ["weapon_nova"] = 2.0,
        ["weapon_sawedoff"] = 2.0,
        ["weapon_mag7"] = 1.6,
        ["weapon_xm1014"] = 1.0,
        ["weapon_mac10"] = 1.0,
        ["weapon_ump45"] = 1.2,
        ["weapon_mp9"] = 1.0,
        ["weapon_bizon"] = 1.0,
        ["weapon_mp7"] = 1.1,
        ["weapon_mp5sd"] = 1.2,
        ["weapon_p90"] = 1.0,
        ["weapon_galilar"] = 1.3,
        ["weapon_famas"] = 1.3,
        ["weapon_ak47"] = 1.4,
        ["weapon_m4a1_silencer"] = 1.4,
        ["weapon_sg556"] = 1.4,
        ["weapon_m4a1"] = 1.3,
        ["weapon_aug"] = 1.4,
        ["weapon_ssg08"] = 4.0,
        ["weapon_awp"] = 5.0,
        ["weapon_g3sg1"] = 2.0,
        ["weapon_scar20"] = 2.0,
        ["weapon_negev"] = 1.0,
        ["weapon_m249"] = 1.0,
    };
}

/// <summary>Weapon damage extension for cs2-zombie-mode.</summary>
public sealed class WeaponDamagePlugin : BasePlugin, IPluginConfig<WeaponDamageConfig>
{
    public override string ModuleName => "cs2-zombie-mode: weapon damage";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "Project Zero";
    public override string ModuleDescription => "Per-weapon damage multipliers against zombies.";

    public WeaponDamageConfig Config { get; set; } = new();

    private IZombieCore? _core;
    private WeaponDamageModifier? _modifier;

    public void OnConfigParsed(WeaponDamageConfig config) => Config = config;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _core = ZombieModeCapabilities.Core.Get();
        if (_core is null)
        {
            Logger.LogError("cs2-zombie-mode core is not loaded — weapon damage is disabled");
            return;
        }

        _modifier = new WeaponDamageModifier(() => Config);
        _core.AddDamageModifier(_modifier);
    }

    public override void Unload(bool hotReload)
    {
        if (_core is not null && _modifier is not null) _core.RemoveDamageModifier(_modifier);
    }
}
