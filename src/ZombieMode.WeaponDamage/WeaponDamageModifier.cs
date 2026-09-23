using ZombieMode.Api;

namespace ZombieMode.WeaponDamage;

/// <summary>
/// Multiplies human damage to zombies by weapon, knife and grenade.
///
/// In a zombie round a gun's strength is not its damage per shot but its magazine and fire rate, so slow guns
/// with small magazines get more damage and fast ones do not.
/// </summary>
public sealed class WeaponDamageModifier : IDamageModifier
{
    private readonly Func<WeaponDamageConfig> _config;

    public WeaponDamageModifier(Func<WeaponDamageConfig> config) => _config = config;

    /// <summary>First: other modifiers (armour and the like) cut the final weapon damage.</summary>
    public int Order => 0;

    public void Modify(ZombieDamage damage)
    {
        var config = _config();
        double multiplier;
        if (damage.Grenade is { } grenade)
        {
            var key = grenade switch
            {
                GrenadeKind.He => "he",
                GrenadeKind.Fire => "fire",
                _ => "decoy",
            };
            multiplier = config.Grenades.TryGetValue(key, out var g) ? g : 1.0;
        }
        else if (!string.IsNullOrEmpty(damage.Weapon))
        {
            multiplier = IsKnife(damage.Weapon!)
                ? config.Knife
                : config.Weapons.TryGetValue(damage.Weapon!, out var w) ? w : config.Default;
        }
        else
        {
            return;
        }

        if (Math.Abs(multiplier - 1.0) >= 0.001)
            damage.Damage = (float)(damage.Damage * multiplier);
    }

    /// <summary>CS2 has many knives and they all share the same name parts.</summary>
    public static bool IsKnife(string designerName) =>
        designerName.Contains("knife", StringComparison.OrdinalIgnoreCase)
        || designerName.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
}
