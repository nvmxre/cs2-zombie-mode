using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace ZombieMode.Runtime;

/// <summary>
/// Server-side sound: played by sound event name, with a volume that we control rather than the player.
///
/// WHY NOT CLIENT COMMANDS. The obvious approach is `player.ExecuteClientCommand("play &lt;file&gt;")`.
/// It works, but at the cost of three things: the sound plays "inside the player's head", with no
/// position in the world; the volume comes from the player's own settings — quiet for one, deafening
/// for another; and files under `sounds/ui/*` go through a separate, quieter mixer channel. The server
/// has no control over any of this: CS2 has no `playvol`, and trying to set a level on
/// `ambient_generic` made the sound inaudible.
///
/// THE RIGHT WAY is `CBaseEntity.EmitSound(name, recipients, volume, pitch)`. It is positional, has a
/// volume, and lets you choose who hears it. The catch is that `EmitSound` accepts ONLY a sound event
/// name — passing a file path is useless — and the event names cannot easily be pulled out of the game
/// paks: the event files are compiled, and grepping them yields mostly fragments.
///
/// WHERE THE NAMES ACTUALLY ARE. SteamDatabase/GameTracking-CS2 publishes the decompiled event files as
/// plain text: `game/csgo/pak01_dir/soundevents/*.vsndevts`. That list has around 1700 events, each with
/// its volume, mixer group, distance falloff curve and file list. It also explains why some obvious
/// candidates are silent: `helicopter_main`, `helicopter_sweetner` and `drone.engine` point to
/// `sounds/common/null.vsnd` — they are placeholders, and the real sound is supplied by the map.
///
/// So only use event names that exist in that list (or in your own addon). The engine plays an unknown
/// name silently — no error, just silence, which is indistinguishable from broken game logic.
/// </summary>
public static class Sound
{
    /// <summary>
    /// Play an event from an entity: the sound comes FROM its position, with distance falloff.
    /// Use this when players should be able to locate the source by ear.
    /// </summary>
    /// <param name="entity">Sound source in the world.</param>
    /// <param name="soundEvent">Event name. An empty string means silence without an error.</param>
    /// <param name="volume">Volume. 1.0 is the level defined in the event itself.</param>
    /// <param name="recipients">Who hears it. null means everyone.</param>
    public static bool AtEntity(CBaseEntity? entity, string soundEvent, double volume,
        RecipientFilter? recipients = null)
    {
        if (entity is null || !entity.IsValid || string.IsNullOrWhiteSpace(soundEvent)) return false;

        try
        {
            entity.EmitSound(soundEvent, recipients, (float)volume);
            return true;
        }
        catch
        {
            // Sound is not critical: silence is better than a broken round because of a typo in a name.
            return false;
        }
    }

    /// <summary>
    /// Play an event to a single player, emitted from that player. The distance to the source is zero,
    /// so falloff does not apply and the volume is exactly what we set.
    ///
    /// Use this for anything that should sound "for me" rather than "over there".
    /// </summary>
    public static bool ToPlayer(CCSPlayerController? player, string soundEvent, double volume)
    {
        if (player is null || !player.IsValid || player.IsBot) return false;
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return false;

        return AtEntity(pawn, soundEvent, volume, new RecipientFilter(player));
    }

    /// <summary>
    /// Play an event to everyone on the map at the same loudness.
    ///
    /// Not from a single entity in the world but to each player from themselves — and this matters.
    /// An entity sounds from its position and falls off with distance, so a map-wide warning ends up
    /// sounding "somewhere far away". Sound level 0, which means "no falloff" in Source, makes the sound
    /// inaudible in CS2. When the source is the player, the distance is always zero, and everyone gets the
    /// warning wherever they stand.
    /// </summary>
    /// <returns>How many players the sound was sent to.</returns>
    public static int ToEveryone(string soundEvent, double volume)
    {
        if (string.IsNullOrWhiteSpace(soundEvent)) return 0;

        var count = 0;
        foreach (var player in Utilities.GetPlayers())
            if (ToPlayer(player, soundEvent, volume))
                count++;

        return count;
    }

    /// <summary>
    /// SteamID64s of players who muted music for themselves (useful e.g. for recording gameplay with only
    /// game sounds). Lives until the plugin is reloaded; keyed by SteamID rather than slot, so reconnecting
    /// does not reset it.
    /// </summary>

    /// <summary>
    /// Music to everyone except players who muted it: round start music, ambience, theme, round end music.
    /// Sirens, countdown, heartbeat and zombie voices are game sounds and still go through ToEveryone/AtEntity.
    /// </summary>
    public static int MusicToEveryone(Func<CCSPlayerController, bool>? muted, string soundEvent, double volume)
    {
        if (string.IsNullOrWhiteSpace(soundEvent)) return 0;

        var count = 0;
        foreach (var player in Utilities.GetPlayers())
            if (muted?.Invoke(player) != true && ToPlayer(player, soundEvent, volume))
                count++;

        return count;
    }
}
