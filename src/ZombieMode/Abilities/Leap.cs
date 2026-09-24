using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using ZombieMode.Config;

using ZombieMode.Runtime;

namespace ZombieMode.Abilities;

/// <summary>
/// Zombie leap: Ctrl and Space together, in either order, launch the infected along their view direction.
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
    /// The leap is Ctrl and Space held together, in either order (it used to be E).
    ///
    /// The trap of the first attempt: for a crouching player the engine clears the jump bit in the processed
    /// button mask, so "Ctrl first" never fired. So Space is also looked for in the raw client input
    /// (<see cref="SeenInRaw"/>), and the keys need not be simultaneous: each counts as held for
    /// <see cref="Window"/> seconds after it was last seen, and so does the ground — Space starts a normal jump,
    /// so by the time Ctrl is down the player is already in the air.
    /// </summary>
    private const long JumpButton = 2;    // IN_JUMP
    private const long DuckButton = 4;    // IN_DUCK

    private readonly Dictionary<int, float> _jumpSeen = new();
    private readonly Dictionary<int, float> _duckSeen = new();
    private readonly Dictionary<int, float> _groundSeen = new();

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
        _jumpSeen.Clear();
        _duckSeen.Clear();
        _groundSeen.Clear();
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

            if (!ComboHeld(player, pawn, now)) continue;

            // From the ground — or just off it: Space lifts the player before Ctrl is down.
            if (!_groundSeen.TryGetValue(player.Slot, out var grounded) || now - grounded > Window) continue;

            // Not ready yet — just wait. The remaining time is shown by the shared ability HUD.
            if (_ready.TryGetValue(player.Slot, out var ready) && now < ready) continue;

            // The combo is spent: the next leap needs a new press, not a held one.
            _jumpSeen.Remove(player.Slot);
            _duckSeen.Remove(player.Slot);
            Jump(player, pawn, now);
        }
        }
        catch (CounterStrikeSharp.API.Core.NativeException)
        {
            // The world is not up yet. Stay silent: a flood of these exceptions once kept the server from finishing the map load.
            World.MapGone();
        }
    }

    /// <summary>Whether both keys were seen within <see cref="Window"/>. Also remembers when the player stood on the ground.</summary>
    private bool ComboHeld(CCSPlayerController player, CCSPlayerPawn pawn, float now)
    {
        if (pawn.GroundEntity.Value is not null) _groundSeen[player.Slot] = now;

        var mask = (long)player.Buttons;
        if ((mask & JumpButton) != 0 || SeenInRaw(pawn, JumpButton)) _jumpSeen[player.Slot] = now;
        if ((mask & DuckButton) != 0 || SeenInRaw(pawn, DuckButton)) _duckSeen[player.Slot] = now;

        return _jumpSeen.TryGetValue(player.Slot, out var j) && now - j <= Window
            && _duckSeen.TryGetValue(player.Slot, out var d) && now - d <= Window;
    }

    /// <summary>
    /// The bit in the raw input states (all three slots: held, pressed this frame, released) — the key is there
    /// even when the engine did not let it through, as with a crouching player's jump.
    /// </summary>
    private static bool SeenInRaw(CCSPlayerPawn pawn, long bit)
    {
        try
        {
            var services = pawn.MovementServices;
            if (services is null) return false;
            var states = services.Buttons.ButtonStates;
            foreach (var state in states)
                if (((long)state & bit) != 0) return true;
            return false;
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

            // The vertical part REPLACES the engine jump. With Ctrl + Space a normal jump (about 301 up) has
            // already started, and adding to it threw the infected far too high.
            var velocity = new Vector(fx * power, fy * power, fz * power + up);
            p.Teleport(null, null, velocity);

            if (_config.Debug)
                _log($"leap: {player.PlayerName}, view ({fx:0.##} {fy:0.##} {fz:0.##}), velocity ({velocity.X:0} {velocity.Y:0} {velocity.Z:0})");
        });

        // Played from the leaper's position, at our own volume.
        Sound.AtEntity(pawn, _config.Infection.LeapSound, _config.Infection.LeapSoundVolume);
    }
}
