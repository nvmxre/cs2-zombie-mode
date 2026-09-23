using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using ZombieMode.Abilities;
using Guard = ZombieMode.Abilities.Guard;
using ZombieMode.Api;
using ZombieMode.Config;

using CounterStrikeSharp.API.Modules.UserMessages;
using ZombieMode.Runtime;

namespace ZombieMode.Core;

/// <summary>
/// Custom infection mechanics: a hidden first infected, bleeding from bites, turning at zero HP.
///
/// How a round works:
///   • `Hidden` — a safe window to buy and take cover, then a random player turns into the first infected.
///     The fewer humans alive, the sooner it happens.
///   • `Active` — the outbreak. A bite does not turn you: it starts bleeding, and you turn
///     when HP reaches zero. Every new bite speeds up the bleeding.
///   • The round ends on its own: the infected are terrorists, humans are counter-terrorists, and the engine's
///     stock win conditions give exactly what we need. They are disabled during the hidden phase (otherwise the round
///     would end instantly: there are no infected yet) and re-enabled after the first turn.
///
/// Almost everything is built on game events and schema fields rather than signature hooks (the two exceptions are
/// HookWeaponPickup and HookTakeDamage), so game updates break it far less often than typical mods.
/// </summary>
public sealed class ZombieCore : IZombieCore
{
    public string Name => "pz";

    private readonly ZombieModeConfig _config;
    private readonly Action<string> _log;
    private readonly Random _random = new();

    private BasePlugin? _plugin;
    private Leap? _leap;
    private Guard? _guard;
    /// <summary>Slot → until when the player passes through others after turning.</summary>
    private readonly Dictionary<int, float> _passable = new();
    private bool _pickupHooked;
    private bool _useHooked;
    private bool _damageHooked;

    /// <summary>Whether a fake death event (the infection shown in the kill feed) is being fired right now.</summary>
    private bool _fakeDeath;
    /// <summary>
    /// How many weapon grants are in progress. While above zero, the pickup block is lifted: otherwise the core
    /// could not give an infected their own knife, and extensions could not hand out items (see <see cref="AllowWeaponGive"/>).
    /// </summary>
    private int _giving;

    public IDisposable AllowWeaponGive()
    {
        _giving++;
        return new GiveScope(this);
    }

