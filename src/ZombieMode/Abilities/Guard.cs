using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using ZombieMode.Config;
using ZombieMode.Runtime;

namespace ZombieMode.Abilities;

/// <summary>
/// First infected's guard (berserk): pressing R makes them immune to bullet knockback for a few
/// seconds, after which the ability goes on a long cooldown.
///
/// Why. The first infected is supposed to hunt — that is their role. But a group of humans with
/// shotguns can keep them at range with knockback alone, and the hunt turns into running in place:
/// they do not lose, but they cannot close in either. The guard gives a short window to close the
/// distance, and the long cooldown keeps it from becoming the normal way to fight.
///
/// While the guard is active, hit slowdown is removed as well: otherwise the ability would be only
/// half-useful — bullets no longer push them, but a hail of fire still pins them in place.
///
/// Immunity covers firearm knockback only. The human knife still pushes: the knife is the way to
/// break free from a close-range grab, and taking it away would leave a human who has already been
/// caught with no answer at all.
///
/// While the guard is active the infected can be tinted: both they and the humans should be able to
/// see why shots stopped moving them. Otherwise it looks like a bug rather than an ability.
/// </summary>
public sealed class Guard
{
    /// <summary>Reload button (IN_RELOAD). Free for the infected: they have nothing to reload.</summary>
    private const long ReloadButton = 8192;

    private readonly ZombieModeConfig _config;
    private readonly Func<CCSPlayerController, bool> _canUse;
    private readonly Action<string> _log;

    /// <summary>Slot → time until which the guard lasts.</summary>
    /// <summary>Raised when berserk is activated — used for new-player hints (AbilityHints).</summary>
    public event Action<CCSPlayerController>? Used;

    private readonly Dictionary<int, float> _until = new();

    /// <summary>Slot → time when the ability is ready again.</summary>
    private readonly Dictionary<int, float> _ready = new();

    /// <summary>
    /// Slot → horizontal velocity on the previous tick. Sampled while the guard is active and used
    /// to restore movement after a hit.
    /// </summary>
    private readonly Dictionary<int, (float X, float Y)> _speed = new();

    /// <summary>Slot → tint is currently on (so the color is restored exactly once).</summary>
    private readonly HashSet<int> _lit = new();

    public Guard(ZombieModeConfig config, Func<CCSPlayerController, bool> canUse, Action<string> log)
    {
        _config = config;
        _canUse = canUse;
        _log = log;
    }

    public void Start(BasePlugin plugin) => plugin.RegisterListener<Listeners.OnTick>(OnTick);

    public void Stop(BasePlugin plugin)
    {
        plugin.RemoveListener<Listeners.OnTick>(OnTick);
        Reset();
    }

    /// <summary>New round: no active guard, no cooldowns.</summary>
    public void Reset()
    {
        foreach (var slot in _lit.ToList()) Light(slot, false);
        _until.Clear();
        _ready.Clear();
        _lit.Clear();
        _speed.Clear();
    }

    /// <summary>Whether the guard is active for this slot right now.</summary>
    public bool Active(int slot) => _until.TryGetValue(slot, out var until) && until > Server.CurrentTime;

    /// <summary>
    /// Activate the guard ignoring the cooldown — for testing from the console. Does everything
    /// the R key does: duration, tint, message; the cooldown is set afterwards as usual.
    /// </summary>
    public void Force(CCSPlayerController player)
    {
        if (!player.IsValid) return;
        var now = Server.CurrentTime;
        _until[player.Slot] = now + (float)_config.Infection.GuardDuration;
        _ready[player.Slot] = now + (float)_config.Infection.GuardCooldown;
        Light(player.Slot, true);
        Sound.AtEntity(player.PlayerPawn.Value, _config.Infection.GuardSound, _config.Infection.GuardSoundVolume);
        Texts.To(player, "berserk.on", _config.Infection.GuardDuration.ToString("0"));
        if (_config.Debug) _log($"guard forced by command: {player.PlayerName}");
    }

    /// <summary>Seconds until ready; 0 means ready.</summary>
    public float Cooldown(CCSPlayerController player) =>
        _ready.TryGetValue(player.Slot, out var ready) ? Math.Max(0f, ready - Server.CurrentTime) : 0f;

    public string Status(CCSPlayerController player) =>
        Active(player.Slot) ? "guard: active"
        : Cooldown(player) > 0 ? $"guard: ready in {Cooldown(player):0} s"
        : "guard: ready";

