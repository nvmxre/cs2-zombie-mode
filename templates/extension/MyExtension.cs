using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using ZombieMode.Api;

namespace MyExtension;

/// <summary>
/// Settings are created on first start in
/// addons/counterstrikesharp/configs/plugins/MyExtension/MyExtension.json.
/// </summary>
public sealed class MyExtensionConfig : BasePluginConfig
{
    /// <summary>How many players to list at the end of the round.</summary>
    [JsonPropertyName("top")] public int Top { get; set; } = 3;
}

/// <summary>
/// Example extension: counts the damage every human deals to zombies and announces the top players when the
/// round is decided. It shows the three things most extensions need — resolving the core, subscribing to its
/// events, and cleaning up on unload.
/// </summary>
public sealed class MyExtensionPlugin : BasePlugin, IPluginConfig<MyExtensionConfig>
{
    public override string ModuleName => "cs2-zombie-mode: top damage";
    public override string ModuleVersion => "0.1.0";

    public MyExtensionConfig Config { get; set; } = new();
    public void OnConfigParsed(MyExtensionConfig config) => Config = config;

    private IZombieCore? _core;

    /// <summary>Damage per player this round, keyed by slot: bots all have SteamID 0.</summary>
    private readonly Dictionary<int, (string Name, int Damage)> _damage = new();

    // Resolve the core here, not in Load(): plugins load in any order.
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _core = ZombieModeCapabilities.Core.Get();
        if (_core is null)
        {
            Logger.LogError("cs2-zombie-mode core is not loaded");
            return;
        }

        _core.ZombieDamaged += OnZombieDamaged;
        _core.PhaseChanged += OnPhaseChanged;
    }

    // Always unsubscribe: after a hot reload the old delegates would keep running.
    public override void Unload(bool hotReload)
    {
        if (_core is null) return;
        _core.ZombieDamaged -= OnZombieDamaged;
        _core.PhaseChanged -= OnPhaseChanged;
    }

    private void OnZombieDamaged(ZombieDamagedEvent e)
    {
        var slot = e.Attacker.Slot;
        var total = _damage.TryGetValue(slot, out var row) ? row.Damage : 0;
        _damage[slot] = (e.Attacker.PlayerName, total + e.Damage);
    }

    private void OnPhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.Hidden) _damage.Clear();
        if (phase != RoundPhase.Ended || _damage.Count == 0) return;

        var place = 1;
        foreach (var (name, damage) in _damage.Values.OrderByDescending(r => r.Damage).Take(Config.Top))
            Server.PrintToChatAll($" #{place++} {name} — {damage} damage to zombies");
    }
}