    private sealed class GiveScope(ZombieCore owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (owner._giving > 0) owner._giving--;
        }
    }

    /// <summary>
    /// Whether character models may be set: a model not declared in the map manifest crashes the server on SetModel.
    /// The host plugin owns this flag; until it provides one, no models are set.
    /// </summary>
    public Func<bool> ModelsReady { get; set; } = () => false;

    /// <summary>Human-vs-infected damage modifiers in application order (<see cref="IDamageModifier.Order"/>).</summary>
    private readonly List<IDamageModifier> _modifiers = new();

    public void AddDamageModifier(IDamageModifier modifier)
    {
        if (_modifiers.Contains(modifier)) return;
        _modifiers.Add(modifier);
        _modifiers.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    public void RemoveDamageModifier(IDamageModifier modifier) => _modifiers.Remove(modifier);

    /// <summary>Player model before turning — restored at the start of the next round.</summary>
    private readonly Dictionary<int, string> _humanModel = new();

    /// <summary>When to repeat the screen effect for an infected player next.</summary>
    private readonly Dictionary<int, float> _nextScreenPulse = new();

    /// <summary>Infected tint, parsed once on load.</summary>
    private int _tintR = 150, _tintG = 60, _tintB = 55;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _tick;

    /// <summary>
    /// State is keyed by **player slot, not Steam ID**: bots all share a zero Steam ID,
    /// so keying by it would merge every bot into a single entry.
    /// </summary>
    private readonly HashSet<int> _infected = new();
    private int _firstInfected = -1;


    /// <summary>Bleeding: when the next tick is and the current interval (it shrinks with each new bite).</summary>
    private readonly Dictionary<int, Bleed> _bleeding = new();

    private double _roundStarted;
    private double _infectAt;
    private bool _firstInfectionDone;

    /// <summary>
    /// Real infected health when spoofing (`hpSpoof`): slot → (health, max). The pawn carries the
    /// displayed number (at most hpSpoofShown); before damage the real value is put back on the pawn (SpoofBefore),
    /// after damage the engine's remainder is stored here and the pawn gets the displayed value again (OnTakeDamagePost).
    /// </summary>
    private readonly Dictionary<int, (int Hp, int Max)> _shadow = new();

    /// <summary>
    /// Slots whose real health was restored before damage (SpoofBefore). Only for these does OnTakeDamagePost
    /// accept the post-damage remainder as real. Without this mark, any early exit from the hook without a restore
    /// (berserk, infected hitting infected) left the displayed 999 on the pawn, the engine subtracted zero from it,
    /// and the post hook stored 999 as the real value: 4300/5000 turned into 999 (and 1499 after a bite heal).
    /// </summary>
    private readonly HashSet<int> _restored = new();

    /// <summary>
    /// Infected slot → time of the last damage taken (Server.CurrentTime); on turning — the moment of the turn.
    /// Regeneration counts from it, see RegenTick.
    /// </summary>
    private readonly Dictionary<int, double> _lastHurt = new();

    /// <summary>Infected slot → when the next regeneration step happens (once per second with a 0.25 s tick).</summary>
    private readonly Dictionary<int, double> _regenNext = new();

    /// <summary>
    /// When the first infected will turn (server time), until it happens; 0 — not scheduled or already
    /// happened. Pre-outbreak features (such as a buy window and its timer) can close exactly at this moment.
    /// The 10…1 countdown sound announces the moment to everyone anyway.
    /// </summary>
    public double FirstInfectionAt => _firstInfectionDone || _infectAt <= 0 ? 0 : _infectAt;

    /// <summary>When we last announced that we are waiting for players, so it is not repeated every three seconds.</summary>
    private double _waitingSaid;
    private bool _roundEnded;

    /// <summary>When we asked the engine to end the round. 0 — not asked. Used by the end-of-round watchdog.</summary>
    private double _endRequestedAt;

    /// <summary>How many times the end-of-round request was repeated.</summary>
    private int _endRetries;

    /// <summary>Whether the "round time is running out" warning was already shown.</summary>
    private bool _limitWarned;

    // ── air strike (see CheckRoundLimit and Explode) ──
    private bool _strikeJetPlayed;
    /// <summary>Scene test via command: when to detonate (0 — not waiting).</summary>
    private double _strikeTestAt;
    /// <summary>Until this expires humans take no damage: shields them from the blast of our own strike.</summary>
    private double _strikeShieldUntil;
    /// <summary>When to finish off the infected after the blast (0 — not waiting) and whether to end the round then.</summary>
    private double _strikeFinishAt;
    private bool _strikeFinishEnds;
    /// <summary>When to detonate at the round limit — after the round has already ended with a human win.</summary>
    private double _strikeExplodeAt;
    /// <summary>The future first infected (slot), picked at the stinger moment; −1 — not picked yet.</summary>
    private int _chosen = -1;
    /// <summary>When the "victim" gets the stinger: end of the round-start music, but no later than one second before turning.</summary>
    private double _stingerAt;
    /// <summary>Which countdown number has already been played.</summary>
    private int _countdownSaid;
    /// <summary>Per slot: when the zombie growls next and when it may groan in pain again.</summary>
    private readonly Dictionary<int, double> _idleAt = new();
    private readonly Dictionary<int, double> _painAt = new();
    private readonly Dictionary<int, double> _attackAt = new();
    private readonly Dictionary<int, double> _burnAt = new();
    /// <summary>When to shake humans: the bomb goes off 0.1 s after it is created, the shake goes with it.</summary>
    private double _strikeShakeAt;
    /// <summary>Effect entities and when to remove them: particles and shake.</summary>
    private readonly List<(CBaseEntity Entity, double Until)> _strikeFx = new();
    /// <summary>Supplies the map center for the air strike (e.g. the centroid of points spread across the map); null — centroid of living players.</summary>
    public Func<Vector?>? StrikeTarget { get; set; }

    /// <summary>The reason we are ending with — the watchdog needs it for retries.</summary>
    private RoundEndReason _endReason = RoundEndReason.RoundDraw;
    private int _lastRoundFirst = -1;

    private sealed class Bleed
    {
        public double NextTick;
        public double Interval;
        /// <summary>Slot of the last biter: they get credit for the infection when the victim turns.</summary>
        public int Attacker = -1;

        /// <summary>When to restart the heartbeat clip (see HeartbeatTick).</summary>
        public double HeartbeatAt;

        /// <summary>How much HP bleeding has taken so far. Used only for the debug log.</summary>
        public int Taken;

        /// <summary>
        /// Health we set on the previous bleed step. While the wound is open only we set health,
        /// so any INCREASE means one thing: the player used a Medi-Shot.
        /// That is how bleeding is stopped — no event and no signatures needed.
        /// </summary>
        public int LastHealth = -1;
    }

    public event Action<InfectedEvent>? Infected;
    public event Action<InfectedKilledEvent>? InfectedKilled;
    public event Action<ZombieDamagedEvent>? ZombieDamaged;
    public event Action<RoundPhase>? PhaseChanged;

    public RoundPhase Phase =>
        _roundEnded ? RoundPhase.Ended
        : _firstInfectionDone ? RoundPhase.Outbreak
        : _infectAt > 0 ? RoundPhase.Hidden
        : RoundPhase.Waiting;

    /// <summary>Last phase reported through <see cref="PhaseChanged"/>; checked once per tick.</summary>
    private RoundPhase _reportedPhase = RoundPhase.Waiting;

    private void ReportPhase()
    {
        var phase = Phase;
        if (phase == _reportedPhase) return;
        _reportedPhase = phase;
        try { PhaseChanged?.Invoke(phase); }
        catch (Exception e) { _log($"PhaseChanged handler failed: {e.Message}"); }
    }

    public INoticeSink Notices { get => Texts.Sink; set => Texts.Sink = value ?? new CenterAlertSink(); }

    public Func<CCSPlayerController, bool>? MusicMuted { get; set; }


    public ZombieCore(ZombieModeConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public void Start(BasePlugin plugin)
    {
        _plugin = plugin;

        var tint = _config.Infection.ZombieTint.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tint.Length == 3 && int.TryParse(tint[0], out var r) && int.TryParse(tint[1], out var g) && int.TryParse(tint[2], out var b))
        {
            _tintR = r;
            _tintG = g;
            _tintB = b;
        }
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        // One shared tick for everyone instead of a timer per player — cheaper with 64 players.
        _tick = plugin.AddTimer(0.25f, Tick, TimerFlags.REPEAT);
        HookWeaponPickup(plugin);
        HookTakeDamage(plugin);
        // The side is chosen before spawning, so it must be intercepted there. Otherwise the engine manages to respawn
        // the player as a terrorist with its own spawn point and pistol, and our switch arrives a frame later
        // and finds them already standing in the wrong place.
        plugin.AddCommandListener("jointeam", OnJoinTeam);

        // Leap for all infected: full strength for the first infected, a fraction of it for regular ones.
        // Rage (Guard) is still first-infected only.
        _leap = new Leap(_config, p => _infected.Contains(p.Slot), p => p.Slot == _firstInfected, _log);
        _leap.Start(plugin);

        // First infected stance: immunity to knockback from guns, on the R key.
        _guard = new Guard(_config, p => p.Slot == _firstInfected && _infected.Contains(p.Slot), _log);
        _leap!.Used += p => AbilityUsed?.Invoke(new AbilityUsedEvent(p, "leap"));
        _guard.Used += p => AbilityUsed?.Invoke(new AbilityUsedEvent(p, "rage"));
        _guard.Start(plugin);

        // The plugin may be reloaded mid-round (css_plugins reload) — then all infection state
        // is reset while players stay on their sides. The round then hangs: we no longer
        // wait for the first infected, but don't check for a win either, because "there was no first yet".
        // So on start we adopt whatever is already on the server.
        Server.NextWorldUpdate(AdoptRunningRound);

        // The model must be precached at map start, otherwise SetModel silently fails.
        plugin.RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            var model = _config.Infection.ZombieModel;
            if (!string.IsNullOrWhiteSpace(model)) manifest.AddResource(model);
        });
    }

    public void Stop(BasePlugin plugin)
    {
        _tick?.Kill();
        _tick = null;
        _leap?.Stop(plugin);
        // The stance listens to OnTick on its own — always remove it. This used to sit inside the
        // "damage hooks installed" branch, and without them the stance listener survived unload.
        _guard?.Stop(plugin);
        plugin.DeregisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.DeregisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        plugin.DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        plugin.DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.DeregisterEventHandler<EventWeaponFire>(OnWeaponFire);
        plugin.RemoveCommandListener("jointeam", OnJoinTeam, HookMode.Pre);

        // Signature hooks are removed manually: BasePlugin does not own them, and they survive
        // unload. Without this every hot reload stacked another hook on top of the previous
        // one — damage was processed two or three times (one hit was logged three times
        // after three reloads).
        if (_pickupHooked)
        {
            try { VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Unhook(OnCanAcquire, HookMode.Pre); }
            catch (Exception e) { _log($"pickup hook was not removed: {e.Message}"); }
            _pickupHooked = false;
        }

        if (_useHooked)
        {
            try { VirtualFunctions.CCSPlayer_WeaponServices_CanUseFunc.Unhook(OnCanUse, HookMode.Pre); }
            catch (Exception e) { _log($"use hook was not removed: {e.Message}"); }
            _useHooked = false;
        }

        if (_damageHooked)
        {
            try { VirtualFunctions.TerminateRoundFunc.Unhook(OnTerminateRound, HookMode.Pre); } catch { /* was not hooked */ }
            try { VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Unhook(OnTakeDamage, HookMode.Pre); }
            catch (Exception e) { _log($"damage hook was not removed: {e.Message}"); }
            try { VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Unhook(OnTakeDamagePost, HookMode.Post); } catch { /* was not hooked */ }
            _damageHooked = false;
        }
    }

    public bool IsInfected(CCSPlayerController player) => player.IsValid && _infected.Contains(player.Slot);
    /// <summary>A player used an ability: "leap" or "rage" — for newcomer hints.</summary>
    public event Action<AbilityUsedEvent>? AbilityUsed;

    public bool IsBleeding(CCSPlayerController player) => player.IsValid && _bleeding.ContainsKey(player.Slot);

    // By slot — for frequent polling by extensions (infected vision), without looking up the controller.
    public bool IsInfectedSlot(int slot) => _infected.Contains(slot);
    public bool IsFirstSlot(int slot) => slot == _firstInfected;
    public bool IsBleedingSlot(int slot) => _bleeding.ContainsKey(slot);
    public bool IsBerserk(CCSPlayerController player) => player.IsValid && IsBerserk(player.Slot);

    /// <summary>This round's first infected, already turned (for the aura).</summary>
    public bool IsFirst(int slot) => _firstInfectionDone && slot == _firstInfected && _infected.Contains(slot);
    /// <summary>Berserk (the stance) is active right now.</summary>
    public bool IsBerserk(int slot) => _guard?.Active(slot) == true;

    /// <summary>
    /// Infected ability cooldowns for the HUD: seconds left on the leap and on the stance.
    /// `Visible = false` — not infected or dead, nothing to show. `First` — the first infected:
    /// only they have the stance (R), regular infected only have the leap, so for them `Rage`
    /// is always 0 and the HUD hides the rage icon.
    /// </summary>
    public AbilityState Abilities(CCSPlayerController player)
    {
        if (!player.IsValid || !_infected.Contains(player.Slot))
            return default;

        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || pawn.Health <= 0) return default;

        var first = player.Slot == _firstInfected;
        return new AbilityState(true, first, _leap?.Cooldown(player) ?? 0f, first ? _guard?.Cooldown(player) ?? 0f : 0f);
    }

    /// <summary>Stance on command, without cooldown. Infected only: humans have no stance.</summary>
    public bool ForceGuard(CCSPlayerController player)
    {
        // First infected only: berserk is their ability, regular infected have nothing on R.
        if (!player.IsValid || !_infected.Contains(player.Slot) || player.Slot != _firstInfected || _guard is null) return false;
        _guard.Force(player);
        return true;
    }

    public bool Raging(CCSPlayerController player) =>
        player.IsValid && _guard?.Active(player.Slot) == true;

    /// <summary>
    /// Remove the hit slowdown if the player is in the stance. Called from the damage handler: this is the second
    /// moment where the engine lowers the speed multiplier (see Guard.Unslow).
    /// </summary>
    public void UnslowIfGuarding(CCSPlayerController player)
    {
        if (_guard?.Active(player.Slot) != true) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return;

        _guard.Unslow(pawn, player);
        _guard.Restore(pawn, player);
    }

    /// <summary>Whether the player holds the stance: gun knockback does not affect them.</summary>
    public bool IsGuarding(CCSPlayerController player) => player.IsValid && _guard?.Active(player.Slot) == true;

    public bool IsFirstInfected(CCSPlayerController player) => player.IsValid && player.Slot == _firstInfected;

    // ── hooks for game modes built on top (e.g. an arena mode) ────────────────────────────────────
    // An arena is the same infection round, only the first infected is assigned in advance (e.g. an armored bot)
    // and arrives quickly. The mode does not run its own round: everything the core does works as is.

    /// <summary>Pick the first infected from living humans. null — default pick.</summary>
    public Func<List<CCSPlayerController>, CCSPlayerController?>? PickFirst { get; set; }

    /// <summary>Seconds from round start until the first infected turns. null — default timings.</summary>
    public Func<double?>? InfectDelay { get; set; }

    /// <summary>Set an infected player's health, accounting for displayed-health spoofing.</summary>
    public void SetHealth(CCSPlayerController player, int hp, int max)
    {
        if (player.IsValid && _infected.Contains(player.Slot)) SetZombieHealth(player, hp, max);
    }

    /// <summary>End the round with a custom announcement. A repeated call in the same round does nothing.</summary>
    public void EndRound(RoundEndReason reason, string message)
    {
        if (_roundEnded) return;
        Terminate(reason, _ => message);
    }

    /// <summary>The round is already over (end announced) — abilities have nothing left to do.</summary>
    public bool RoundOver => _roundEnded;

    /// <summary>Infect a player on command — for testing mechanics.</summary>
    public bool Infect(CCSPlayerController player, bool first) => ForceInfect(player, first);

    public bool ForceInfect(CCSPlayerController player, bool first)
    {
        if (!player.IsValid || _infected.Contains(player.Slot)) return false;

        if (first)
        {
            _firstInfectionDone = true;
            _firstInfected = player.Slot;
            _lastRoundFirst = player.Slot;
        }
        Convert(player, first, attacker: null);
        // The infected side is now occupied — apply the same cvars as InfectFirst.
        if (first)
            foreach (var (cvar, value) in _config.CvarsOnInfection)
                Server.ExecuteCommand($"{cvar} {value}");
        return true;
    }

    /// <summary>
    /// Adopt a round already in progress: everyone on the infected side counts as infected,
    /// the first one found becomes the first infected. Called once on plugin load.
    /// </summary>
    private void AdoptRunningRound()
    {
        // Runs via Server.NextWorldUpdate from plugin load, and on a cold start that is still map
        // loading: without this check the first access to players would poison the entity pointer forever
        // (see World). There is nothing to adopt on a cold start anyway — no round is running.
        if (!World.Ready || PlayersOnTeams() == 0) return;

        var now = Server.CurrentTime;
        if (_roundStarted <= 0) _roundStarted = now;

        var infected = AlivePlayers(CsTeam.Terrorist);
        var anyOnT = Utilities.GetPlayers().Any(p => p.IsValid && p.Team == CsTeam.Terrorist);

        if (anyOnT)
        {
            foreach (var p in infected)
            {
                _infected.Add(p.Slot);
                if (_firstInfected < 0) _firstInfected = p.Slot;
            }

            // The infected side is occupied — so the first turn this round already happened, even if all
            // infected are dead by now. Otherwise the win check would never be enabled.
            _firstInfectionDone = true;
            _log($"round adopted after reload: infected alive {infected.Count}");
            return;
        }

        // No infected yet. We missed the round start event, so the first turn time is not
        // scheduled — without it the round stalls: nobody to turn, nothing to end it. Schedule it again
        // from the number of living humans, like a normal round start does.
        var humans = AlivePlayers(CsTeam.CounterTerrorist).Count;
        var timing = _config.Infection.TimingFor(humans, Server.MapName);
        _infectAt = now + Math.Max(timing.Safe, timing.WindowMin);
        _log($"round adopted after reload: no infected, first in {_infectAt - now:0} s");
    }

    /// <summary>
    /// Round state for the `pz_round` debug command. Rounds sometimes stop ending,
    /// and without these numbers the causes are indistinguishable: "humans are still counted as alive" or
    /// "the round is already marked as ended and the check is off".
    /// </summary>
    public string RoundStatus()
    {
        var humans = AlivePlayers(CsTeam.CounterTerrorist).Count;
        var infected = AlivePlayers(CsTeam.Terrorist).Count;
        var onTeams = PlayersOnTeams();
        var left = _roundStarted > 0 ? Server.CurrentTime - _roundStarted : 0;

        var lines = new List<string>
        {
            $"first turn happened: {_firstInfectionDone} · round marked ended: {_roundEnded}",
            $"humans alive: {humans} · infected alive: {infected} · total on teams: {onTeams}",
            $"round running: {left:0} s · end requests: {_endRetries} · awaiting response: {_endRequestedAt > 0}",
        };

        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid) continue;
            var pawn = p.PlayerPawn.Value;
            var team = p.Team == CsTeam.CounterTerrorist ? "CT" : p.Team == CsTeam.Terrorist ? "T" : "-";
            lines.Add($"  {p.PlayerName}: {team}, alive={IsAlive(p)}, HP={(pawn is not null && pawn.IsValid ? pawn.Health : 0)}, infected={_infected.Contains(p.Slot)}");
        }

        return string.Join("\n", lines);
    }


    public void ResetRound()
    {
        _infected.Clear();
        _shadow.Clear();
        _restored.Clear();
        _lastHurt.Clear();
        _regenNext.Clear();
        _bleeding.Clear();
        _nextScreenPulse.Clear();
        _firstInfected = -1;
        _firstInfectionDone = false;
        _waitingSaid = 0;
        _passable.Clear();
        _guard?.Reset();
        _roundEnded = false;
        _endRequestedAt = 0;
        _endRetries = 0;
        _limitWarned = false;
        _strikeJetPlayed = false;
        _strikeTestAt = 0;
        _strikeFinishAt = 0;
        _strikeFinishEnds = false;
        _strikeShakeAt = 0;
        _strikeExplodeAt = 0;
        _chosen = -1;
        _stingerAt = 0;
        _countdownSaid = 0;
        _idleAt.Clear();
        _painAt.Clear();
        _attackAt.Clear();
        _burnAt.Clear();
    }

    // ── teams ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Everyone joins as a human (CT), and an infected player cannot change sides: you cannot leave the infection at will.
    /// Spectating is allowed.
    /// </summary>
    private HookResult OnJoinTeam(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid || player.IsBot) return HookResult.Continue;

        var requested = command.ArgCount > 1 && int.TryParse(command.GetArg(1), out var team) ? (CsTeam)team : CsTeam.None;
        if (requested == CsTeam.Spectator) return HookResult.Continue;

        // An infected player cannot change sides: you cannot leave the infection at will.
        if (IsInfected(player)) return HookResult.Stop;

        if (requested == CsTeam.CounterTerrorist) return HookResult.Continue;

        // `ChangeTeam`, not `SwitchTeam`: the player is not in the game yet and must actually be assigned to a team,
        // so that the engine picks the human spawn point and loadout itself.
        player.ChangeTeam(CsTeam.CounterTerrorist);
        return HookResult.Stop;
    }

    // ── round ──────────────────────────────────────────────────────────────────────────────────

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ResetRound();
        RestoreHumans();
        _leap?.Reset();
        _roundStarted = Server.CurrentTime;

        // Stock win conditions are disabled ONLY for the window before the first turn. While the infected side
        // is empty, the engine would end the round instantly: a team with no living players loses.
        // Once the first infected appears the block is lifted, and from then on the engine ends the round itself
        // (see InfectFirst).
        //
        // Previously the block stayed on for the whole round, and only our own `TerminateRound` could end it — with a watchdog,
        // retries and a whole litter of bugs: a dead CT bot kept the round alive forever, the round went on after the last
        // human died from a fall, a plugin reload hung it until the map changed. The stock mechanism is more
        // reliable than ours.
        Server.ExecuteCommand("mp_ignore_round_win_conditions 1");

        var humans = AlivePlayers(CsTeam.CounterTerrorist).Count;
        var timing = _config.Infection.TimingFor(humans, Server.MapName);
        var delay = timing.Safe + _random.NextDouble() * Math.Max(0, timing.WindowMax - timing.WindowMin) + (timing.WindowMin - timing.Safe);
        _infectAt = _roundStarted + Math.Max(timing.Safe, delay);
        var forced = InfectDelay?.Invoke();
        if (forced is not null) _infectAt = _roundStarted + Math.Max(1.0, forced.Value);
        // The 10…1 countdown waits for the victim's stinger (CountdownTick), so the stinger must play earlier than
        // `countdownFrom` seconds before turning: with the stinger at 19.5 s and the turn at 26 s the countdown
        // started at six.
        var lead = _config.Sounds.Countdown ? _config.Sounds.CountdownFrom + 1.0 : 1.0;
        _stingerAt = Math.Min(_roundStarted + Math.Max(0.0, _config.Sounds.StingerDelay), _infectAt - lead);

        if (_config.Debug)
            _log($"round started: humans {humans}, safe {timing.Safe:0} s, first infected in {_infectAt - _roundStarted:0} s");

        return HookResult.Continue;
    }

    /// <summary>
    /// Custom health display for the infected. The stock CS2 indicator is built for three digits and
    /// wraps onto two lines in the thousands, so we show the real value ourselves.
    /// It also shows the leap cooldown — the first infected needs that constantly.
    /// </summary>
    public string? Hud(CCSPlayerController player)
    {
        if (!player.IsValid || !_infected.Contains(player.Slot)) return null;

        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || pawn.Health <= 0) return null;

        // Keep lines to a minimum: PrintToCenter shrinks the font to fit the line count, and with four lines
        // it becomes unreadable. So the infected see only health
        // and the leap — the human and bleeding counters were removed from here.
        var (hp, maxHp) = RealHealth(player) ?? (pawn.Health, pawn.MaxHealth);
        var text = $"{hp} / {maxHp}";

        // Abilities are written here too. A HUD panel extension may show them in color, but it reaches
        // players via a Workshop addon — whoever does not have it would see the cooldown nowhere
        // without this line. One line, not two: PrintToCenter shrinks the font by line count.
        // All infected have the leap, only the first infected has the stance.
        if (_infected.Contains(player.Slot))
        {
            var leap = _leap?.Cooldown(player) ?? 0f;
            var rage = _guard?.Cooldown(player) ?? 0f;

            // About asterisks instead of text (e.g. "Leap — *********** — R" on a player's screen): the first
            // guess was the middle dot and long dashes, so they were replaced with a vertical bar. THAT WAS WRONG:
            // the asterisks stayed with plain characters too.
            //
            // The real cause is on the client, not the server: CS2 has text filtering
            // (Settings → Game → Communication → Text filtering), and it masks not only
            // profanity but perfectly harmless words too. The server cannot affect this: the string is sent
            // intact and the client masks it. The only fix is the player turning the setting off.
            //
            // Hence the rule: if text disappears for ONE player while everyone else sees it, look
            // at their settings, not at our string. The bar instead of the dot stayed —
            // it reads just as well, no reason to change it back.
            text += "\n" + (leap <= 0.05f ? Texts.For(player, "hud.leap.ready") : Texts.For(player, "hud.leap.cooldown", leap.ToString("0")));
            if (player.Slot == _firstInfected)
                text += " | " + (rage <= 0.05f ? Texts.For(player, "hud.rage.ready") : Texts.For(player, "hud.rage.cooldown", rage.ToString("0")));
        }

        return text;
    }

    /// <summary>
    /// Safety net: if the engine or a respawn reset the infected look, restore it. The check is cheap —
    /// a color comparison, no writes while everything is in place.
    /// </summary>
    private void KeepZombieLook()
    {
        foreach (var slot in _infected)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is null || pawn is null || !pawn.IsValid || pawn.Health <= 0) continue;
            // The screen effect fades on its own. Repeat it at a steady rhythm, not every tick:
            // otherwise the flashes go out of sync.
            // The panel layer and the flashes do the same thing, but the layer does not fight exposure.
            // When the layer is on, the flashes stay off, otherwise the same washed-out picture comes back.
            if (_config.Infection.ZombieScreenEffect && !_config.Infection.ZombieVisionLayer)
            {
                var now = Server.CurrentTime;
                if (!_nextScreenPulse.TryGetValue(slot, out var next) || now >= next)
                {
                    _nextScreenPulse[slot] = now + (float)_config.Infection.ScreenEffectInterval;
                    pawn.HealthShotBoostExpirationTime = now + (float)_config.Infection.ScreenEffectInterval;
                    Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flHealthShotBoostExpirationTime");
                }
            }

            if (pawn.Render.R == _tintR && pawn.Render.G == _tintG && pawn.Render.B == _tintB) continue;
            ApplyZombieLook(player);
        }
    }

    /// <summary>
    /// Adopt the round on the fly.
    ///
    /// All our checks — both the win and the time limit — count from the round start.
    /// If we missed the start event (the plugin loaded mid-round while the server
    /// was empty), that moment stays zero and both checks are silent. The engine is not allowed to end
    /// rounds, so the round would never end: players join, bots spawn
    /// dead, and nothing happens.
    ///
    /// So as soon as someone appears on a team and we never saw the round start —
    /// assume the round runs from this second.
    /// </summary>
    private void AdoptOnTheFly(double now)
    {
        if (_roundStarted > 0 || PlayersOnTeams() == 0) return;

        _roundStarted = now;
        if (!_firstInfectionDone && _infectAt <= 0)
        {
            var timing = _config.Infection.TimingFor(AlivePlayers(CsTeam.CounterTerrorist).Count, Server.MapName);
            _infectAt = now + Math.Max(timing.Safe, timing.WindowMin);
        }
        _log($"round adopted on the fly: first infected in {_infectAt - now:0} s");
    }

    /// <summary>
    /// Win conditions: the round ends only with a complete victory. A backup for the engine's own conditions,
    /// which are off during the hidden phase and back on after the first infection (<c>cvarsOnInfection</c>).
    /// </summary>
    private void CheckWinConditions()
    {
        var humans = AlivePlayers(CsTeam.CounterTerrorist).Count;
        var infected = AlivePlayers(CsTeam.Terrorist).Count;

        if (humans + infected == 0)
        {
            // Nobody alive at all. There used to be an unconditional return here — protection for an empty server
            // where restarts would loop and get in the way of joining. But it looked at the living, not at those
            // present: once everyone died the round hung forever, and a joining player sat
            // in spectators without a single bot. Now the server counts as empty when nobody
            // is on a team, not when everyone is dead.
            if (PlayersOnTeams() > 0 && Server.CurrentTime - _roundStarted > 10.0)
                Terminate(RoundEndReason.RoundDraw, "round.nobody");
            return;
        }

        // No infected left — humans win. There is deliberately no separate watchdog for "what if the zombie
        // didn't die but vanished" here anymore.
        //
        // It was needed while we decided the end of the round ourselves. Now the engine does (see InfectFirst),
        // and the watchdog started doing harm: `bot_quota_mode fill` keeps kicking and adding bots, slots
        // are reused, and an infected bot disappearing is indistinguishable from "it was never killed". Because
        // of that, a player who killed the last zombie became the first infected himself right at the end of
        // the round — the watchdog decided the start had failed and picked a new one.
        //
        // The case it was added for — the subject vanishing at the moment of turning — is handled where
        // it happens: InfectFirst iterates over candidates and verifies the turn took place.
        // If an infected disappears mid-round, the engine simply awards the win to humans, and that is
        // fair: there are no zombies on the map.
        if (infected == 0) Terminate(RoundEndReason.CTsWin, "round.humans");
        else if (humans == 0) Terminate(RoundEndReason.TerroristsWin, "round.zombies");
    }

    /// <summary>
    /// Round time limit: the infected get a limited time to infect everyone.
    /// If they fail they die and humans win. Without this, a round where the last human barricaded
    /// themselves somewhere the infected cannot reach drags on forever.
    /// </summary>
    private void CheckRoundLimit(double now)
    {
        var limit = _config.Infection.RoundLimitSeconds;
        if (limit <= 0 || _roundStarted <= 0) return;

        var left = _roundStarted + limit - now;

        // Warn once per round: players cannot read a hidden timer.
        var warn = _config.Infection.RoundLimitWarning;
        if (!_limitWarned && warn > 0 && left <= warn)
        {
            _limitWarned = true;
            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid))
                Texts.To(p, "round.time_left", left.ToString("0"));
        }

        // The aircraft is heard a few seconds before the bomb: humans know what's coming, and so do the infected.
        var strike = _config.Infection.StrikeEnabled;
        if (strike && !_strikeJetPlayed && left <= _config.Infection.StrikeLead && AlivePlayers(CsTeam.Terrorist).Count > 0)
        {
            _strikeJetPlayed = true;
            Sound.ToEveryone(_config.Infection.StrikeJetEvent, _config.Infection.StrikeJetVolume);
        }

        if (left > 0) return;

        var infected = AlivePlayers(CsTeam.Terrorist);
        var humans = AlivePlayers(CsTeam.CounterTerrorist).Count;

        // An empty server does not end rounds: restarts would loop and get in the way of joining.
        // Empty means nobody on the teams, not nobody alive.
        if (infected.Count + humans == 0)
        {
            if (PlayersOnTeams() > 0) Terminate(RoundEndReason.RoundDraw, "round.nobody");
            return;
        }

        if (strike)
        {
            // First end the round with a human win, then the bomb: otherwise the explosion itself
            // ended the round as "target bombed" in favor of T. The bomb goes off 0.7 s later, already during
            // the round-end delay; infected the blast did not reach are finished off after strikeFinishSeconds.
            if (_strikeExplodeAt > 0 || _strikeFinishAt > 0) return;
            Terminate(RoundEndReason.CTsWin, "round.strike");
            _strikeExplodeAt = now + 0.7;
            _strikeFinishAt = now + Math.Max(1.0, _config.Infection.StrikeFinishSeconds);
            _strikeFinishEnds = false;
            _log($"round limit {limit:0} s: air strike, infected {infected.Count}, humans survived {humans}");
            return;
        }

        foreach (var p in infected)
        {
            var pawn = p.PlayerPawn.Value;
            if (pawn is not null && pawn.IsValid) pawn.CommitSuicide(false, true);
        }

        _log($"round limit {limit:0} s: infected died {infected.Count}, humans survived {humans}");
        Terminate(RoundEndReason.CTsWin, "round.timeout");
    }

    /// <summary>
    /// Explosion: one bomb (or particle effect) at <see cref="StrikeTarget"/> or the middle of the map, sound and
    /// screen shake for humans. Killing the infected is up to the caller.
    /// </summary>
    private void Explode(List<CCSPlayerController> infected, double now)
    {
        var c = _config.Infection;

        // A single bomb in the middle of the map. The middle comes from StrikeTarget (points spread across
        // the map by hand); without it — the centroid of living players.
        var at = StrikeTarget?.Invoke() ?? Centroid(AlivePlayers(CsTeam.CounterTerrorist).Concat(infected));
        var placed = 0;
        var blasts = 0;
        var bomb = false;
        if (at is not null && c.StrikeC4)
        {
            // A real bomb with a zero timer: since the CS2 update of July 8, 2026 the engine itself propagates the
            // blast wave across the map from the epicenter, blocks it behind corners and shows a damage preview.
            _strikeShieldUntil = now + 6.0;
            try
            {
                var c4 = Utilities.CreateEntityByName<CPlantedC4>("planted_c4");
                if (c4 is not null && c4.IsValid)
                {
                    c4.DispatchSpawn();
                    c4.Teleport(new Vector(at.X, at.Y, at.Z + (float)c.StrikeBombZ), null, null);
                    // Players must not see the bomb itself. Hide it via rendering, not
                    // height: the new explosion's wave follows geometry and does not pass through the floor, and damage
                    // is computed from baked map zones — under the map the bomb would do nothing.
                    c4.RenderMode = RenderMode_t.kRenderNone;
                    c4.Render = System.Drawing.Color.FromArgb(0, 255, 255, 255);
                    Utilities.SetStateChanged(c4, "CBaseModelEntity", "m_nRenderMode");
                    Utilities.SetStateChanged(c4, "CBaseModelEntity", "m_clrRender");
                    c4.BombTicking = true;
                    c4.BeingDefused = false;
                    c4.TimerLength = 0.1f;
                    c4.C4Blow = (float)now + 0.1f;
                    Utilities.SetStateChanged(c4, "CPlantedC4", "m_bBombTicking");
                    Utilities.SetStateChanged(c4, "CPlantedC4", "m_flC4Blow");
                    bomb = true;
                }
            }
            catch (Exception e) { _log($"air strike: bomb not created — {e.Message}; fallback explosion"); }
        }
        if (at is not null && !bomb)
        {
            if (!string.IsNullOrWhiteSpace(c.StrikeParticle))
            {
                try
                {
                    var fx = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
                    if (fx is not null && fx.IsValid)
                    {
                        fx.EffectName = c.StrikeParticle;
                        fx.StartActive = true;
                        fx.DispatchSpawn();
                        fx.Teleport(new Vector(at.X, at.Y, at.Z + 8f), null, null);
                        _strikeFx.Add((fx, now + 6));
                        placed++;
                    }
                }
                catch (Exception e) { _log($"air strike: particle not created — {e.Message}"); }
            }

            // Fallback explosion without a bomb: a custom sound and an engine explosion with a map-wide radius — push and damage.
            Sound.ToEveryone(c.StrikeExplodeEvent, c.StrikeExplodeVolume);
            if (c.StrikeDamage > 0)
            {
                _strikeShieldUntil = now + 2.0;
                try
                {
                    var blast = Utilities.CreateEntityByName<CEnvExplosion>("env_explosion");
                    if (blast is not null && blast.IsValid)
                    {
                        blast.Magnitude = c.StrikeDamage;
                        blast.RadiusOverride = c.StrikeRadius;
                        blast.DispatchSpawn();
                        blast.Teleport(new Vector(at.X, at.Y, at.Z + 16f), null, null);
                        blast.AcceptInput("Explode");
                        _strikeFx.Add((blast, now + 2));
                        blasts++;
                    }
                }
                catch (Exception e) { _log($"air strike: blast wave not created — {e.Message}"); }
            }
        }

        // Screen shake for humans only: the wave removes the infected anyway.
        // With a real bomb — not now but at the moment of detonation: the bomb goes off 0.1 s later, and a shake
        // before the blast was noticeable.
        if (bomb) _strikeShakeAt = now + 0.25;
        else ShakeHumans();

        _log($"air strike: infected {infected.Count}, center {(at is null ? "none" : $"{at.X:0} {at.Y:0} {at.Z:0}")}, bomb {bomb}, particles {placed}, waves {blasts}");
    }

    /// <summary>Screen shake for all humans — via a user message to clients; not sent to the infected.</summary>
    private void ShakeHumans()
    {
        var c = _config.Infection;
        if (c.StrikeShake > 0)
        {
            try
            {
                var shake = UserMessage.FromPartialName("Shake");
                shake.SetUInt("command", 0);
                shake.SetFloat("amplitude", (float)c.StrikeShake);
                shake.SetFloat("frequency", 2.5f);
                shake.SetFloat("duration", (float)c.StrikeShakeSeconds);
                var sent = 0;
                foreach (var p in Utilities.GetPlayers())
                {
                    if (!p.IsValid || p.IsBot || _infected.Contains(p.Slot)) continue;
                    shake.Recipients.Add(p);
                    sent++;
                }
                if (sent > 0) shake.Send();
            }
            catch (Exception e) { _log($"air strike: shake not sent — {e.Message}"); }
        }
    }

    /// <summary>Centroid of pawn positions; null — nobody.</summary>
    private static Vector? Centroid(IEnumerable<CCSPlayerController> players)
    {
        float x = 0, y = 0, z = 0;
        var n = 0;
        foreach (var p in players)
        {
            var pawn = p.PlayerPawn.Value;
            var at = pawn is not null && pawn.IsValid ? pawn.AbsOrigin : null;
            if (at is null) continue;
            x += at.X; y += at.Y; z += at.Z; n++;
        }
        return n == 0 ? null : new Vector(x / n, y / n, z / n);
    }

    /// <summary>The whole scene via the `pz_strike` command: aircraft now, explosion after strikeLead; the infected die, the round does not end.</summary>
    public void StrikeTest(double now)
    {
        var c = _config.Infection;
        Sound.ToEveryone(c.StrikeJetEvent, c.StrikeJetVolume);
        _strikeTestAt = now + Math.Max(0.5, c.StrikeLead);
    }

    /// <summary>Carry the scene test through to the explosion and remove expired effects.</summary>
    private void StrikeTick(double now)
    {
        if (_strikeTestAt > 0 && now >= _strikeTestAt)
        {
            _strikeTestAt = 0;
            Explode(AlivePlayers(CsTeam.Terrorist), now);
            _strikeFinishAt = now + Math.Max(0.5, _config.Infection.StrikeFinishSeconds);
            _strikeFinishEnds = false;
        }

        if (_strikeExplodeAt > 0 && now >= _strikeExplodeAt)
        {
            _strikeExplodeAt = 0;
            Explode(AlivePlayers(CsTeam.Terrorist), now);
        }

        if (_strikeShakeAt > 0 && now >= _strikeShakeAt)
        {
            _strikeShakeAt = 0;
            ShakeHumans();
        }

        if (_strikeFinishAt > 0 && now >= _strikeFinishAt)
        {
            _strikeFinishAt = 0;
            var left = AlivePlayers(CsTeam.Terrorist);
            foreach (var p in left)
            {
                var pawn = p.PlayerPawn.Value;
                if (pawn is not null && pawn.IsValid) pawn.CommitSuicide(false, true);
            }
            _log($"air strike: infected finished off {left.Count}");
            if (_strikeFinishEnds)
            {
                _strikeFinishEnds = false;
                Terminate(RoundEndReason.CTsWin, "round.strike");
            }
        }

        for (var i = _strikeFx.Count - 1; i >= 0; i--)
        {
            var (entity, until) = _strikeFx[i];
            if (now < until) continue;
            try { if (entity.IsValid) entity.Remove(); } catch { /* the entity may have gone with the map */ }
            _strikeFx.RemoveAt(i);
        }
    }

    /// <summary>
    /// Nobody alive, but there are people on the server — the round must end.
    ///
    /// Separate from the win check and deliberately **without** the "first turn happened" condition. Otherwise
    /// it deadlocks: if everyone dies before the first infected appears, we don't check for a win,
    /// the engine is not allowed to end the round, and it hangs forever.
    /// </summary>
    private void CheckNobodyAlive(double now)
    {
        if (_roundStarted <= 0 || now - _roundStarted < 10.0) return;
        if (PlayersOnTeams() == 0) return;
        if (AlivePlayers(CsTeam.CounterTerrorist).Count > 0) return;
        if (AlivePlayers(CsTeam.Terrorist).Count > 0) return;

        Terminate(RoundEndReason.RoundDraw, "round.nobody");
    }

    /// <summary>End the round with a message from the language files (<paramref name="key"/>).</summary>
    private void Terminate(RoundEndReason reason, string key) => Terminate(reason, p => Texts.For(p, key));

    private void Terminate(RoundEndReason reason, Func<CCSPlayerController, string> message)
    {
        _roundEnded = true;
        _bleeding.Clear();
        foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid))
            Texts.Show(p, message(p));

        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (rules is null)
        {
            _log("game rules not found — nothing to end the round with");
            return;
        }
        // The engine is told not to evaluate win conditions, otherwise it would end the round itself and not by our
        // rules. But the same block also silences our own ending: TerminateRound does nothing, while the
        // _roundEnded flag is already set — further checks are skipped and the round hangs forever
        // (e.g. a dead CT bot kept the round from ending).
        // So the block is lifted right at the moment of ending; it is re-enabled at the start of the next round.
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0");
        rules.TerminateRound(3.0f, reason);
        _endReason = reason;
        _endRequestedAt = Server.CurrentTime;
        _log($"round ending: {reason}");
    }

    /// <summary>
    /// End-of-round watchdog: the engine may ignore the request. If the round is still running after three seconds —
    /// ask again. Without this a single ignored call hangs the round forever: `_roundEnded` is already
    /// set, and the win check no longer runs.
    /// </summary>
    private void EndRoundWatchdog(double now)
    {
        if (now - _endRequestedAt < 3.0) return;

        if (_endRetries >= 3)
        {
            _log("round does not end: the engine ignores TerminateRound, retries stopped");
            _endRequestedAt = 0;
            return;
        }

        _endRetries++;
        _endRequestedAt = now;
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0");
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        rules?.TerminateRound(1.0f, _endReason);
        _log($"round end retry ({_endRetries})");
    }

    /// <summary>
    /// At round start nobody may be on the infected side: it stays empty until the first infected
    /// appears. Move everyone back to the humans and remove the infected look.
    /// </summary>
    private void RestoreHumans()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid) continue;
            if (player.Team == CsTeam.Terrorist)
            {
                player.SwitchTeam(CsTeam.CounterTerrorist);
                // Respawn on the next frame: the engine spawns players BEFORE the round start event, so
                // a former infected is already standing on a terrorist spawn with their pistol. Switching sides by
                // itself neither moves them nor re-issues weapons — hence a Glock for humans every round.
                // Waiting one frame also avoids fighting the engine's own spawn.
                var slot = player.Slot;
                Server.NextWorldUpdate(() =>
                {
                    var p = Utilities.GetPlayerFromSlot(slot);
                    if (p is not null && p.IsValid && p.Team == CsTeam.CounterTerrorist) p.Respawn();
                });
            }

            var pawn = player.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid) continue;

            pawn.Render = System.Drawing.Color.FromArgb(255, 255, 255, 255);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

            if (_humanModel.TryGetValue(player.Slot, out var model) && !string.IsNullOrEmpty(model))
            {
                try { pawn.SetModel(model); } catch { /* the model may have gone with the map */ }
            }
        }
        _humanModel.Clear();
    }

    /// <summary>Round end: bleeding stops so that the winners do not turn after winning.</summary>
    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        // Logged because this handler sets a flag that disables the win check until the end of the
        // round. If no new round start follows, the server hangs — and without this entry
        // it is impossible to tell apart from "humans are still counted as alive".
        _log("round ended by engine event");
        // Round-end music for everyone, by winner: 3 — humans (CT), 2 — infected (T).
        // Draws and other outcomes — no music.
        var snd = _config.Sounds;
        if (snd.Enabled)
        {
            var (prefix, count) = @event.Winner switch
            {
                3 => (snd.WinHuman, snd.WinHumanCount),
                2 => (snd.WinZombie, snd.WinZombieCount),
                _ => (string.Empty, 0),
            };
            if (!string.IsNullOrWhiteSpace(prefix) && count > 0)
                Sound.MusicToEveryone(MusicMuted, prefix + Random.Shared.Next(1, count + 1), snd.WinVolume);
        }
        _roundEnded = true;
        _bleeding.Clear();
        _stuckSince.Clear();
        return HookResult.Continue;
    }

    /// <summary>Shared tick: turning the first infected and bleeding of the bitten.</summary>
    private void Tick()
    {
        // No entities during map load — the tick stays silent (see World).
        if (!World.Ready) return;
        var now = Server.CurrentTime;

        AdoptOnTheFly(now);
        AdoptStrays();

        ChooseTick(now);
        CountdownTick(now);
        if (!_firstInfectionDone && _infectAt > 0 && now >= _infectAt) StartInfectionIfEnough(now);
        if (_bleeding.Count > 0) { BleedTick(now); HeartbeatTick(now); }
        if (_infected.Count > 0) StripInfectedWeapons();
        if (_infected.Count > 0) RegenTick(now);
        if (_infected.Count > 0) ZombieVoiceTick(now);
        if (_passable.Count > 0) RestoreCollision(now);
        StuckTick(now);
        if (_firstInfectionDone && !_roundEnded) CheckWinConditions();
        if (!_roundEnded) CheckNobodyAlive(now);
        if (_roundEnded && _endRequestedAt > 0) EndRoundWatchdog(now);
        StrikeTick(now);
        if (!_roundEnded) CheckRoundLimit(now);
        if (_infected.Count > 0) KeepZombieLook();
        ReportPhase();
    }

    /// <summary>
    /// Infected list vs. actual sides — for debugging. Needed to see a
    /// mismatch live rather than guess from indirect signs (e.g. damage to some infected
    /// not being paid).
    /// </summary>
    public string Dump()
    {
        var lines = new List<string> { $"infected list: {string.Join(", ", _infected)}; first: {_firstInfected}" };
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || (p.Team != CsTeam.Terrorist && p.Team != CsTeam.CounterTerrorist)) continue;
            var pawn = p.PlayerPawn.Value;
            var hp = pawn is not null && pawn.IsValid ? pawn.Health : -1;
            var mark = _infected.Contains(p.Slot) ? "infected" : "human";
            var odd = (p.Team == CsTeam.Terrorist) != _infected.Contains(p.Slot) && IsAlive(p) ? "  ← MISMATCH" : string.Empty;
            lines.Add($"  slot {p.Slot} {p.PlayerName}{(p.IsBot ? " (bot)" : string.Empty)}: {p.Team}, HP {hp}, {mark}{odd}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Adopt "stray" pawns. A dead player could take control of a bot: the pawn stays a zombie —
    /// with its health, look and side — but its controller is different, and that slot is not in the
    /// infected list. To the plugin such a player is human: the infected kill them instead of
    /// turning them, and humans get neither money nor knockback for hitting them (e.g. a player bitten by
    /// a zombie simply died instead of turning; the log showed a 1700 HP player being hit as a human right after
    /// he had been killed as the first infected). Bot takeover is disabled with the `bot_controllable 0`
    /// cvar; this is a safety net in case the pawn changed controller some other way.
    /// Anyone alive on the infected side is infected: nobody else alive can be there.
    /// </summary>
    private void AdoptStrays()
    {
        if (!_firstInfectionDone || _roundEnded) return;
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.Team != CsTeam.Terrorist || _infected.Contains(p.Slot) || !IsAlive(p)) continue;
            _infected.Add(p.Slot);
            _log($"adopted on the fly: {p.PlayerName} is alive on the infected side without a record — counting as infected");
        }
    }

    /// <summary>Turn the first infected: a random human, but not the same one as last round.</summary>
    /// <summary>
    /// The time for the first turn has come — but is there anyone to play with?
    ///
    /// A lone player on the server turned into the infected and was left alone on an empty map: nobody
    /// to infect, no way to win, the round ticked on until the time limit. The game should not start
    /// with a single human and no bots.
    ///
    /// We don't cancel, we WAIT: check again every few seconds and once every half minute say what we are
    /// waiting for. Postponing beats cancelling — the second player to join should not have to restart
    /// the round by hand to get the game going.
    /// </summary>
    private void StartInfectionIfEnough(double now)
    {
        var need = _config.Infection.MinToStart;
        var have = PlayersOnTeams();
        if (need <= 0 || have >= need)
        {
            InfectFirst();
            return;
        }

        // Keep waiting. Shift the deadline rather than reset it: once there are enough players, the infection starts
        // at the very next check, without a new safe pause — the player has already waited it out.
        _infectAt = now + 3.0;

        if (now - _waitingSaid < 30.0 && _waitingSaid > 0) return;
        _waitingSaid = now;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot) continue;
            Texts.To(player, "waiting.players", need, have);
        }
        _log($"infection postponed: on teams {have}, need {need}");
    }

    /// <summary>What to do right after a successful first turn (shared by the pre-picked "victim" and a random pick).</summary>
    private void FirstInfectionArmed()
    {
            // The infected side is no longer empty — give the engine back its win conditions. From now on
            // it ends the round itself: all infected killed or all humans infected. Order matters:
            // first the turn has happened and been confirmed, only then the block is lifted. Lifting it
            // earlier lets the engine see an empty T side and end the round instantly.
            // What gets enabled with the first infected lives in the config (`cvarsOnInfection`): restoring the stock
            // win conditions to the engine and solid teammates.
            foreach (var (cvar, value) in _config.CvarsOnInfection)
                Server.ExecuteCommand($"{cvar} {value}");

            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid))
                Texts.To(p, "infection.started");
    }

    /// <summary>
    /// Stinger moment: the round-start music has ended — pick the future first infected (same
    /// logic as InfectFirst: a living human, not last round's first) and play the drum stinger to them privately.
    /// They turn at `_infectAt`. The countdown to the outbreak does not start before this moment.
    /// </summary>
    private void ChooseTick(double now)
    {
        if (_firstInfectionDone || _chosen >= 0 || _stingerAt <= 0 || now < _stingerAt) return;
        var candidates = AlivePlayers(CsTeam.CounterTerrorist);
        if (candidates.Count == 0) return;
        // A game mode may assign the first itself: no stinger needed — it's a bot, and everyone knows who it is.
        var picked = PickFirst?.Invoke(candidates);
        if (picked is not null) { _chosen = picked.Slot; return; }
        var pool = candidates.Where(p => p.Slot != _lastRoundFirst).ToList();
        if (pool.Count == 0) pool = candidates;
        var people = pool.Where(p => !p.IsBot).ToList();
        var bots = pool.Where(p => p.IsBot).ToList();
        if (people.Count >= 2) pool = people;
        else if (people.Count == 1 && bots.Count > 0) pool = bots;
        var victim = pool[_random.Next(pool.Count)];
        _chosen = victim.Slot;
        var snd = _config.Sounds;
        if (snd.Enabled && !victim.IsBot && !string.IsNullOrWhiteSpace(snd.Infect)) Sound.ToPlayer(victim, snd.Infect, snd.InfectVolume);
        if (_config.Debug) _log($"victim picked: {victim.PlayerName}, turning in {_infectAt - now:0} s");
    }

    private void InfectFirst()
    {
        var candidates = AlivePlayers(CsTeam.CounterTerrorist);
        if (candidates.Count == 0)
        {
            // Nobody to infect: don't count the phase as passed, otherwise on an empty server the round ends immediately
            // and joining players get kicked around by an endless restart loop.
            _infectAt = Server.CurrentTime + 5.0;
            return;
        }

        // The "victim" picked at the stinger moment goes first: they already know. If they didn't survive — default pick.
        // If a game mode assigns the first, ask it again: the bot may have been recreated after the stinger.
        var chosen = PickFirst?.Invoke(candidates)
                     ?? (_chosen >= 0 ? candidates.FirstOrDefault(p => p.Slot == _chosen) : null);
        if (chosen is not null)
        {
            Convert(chosen, first: true, attacker: null);
            if (Materialised(chosen))
            {
                _lastRoundFirst = chosen.Slot;
                _firstInfected = chosen.Slot;
                _firstInfectionDone = true;
                FirstInfectionArmed();
                return;
            }
            _infected.Remove(chosen.Slot);
            _log($"first infection failed on {chosen.PlayerName}: subject vanished — trying the next one");
        }

        var pool = candidates.Where(p => p.Slot != _lastRoundFirst).ToList();
        if (pool.Count == 0) pool = candidates;

        // A real player goes first, not a bot: a bot zombie is boring, it is no fun to hunt or
        // run from. Exception — a single human on the server: they need an opponent, not the role
        // (with 1 player and 1 bot, the bot becomes the zombie).
        var people = pool.Where(p => !p.IsBot).ToList();
        var bots = pool.Where(p => p.IsBot).ToList();
        if (people.Count >= 2) pool = people;
        else if (people.Count == 1 && bots.Count > 0) pool = bots;

        // Iterate over candidates one by one and verify the result each time instead of trusting the first call.
        // The subject can vanish at exactly this moment: a player leaves the server, or a dead player takes
        // control of a bot — the bot's controller is removed, and the turn goes nowhere.
        //
        // Previously the "first infection done" flag was set BEFORE the turn, so a failed attempt
        // counted as a success: zero infected, win check already enabled — and the round instantly
        // ended with a "human win" without ever starting (the zombie never appeared
        // and the game effectively did not start).
        foreach (var candidate in pool.OrderBy(_ => _random.Next()))
        {
            Convert(candidate, first: true, attacker: null);
            if (!Materialised(candidate))
            {
                // Clean up after the failed attempt: otherwise the vanished player's slot would stay in the infected
                // list and permanently skew the alive count.
                _infected.Remove(candidate.Slot);
                _log($"first infection failed on {candidate.PlayerName}: subject vanished — trying the next one");
                continue;
            }

            _lastRoundFirst = candidate.Slot;
            _firstInfected = candidate.Slot;
            _firstInfectionDone = true;
            FirstInfectionArmed();
            return;
        }

        // Nobody held: don't count the phase as passed and try again. The round meanwhile keeps
        // going as usual — a couple of seconds' delay beats a round without infected.
        _log("first infection did not take on anyone — retrying in 2 s");
        _infectAt = Server.CurrentTime + 2.0;
    }

    /// <summary>
    /// Whether the turn actually happened. We check its trace, not the call: the player is still on the server,
    /// alive and marked infected. That's enough to catch both a player who left and a removed
    /// bot controller taken over by a dead player.
    /// </summary>
    private bool Materialised(CCSPlayerController player) =>
        player.IsValid && IsAlive(player) && _infected.Contains(player.Slot);

    /// <summary>
    /// The infected only have a knife. Pickup is **blocked**, not undone after the fact:
    /// stripping a weapon a tick later is a visible glitch — the weapon manages to appear in hand.
    ///
    /// This is the only place in the plugin that relies on a game function signature rather than an event.
    /// If the hook breaks after a patch, the infected will be able to pick up weapons,
    /// but nothing breaks: the fallback below strips extras on the next tick.
    /// </summary>
    private void HookWeaponPickup(BasePlugin plugin)
    {
        try
        {
            VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(OnCanAcquire, HookMode.Pre);
            _pickupHooked = true;
            VirtualFunctions.CCSPlayer_WeaponServices_CanUseFunc.Hook(OnCanUse, HookMode.Pre);
            _useHooked = true;
        }
        catch (Exception e)
        {
            _log($"weapon pickup hook unavailable ({e.Message}) — weapons are stripped from the infected on tick");
        }
    }

    /// <summary>
    /// Infected melee is regular CS2 knife combat: light and heavy attacks,
    /// damage by hit zone, backstab. We touch none of that.
    /// Only one thing changes: **a lethal hit does not kill, it turns** — the human joins the infected
    /// instead of dropping out of the round.
    ///
    /// The second and last place in the plugin that relies on a game function signature:
    /// the damage event arrives after death and cannot cancel it.
    /// </summary>
    private void HookTakeDamage(BasePlugin plugin)
    {
        try
        {
            VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Hook(OnTakeDamage, HookMode.Pre);
            // Infected health spoofing: after the engine processes damage, the pawn gets the displayed number back.
            VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Hook(OnTakeDamagePost, HookMode.Post);
            // Engine round termination: during the air strike the bomb explosion was awarded to T (see OnTerminateRound).
            VirtualFunctions.TerminateRoundFunc.Hook(OnTerminateRound, HookMode.Pre);
            _damageHooked = true;
        }
        catch (Exception e)
        {
            _log($"damage hook unavailable ({e.Message}) — a lethal bite will kill the human instead of infecting");
        }
    }

    /// <summary>
    /// The engine ends the round itself on a bomb explosion — "target bombed", T wins — and the
    /// mp_ignore_round_win_conditions block does not apply to that. The air strike bomb is ours: at the round
    /// limit the round has already been ended as a human win, and during the command test (`pz_strike`) the ending is not
    /// let through at all — the round goes on. All calls go to the debug log: rounds awarded to CT after an
    /// infected win have been observed, and we need the full picture.
    /// </summary>
    private HookResult OnTerminateRound(DynamicHook hook)
    {
        RoundEndReason reason;
        try { reason = hook.GetParam<RoundEndReason>(2); }
        catch { return HookResult.Continue; }

        var now = Server.CurrentTime;
        var strike = _strikeExplodeAt > 0 || _strikeFinishAt > 0 || (_strikeShieldUntil > 0 && now < _strikeShieldUntil);
        if (_config.Debug) _log($"TerminateRound: reason {reason}{(strike ? " (air strike)" : "")}");

        if (!strike || reason != RoundEndReason.TargetBombed) return HookResult.Continue;

        // The bomb is ours. At the limit we have already ended the round as a human win; during the command test the round
        // must go on — so the engine call is let through in neither case.
        _log("air strike bomb explosion: engine round end ('target bombed') cancelled");
        return HookResult.Stop;
    }

    private HookResult OnTakeDamage(DynamicHook hook)
    {
        var victimPawn = hook.GetParam<CCSPlayerPawn>(0);
        var info = hook.GetParam<CTakeDamageInfo>(1);
        if (victimPawn is null || !victimPawn.IsValid || info is null) return HookResult.Continue;

        var victim = victimPawn.Controller.Value?.As<CCSPlayerController>();
        if (victim is null || !victim.IsValid) return HookResult.Continue;

        // Health spoofing, step one — before any damage handling: the infected pawn gets its real
        // health back. This used to be only in the damage-to-infected branch, and the branches that zero the damage
        // (berserk, infected hitting infected) exited without restoring — the engine still ran its
        // processing, and OnTakeDamagePost stored the displayed 999 as the real value.
        SpoofBefore(victim, victimPawn);

        // Berserk is full invulnerability: bullets, knife, grenades, fire, falling — all zeroed while
        // the stance holds. The check comes before examining the attacker to also cover damage
        // without one (fire, falling). Specifically Changed with zero, not Handled: the engine's
        // damage processing must not be cut short (see below).
        // The air strike wave hits everyone in range, but it must only hurt the infected.
        if (_strikeShieldUntil > 0 && Server.CurrentTime < _strikeShieldUntil && !_infected.Contains(victim.Slot))
        {
            info.Damage = 0;
            return HookResult.Changed;
        }

        if (_config.Infection.GuardGodmode && _infected.Contains(victim.Slot) && _guard?.Active(victim.Slot) == true)
        {
            info.Damage = 0;
            return HookResult.Changed;
        }

        var attackerPawn = info.Attacker.Value?.As<CCSPlayerPawn>();
        var attacker = attackerPawn?.Controller.Value?.As<CCSPlayerController>();
        var attackerValid = attacker is not null && attacker.IsValid;

        // Damage to an infected: modifiers when a human is shooting, and health spoofing — always,
        // including damage without an attacker (falling, ownerless fire): otherwise the displayed number
        // would diverge from the real one.
        if (_infected.Contains(victim.Slot))
        {
            // Infected do not damage infected: an infected bot kept hitting the player it had just
            // turned, and the damage went through. Zero, not Handled — the engine's damage processing
            // must not be cut short (see below).
            if (attackerValid && _infected.Contains(attacker!.Slot))
            {
                info.Damage = 0;
                return HookResult.Changed;
            }

            var changed = false;

            // A human hits an infected: the damage goes through modifiers (weapon damage, armor, ...).
            // The core itself knows no weapon numbers — a modifier provides them, see ZombieMode.WeaponDamage.
            if (attackerValid && !_infected.Contains(attacker!.Slot) && _modifiers.Count > 0)
            {
                var damage = new ZombieDamage
                {
                    Victim = victim,
                    Attacker = attacker!,
                    Grenade = GrenadeOf(info),
                    Weapon = attackerPawn?.WeaponServices?.ActiveWeapon?.Value?.DesignerName,
                    Damage = info.Damage,
                };
                foreach (var modifier in _modifiers.ToArray())
                {
                    try { modifier.Modify(damage); }
                    catch (Exception e) { _log($"damage modifier {modifier.GetType().Name} failed: {e.Message}"); }
                }

                if (Math.Abs(damage.Damage - info.Damage) >= 0.01f)
                {
                    info.Damage = damage.Damage;
                    changed = true;
                }
            }

            return changed ? HookResult.Changed : HookResult.Continue;
        }

        if (!attackerValid || !_infected.Contains(attacker!.Slot)) return HookResult.Continue;

        // Zombie damage to humans is the stock knife damage: a bite starts the bleeding, and the bleeding
        // does the main work. Balance measurement: the hit itself, HP before the hit and how much bleeding has
        // already taken by then. Without these three numbers arguing about knife damage is pointless
        // (the stock light + heavy combo falls short of a hundred).
        if (_config.Debug)
        {
            var taken = _bleeding.TryGetValue(victim.Slot, out var b) ? b.Taken : 0;
            _log($"infected hit: damage {info.Damage:0.#}, {victim.PlayerName} HP {victimPawn.Health}"
                 + $" (armor {victimPawn.ArmorValue}), taken by bleeding {taken}");
        }

        // We deliberately do not compute damage ourselves: the hit, its strength, hit zones
        // and backstab are all stock, the engine computes them. Non-lethal damage passes through untouched.
        if (info.Damage < victimPawn.Health) return HookResult.Continue;

        // Lethal hit: turn instead of kill, zero the damage.
        // Specifically Changed, not Handled: the engine's damage processing must not be cut short — it won't finish
        // its part of the work, and the server crashes a few minutes later (verified).
        info.Damage = 0;
        var biter = attacker;
        Server.NextWorldUpdate(() =>
        {
            if (victim.IsValid && !_infected.Contains(victim.Slot)) Convert(victim, first: false, attacker: biter);
        });
        return HookResult.Changed;
    }

    /// <summary>
    /// Picking a weapon up from the floor — by touch. That is decided by CanUse, not CanAcquire (which covers gives
    /// and purchases). Without this hook a zombie picked the gun up, the per-tick safety net dropped it, the zombie was
    /// still standing on it and picked it up again — "picks it up and throws it away" in a loop. Now it stays on the floor.
    /// </summary>
    private HookResult OnCanUse(DynamicHook hook)
    {
        if (_giving > 0) return HookResult.Continue;

        var services = hook.GetParam<CCSPlayer_WeaponServices>(0);
        var pawn = services?.Pawn.Value;
        var controller = pawn?.Controller.Value?.As<CCSPlayerController>();
        if (controller is null || !controller.IsValid || !_infected.Contains(controller.Slot)) return HookResult.Continue;

        // The knife stays usable: it is the zombie's only weapon.
        var weapon = hook.GetParam<CBasePlayerWeapon>(1);
        var name = weapon?.DesignerName ?? string.Empty;
        if (name.Contains("knife", StringComparison.OrdinalIgnoreCase) || name.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
            return HookResult.Continue;

        hook.SetReturn(false);
        return HookResult.Stop;
    }

    private HookResult OnCanAcquire(DynamicHook hook)
    {
        if (_giving > 0) return HookResult.Continue;

        var services = hook.GetParam<CCSPlayer_ItemServices>(0);
        var pawn = services.Pawn.Value;
        var controller = pawn?.Controller.Value?.As<CCSPlayerController>();
        if (controller is null || !controller.IsValid || !_infected.Contains(controller.Slot)) return HookResult.Continue;

        hook.SetReturn(AcquireResult.NotAllowedByProhibition);
        return HookResult.Stop;
    }

    /// <summary>
    /// Fallback in case the pickup hook is unavailable: every tick strip everything from the infected
    /// except the knife. With a working hook this pass finds nothing.
    /// </summary>
    private void StripInfectedWeapons()
    {
        // This pass used to be disabled when the pickup hook worked. It turned out the hook doesn't catch everything:
        // weapons picked up from the floor slip past it. So we always check —
        // with a working hook the pass finds almost nothing and costs next to nothing.

        foreach (var slot in _infected)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is null || pawn is null || !pawn.IsValid || pawn.Health <= 0) continue;

            var weapons = pawn.WeaponServices?.MyWeapons;
            if (weapons is null) continue;

            var hasExtra = false;
            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon is null || !weapon.IsValid) continue;
                var name = weapon.DesignerName ?? "";
                if (name.Contains("knife") || name.Contains("bayonet")) continue;
                hasExtra = true;
                break;
            }
            if (!hasExtra) continue;

            GiveKnifeOnly(player);
        }
    }

    /// <summary>Strip everything and give a knife. The grant bypasses the pickup block — otherwise the infected would be left empty-handed.</summary>
    private void GiveKnifeOnly(CCSPlayerController player)
    {
        // Drop guns on the ground first, and only then remove the rest. `RemoveWeapons` deletes
        // everything without a trace, while by design a turned player's weapons should go to the living:
        // ammo can be found, weapons picked up from other players. Without this there is nothing to pick up
        // on the map, and half the point of melee is lost.
        DropGuns(player);

        using (AllowWeaponGive())
        {
            player.RemoveWeapons();
            player.GiveNamedItem("weapon_knife");
        }
    }

    /// <summary>Names that should not be dropped: the knife stays with the infected, the rest cannot be thrown.</summary>
    /// <summary>
    /// Separate players stuck inside each other. Turning happens right next to the biter, and at exactly
    /// that moment the player switches sides: former teammates become enemies and start
    /// colliding. If they overlapped at that instant, both end up walled in.
    ///
    /// Fixed with two moves at once: make the new infected briefly passable so there is a way
    /// out of the other body, and push them away from the nearest player so they walk out on their own.
    /// The second alone is not enough — while bodies are solid there is nowhere to push.
    /// </summary>
    private void Unstick(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || pawn.AbsOrigin is null) return;

        // Passability — only if enabled in the config. We push away in any case:
        // it's cheap and harmless even when there is nothing to separate.
        var seconds = _config.Infection.UnstickSeconds;
        if (seconds > 0)
        {
            Passable(pawn, true);
            _passable[player.Slot] = Server.CurrentTime + (float)seconds;
        }

        // Nearest player within body radius: move away from them.
        CCSPlayerPawn? nearest = null;
        var best = double.MaxValue;
        foreach (var other in Utilities.GetPlayers())
        {
            if (!other.IsValid || other.Slot == player.Slot) continue;
            var op = other.PlayerPawn.Value;
            if (op is null || !op.IsValid || op.Health <= 0 || op.AbsOrigin is null) continue;

            var ddx = op.AbsOrigin.X - pawn.AbsOrigin.X;
            var ddy = op.AbsOrigin.Y - pawn.AbsOrigin.Y;
            var d = ddx * ddx + ddy * ddy;
            if (d < best) { best = d; nearest = op; }
        }

        // 48 units — slightly wider than a player: beyond that they're not "stuck", just standing close.
        if (nearest?.AbsOrigin is null || best > 48 * 48) return;

        var dx = pawn.AbsOrigin.X - nearest.AbsOrigin.X;
        var dy = pawn.AbsOrigin.Y - nearest.AbsOrigin.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        // Exactly at the same point there is no direction — pick any, as long as they separate.
        if (length < 0.001) { dx = 1; dy = 0; length = 1; }

        pawn.Teleport(null, null, new Vector((float)(dx / length * 180), (float)(dy / length * 180), 140));
    }

    /// <summary>Slot → since when the player has been standing inside another. No entry — not stuck.</summary>
    private readonly Dictionary<int, float> _stuckSince = new();

    /// <summary>
    /// Separate players stuck inside each other.
    ///
    /// Stuck means centers closer than body width for longer than `stuckSeconds`. In a crowd players constantly
    /// end up pressed together, but the engine pushes overlapping players apart in a fraction of a second; real
    /// stuck-ness differs in that it doesn't resolve itself (e.g. leaping into a crowd while infecting and
    /// getting stuck in a bot).
    ///
    /// Previously "the player is barely moving" was also required — and stuck players were never separated:
    /// a stuck player does not stand still but jerks and jumps, their speed fluctuates, and the timer reset
    /// on every jerk. The overlap itself does not go away — so that alone is what we check.
    ///
    /// Released the same way as on turning: briefly passable and pushed away from the neighbor.
    /// No separate logic — it would diverge from that one at the first edit.
    /// </summary>
    private void StuckTick(float now)
    {
        var wait = (float)_config.Infection.StuckSeconds;
        if (wait <= 0) return;

        // A player body is 32 units wide. Centers closer than 26 — that's an overlap, not "standing close".
        const float Near = 26f * 26f;
        const float Floor = 60f;    // height difference: above this it's another floor, not being stuck

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid || pawn.Health <= 0 || pawn.AbsOrigin is null)
            {
                _stuckSince.Remove(player.Slot);
                continue;
            }

            var inside = false;
            foreach (var other in Utilities.GetPlayers())
            {
                if (!other.IsValid || other.Slot == player.Slot) continue;
                var op = other.PlayerPawn.Value;
                if (op is null || !op.IsValid || op.Health <= 0 || op.AbsOrigin is null) continue;

                var dx = op.AbsOrigin.X - pawn.AbsOrigin.X;
                var dy = op.AbsOrigin.Y - pawn.AbsOrigin.Y;
                if (Math.Abs(op.AbsOrigin.Z - pawn.AbsOrigin.Z) > Floor) continue;
                if (dx * dx + dy * dy > Near) continue;

                inside = true;
                break;
            }

            if (!inside)
            {
                _stuckSince.Remove(player.Slot);
                continue;
            }

            if (!_stuckSince.TryGetValue(player.Slot, out var since))
            {
                _stuckSince[player.Slot] = now;
                continue;
            }

            if (now - since < wait) continue;

            _stuckSince.Remove(player.Slot);
            Unstick(player);
            if (_config.Debug) _log($"unstick: {player.PlayerName} stood inside another player for {wait:0.0} s");
        }
    }

    /// <summary>Restore collisions for those whose separation time has expired.</summary>
    private void RestoreCollision(double now)
    {
        foreach (var (slot, until) in _passable.ToList())
        {
            if (until > now) continue;
            _passable.Remove(slot);

            var pawn = Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value;
            if (pawn is not null && pawn.IsValid) Passable(pawn, false);
        }
    }

    /// <summary>Whether the player passes through others. Restored by the tick, see <see cref="Tick"/>.</summary>
    private static void Passable(CCSPlayerPawn pawn, bool on)
    {
        pawn.Collision.CollisionGroup = (byte)(on ? CollisionGroup.COLLISION_GROUP_DEBRIS : CollisionGroup.COLLISION_GROUP_PLAYER);
        Utilities.SetStateChanged(pawn, "CCollisionProperty", "m_collisionAttribute");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_pCollision");
    }

    /// <summary>
    /// The grenade, if the damage came from one; null — not a grenade. Detected by the projectile (`inflictor`): for HE it is
    /// `hegrenade_projectile`, for molotov and incendiary fire — `inferno` (one entity for both),
    /// for a decoy — `decoy_projectile`. Unknown projectile — judge by damage type: blast or burn.
    /// By the time of the explosion the thrower already holds a gun, so the weapon in hand is not checked for grenades.
    /// </summary>
    private static GrenadeKind? GrenadeOf(CTakeDamageInfo info)
    {
        var inflictor = info.Inflictor.Value?.DesignerName ?? string.Empty;
        return inflictor switch
        {
            "hegrenade_projectile" => GrenadeKind.He,
            "inferno" => GrenadeKind.Fire,
            "decoy_projectile" => GrenadeKind.Decoy,
            _ => (info.BitsDamageType & DamageTypes_t.DMG_BLAST) != 0 ? GrenadeKind.He
               : (info.BitsDamageType & DamageTypes_t.DMG_BURN) != 0 ? GrenadeKind.Fire
               : null,
        };
    }

    private static bool IsDroppable(string designerName)
    {
        if (!designerName.StartsWith("weapon_", StringComparison.Ordinal)) return false;
        foreach (var skip in new[] { "knife", "bayonet", "c4", "taser", "healthshot", "grenade", "molotov", "flashbang", "decoy", "incgrenade" })
            if (designerName.Contains(skip, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Drop firearms on the ground. The engine can only drop the active weapon, so each gun
    /// is made active first and then dropped. Grenades and medical items are skipped: CS2 does not drop them.
    /// </summary>
    private void DropGuns(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        var services = pawn?.WeaponServices;
        if (pawn is null || services is null) return;

        foreach (var handle in services.MyWeapons.ToList())
        {
            var weapon = handle.Value;
            if (weapon is null || !weapon.IsValid) continue;

            var name = weapon.DesignerName ?? string.Empty;
            if (!IsDroppable(name)) continue;

            try
            {
                services.ActiveWeapon.Raw = handle.Raw;
                player.DropActiveWeapon();
            }
            catch (Exception e)
            {
                if (_config.Debug) _log($"failed to drop {name}: {e.Message}");
            }
        }
    }

    // ── bite and bleeding ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bite: the regular knife damage goes through as is — otherwise the hit isn't felt at all, no punch, no blood —
    /// and on top of that it starts bleeding. Each subsequent bite speeds it up.
    /// It is the bleeding, not the hit itself, that brings a human to turning.
    /// </summary>
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        if (victim is null || !victim.IsValid || attacker is null || !attacker.IsValid) return HookResult.Continue;
        if (_infected.Contains(victim.Slot))
        {
            // Molotov and incendiary fire get their own "burning" sound, not a groan; everything else is pain.
            if (string.Equals(@event.Weapon, "inferno", StringComparison.OrdinalIgnoreCase)) ZombieBurn(victim);
            else ZombiePain(victim);
            // Any real damage resets the regeneration timer. Zero means berserk or the
            // air strike wave hitting a human — not a real hit.
            if (@event.DmgHealth > 0) _lastHurt[victim.Slot] = Server.CurrentTime;

            // A human hit a zombie: extensions pay for it (Money) or count it. player_hurt carries the real
            // damage here — the real health is put back on the pawn before the engine applies the hit.
            if (@event.DmgHealth > 0 && victim.Slot != attacker.Slot && !_infected.Contains(attacker.Slot))
            {
                try { ZombieDamaged?.Invoke(new ZombieDamagedEvent(victim, attacker, @event.Weapon ?? string.Empty, @event.DmgHealth)); }
                catch (Exception e) { _log($"ZombieDamaged handler failed: {e.Message}"); }
            }
            return HookResult.Continue;
        }
        if (!_infected.Contains(attacker.Slot)) return HookResult.Continue;

        var pawn = victim.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return HookResult.Continue;

        var now = Server.CurrentTime;
        if (_bleeding.TryGetValue(victim.Slot, out var bleed))
        {
            bleed.Interval = Math.Max(_config.Infection.BleedMinInterval, bleed.Interval - _config.Infection.BleedStep);
            bleed.Attacker = attacker.Slot;
        }
        else
        {
            _bleeding[victim.Slot] = new Bleed
            {
                Interval = _config.Infection.BleedInterval,
                NextTick = now + _config.Infection.BleedFirstTickDelay,
                Attacker = attacker.Slot,
            };
            Texts.To(victim, "bleed.started");
        }

        return HookResult.Continue;
    }

    private void BleedTick(double now)
    {
        // The round is over — bleeding stops. Otherwise a survivor who won the round could
        // still turn after the victory and lose everything they kept by surviving.
        if (_roundEnded)
        {
            _bleeding.Clear();
            return;
        }

        // Copy the list: turning inside the loop modifies the dictionary.
        foreach (var slot in _bleeding.Keys.ToList())
        {
            if (!_bleeding.TryGetValue(slot, out var bleed) || now < bleed.NextTick) continue;
            bleed.NextTick = now + bleed.Interval;

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is null || pawn is null || !pawn.IsValid || pawn.Health <= 0)
            {
                _bleeding.Remove(slot);
                continue;
            }

            // A Medi-Shot stops the bleeding and raises health (there is no separate bandage item). CS2 has
            // no dedicated event for it, but none is needed: while the wound is open only we set health,
            // so any increase means healing. As a bonus this survives any patch — no signatures, no events.
            if (bleed.LastHealth > 0 && pawn.Health > bleed.LastHealth)
            {
                _bleeding.Remove(slot);
                Texts.To(player, "bleed.stopped");
                if (_config.Debug) _log($"{player.PlayerName}'s bleeding stopped by a Medi-Shot ({bleed.LastHealth} → {pawn.Health})");
                continue;
            }

            // Armor halves the bleeding but is consumed along with health.
            var damage = _config.Infection.BleedDamage;
            if (pawn.ArmorValue > 0)
            {
                damage = Math.Max(1, damage / 2);
                pawn.ArmorValue = Math.Max(0, pawn.ArmorValue - damage);
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
            }

            bleed.Taken += damage;
            var health = pawn.Health - damage;
            if (health > 0)
            {
                SetPawnHealth(player, health);
                bleed.LastHealth = health;
                continue;
            }

            _bleeding.Remove(slot);
            // The infection is credited to the last biter: otherwise the credit for the infection is lost.
            Convert(player, first: false, attacker: bleed.Attacker >= 0 ? Utilities.GetPlayerFromSlot(bleed.Attacker) : null);
        }
    }

    // ── turning and death ──────────────────────────────────────────────────────────────────────

    /// <summary>Move a player to the infected: side switch without respawn, knife only, HP by number of humans.</summary>
    /// <summary>
    /// Heartbeat for the wounded — every frame while bleeding: the clip matching the remaining HP is restarted as soon
    /// as it finishes (the wrapper cannot stop a sound — so clips are short, and after healing it plays no
    /// longer than one clip). The tempo changes at a clip boundary, not midway, so two clips don't overlap.
    /// </summary>
    private void HeartbeatTick(double now)
    {
        var snd = _config.Sounds;
        if (!snd.Enabled) return;
        foreach (var (slot, bleed) in _bleeding)
        {
            if (now < bleed.HeartbeatAt) continue;
            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is null || !player.IsValid || player.IsBot || pawn is null || !pawn.IsValid) continue;
            var health = pawn.Health;
            string clip; double length;
            if (health < snd.HeartbeatFastBelow) { clip = snd.HeartbeatFast; length = snd.HeartbeatFastSeconds; }
            else if (health < snd.HeartbeatMediumBelow) { clip = snd.HeartbeatMedium; length = snd.HeartbeatMediumSeconds; }
            else { clip = snd.HeartbeatSlow; length = snd.HeartbeatSlowSeconds; }
            bleed.HeartbeatAt = now + Math.Max(0.5, length);
            var sent = !string.IsNullOrWhiteSpace(clip) && Sound.ToPlayer(player, clip, snd.HeartbeatVolume);
            // Diagnostics for "bleeding players don't hear the heartbeat" reports: config, code, addon and server
            // events all check out; this line shows whether the event is sent at all, and which one.
            if (_config.Debug) _log($"heartbeat: {player.PlayerName}, HP {health}, {clip} × {snd.HeartbeatVolume:0.##} — {(sent ? "sent" : "NOT sent")}");
        }
    }

    /// <summary>
    /// Infected growl — from their position, a random clip, a random pause between min and max. Turned players only:
    /// a hidden infected stays silent, otherwise they'd give themselves away. Bots growl too.
    /// </summary>
    private void ZombieVoiceTick(double now)
    {
        var snd = _config.Sounds;
        if (!snd.Enabled || string.IsNullOrWhiteSpace(snd.ZombieIdle) || snd.ZombieIdleCount <= 0) return;
        foreach (var slot in _infected)
        {
            if (_idleAt.TryGetValue(slot, out var at) && now < at) continue;
            _idleAt[slot] = now + snd.ZombieIdleMin + Random.Shared.NextDouble() * Math.Max(0.0, snd.ZombieIdleMax - snd.ZombieIdleMin);
            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is null || !player.IsValid || pawn is null || !pawn.IsValid || pawn.Health <= 0) continue;
            Sound.AtEntity(pawn, snd.ZombieIdle + Random.Shared.Next(1, snd.ZombieIdleCount + 1), snd.ZombieIdleVolume);
        }
    }

    /// <summary>Zombie pain groan — from their position, no more often than `zombiePainGap`.</summary>
    private void ZombiePain(CCSPlayerController victim)
    {
        var snd = _config.Sounds;
        if (!snd.Enabled || string.IsNullOrWhiteSpace(snd.ZombiePain) || snd.ZombiePainCount <= 0) return;
        var now = Server.CurrentTime;
        if (_painAt.TryGetValue(victim.Slot, out var at) && now < at) return;
        _painAt[victim.Slot] = now + Math.Max(0.1, snd.ZombiePainGap);
        var pawn = victim.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || pawn.Health <= 0) return;
        Sound.AtEntity(pawn, snd.ZombiePain + Random.Shared.Next(1, snd.ZombiePainCount + 1), snd.ZombiePainVolume);
    }

    /// <summary>Infected is burning — from their position, no more often than `zombieBurnGap` (clips are 2–3 s). Without a dedicated sound — regular pain.</summary>
    private void ZombieBurn(CCSPlayerController victim)
    {
        var snd = _config.Sounds;
        if (!snd.Enabled || string.IsNullOrWhiteSpace(snd.ZombieBurn) || snd.ZombieBurnCount <= 0) { ZombiePain(victim); return; }
        var now = Server.CurrentTime;
        if (_burnAt.TryGetValue(victim.Slot, out var at) && now < at) return;
        _burnAt[victim.Slot] = now + Math.Max(0.1, snd.ZombieBurnGap);
        var pawn = victim.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || pawn.Health <= 0) return;
        Sound.AtEntity(pawn, snd.ZombieBurn + Random.Shared.Next(1, snd.ZombieBurnCount + 1), snd.ZombieBurnVolume);
    }

    /// <summary>
    /// Infected knife swing — from their position, no more often than `zombieAttackGap`. The infected only have a knife (GiveKnifeOnly),
    /// so any "shot" of theirs is a swing; hit or miss, the sound is the same.
    /// </summary>
    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is null || !player.IsValid || !_infected.Contains(player.Slot)) return HookResult.Continue;
        var snd = _config.Sounds;
        if (!snd.Enabled || string.IsNullOrWhiteSpace(snd.ZombieAttack) || snd.ZombieAttackCount <= 0) return HookResult.Continue;
        var now = Server.CurrentTime;
        if (_attackAt.TryGetValue(player.Slot, out var at) && now < at) return HookResult.Continue;
        _attackAt[player.Slot] = now + Math.Max(0.1, snd.ZombieAttackGap);
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || pawn.Health <= 0) return HookResult.Continue;
        Sound.AtEntity(pawn, snd.ZombieAttack + Random.Shared.Next(1, snd.ZombieAttackCount + 1), snd.ZombieAttackVolume);
        return HookResult.Continue;
    }

    /// <summary>
    /// Countdown to the outbreak: `countdownFrom` seconds before the first infection everyone hears the numbers 10…1.
    /// The moment is known in advance (`_infectAt` is set at round start). Without enough players the infection
    /// won't start — and the countdown stays silent so it doesn't count down into the void.
    /// </summary>
    private void CountdownTick(double now)
    {
        var snd = _config.Sounds;
        if (!snd.Enabled || !snd.Countdown || _firstInfectionDone || _infectAt <= 0) return;
        if (_stingerAt > 0 && now < _stingerAt + 0.3) return;   // stinger to the victim first, then the countdown
        var left = _infectAt - now;
        if (left <= 0 || left > snd.CountdownFrom) return;
        var n = (int)Math.Ceiling(left);
        if (n < 1 || n == _countdownSaid) return;
        var need = _config.Infection.MinToStart;
        var have = PlayersOnTeams();
        if (need > 0 && have < need) return;
        _countdownSaid = n;
        Sound.ToEveryone(snd.CountdownPrefix + n, snd.CountdownVolume);
    }

    private void Convert(CCSPlayerController player, bool first, CCSPlayerController? attacker)
    {
        if (!player.IsValid) return;

        _infected.Add(player.Slot);
        _bleeding.Remove(player.Slot);
        // Regeneration counts from the turn until the first damage arrives.
        _lastHurt[player.Slot] = Server.CurrentTime;
        _regenNext.Remove(player.Slot);

        // The kill feed entry goes out BEFORE the side switch, and the switch waits one frame. The client draws the
        // feed with the victim's side at the moment the event arrives: switching in the same frame showed
        // "T killed T" instead of "a zombie infected a human".
        AnnounceInfection(player, attacker);
        Server.NextFrame(() =>
        {
            if (!player.IsValid || !_infected.Contains(player.Slot)) return;
            player.SwitchTeam(CsTeam.Terrorist);
            Unstick(player);
        });
        GiveKnifeOnly(player);
        // The engine applies the team model on the next frame and overwrites ours — so the look is set after it.
        Server.NextWorldUpdate(() => ApplyZombieLook(player));

        var humans = Math.Max(0, AlivePlayers(CsTeam.CounterTerrorist).Count(p => p.Slot != player.Slot));
        var hp = Math.Min(_config.Infection.HpCap, _config.Infection.HpBase + _config.Infection.HpPerHuman * humans);
        if (first) hp = (int)(hp * _config.Infection.FirstInfectedMultiplier);

        SetZombieHealth(player, hp, hp);

        Texts.To(player, "turned.you");
        // The round's first turn — a scream for everyone on the map; the first infected already got the drum stinger
        // privately when the music ended. Other turned players get the stinger privately.
        var snd = _config.Sounds;
        if (snd.Enabled)
        {
            if (first)
            {
                if (!string.IsNullOrWhiteSpace(snd.FirstScream)) Sound.ToEveryone(snd.FirstScream, snd.FirstScreamVolume);
                // First infected stinger on top of the scream — for everyone.
                if (!string.IsNullOrWhiteSpace(snd.FirstInfection) && snd.FirstInfectionCount > 0)
                    Sound.ToEveryone(snd.FirstInfection + Random.Shared.Next(1, snd.FirstInfectionCount + 1), snd.FirstInfectionVolume);
            }
            else if (!string.IsNullOrWhiteSpace(snd.Infect)) Sound.ToPlayer(player, snd.Infect, snd.InfectVolume);
            // The turn itself — from the turned player's position: whoever is nearby hears where it happened.
            var body = player.PlayerPawn.Value;
            if (body is not null && body.IsValid && !string.IsNullOrWhiteSpace(snd.ZombieTurn) && snd.ZombieTurnCount > 0)
                Sound.AtEntity(body, snd.ZombieTurn + Random.Shared.Next(1, snd.ZombieTurnCount + 1), snd.ZombieTurnVolume);
        }
        // Tell the first infected right away what they have: otherwise they won't learn about the leap and vision.
        if (first)
            Texts.To(player, "turned.first_hint", _config.Infection.GuardDuration.ToString("0"));


        // The infector heals: a reward for hunting.
        HealBiter(attacker);

        if (_config.Debug) _log($"turned: {player.PlayerName}, HP {hp}, first: {first}");

        Infected?.Invoke(new InfectedEvent(player, attacker, first));
    }

    /// <summary>
    /// Infected appearance: a custom model if set in the config, with a tint on top.
    /// The tint always works — even when the model stays the same, the infected is recognizable immediately.
    /// Everyone nearby hears the turning sound.
    /// </summary>
    private void ApplyZombieLook(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return;

        var current = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
        if (!string.IsNullOrEmpty(current)) _humanModel[player.Slot] = current;

        var model = _config.Infection.ZombieModel;
        if (!string.IsNullOrWhiteSpace(model) && !ModelsReady())
        {
            // The map manifest lacks our models (cold start): SetModel would crash the server, see ModelsReady.
            _log("infected model not set: the map loaded without our models in its manifest");
        }
        else if (!string.IsNullOrWhiteSpace(model))
        {
            try { pawn.SetModel(model); }
            catch (Exception e) { _log($"infected model '{model}' failed to apply: {e.Message}"); }
        }

        pawn.Render = System.Drawing.Color.FromArgb(255, _tintR, _tintG, _tintB);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

        if (_config.Infection.ZombieScreenEffect)
        {
            pawn.HealthShotBoostExpirationTime = Server.CurrentTime + 2.0f;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flHealthShotBoostExpirationTime");
        }

        var sound = _config.Infection.TransformSound;
        if (!string.IsNullOrWhiteSpace(sound))
        {
            try { pawn.EmitSound(sound); }
            catch (Exception e) { _log($"turn sound '{sound}' failed to play: {e.Message}"); }
        }
    }

    /// <summary>
    /// An infected who turned a human restores health: hunting feeds, idling weakens.
    /// Never heals above their own maximum.
    /// </summary>
    private void HealBiter(CCSPlayerController? attacker)
    {
        if (attacker is null || !attacker.IsValid || !_infected.Contains(attacker.Slot)) return;
        var heal = _config.Infection.HealOnInfect;
        if (heal <= 0) return;

        var pawn = attacker.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid || pawn.Health <= 0) return;

        // The cap is the infected's own maximum; healing never goes above it
        // (4750/5000 must not become 5250/5500).
        // If they are already at full health there is nothing to heal — not an error.
        var (before, max) = RealHealth(attacker) ?? (pawn.Health, pawn.MaxHealth);
        var health = Math.Min(max, before + heal);
        if (health > before) SetZombieHealth(attacker, health, max);

        // Money for the infection, if any, is paid and shown by the Money extension.
        if (!attacker.IsBot && health > before) Texts.To(attacker, "bite.healed", health - before);
    }

    /// <summary>
    /// Infected regeneration: after a few seconds without taking damage the zombie starts restoring a percentage
    /// of its HP every second (by default 5 s and 1%). It replaced HP decay: the round time limit already
    /// keeps rounds from lasting forever, and there is no reason for the infected to lose health for nothing.
    ///
    /// Counted from the last real damage (`OnPlayerHurt`) or from the turn. One step per second of
    /// `regenPercent` of their own maximum, never above it (same cap as HealBiter). The tick runs
    /// four times per second, so the next step is marked by time, not by a counter. No chat messages:
    /// the number in the infected health display visibly grows anyway.
    /// </summary>
    private void RegenTick(double now)
    {
        var delay = _config.Infection.RegenDelay;
        var percent = _config.Infection.RegenPercent;
        if (delay < 0 || percent <= 0) return;

        foreach (var slot in _infected)
        {
            if (!_lastHurt.TryGetValue(slot, out var hurt) || now - hurt < delay) continue;
            if (_regenNext.TryGetValue(slot, out var next) && now < next) continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is null || !player.IsValid) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid || pawn.Health <= 0) continue;

            _regenNext[slot] = now + 1.0;
            var (hp, max) = RealHealth(player) ?? (pawn.Health, pawn.MaxHealth);
            if (hp >= max) continue;

            var step = Math.Max(1, (int)Math.Round(max * percent / 100.0));
            SetZombieHealth(player, Math.Min(max, hp + step), max);
        }
    }

    /// <summary>
    /// The infection must appear in the top-right kill feed — like a regular kill.
    /// For that we fire the same death event the game uses to draw the feed, and mark it with our own flag
    /// so that our own handler does not take it for a real death.
    /// </summary>
    private void AnnounceInfection(CCSPlayerController victim, CCSPlayerController? attacker)
    {
        if (attacker is null || !attacker.IsValid || !_config.Infection.KillFeed) return;
        try
        {
            _fakeDeath = true;
            var death = new EventPlayerDeath(true)
            {
                Userid = victim,
                Attacker = attacker,
                Weapon = "knife",
                Headshot = false,
                // Set the assister explicitly even though there is none. An unset player field in the event
                // does not point to nothing but to whoever comes first — and the game drew someone
                // who was sitting in spectators at the time as the assister in the feed.
                Assister = null,
                Assistedflash = false,
            };
            death.FireEvent(false);
        }
        catch (Exception e)
        {
            _log($"infection event did not reach the kill feed: {e.Message}");
        }
        finally
        {
            _fakeDeath = false;
        }
    }

    /// <summary>Infected killed: out until the end of the round, no respawn.</summary>
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim is null || !victim.IsValid || _fakeDeath) return HookResult.Continue;

        _bleeding.Remove(victim.Slot);
        _shadow.Remove(victim.Slot);
        _restored.Remove(victim.Slot);
        _lastHurt.Remove(victim.Slot);
        _regenNext.Remove(victim.Slot);
        if (!_infected.Remove(victim.Slot)) return HookResult.Continue;

        var snd = _config.Sounds;
        if (snd.Enabled && !string.IsNullOrWhiteSpace(snd.ZombieDie) && snd.ZombieDieCount > 0)
        {
            var body = victim.PlayerPawn.Value;
            if (body is not null && body.IsValid) Sound.AtEntity(body, snd.ZombieDie + Random.Shared.Next(1, snd.ZombieDieCount + 1), snd.ZombieDieVolume);
        }

        var wasFirst = victim.Slot == _firstInfected;
        InfectedKilled?.Invoke(new InfectedKilledEvent(victim, @event.Attacker, wasFirst));
        return HookResult.Continue;
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the player is alive. `Health > 0` alone is not enough: a bot's corpse pawn stays in place and its health
    /// is not always zeroed, so a dead bot counted as a living human and the round did not end.
    /// So we ask the engine directly: `PawnIsAlive` is the state the engine itself keeps
    /// on the controller, and `LifeState` confirms it on the pawn.
    /// </summary>
    private static bool IsAlive(CCSPlayerController player)
    {
        if (!player.IsValid || !player.PawnIsAlive) return false;
        var pawn = player.PlayerPawn.Value;
        return pawn is not null && pawn.IsValid && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE && pawn.Health > 0;
    }

    /// <summary>
    /// How many players are on the teams — alive and dead. Spectators and those not yet joined don't count.
    /// This, not the number alive, is what tells an empty server from a round where everyone died.
    /// </summary>
    private static int PlayersOnTeams()
    {
        var count = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid) continue;
            if (p.Team == CsTeam.CounterTerrorist || p.Team == CsTeam.Terrorist) count++;
        }
        return count;
    }

    private static List<CCSPlayerController> AlivePlayers(CsTeam team)
    {
        var list = new List<CCSPlayerController>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.Team != team) continue;
            if (!IsAlive(p)) continue;
            list.Add(p);
        }
        return list;
    }

    /// <summary>Health lives in the schema: after writing, the engine must be told the field changed.</summary>
    /// <summary>The infected's real health and max when spoofing; null — no spoofing.</summary>
    public (int Hp, int Max)? RealHealth(CCSPlayerController player) =>
        player.IsValid && _shadow.TryGetValue(player.Slot, out var s) ? s : null;

    /// <summary>
    /// Set infected health accounting for spoofing: the real value goes to bookkeeping, the pawn gets no more than the displayed one.
    /// Armor is removed: the engine would subtract less from the pawn than we do from the real value, and the numbers would diverge.
    /// </summary>
    private void SetZombieHealth(CCSPlayerController player, int hp, int max)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn is not null && pawn.IsValid && pawn.ArmorValue > 0)
        {
            pawn.ArmorValue = 0;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        }

        if (!_config.Infection.HpSpoof)
        {
            _shadow.Remove(player.Slot);
            _restored.Remove(player.Slot);
            SetPawnHealth(player, hp, max: max);
            return;
        }

        var cap = Math.Max(1, _config.Infection.HpSpoofShown);
        _shadow[player.Slot] = (hp, max);
        SetPawnHealth(player, Math.Min(hp, cap), max: Math.Min(max, cap));
    }

    /// <summary>
    /// Health spoofing, step one (before the engine processes damage): the pawn gets its REAL health back.
    /// The engine subtracts the damage from it itself — with its own rounding, hit zones and death on the final hit —
    /// and `player_hurt` carries the real damage, so everything downstream counts as before. There are deliberately
    /// no damage calculations of our own here (no point computing it twice).
    /// </summary>
    private void SpoofBefore(CCSPlayerController victim, CCSPlayerPawn pawn)
    {
        if (!_shadow.TryGetValue(victim.Slot, out var real) || real.Hp <= 0) return;
        // The mark is set even when there is nothing to write (real not above displayed — the numbers match):
        // the pawn carries the real value either way, and the post-damage remainder can be accepted.
        _restored.Add(victim.Slot);
        if (pawn.Health == real.Hp) return;
        pawn.Health = real.Hp;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    /// <summary>
    /// Health spoofing, step two (after the engine processes damage): whatever the pawn has left is the
    /// real health; store it and give the pawn the displayed value — at most hpSpoofShown. The client
    /// sees nothing between the steps: the network snapshot goes out at the end of the tick.
    /// </summary>
    private HookResult OnTakeDamagePost(DynamicHook hook)
    {
        var pawn = hook.GetParam<CCSPlayerPawn>(0);
        if (pawn is null || !pawn.IsValid) return HookResult.Continue;
        var victim = pawn.Controller.Value?.As<CCSPlayerController>();
        if (victim is null || !victim.IsValid) return HookResult.Continue;
        if (!_shadow.TryGetValue(victim.Slot, out var real)) return HookResult.Continue;

        var restored = _restored.Remove(victim.Slot);
        var now = pawn.Health;
        if (now <= 0)
        {
            _shadow.Remove(victim.Slot);
            return HookResult.Continue;
        }

        // The pawn's remainder is the real health only if the real value was restored before damage. Otherwise the pawn
        // carries the displayed value, and taking it as real would cut the infected down to 999; in that case the real
        // value is left untouched and we only make sure the pawn shows no more than it should.
        if (restored)
        {
            real = (now, real.Max);
            _shadow[victim.Slot] = real;
        }
        var shown = Math.Min(real.Hp, Math.Max(1, _config.Infection.HpSpoofShown));
        if (pawn.Health != shown)
        {
            pawn.Health = shown;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }
        return HookResult.Continue;
    }

    private static void SetPawnHealth(CCSPlayerController player, int health, int? max = null)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return;
        if (max is not null) pawn.MaxHealth = max.Value;
        pawn.Health = health;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }
}
