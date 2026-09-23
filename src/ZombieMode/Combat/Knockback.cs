using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using ZombieMode.Config;

namespace ZombieMode.Combat;

/// <summary>
/// Knockback applied to the infected. Formula:
/// <c>force = damage × kb(source) × global multiplier</c>.
///
/// Why it exists. Without knockback, fighting an infected is a plain shootout against a bag of HP:
/// they walk up and bite until you turn. Knockback makes shotguns and sniper rifles a tool to
/// "push back", and the human knife a way to break free from a grab. This is what separates a zombie
/// mode from deathmatch.
///
/// About the numbers. The formula is defined in relative units: the global multiplier is 1.0 and the
/// kb table is only compared against itself. In game units that is far too little — a 30-damage rifle
/// hit would give an impulse of 30 against a running speed of about 250, i.e. unnoticeable. So the
/// global multiplier is scaled to game units (10 by default), while the table's proportions are kept.
///
/// Two rules that are easy to break by accident:
/// **direction is computed from the damage (attacker → victim), not from the shooter's view angle** —
/// otherwise a shot in the back would push the infected toward the shooter; and **the result is
/// capped**, otherwise a point-blank shotgun becomes a "cancel attack" button.
/// </summary>
public sealed class Knockback
{
    /// <summary>Values that can be changed live through a tuning command (see <see cref="Read"/> and <see cref="Write"/>).</summary>
    public static readonly string[] Tunable =
    {
        "multiplier", "knifeMultiplier", "cap", "knifeCap", "up", "lift", "damageCap", "firstInfectedResist",
    };

    private readonly KnockbackConfig _config;
    /// <summary>Weapon name from the event (`ak47`) → weapon category (`rifle`), taken from the weapon catalog.</summary>
    private readonly Func<string, string?> _categoryOf;
    private readonly Func<CCSPlayerController, bool> _isFirstInfected;
    private readonly Func<CCSPlayerController, bool> _isGuarding;
    private readonly Action<string> _log;

    public Knockback(KnockbackConfig config, Func<string, string?> categoryOf, Func<CCSPlayerController, bool> isFirstInfected, Func<CCSPlayerController, bool> isGuarding, Action<string> log)
    {
        _config = config;
        _categoryOf = categoryOf;
        _isFirstInfected = isFirstInfected;
        _isGuarding = isGuarding;
        _log = log;
    }

    private static bool IsKnife(string weapon) =>
        weapon.Contains("knife", StringComparison.OrdinalIgnoreCase) || weapon.Contains("bayonet", StringComparison.OrdinalIgnoreCase);

    /// <summary>Read a value by name — for the tuning command.</summary>
    public double? Read(string key) => key.ToLowerInvariant() switch
    {
        "multiplier" => _config.Multiplier,
        "knifemultiplier" => _config.KnifeMultiplier,
        "cap" => _config.Cap,
        "knifecap" => _config.KnifeCap,
        "up" => _config.Up,
        "lift" => _config.Lift,
        "damagecap" => _config.DamageCap,
        "firstinfectedresist" => _config.FirstInfectedResist,
        _ => null,
    };

    /// <summary>
    /// Change a value live. Not written to the file: this is for tuning on a live server, not for
    /// configuration — copy the value you like into the config file by hand, or a restart will lose it.
    /// </summary>
    public bool Write(string key, double value)
    {
        switch (key.ToLowerInvariant())
        {
            case "multiplier": _config.Multiplier = value; return true;
            case "knifemultiplier": _config.KnifeMultiplier = value; return true;
            case "cap": _config.Cap = value; return true;
            case "knifecap": _config.KnifeCap = value; return true;
            case "up": _config.Up = value; return true;
            case "lift": _config.Lift = value; return true;
            case "damagecap": _config.DamageCap = (int)value; return true;
            case "firstinfectedresist": _config.FirstInfectedResist = value; return true;
            default: return false;
        }
    }

    /// <summary>Toggle printing the force of every hit.</summary>
    public bool ToggleDebug()
    {
        _config.Debug = !_config.Debug;
        return _config.Debug;
    }

    public string Status() =>
        $"Knockback: {(_config.Enabled ? "enabled" : "disabled")} · debug: {(_config.Debug ? "on" : "off")}\n"
        + $"guns: multiplier {_config.Multiplier:0.##}, cap {_config.Cap:0}, damageCap {_config.DamageCap}, up {_config.Up:0.##}, lift {_config.Lift:0}\n"
        + $"knife: knifeMultiplier {_config.KnifeMultiplier:0.##}, knifeCap {_config.KnifeCap:0} · first infected: ×{_config.FirstInfectedResist:0.##}";

