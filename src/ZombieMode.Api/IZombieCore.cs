using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace ZombieMode.Api;

/// <summary>
/// Entry point for extensions. The core plugin registers <see cref="Core"/> in <c>Load</c>;
/// an extension resolves it in <c>OnAllPluginsLoaded</c>:
/// <code>
/// public override void OnAllPluginsLoaded(bool hotReload)
/// {
///     var core = ZombieModeCapabilities.Core.Get()
///         ?? throw new Exception("cs2-zombie-mode core is not loaded");
///     core.Infected += e => Server.PrintToChatAll($"{e.Victim.PlayerName} has turned");
/// }
/// </code>
/// </summary>
public static class ZombieModeCapabilities
{
    public static readonly PluginCapability<IZombieCore> Core = new("zombiemode:core");
}

/// <summary>Where the current round is.</summary>
public enum RoundPhase
{
    /// <summary>Not enough players on the teams yet; nobody will be infected until more join.</summary>
    Waiting,

    /// <summary>Everyone looks human. The first infected is chosen and will turn at <see cref="IZombieCore.FirstInfectionAt"/>.</summary>
    Hidden,

    /// <summary>The first infected has turned; bites spread the infection.</summary>
    Outbreak,

    /// <summary>The round is decided and waiting for the next one.</summary>
    Ended,
}

/// <summary>A human has been infected and is now a zombie. <c>Attacker</c> is null for the round's first infected.</summary>
public sealed record InfectedEvent(CCSPlayerController Victim, CCSPlayerController? Attacker, bool IsFirstInfected);

/// <summary>A zombie has been killed.</summary>
public sealed record InfectedKilledEvent(CCSPlayerController Victim, CCSPlayerController? Attacker, bool WasFirstInfected);

/// <summary>A human damaged a zombie. <c>Damage</c> is the real damage dealt, after all <see cref="IDamageModifier"/>s.</summary>
public sealed record ZombieDamagedEvent(CCSPlayerController Victim, CCSPlayerController Attacker, string Weapon, int Damage);

/// <summary>A zombie used an ability: <c>leap</c> (every zombie) or <c>rage</c> (berserk, first infected only).</summary>
public sealed record AbilityUsedEvent(CCSPlayerController Player, string Ability);

/// <summary>
/// Ability cooldowns for a HUD, in seconds left. <c>Visible</c> is false when the player is not a live zombie.
/// Regular zombies have no berserk, so their <c>Rage</c> is always 0.
/// </summary>
public readonly record struct AbilityState(bool Visible, bool First, float Leap, float Rage);

/// <summary>Grenade that caused the damage. Detected by the projectile, not by the weapon in hand.</summary>
public enum GrenadeKind { He, Fire, Decoy }

/// <summary>
/// Damage from a human to a zombie that can still be changed. The core builds it in its damage hook and runs
/// it through every <see cref="IDamageModifier"/> in order; only <see cref="Damage"/> is meant to change.
/// </summary>
public sealed class ZombieDamage
{
    public required CCSPlayerController Victim { get; init; }
    public required CCSPlayerController Attacker { get; init; }

    /// <summary>Weapon in the attacker's hands (<c>weapon_ak47</c>); null for grenades and weaponless damage.</summary>
    public string? Weapon { get; init; }

    /// <summary>Set when the damage comes from a grenade. By the time it explodes the thrower holds something else.</summary>
    public GrenadeKind? Grenade { get; init; }

    public float Damage { get; set; }
}

/// <summary>Changes human-to-zombie damage. Lower <see cref="Order"/> runs first.</summary>
public interface IDamageModifier
{
    int Order { get; }
    void Modify(ZombieDamage damage);
}

/// <summary>Receives player-facing messages from the core. Replace it to route them into your own HUD.</summary>
public interface INoticeSink
{
    void Show(CCSPlayerController player, string text);
}

/// <summary>
/// Knobs for game modes built on top of the normal round (boss arenas and the like). Leave a knob null for
/// the core's default behaviour.
/// </summary>
public interface IModeHooks
{
    /// <summary>Pick the first infected from the candidates. Returning null falls back to the default pick.</summary>
    Func<List<CCSPlayerController>, CCSPlayerController?>? PickFirst { get; set; }

    /// <summary>Seconds from round start until the first infection. Returning null keeps the configured timings.</summary>
    Func<double?>? InfectDelay { get; set; }

    /// <summary>Where the round-limit airstrike lands. Null or a null result: the middle of the map's spawn points.</summary>
    Func<Vector?>? StrikeTarget { get; set; }
}

public interface IZombieCore : IModeHooks
{
    RoundPhase Phase { get; }

    /// <summary>Server time when the first infected turns; 0 when not scheduled yet or already happened.</summary>
    double FirstInfectionAt { get; }

    /// <summary>The round is already decided: abilities and modes have nothing left to do.</summary>
    bool RoundOver { get; }

    bool IsInfected(CCSPlayerController player);
    bool IsFirstInfected(CCSPlayerController player);

    /// <summary>The human has an open wound after a bite and is losing health.</summary>
    bool IsBleeding(CCSPlayerController player);

    /// <summary>The first infected is in berserk right now (full invulnerability).</summary>
    bool IsBerserk(CCSPlayerController player);

    /// <summary>
    /// Real zombie health. The pawn never shows more than the configured cap (999 by default) so the HUD
    /// stays readable; the real number lives in the core. Null when health is not spoofed for this player.
    /// </summary>
    (int Hp, int Max)? RealHealth(CCSPlayerController player);

    /// <summary>Set zombie health, keeping the spoofed display in sync.</summary>
    void SetHealth(CCSPlayerController player, int hp, int max);

    AbilityState Abilities(CCSPlayerController player);

    event Action<InfectedEvent>? Infected;
    event Action<InfectedKilledEvent>? InfectedKilled;
    event Action<ZombieDamagedEvent>? ZombieDamaged;
    event Action<AbilityUsedEvent>? AbilityUsed;
    event Action<RoundPhase>? PhaseChanged;

    /// <summary>
    /// Give weapons while zombies are blocked from picking them up. The same block would otherwise cancel a
    /// shop purchase or a crate drop. The block is lifted until the returned object is disposed:
    /// <code>using (core.AllowWeaponGive()) player.GiveNamedItem("weapon_ak47");</code>
    /// </summary>
    IDisposable AllowWeaponGive();

    void AddDamageModifier(IDamageModifier modifier);
    void RemoveDamageModifier(IDamageModifier modifier);

    /// <summary>Where player-facing messages go. Default: centre-screen alert.</summary>
    INoticeSink Notices { get; set; }

    /// <summary>Players who turned the round music off. Null: music plays for everyone.</summary>
    Func<CCSPlayerController, bool>? MusicMuted { get; set; }

    /// <summary>Infect a player right now. False when already infected or invalid.</summary>
    bool Infect(CCSPlayerController player, bool first);

    /// <summary>End the round with your own announcement. A second call in the same round does nothing.</summary>
    void EndRound(RoundEndReason reason, string message);
}
