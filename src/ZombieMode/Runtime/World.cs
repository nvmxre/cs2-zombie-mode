namespace ZombieMode.Runtime;

/// <summary>
/// Whether the game world is ready. While the map is loading, entities do not exist, and any access
/// to them ends with the exception "Entity system yet is not initialized".
///
/// THIS IS NOT AN ORDINARY EXCEPTION, AND YOU CANNOT JUST CATCH IT. CounterStrikeSharp keeps the pointer
/// to the entity list in a static field:
///
///     private static Lazy&lt;IntPtr&gt; ConcreteEntityListPointer = new(NativeAPI.GetConcreteEntityListPointer);
///
/// `Lazy&lt;T&gt;` in its default mode (ExecutionAndPublication) **caches the factory's exception forever**.
/// One early call, and CSS answers "no world" for the rest of the process lifetime, even when the map
/// has long been loaded and players are running around. The field is static and lives in the CSS
/// assembly itself, so `css_plugins reload` does not reset it: only a server restart fixes it.
///
/// What it cost. A HUD panel was once created directly in `Load()` — before the map. One call at server
/// start, and nothing worked afterwards: no infection, no sky, no knockback, no menus. From the outside
/// it looked like "just a regular server without zombies", and tracking it down took half a day on a
/// false trail, because the server itself is perfectly healthy: the map is loaded, spawngroups are up,
/// players are `active`. Only the plugin is blind.
///
/// Hence the rule: **access entities only when <see cref="Ready"/>**. The check costs one `if`; the bug
/// costs an evening. This applies especially to code that runs on its own: `Load()`,
/// `Listeners.OnMapStart` (fires at the BEGINNING of the load) and `AddTimer` timers created at load
/// time — their first tick lands right in the middle of the map load.
///
/// **The second trap, more expensive than the first: plugin reload.** This class is static and lives in
/// OUR assembly, and `css_plugins reload` creates the assembly anew — <see cref="Ready"/> resets to false.
/// The flag is only raised on round start, but a round cannot start until the current one ends, and
/// nothing can end it: the tick is silent because of this same flag. The loop closes, and the round hangs
/// until the map changes. This happened in practice: the last human died from a fall and the round never
/// ended, precisely because the plugin had been reloaded mid-round forty seconds earlier.
/// That is why the plugin's `Load(hotReload: true)` raises the flag immediately: a reload on a live
/// server means the map is up. This is safe — the `Lazy` trap only affects a pointer that has never been
/// created successfully, and on a live server it was created long ago and lives in the CSS assembly,
/// which a plugin reload does not touch.
/// </summary>
public static class World
{
    /// <summary>The map is loaded and entities exist.</summary>
    public static bool Ready { get; private set; }

    /// <summary>
    /// The world is up. Set on round start, not on `Listeners.OnMapStart`: that one fires **at the
    /// beginning** of the load, when entities do not exist yet — setting the flag there once protected
    /// nothing. A round start only happens with a live world.
    /// </summary>
    public static void MapLoaded() => Ready = true;

    /// <summary>The map is unloading or the server is stopping — entities must not be accessed anymore.</summary>
    public static void MapGone() => Ready = false;
}
