using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using Microsoft.Extensions.Logging;

namespace ZombieMode.Magazines;

/// <summary>
/// Settings: <c>addons/counterstrikesharp/configs/plugins/ZombieMode.Magazines/ZombieMode.Magazines.json</c>.
/// </summary>
public sealed class MagazinesConfig : BasePluginConfig
{
    /// <summary>
    /// Spare magazines per weapon, by the weapon's real name (<c>weapon_m4a1_silencer</c>, not the entity name).
    /// Ammo is counted from the weapon's own magazine size, so nothing needs recalculating if Valve changes it.
    /// Stock reserves are tiny for some guns — the CZ75-Auto gets one spare magazine, the M4A1-S two — and
    /// against zombies with thousands of health that is half a second of shooting.
    /// Weapons missing here keep what the game gives.
    /// </summary>
    [JsonPropertyName("spareMagazines")]
    public Dictionary<string, int> SpareMagazines { get; set; } = new()
    {
        ["weapon_cz75a"] = 9,
        ["weapon_m4a1_silencer"] = 6,
        ["weapon_m4a1"] = 4,
    };

    /// <summary>
    /// Also refill on pickup, not only on purchase. Off by default: a gun picked up from a fallen player keeps
    /// whatever ammo they left, and new magazines are something you buy.
    /// </summary>
    [JsonPropertyName("restockOnPickup")] public bool RestockOnPickup { get; set; }
}

/// <summary>Magazines extension for cs2-zombie-mode: set the spare magazines a weapon comes with.</summary>
public sealed class MagazinesPlugin : BasePlugin, IPluginConfig<MagazinesConfig>
{
    public override string ModuleName => "cs2-zombie-mode: magazines";
    public override string ModuleVersion => "0.1.1";
    public override string ModuleAuthor => "Project Zero";
    public override string ModuleDescription => "Spare magazines per weapon.";

    public MagazinesConfig Config { get; set; } = new();

    public void OnConfigParsed(MagazinesConfig config) => Config = config;

    [GameEventHandler]
    public HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not null && player.IsValid) Server.NextFrame(() => Restock(player));
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnItemPickup(EventItemPickup @event, GameEventInfo info)
    {
        if (!Config.RestockOnPickup) return HookResult.Continue;
        var player = @event.Userid;
        if (player is not null && player.IsValid) Server.NextFrame(() => Restock(player));
        return HookResult.Continue;
    }

    /// <summary>
    /// Bring every configured weapon the player carries up to its spare magazines. We look at the weapons that
    /// actually ended up in hand rather than at the event's item name: CS2 has several pairs that share one
    /// entity name (see <see cref="RealName"/>), and the loadout decides which one the player gets.
    /// </summary>
    private void Restock(CCSPlayerController player)
    {
        if (!player.IsValid) return;
        var services = player.PlayerPawn.Value?.WeaponServices;
        if (services is null) return;

        foreach (var handle in services.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon is null || !weapon.IsValid) continue;

            var name = RealName(weapon);
            if (!Config.SpareMagazines.TryGetValue(name, out var magazines) || magazines <= 0) continue;

            var data = weapon.As<CCSWeaponBase>().VData;
            if (data is null) continue;

            // Some weapons count their reserve in magazines, not rounds (CZ75-Auto, R8). The weapon knows.
            var ammo = data.ReserveAmmoAsClips ? magazines : magazines * Math.Max(1, data.MaxClip1);

            // Weapon data is shared by the whole weapon type and lives until the server restarts. Set the cap to
            // exactly our number rather than "raise if lower": a raised cap never comes down by itself, and the
            // engine tops the reserve up to it on the next reload.
            if (data.PrimaryReserveAmmoMax != ammo) data.PrimaryReserveAmmoMax = ammo;
            if (weapon.ReserveAmmo[0] >= ammo) continue;

            weapon.ReserveAmmo[0] = ammo;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
            Logger.LogDebug("{Weapon}: {Magazines} spare magazines, {Ammo} in reserve", name, magazines, ammo);
        }
    }

    /// <summary>
    /// The weapon's real name. CS2 gives some pairs one entity name and tells them apart only by item definition:
    /// M4A4 / M4A1-S are both <c>weapon_m4a1</c>, P2000 / USP-S <c>weapon_hkp2000</c>, P250 / CZ75-Auto
    /// <c>weapon_p250</c>, Desert Eagle / R8 <c>weapon_deagle</c>, MP7 / MP5-SD <c>weapon_mp7</c>.
    /// </summary>
    public static string RealName(CBasePlayerWeapon weapon)
    {
        ushort index = 0;
        try { index = weapon.AttributeManager.Item.ItemDefinitionIndex; } catch { /* keep the entity name */ }
        return index switch
        {
            60 => "weapon_m4a1_silencer",
            61 => "weapon_usp_silencer",
            63 => "weapon_cz75a",
            64 => "weapon_revolver",
            23 => "weapon_mp5sd",
            _ => weapon.DesignerName ?? string.Empty,
        };
    }
}