    private void OnTick()
    {
        if (!World.Ready) return;

        try
        {
            var now = Server.CurrentTime;

            // First turn off the tint for anyone whose guard has ended: the tint must not outlive the ability.
            foreach (var slot in _lit.ToList())
            {
                if (!_until.TryGetValue(slot, out var until) || until <= now) Light(slot, false);
            }

            if (_config.Infection.GuardDuration <= 0) return;

            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.IsValid || !_canUse(player)) continue;

                var pawn = player.PlayerPawn.Value;
                if (pawn is null || !pawn.IsValid || pawn.Health <= 0) continue;

                // While the guard is active, also remove hit slowdown. CS2 reduces the player's speed
                // on every hit, and without this the ability was only half-useful: bullets no longer
                // push them, but a hail of fire still pins them in place. The slowdown is reapplied
                // by the engine on every hit, so a one-off fix is not enough — it runs every tick.
                if (Active(player.Slot))
                {
                    // The tick ONLY samples speed and clears the flinch stack. Speed must not be
                    // restored here: the stored speed was applied back to the player and then
                    // immediately sampled again, so the player slid in one direction and ignored
                    // their input. Speed is restored only on a hit, see Restore.
                    Unslow(pawn, player);
                    var v = pawn.AbsVelocity;
                    _speed[player.Slot] = (v.X, v.Y);
                }
                else _speed.Remove(player.Slot);

                if (((long)player.Buttons & ReloadButton) == 0) continue;
                if (Active(player.Slot)) continue;
                if (_ready.TryGetValue(player.Slot, out var ready) && now < ready) continue;

                _until[player.Slot] = now + (float)_config.Infection.GuardDuration;
                _ready[player.Slot] = now + (float)_config.Infection.GuardCooldown;
                Light(player.Slot, true);
                Used?.Invoke(player);
                // Berserk sound from the infected's position: nearby humans can hear that shooting is pointless right now.
                Sound.AtEntity(pawn, _config.Infection.GuardSound, _config.Infection.GuardSoundVolume);

                Texts.To(player, "berserk.on", _config.Infection.GuardDuration.ToString("0"));
                if (_config.Debug) _log($"guard: {player.PlayerName} for {_config.Infection.GuardDuration:0} s");
            }
        }
        catch (CounterStrikeSharp.API.Core.NativeException)
        {
            // The world is not up yet — stay silent (see World).
            World.MapGone();
        }
    }

    /// <summary>
    /// Remove hit slowdown while the guard is active.
    ///
    /// The CS2 mechanism is "flinch": every hit accumulates `m_flFlinchStack` on the pawn, and how much
    /// it slows the player is defined by the weapon itself
    /// (`CCSWeaponBaseVData.FlinchVelocityModifierLarge/Small`). We reset the accumulator: it is shared
    /// across all weapons, so weapon data does not need to be touched.
    ///
    /// The path here was not direct, and it is worth remembering. At first `m_flVelocityModifier` was
    /// modified — it exists on the pawn and was responsible for exactly this in CS:GO. Measurements
    /// showed that in CS2 it is always 1: across seven guard activations it never changed, so the fix
    /// did nothing. The actual fields were found by reflecting over the CounterStrikeSharp assembly,
    /// not by guessing: `SuppressFlinch` lives in the damage result (`CTakeDamageResult`), which our
    /// hook cannot reach, while the accumulator is available on the pawn.
    /// </summary>
    public void Unslow(CCSPlayerPawn pawn, CCSPlayerController player)
    {
        if (!pawn.IsValid) return;

        try
        {
            if (pawn.FlinchStack > 0.001f)
            {
                if (_config.Debug) _log($"guard: {player.PlayerName} flinch {pawn.FlinchStack:0.00} → 0");
                pawn.FlinchStack = 0f;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flFlinchStack");
            }

            // The old field too: it exists on the pawn, is cheap, and does no harm if it ever starts working.
            if (pawn.VelocityModifier < 0.999f)
            {
                pawn.VelocityModifier = 1.0f;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
            }

        }
        catch (Exception e)
        {
            if (_config.Debug) _log($"guard: flinch not cleared — {e.Message}");
        }
    }

    /// <summary>
    /// Restore the speed taken away by a hit. Called ONLY from the damage handler — once per hit,
    /// not every frame.
    ///
    /// Resetting the flinch accumulator is not enough: speed is cut once, at the moment of the hit, and
    /// does not come back on its own — measurements showed an accumulator of 0.10 that we dutifully
    /// cleared five times a second while the player still got bogged down. So we take the speed from
    /// the previous tick, where it has not been cut yet.
    /// </summary>
    public void Restore(CCSPlayerPawn pawn, CCSPlayerController player)
    {
        if (!pawn.IsValid || !_speed.TryGetValue(player.Slot, out var was)) return;

        var now = pawn.AbsVelocity;
        var wasSpeed = Math.Sqrt(was.X * was.X + was.Y * was.Y);
        var nowSpeed = Math.Sqrt(now.X * now.X + now.Y * now.Y);

        // Only if the player got slower: we never speed them up beyond how fast they were running.
        if (wasSpeed <= 1.0 || nowSpeed >= wasSpeed - 1.0) return;

        if (_config.Debug) _log($"guard: {player.PlayerName} speed {nowSpeed:0} → {wasSpeed:0}");
        pawn.Teleport(null, null, new Vector(was.X, was.Y, now.Z));
    }

    /// <summary>
    /// Tint while the guard is active. The player model itself is colored instead of spawning a
    /// separate entity: it must be visible to everyone immediately, without recipient filters — unlike
    /// vision silhouettes, where who sees what matters.
    /// </summary>
    private void Light(int slot, bool on)
    {
        // The tint can be disabled (it was replaced by an on-screen vein effect for the infected
        // player). Turning it off is always allowed — otherwise an already lit tint would get stuck.
        if (on && !_config.Infection.GuardTint) return;

        var pawn = Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) { _lit.Remove(slot); return; }

        pawn.Render = on
            ? System.Drawing.Color.FromArgb(255, 255, 150, 60)
            : System.Drawing.Color.FromArgb(255, 255, 255, 255);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

        if (on) _lit.Add(slot);
        else _lit.Remove(slot);
    }
}
