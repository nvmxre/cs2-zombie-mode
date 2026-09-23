namespace ZombieMode.Combat;

/// <summary>
/// Weapon class of every stock CS2 firearm, by entity name (<c>weapon_ak47</c>). Knockback strength is set per
/// class in the config (<c>knockback.pistol</c>, <c>knockback.rifle</c>…), with per-weapon overrides in
/// <c>knockback.weapon</c>. A weapon missing from this table is treated as a rifle.
/// </summary>
public static class WeaponCategories
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_glock"] = "pistol",
        ["weapon_hkp2000"] = "pistol",
        ["weapon_usp_silencer"] = "pistol",
        ["weapon_p250"] = "pistol",
        ["weapon_elite"] = "pistol",
        ["weapon_fiveseven"] = "pistol",
        ["weapon_tec9"] = "pistol",
        ["weapon_cz75a"] = "pistol",
        ["weapon_deagle"] = "pistol",
        ["weapon_revolver"] = "pistol",

        ["weapon_mac10"] = "smg",
        ["weapon_mp9"] = "smg",
        ["weapon_mp7"] = "smg",
        ["weapon_mp5sd"] = "smg",
        ["weapon_ump45"] = "smg",
        ["weapon_p90"] = "smg",
        ["weapon_bizon"] = "smg",

        ["weapon_nova"] = "shotgun",
        ["weapon_xm1014"] = "shotgun",
        ["weapon_mag7"] = "shotgun",
        ["weapon_sawedoff"] = "shotgun",

        ["weapon_m249"] = "mg",
        ["weapon_negev"] = "mg",

        ["weapon_galilar"] = "rifle",
        ["weapon_famas"] = "rifle",
        ["weapon_ak47"] = "rifle",
        ["weapon_m4a1"] = "rifle",
        ["weapon_m4a1_silencer"] = "rifle",
        ["weapon_sg556"] = "rifle",
        ["weapon_aug"] = "rifle",

        ["weapon_ssg08"] = "sniper",
        ["weapon_awp"] = "sniper",
        ["weapon_g3sg1"] = "sniper",
        ["weapon_scar20"] = "sniper",

        ["weapon_hegrenade"] = "grenade",
        ["weapon_molotov"] = "grenade",
        ["weapon_incgrenade"] = "grenade",
        ["weapon_decoy"] = "grenade",
        ["weapon_flashbang"] = "grenade",
        ["weapon_smokegrenade"] = "grenade",
    };

    /// <summary>Class of a weapon by its entity name, or null when unknown.</summary>
    public static string? Of(string weapon) => Table.TryGetValue(weapon, out var category) ? category : null;
}
