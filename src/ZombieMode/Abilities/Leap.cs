using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using ZombieMode.Config;

using ZombieMode.Runtime;

namespace ZombieMode.Abilities;

/// <summary>
/// Zombie leap: the E key launches the infected along their view direction.
/// The first infected gets full strength; regular infected get the `leapOthersFactor` fraction
/// (half by default). All share the same cooldown.
///
/// This is a jump, not a dash: it gives height, so the infected can reach spots humans cannot
/// and close distance vertically. The cooldown matters more than the strength: without it the
/// first infected flies all over the map.
///
/// Why held buttons are polled every tick instead of reacting to a press event: Ctrl and Space
/// rarely land in the same frame, so requiring a simultaneous press meant players had to mash
/// the keys until they lined up. Holding the key is enough — the leap fires on its own as soon as
/// the player is on the ground and the cooldown has expired.
/// </summary>
public sealed class Leap
{
    private readonly ZombieModeConfig _config;
    private readonly Func<CCSPlayerController, bool> _canLeap;
    /// <summary>Whether this is the first infected: full strength for them, a fraction for everyone else.</summary>
    private readonly Func<CCSPlayerController, bool> _isFirst;
    private readonly Action<string> _log;

    /// <summary>Slot → time when the leap is ready again.</summary>
    private readonly Dictionary<int, float> _ready = new();

    /// <summary>
    /// How many seconds a press still counts as "fresh". Simultaneous presses are not required:
    /// the engine reports the jump and duck bits in different frames, and with "duck first" the jump
    /// bit of a crouching player may never arrive at all.
    /// </summary>
    private const float Window = 0.4f;

    /// <summary>
    /// Button bit for the use key (E). The leap is bound to it.
    ///
    /// Why not Ctrl+Space as originally planned: CS2 does not allow jumping from a full crouch, and
    /// the engine clears the jump bit before the plugin sees it — across dozens of attempts in the log
    /// the bit never showed up once. The combo only worked as "Space, then Ctrl in the air", which is
    /// awkward to play. The engine leaves E alone, and the infected have no use for it anyway:
    /// there is nothing for them to pick up.
    /// </summary>
    private const long UseButton = 32;

    public Leap(ZombieModeConfig config, Func<CCSPlayerController, bool> canLeap, Func<CCSPlayerController, bool> isFirst, Action<string> log)
    {
        _config = config;
        _canLeap = canLeap;
        _isFirst = isFirst;
        _log = log;
    }

    public void Start(BasePlugin plugin)
    {
        plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    public void Stop(BasePlugin plugin)
    {
        plugin.RemoveListener<Listeners.OnTick>(OnTick);
    }

    public void Reset()
    {
        _ready.Clear();
    }

    /// <summary>Seconds left until the leap is ready; 0 means ready.</summary>
    public float Cooldown(CCSPlayerController player)
    {
        if (!_ready.TryGetValue(player.Slot, out var ready)) return 0f;
        return Math.Max(0f, ready - Server.CurrentTime);
    }

    private void OnTick()
    {
        if (!World.Ready) return;
        try
        {
        var now = Server.CurrentTime;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || !_canLeap(player)) continue;
            // A factor of 0 in the config disables the leap for regular infected; the first one is unaffected.
            if (!_isFirst(player) && _config.Infection.LeapOthersFactor <= 0) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid || pawn.Health <= 0) continue;

            if (!UsePressed(player, pawn)) continue;

            // Ground only: the leap does not work in mid-air.
            if (pawn.GroundEntity.Value is null) continue;

            // Not ready yet — just wait. The remaining time is shown by the shared ability HUD.
            if (_ready.TryGetValue(player.Slot, out var ready) && now < ready) continue;

            Jump(player, pawn, now);
        }
        }
        catch (CounterStrikeSharp.API.Core.NativeException)
        {
            // The world is not up yet. Stay silent: a flood of these exceptions once kept the server from finishing the map load.
            World.MapGone();
        }
    }

    /// <summary>
    /// Whether the use key is held. Reads both the processed button mask and the raw input state:
    /// the raw state contains the button regardless of whether the engine let it through.
    /// </summary>
    private static bool UsePressed(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (((long)player.Buttons & UseButton) != 0) return true;

        try
        {
            var services = pawn.MovementServices;
            if (services is null) return false;
            var states = services.Buttons.ButtonStates;
            return states.Length > 0 && ((long)states[0] & UseButton) != 0;
        }
        catch
        {
            // Schema fields may have moved after a game update — fall back to the processed mask.
            return false;
        }
    }

    /// <summary>Raised when a leap is performed — used for new-player hints (AbilityHints).</summary>
    public event Action<CCSPlayerController>? Used;

    private void Jump(CCSPlayerController player, CCSPlayerPawn pawn, float now)
    {
        _ready[player.Slot] = now + (float)_config.Infection.LeapCooldown;
        Used?.Invoke(player);



        // Direction is exactly where the player is looking, including mouse pitch:
        // look up to go up, look ahead to fly forward.
        var angles = pawn.EyeAngles;
        var pitch = angles.X * Math.PI / 180.0;
        var yaw = angles.Y * Math.PI / 180.0;
        var fx = (float)(Math.Cos(pitch) * Math.Cos(yaw));
        var fy = (float)(Math.Cos(pitch) * Math.Sin(yaw));
        var fz = (float)(-Math.Sin(pitch));

        // Regular infected get a fraction of the first infected's leap, both along the view and upward.
        var factor = _isFirst(player) ? 1.0 : _config.Infection.LeapOthersFactor;
        var power = (float)(_config.Infection.LeapPower * factor);
        var up = (float)(_config.Infection.LeapUp * factor);

        // Apply the impulse on the next frame: right now the player is still on the ground, and the engine
        // would strip the horizontal part with friction and the speed cap, leaving an almost pure "up".
        Server.NextWorldUpdate(() =>
        {
            if (!player.IsValid) return;
            var p = player.PlayerPawn.Value;
            if (p is null || !p.IsValid || p.Health <= 0) return;

            // Add the vertical part to the engine's own jump instead of replacing it: otherwise the
            // leap came out weaker than a normal jump (the engine gives about 301 upward).
            var jumpZ = Math.Max(p.AbsVelocity?.Z ?? 0f, 0f);
            var velocity = new Vector(fx * power, fy * power, jumpZ + fz * power + up);
            p.Teleport(null, null, velocity);

            if (_config.Debug)
                _log($"leap: {player.PlayerName}, view ({fx:0.##} {fy:0.##} {fz:0.##}), velocity ({velocity.X:0} {velocity.Y:0} {velocity.Z:0})");
        });

        // Played from the leaper's position, at our own volume.
        Sound.AtEntity(pawn, _config.Infection.LeapSound, _config.Infection.LeapSoundVolume);
    }
}