    /// <summary>Damage source factor. The human knife is split into light and heavy stabs by damage amount.</summary>
    private double Factor(string weapon, int damage)
    {
        if (IsKnife(weapon))
            return damage >= _config.KnifeHeavyDamage ? _config.KnifeHeavy : _config.KnifeLight;

        // A per-weapon override beats the category: the damage event gives the short name (glock),
        // while the config and catalog use the full one (weapon_glock).
        if (_config.Weapon.TryGetValue("weapon_" + weapon, out var own)) return own;

        // The category comes from the weapon catalog the plugin already has: a separate weapon list
        // hardcoded here would drift out of sync with it right away.
        return _categoryOf("weapon_" + weapon) switch
        {
            "pistol" => _config.Pistol,
            "smg" => _config.Smg,
            "rifle" => _config.Rifle,
            "mg" => _config.Mg,
            "shotgun" => _config.Shotgun,
            "sniper" => _config.Sniper,
            "grenade" => _config.Grenade,
            _ => _config.Rifle,   // unknown weapon — treat as a rifle: an average value, no surprises
        };
    }

    /// <summary>
    /// Push the infected. Called after the damage has been applied: we use the final damage value,
    /// including armor and hit groups — computing it ourselves would duplicate the engine's work.
    /// </summary>
    public void Apply(CCSPlayerController victim, CCSPlayerController attacker, string weapon, int damage)
    {
        if (!_config.Enabled || damage <= 0) return;
        // Fire (molotov, incendiary) deals damage in ticks, and every tick arrived as a hit — the infected
        // shook next to a fire as if being shot. Burning does not strike, so it does not push.
        if (weapon.Contains("inferno", StringComparison.OrdinalIgnoreCase)) return;

        var pawn = victim.PlayerPawn.Value;
        var from = attacker.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || from is null || !from.IsValid) return;
        if (pawn.AbsOrigin is null || from.AbsOrigin is null) return;

        var knife = IsKnife(weapon);

        // First infected's guard: bullets do not move them at all. The human knife still does — it is
        // the way to break free from a close-range grab, and taking it away would leave a human who has
        // already been caught with no answer.
        if (!knife && _isGuarding(victim)) return;
        // The knife stab is classified by the real damage, but the force uses the capped damage:
        // otherwise a headshot would push four times harder than a body hit (hit groups multiply damage).
        var pushDamage = Math.Min(damage, _config.DamageCap);
        var force = pushDamage * Factor(weapon, damage) * (knife ? _config.KnifeMultiplier : _config.Multiplier);
        if (_isFirstInfected(victim)) force *= _config.FirstInfectedResist;
        if (force <= 0) return;

        // Direction is from the attacker to the victim. The attacker's view angle is irrelevant.
        var dx = pawn.AbsOrigin.X - from.AbsOrigin.X;
        var dy = pawn.AbsOrigin.Y - from.AbsOrigin.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return;   // same position — no direction to push in

        var cap = knife ? _config.KnifeCap : _config.Cap;
        force = Math.Min(force, cap);

        var velocity = pawn.AbsVelocity;
        var nx = velocity.X + dx / length * force;
        var ny = velocity.Y + dy / length * force;
        // A small vertical component: without it the hit "smears" along the ground and barely reads;
        // with it the infected visibly bounces, which tells you the shot landed.
        var nz = velocity.Z + force * _config.Up;

        // The main limit is on the **resulting** velocity, not the per-hit impulse. A shotgun fires nine
        // pellets and a machine gun fires bursts, and each one arrives as a separate damage event: a per-hit
        // cap does not contain them, the pushes stack up, and the infected gets launched across the map.
        var horizontal = Math.Sqrt(nx * nx + ny * ny);
        if (horizontal > cap)
        {
            var scale = cap / horizontal;
            nx *= scale;
            ny *= scale;
        }
        // The vertical part is capped separately: otherwise a burst into someone standing point-blank
        // lifts them straight up, where the horizontal cap does not look.
        nz = Math.Min(nz, cap * _config.Up * 2);

        // The key condition for knockback to be visible at all. While the infected touches the ground, the
        // movement code recomputes velocity from their input every tick and eats the horizontal impulse in
        // the same frame: the force is in the log, nothing on screen. The floor is applied AFTER the vertical
        // cap — otherwise the cap would cut it. In the air the lift is not needed and does not stack: it is
        // a floor, not an addend.
        if (_config.Lift > 0 && pawn.GroundEntity.Value is not null) nz = Math.Max(nz, _config.Lift);

        // Velocity is set via Teleport, not by writing AbsVelocity: the engine ignores a direct field
        // write — it never reaches physics, and the knockback is simply not visible (verified on a live server).
        pawn.Teleport(null, null, new Vector((float)nx, (float)ny, (float)nz));

        if (_config.Debug)
        {
            var line = $"{weapon} · damage {damage} · force {force:0}" + (knife ? " · knife" : string.Empty);
            _log($"knockback: {victim.PlayerName} — {line}");
            // To the shooter's chat: tuning numbers against your own shots is much faster
            // than matching a description against the server console.
            if (!attacker.IsBot) attacker.PrintToChat($" \x04kb\x01: {line}");
        }
    }
}
