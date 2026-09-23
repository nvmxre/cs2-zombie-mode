using System.Text.Json.Serialization;

namespace ZombieMode.Config;

/// <summary>
/// Knockback applied to the infected: <c>force = damage × kb(source) × multiplier</c>.
/// The per-category kb values are relative to each other, not to an absolute scale — if you change
/// them, change them as a set rather than one by one.
/// </summary>
public sealed class KnockbackConfig
{
    /// <summary>Master switch for knockback.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>
    /// Global multiplier for firearms — tune this first, not the individual kb values.
    /// Scaled to game units: with a multiplier of 1 a 30-damage hit would give an impulse of 30 against a
    /// running speed of about 250, i.e. nothing visible. At 10, rifles, shotguns and machine guns felt far
    /// too strong (mostly because pushes stacked — which `cap` now fixes); at 3 knockback almost vanished
    /// (a rifle hit gave ~108 vs. 250 running speed). 6 is the middle ground. Sensible range: 3–10.
    /// </summary>
    [JsonPropertyName("multiplier")] public double Multiplier { get; set; } = 6.0;

    /// <summary>
    /// Separate multiplier for the human knife, so firearm balance changes do not affect it.
    /// The knife is the way to break free from a grab, so its push is kept strong. Sensible range: 6–15.
    /// </summary>
    [JsonPropertyName("knifeMultiplier")] public double KnifeMultiplier { get; set; } = 10.0;

    /// <summary>
    /// Per-weapon knockback factor that overrides the weapon's category: CS2 weapon name → factor
    /// (e.g. <c>"weapon_glock": 0.15</c>).
    ///
    /// Knockback is computed from the damage DEALT, so any damage change drags knockback along with it.
    /// This override lets you separate the two: lowering damage just to reduce knockback (or the other way
    /// round) is wrong, they are different things.
    ///
    /// Weapons not listed here use their category value (pistol, smg, rifle, …).
    /// </summary>
    [JsonPropertyName("weapon")]
    public Dictionary<string, double> Weapon { get; set; } = new()
    {
        // The Glock barely pushes: pistols are already at 0.8, so this leaves it under a fifth of that.
        // It is a fast-firing starter pistol and should not be able to push an infected away with spam.
        ["weapon_glock"] = 0.15,
    };

    /// <summary>kb factor for pistols (relative to rifle = 1.0).</summary>
    [JsonPropertyName("pistol")] public double Pistol { get; set; } = 0.8;
    /// <summary>kb factor for submachine guns.</summary>
    [JsonPropertyName("smg")] public double Smg { get; set; } = 0.7;
    /// <summary>kb factor for rifles; the reference value. Unknown weapons also use it.</summary>
    [JsonPropertyName("rifle")] public double Rifle { get; set; } = 1.0;
    /// <summary>kb factor for machine guns.</summary>
    [JsonPropertyName("mg")] public double Mg { get; set; } = 0.9;
    /// <summary>kb factor for shotguns (applied per pellet; the total is limited by `cap`).</summary>
    [JsonPropertyName("shotgun")] public double Shotgun { get; set; } = 1.6;
    /// <summary>kb factor for sniper rifles.</summary>
    [JsonPropertyName("sniper")] public double Sniper { get; set; } = 2.0;
    /// <summary>kb factor for grenades.</summary>
    [JsonPropertyName("grenade")] public double Grenade { get; set; } = 0.5;

    /// <summary>
    /// kb factors for a light and a heavy human knife stab. The knife is a way to break free, not a backup
    /// weapon, so it pushes harder than any gun. Which stab counts as heavy is set by `knifeHeavyDamage`.
    /// </summary>
    [JsonPropertyName("knifeLight")] public double KnifeLight { get; set; } = 2.5;
    [JsonPropertyName("knifeHeavy")] public double KnifeHeavy { get; set; } = 3.5;

    /// <summary>
    /// Resulting-velocity cap for the knife, in units/s. The general `cap` exists so a shotgun does not become
    /// a "cancel attack" button — but that is exactly what the knife is meant to be, so it gets its own higher cap.
    /// </summary>
    [JsonPropertyName("knifeCap")] public double KnifeCap { get; set; } = 1600;

    /// <summary>
    /// Damage at which a knife hit counts as a heavy stab. The damage event does not say which attack was
    /// used, so it is inferred from the amount: measured about 25 for a light and 40 for a heavy stab;
    /// hit groups and armor shift these numbers, so the threshold sits in between.
    /// </summary>
    [JsonPropertyName("knifeHeavyDamage")] public int KnifeHeavyDamage { get; set; } = 33;

    /// <summary>
    /// Cap on the infected's **resulting** horizontal velocity from firearms, in units/s — not on the
    /// per-hit impulse. The difference matters: a shotgun fires nine pellets, a machine gun fires bursts,
    /// and each pellet arrives as a separate damage event. A per-hit cap does not contain them — pushes
    /// stack up and the infected gets launched. For reference, running speed is about 250.
    /// Sensible range: 400–1000.
    /// </summary>
    [JsonPropertyName("cap")] public double Cap { get; set; } = 700;

    /// <summary>
    /// Vertical share of the impulse (fraction of the force). Without it hits barely read; with a large
    /// value the infected leaves the ground, loses friction and flies much further than intended.
    /// Keep it small: a bump, not a launch. Sensible range: 0.05–0.25.
    /// </summary>
    [JsonPropertyName("up")] public double Up { get; set; } = 0.12;

    /// <summary>
    /// Maximum damage fed into the knockback formula. Hit groups multiply damage — a rifle headshot does
    /// four times more — and without this cap a single headshot pushed harder than a burst to the body.
    /// Does not affect actual damage: the infected still takes whatever the engine computed.
    /// </summary>
    [JsonPropertyName("damageCap")] public int DamageCap { get; set; } = 60;

    /// <summary>
    /// Ground lift: the minimum upward velocity (units/s) given to an infected standing on the ground when hit.
    ///
    /// Without it knockback is invisible no matter how large the horizontal force is. While a player touches
    /// the ground, the movement code recomputes their velocity from input every tick and eats our impulse in
    /// the same frame — the server log shows a fair 259 units, while on screen the infected does not even
    /// twitch. In Source a player only counts as airborne above 140 upward velocity (`NON_JUMP_VELOCITY`),
    /// so the value must be above that threshold or it does nothing.
    ///
    /// It is a fixed number rather than a fraction of damage on purpose: a damage-based vertical (`up`) made
    /// headshots launch the infected, and lowering `up` to stop that killed the knockback itself. A fixed lift
    /// separates the two: "get them off the ground" no longer depends on what hit them and where.
    /// 0 disables it. Sensible range: 145–200.
    /// </summary>
    [JsonPropertyName("lift")] public double Lift { get; set; } = 150;

    /// <summary>Knockback multiplier for the first infected (0–1): they are pushed less, so they can hunt instead of flying around.</summary>
    [JsonPropertyName("firstInfectedResist")] public double FirstInfectedResist { get; set; } = 0.45;

    /// <summary>Log the force of every hit and echo it to the shooter's chat — for tuning.</summary>
    [JsonPropertyName("debug")] public bool Debug { get; set; }
}

/// <summary>The infection itself: hidden phase, zombies, bleeding, abilities, round limit.</summary>
public sealed class InfectionConfig
{

    /// <summary>
    /// Berserk = full invulnerability: while the guard is active, the infected takes no damage at all
    /// (bullets, knife, grenades, fire, falling). When false, the guard only removes knockback and hit slowdown.
    /// </summary>
    [JsonPropertyName("guardGodmode")] public bool GuardGodmode { get; set; } = true;


    /// <summary>
    /// Tint the infected's model orange while the guard is active. Disabled by default in favour of the vein overlay.
    ///
    /// Trade-off: everyone saw the tint, and it told humans why their shots stopped pushing. The veins are
    /// seen only by the infected player — so for humans that cue is gone. Turn this on to bring it back.
    /// </summary>
    [JsonPropertyName("guardTint")] public bool GuardTint { get; set; } = false;

    /// <summary>How many seconds the first infected's guard (berserk, R key) lasts. 0 disables the ability.</summary>
    [JsonPropertyName("guardDuration")] public double GuardDuration { get; set; } = 1.0;

    /// <summary>
    /// Guard cooldown, seconds. Long on purpose: the ability should decide one engagement, not be a
    /// permanent fighting mode.
    /// </summary>
    [JsonPropertyName("guardCooldown")] public double GuardCooldown { get; set; } = 35.0;

    /// <summary>Berserk sound event, played from the first infected's position. Empty name — silent.</summary>
    [JsonPropertyName("guardSound")] public string GuardSound { get; set; } = "zombiemode.berserk";
    /// <summary>Berserk sound volume; 1.0 is the level defined in the sound event.</summary>
    [JsonPropertyName("guardSoundVolume")] public double GuardSoundVolume { get; set; } = 1.0;

    /// <summary>
    /// How many seconds a newly turned infected can pass through players after turning. 0 disables the
    /// pass-through and keeps only the push-away.
    ///
    /// Needed exactly at the transition. During the buy phase teammates are intentionally non-solid
    /// (`mp_solid_teammates 0`): everyone stands in a pile at spawn, and solid bodies only get in the way.
    /// Solidity turns on with the first infection — and if two players were overlapping at that moment,
    /// both would be stuck inside each other. So the new infected stays passable briefly and is pushed away.
    /// </summary>
    [JsonPropertyName("unstickSeconds")] public double UnstickSeconds { get; set; } = 1.2;

    /// <summary>
    /// How many seconds a player must stay inside another player before they are forcibly separated.
    /// 0 disables continuous unsticking, leaving only the one-off unstick on infection.
    ///
    /// Why this is separate from `unstickSeconds`: that one only works at the moment of turning. But players
    /// can get stuck later too — e.g. an infected leaping into a crowd and ending up inside a bot. The
    /// transition has nothing to do with it, and nothing else would help.
    ///
    /// We wait instead of separating immediately for two reasons. In a fight players constantly brush against
    /// each other, and instant separation would turn every touch into a jerk. More importantly, a real stuck
    /// state is one that does not resolve itself: the engine pushes overlapping players apart within a fraction
    /// of a second, so if nothing changed after a second, the player genuinely cannot get out.
    /// </summary>
    [JsonPropertyName("stuckSeconds")] public double StuckSeconds { get; set; } = 0.9;

    /// <summary>
    /// Infected HP: `hpBase` + `hpPerHuman` for every living human, capped at `hpCap`.
    /// The first infected additionally gets `firstInfectedMultiplier`.
    /// </summary>
    [JsonPropertyName("hpBase")] public int HpBase { get; set; } = 1500;
    [JsonPropertyName("hpPerHuman")] public int HpPerHuman { get; set; } = 100;
    [JsonPropertyName("hpCap")] public int HpCap { get; set; } = 6000;

    /// <summary>
    /// Mask the infected's health in the stock HUD. The plugin tracks the real health itself and subtracts
    /// damage in its damage hook, while the pawn only gets the displayed value — no more than `hpSpoofShown`
    /// (so four-digit HP shows as 999). Any custom infected HUD should show the real value.
    /// </summary>
    [JsonPropertyName("hpSpoof")] public bool HpSpoof { get; set; } = true;
    [JsonPropertyName("hpSpoofShown")] public int HpSpoofShown { get; set; } = 999;

    /// <summary>
    /// Model path for the infected (e.g. <c>characters/models/…/model.vmdl</c>). Empty string — the model is
    /// not changed. The model must be precached, otherwise setting it can crash the server.
    /// </summary>
    [JsonPropertyName("zombieModel")] public string ZombieModel { get; set; } = "";

    /// <summary>
    /// Full-screen color overlay for the infected's view, drawn by a HUD panel instead of the stock
    /// health-shot screen effect.
    ///
    /// Why not the stock effect: the `HealthShot` flashes raise exposure on their own, which washes out the
    /// picture on dark maps instead of darkening it. A layer does not touch exposure and works with darkness.
    /// The layer requires the client-side HUD addon; players without it get no color grading at all.
    /// Disabled by default: the radial gradient produced visible banding rings on dark maps.
    /// </summary>
    [JsonPropertyName("zombieVisionLayer")] public bool ZombieVisionLayer { get; set; } = false;

    /// <summary>
    /// Periodic stock health-shot screen effect on the infected, as cheap color grading — the only
    /// post-effect the server can enable for a single player. Disabled by default: `HealthShot` raises
    /// exposure and looks bad on night maps; it can be fine on day maps.
    /// </summary>
    [JsonPropertyName("zombieScreenEffect")] public bool ZombieScreenEffect { get; set; } = true;

    /// <summary>How often to repeat the infected screen effect, seconds. A steady rhythm of 2–3 s works best.</summary>
    [JsonPropertyName("screenEffectInterval")] public double ScreenEffectInterval { get; set; } = 3.5;

    /// <summary>Infected model tint as "R G B" (0–255 each). Applied always, even if the model is not changed.</summary>
    [JsonPropertyName("zombieTint")] public string ZombieTint { get; set; } = "150 60 55";

    /// <summary>
    /// Extra sound event played on infection, on top of the zombie turn sound (`zombieTurn` in the sounds section).
    /// Empty — silent (the stock Player.Death event sounds like a player dying, so it is not a good choice).
    /// </summary>
    [JsonPropertyName("transformSound")] public string TransformSound { get; set; } = "";

    /// <summary>
    /// Leap strength along the view direction, units/s. The leap is bound to the E key; the first infected
    /// gets the full value, regular infected get `leapOthersFactor` of it. For reference: running in CS2 is
    /// about 250 u/s, a normal jump gives about 301 upward. 1200 is roughly five times running speed.
    /// Sensible range: 600–1500.
    /// </summary>
    [JsonPropertyName("leapPower")] public double LeapPower { get; set; } = 1200;

    /// <summary>Extra upward velocity added on top of the engine's normal jump, units/s — so even looking at the floor gives some lift.</summary>
    [JsonPropertyName("leapUp")] public double LeapUp { get; set; } = 300;
    /// <summary>
    /// Leap cooldown, seconds; shared by all infected. The cooldown matters more than the strength:
    /// below about 6 s the first infected flies all over the map.
    /// </summary>
    [JsonPropertyName("leapCooldown")] public double LeapCooldown { get; set; } = 8.0;
    /// <summary>Leap sound event, played from the leaper's position. Empty — silent.</summary>
    [JsonPropertyName("leapSound")] public string LeapSound { get; set; } = "zombiemode.leap";
    /// <summary>Leap sound volume; 1.0 is the level defined in the sound event.</summary>
    [JsonPropertyName("leapSoundVolume")] public double LeapSoundVolume { get; set; } = 0.9;

    /// <summary>
    /// Regular infected leap as a fraction of the first infected's leap (0–1). Both the view-direction
    /// strength and the upward bonus are scaled. Regular infected use the same `leapCooldown`.
    /// 0 — regular infected cannot leap at all.
    /// </summary>
    // 0.75 rather than half: at 0.5 the leap felt sluggish.
    [JsonPropertyName("leapOthersFactor")] public double LeapOthersFactor { get; set; } = 0.75;

    /// <summary>
    /// HP restored to an infected who turns a human. A reward for hunting: whoever infects heals immediately.
    /// 0 disables it.
    /// </summary>
    [JsonPropertyName("healOnInfect")] public int HealOnInfect { get; set; } = 500;

    /// <summary>
    /// Infected regeneration. `regenDelay` — seconds without taking damage before regeneration starts;
    /// `regenPercent` — HP restored per second as a percentage of the infected's own maximum HP; never heals
    /// above the maximum. Any damage resets the delay. 0 in either key disables regeneration.
    /// (With a round time limit in place, there is no need for passive HP decay on the infected.)
    /// </summary>
    [JsonPropertyName("regenDelay")] public double RegenDelay { get; set; } = 5.0;
    [JsonPropertyName("regenPercent")] public double RegenPercent { get; set; } = 1.0;

    /// <summary>
    /// Minimum number of players on teams (humans and bots) required for infection to start at all.
    ///
    /// Why: a single player on an empty server would become the first infected and be left alone — nobody to
    /// infect, no way to win, the round just ticks until the time limit. Two is the minimum at which anything
    /// happens. 0 disables the check.
    /// </summary>
    [JsonPropertyName("minToStart")] public int MinToStart { get; set; } = 2;

    /// <summary>
    /// Round time limit, seconds: if the infected have not infected everyone by then, the infected die and
    /// humans win. Without a limit, a round where the last human barricaded themselves in a corner drags on
    /// forever. 0 — no limit; the round ends only when one side wins outright. Sensible range: 300–600.
    /// </summary>
    [JsonPropertyName("roundLimitSeconds")] public double RoundLimitSeconds { get; set; } = 420;

    /// <summary>How many seconds before the round limit to post a chat warning. 0 — no warning.</summary>
    [JsonPropertyName("roundLimitWarning")] public double RoundLimitWarning { get; set; } = 60;

    /// <summary>
    /// Air strike at the round limit: `strikeLead` seconds before the limit everyone hears an aircraft; at the
    /// limit there is an explosion, C4 particles on every infected, optional screen shake, and the infected die.
    /// </summary>
    [JsonPropertyName("strikeEnabled")] public bool StrikeEnabled { get; set; } = true;
    /// <summary>
    /// Use a real bomb for the explosion (`planted_c4` with a zero timer in the middle of the map). Since the
    /// CS2 update of July 2026 its blast wave spreads across the map from the epicenter, fades around corners
    /// and does not go through walls, and the health bar shows a damage preview. false — a custom particle and
    /// `env_explosion` instead.
    /// </summary>
    [JsonPropertyName("strikeC4")] public bool StrikeC4 { get; set; } = true;
    /// <summary>Seconds after the explosion to kill any infected the blast did not reach and end the round.</summary>
    [JsonPropertyName("strikeFinishSeconds")] public double StrikeFinishSeconds { get; set; } = 2.5;
    /// <summary>Bomb height above the map center, units. The bomb itself is invisible; do not move it far down — the wave will not travel below the floor.</summary>
    [JsonPropertyName("strikeBombZ")] public double StrikeBombZ { get; set; } = 4;
    /// <summary>Seconds before the bomb that the aircraft is heard. The stock airplane event has a ten-second fade-in: with a lead of four it is inaudible.</summary>
    [JsonPropertyName("strikeLead")] public double StrikeLead { get; set; } = 9;
    /// <summary>Aircraft sound event and its volume.</summary>
    [JsonPropertyName("strikeJetEvent")] public string StrikeJetEvent { get; set; } = "baggage.ctspawn.airplanes";
    [JsonPropertyName("strikeJetVolume")] public double StrikeJetVolume { get; set; } = 1.5;
    /// <summary>Explosion sound event and its volume.</summary>
    [JsonPropertyName("strikeExplodeEvent")] public string StrikeExplodeEvent { get; set; } = "c4.explode";
    [JsonPropertyName("strikeExplodeVolume")] public double StrikeExplodeVolume { get; set; } = 1.0;
    /// <summary>Explosion particle at the map center. Must be declared in the precache manifest; takes effect from the next map load.</summary>
    [JsonPropertyName("strikeParticle")] public string StrikeParticle { get; set; } = "particles/explosions_fx/explosion_c4_500.vpcf";
    /// <summary>Screen shake for humans: amplitude and duration in seconds. 0 — no shake (the explosion effect is usually enough).</summary>
    [JsonPropertyName("strikeShake")] public double StrikeShake { get; set; } = 0;
    [JsonPropertyName("strikeShakeSeconds")] public double StrikeShakeSeconds { get; set; } = 2.5;
    /// <summary>
    /// Blast wave (non-C4 mode): a single `env_explosion` at the map center — push and damage within the radius
    /// (the default covers the whole map; a typical map is about 5000 units across). Humans are not hurt by it.
    /// </summary>
    [JsonPropertyName("strikeDamage")] public int StrikeDamage { get; set; } = 1000;
    [JsonPropertyName("strikeRadius")] public int StrikeRadius { get; set; } = 6000;

    /// <summary>Show infections in the kill feed (top right).</summary>
    [JsonPropertyName("killFeed")] public bool KillFeed { get; set; } = true;

    /// <summary>HP multiplier for the first infected (applied on top of the regular infected HP).</summary>
    [JsonPropertyName("firstInfectedMultiplier")] public double FirstInfectedMultiplier { get; set; } = 2.5;

    /// <summary>
    /// Bleeding after a bite: `bleedDamage` HP every `bleedInterval` seconds; each new bite shortens the interval
    /// by `bleedStep` seconds, but not below `bleedMinInterval`; the first tick comes `bleedFirstTickDelay`
    /// seconds after the bite. When bleeding brings HP to 0, the human turns.
    /// </summary>
    [JsonPropertyName("bleedDamage")] public int BleedDamage { get; set; } = 5;
    [JsonPropertyName("bleedInterval")] public double BleedInterval { get; set; } = 1.0;
    [JsonPropertyName("bleedStep")] public double BleedStep { get; set; } = 0.15;
    [JsonPropertyName("bleedMinInterval")] public double BleedMinInterval { get; set; } = 0.5;
    [JsonPropertyName("bleedFirstTickDelay")] public double BleedFirstTickDelay { get; set; } = 2.0;

    /// <summary>
    /// Map scale by map name: small | mid | large (roughly up to 10, up to 30 and more than 30 players).
    /// The plugin cannot detect the scale by itself — neither from spawns nor from map size — hence the table.
    /// Unlisted maps are small.
    /// </summary>
    [JsonPropertyName("mapScale")] public Dictionary<string, string> MapScale { get; set; } = new()
    {
        ["de_dust2"] = "mid",
        ["night_bs_dust2"] = "mid",
        // Acre is big enough for large lobbies (up to ~60 players).
        ["cs_acre_06"] = "large",
    };

    /// <summary>
    /// Extra buy time by map scale, seconds (scale → seconds). Shifts both the safe window and the whole
    /// infection window. Scales not listed get no bonus.
    /// </summary>
    [JsonPropertyName("safeBonus")] public Dictionary<string, int> SafeBonus { get; set; } = new()
    {
        ["mid"] = 5,
        ["large"] = 10,
    };

    public int SafeBonusFor(string? map)
    {
        if (string.IsNullOrEmpty(map)) return 0;
        foreach (var (name, scale) in MapScale)
        {
            if (!string.Equals(name, map, StringComparison.OrdinalIgnoreCase)) continue;
            return SafeBonus.TryGetValue(scale, out var bonus) ? bonus : 0;
        }
        return 0;
    }

    /// <summary>
    /// Hidden-phase timings by number of living humans: a safe window for buying and taking cover, then the
    /// first infection happens at a random moment inside [windowMin, windowMax]. You can make it faster with
    /// fewer humans by adding steps. Steps are checked from the highest `minPlayers` down; the first match wins.
    ///
    /// The safe window is the buy time: buying ends with the first infection. The default is a single step for
    /// any player count — 20 s to buy, then the first infected appears 0–15 s later. Longer waits felt tedious.
    /// </summary>
    [JsonPropertyName("timings")] public List<HiddenPhaseTiming> Timings { get; set; } = new()
    {
        new HiddenPhaseTiming { MinPlayers = 0, Safe = 20, WindowMin = 20, WindowMax = 35 },
    };

    /// <summary>Step for the number of humans, shifted by the map's buy-time bonus (`SafeBonusFor`).</summary>
    public HiddenPhaseTiming TimingFor(int humans, string? map = null)
    {
        var bonus = SafeBonusFor(map);
        foreach (var t in Timings.OrderByDescending(t => t.MinPlayers))
            if (humans >= t.MinPlayers) return t.Shift(bonus);
        return new HiddenPhaseTiming { MinPlayers = 0, Safe = 20, WindowMin = 20, WindowMax = 35 }.Shift(bonus);
    }
}

/// <summary>One step of the timing table: applies when there are at least `minPlayers` living humans.</summary>
public sealed class HiddenPhaseTiming
{
    /// <summary>Minimum number of living humans for this step to apply.</summary>
    [JsonPropertyName("minPlayers")] public int MinPlayers { get; set; }
    /// <summary>Safe window from round start, seconds: nobody can turn before it ends. This is also the buy time.</summary>
    [JsonPropertyName("safe")] public double Safe { get; set; }
    /// <summary>Earliest moment of the first infection, seconds from round start. Should be ≥ `safe`.</summary>
    [JsonPropertyName("windowMin")] public double WindowMin { get; set; }
    /// <summary>Latest moment of the first infection, seconds from round start. Should be ≥ `windowMin`.</summary>
    [JsonPropertyName("windowMax")] public double WindowMax { get; set; }

    /// <summary>The same step shifted by `seconds`: the map-scale buy-time bonus.</summary>
    public HiddenPhaseTiming Shift(double seconds) => seconds == 0
        ? this
        : new HiddenPhaseTiming { MinPlayers = MinPlayers, Safe = Safe + seconds, WindowMin = WindowMin + seconds, WindowMax = WindowMax + seconds };
}

/// <summary>
/// Atmosphere sounds. All of them are sound events from a client-side sound addon; an empty name means
/// silence, and a wrong name is silence without a crash (see Runtime.Sound). The sound wrapper cannot stop
/// a sound, so the heartbeat is cut into short loops that are restarted while bleeding lasts: once bleeding
/// stops, it plays for at most one more clip.
/// Volumes: 1.0 is the level defined in the sound event itself.
/// </summary>
public sealed class SoundsConfig
{
    /// <summary>Master switch for all atmosphere sounds.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>Round start music — to everyone. Put the event in the Music mixer group so the CS2 music volume slider affects it.</summary>
    [JsonPropertyName("roundStart")] public string RoundStart { get; set; } = "zombiemode.round_start";
    // The volume parameter did not audibly change the loudness of Music-group events (three successive
    // reductions made no difference). Bake the music loudness into the sound events themselves and keep 1.0
    // here; the same applies to ambience, theme and win music.
    [JsonPropertyName("roundStartVolume")] public double RoundStartVolume { get; set; } = 1.0;

    /// <summary>
    /// Drum stinger. Played privately to the future first infected when the round start music ends
    /// (`stingerDelay` seconds after round start, but no later than one second before they turn); also played
    /// to everyone who turns later. The first infection of the round is announced by the scream/stinger below instead.
    /// </summary>
    [JsonPropertyName("infect")] public string Infect { get; set; } = "zombiemode.infect";
    [JsonPropertyName("infectVolume")] public double InfectVolume { get; set; } = 0.9;
    [JsonPropertyName("stingerDelay")] public double StingerDelay { get; set; } = 19.5;

    /// <summary>Heartbeat for wounded humans: three clips by remaining HP; clip lengths in seconds let them be restarted back to back.</summary>
    [JsonPropertyName("heartbeatSlow")] public string HeartbeatSlow { get; set; } = "zombiemode.heartbeat_slow";
    [JsonPropertyName("heartbeatMedium")] public string HeartbeatMedium { get; set; } = "zombiemode.heartbeat_medium";
    [JsonPropertyName("heartbeatFast")] public string HeartbeatFast { get; set; } = "zombiemode.heartbeat_fast";
    [JsonPropertyName("heartbeatSlowSeconds")] public double HeartbeatSlowSeconds { get; set; } = 6.0;
    [JsonPropertyName("heartbeatMediumSeconds")] public double HeartbeatMediumSeconds { get; set; } = 6.0;
    [JsonPropertyName("heartbeatFastSeconds")] public double HeartbeatFastSeconds { get; set; } = 4.0;
    /// <summary>Below this HP the medium tempo plays; below `heartbeatFastBelow` — the fast one.</summary>
    [JsonPropertyName("heartbeatMediumBelow")] public int HeartbeatMediumBelow { get; set; } = 60;
    [JsonPropertyName("heartbeatFastBelow")] public int HeartbeatFastBelow { get; set; } = 30;
    [JsonPropertyName("heartbeatVolume")] public double HeartbeatVolume { get; set; } = 0.8;

    /// <summary>
    /// Countdown to the end of buy time: events `countdownPrefix` + N (from `countdownFrom` down to 1),
    /// to everyone, N seconds before the first infection.
    /// </summary>
    [JsonPropertyName("countdown")] public bool Countdown { get; set; } = true;
    [JsonPropertyName("countdownPrefix")] public string CountdownPrefix { get; set; } = "zombiemode.count_";
    [JsonPropertyName("countdownFrom")] public int CountdownFrom { get; set; } = 10;
    [JsonPropertyName("countdownVolume")] public double CountdownVolume { get; set; } = 0.8;

    /// <summary>
    /// Infected voice: idle growls, pain and death. Played FROM the zombie's position with falloff: humans can
    /// tell by the growl where zombies are coming from. A hidden infected stays silent until they turn.
    /// Events are `prefix` + number 1…count, picked at random. Keep each count in sync with the events that
    /// actually exist in your addon — missing numbers play as silence.
    /// </summary>
    [JsonPropertyName("zombieIdle")] public string ZombieIdle { get; set; } = "zombiemode.zombie_idle_";
    [JsonPropertyName("zombieIdleCount")] public int ZombieIdleCount { get; set; } = 6;
    /// <summary>Pause between growls of one zombie, seconds: random between min and max.</summary>
    [JsonPropertyName("zombieIdleMin")] public double ZombieIdleMin { get; set; } = 6;
    [JsonPropertyName("zombieIdleMax")] public double ZombieIdleMax { get; set; } = 14;
    [JsonPropertyName("zombieIdleVolume")] public double ZombieIdleVolume { get; set; } = 0.8;
    [JsonPropertyName("zombiePain")] public string ZombiePain { get; set; } = "zombiemode.zombie_pain_";
    [JsonPropertyName("zombiePainCount")] public int ZombiePainCount { get; set; } = 12;
    /// <summary>At most once per this many seconds per zombie — otherwise a burst of fire turns pain sounds into crackling.</summary>
    [JsonPropertyName("zombiePainGap")] public double ZombiePainGap { get; set; } = 0.7;
    [JsonPropertyName("zombiePainVolume")] public double ZombiePainVolume { get; set; } = 0.9;
    [JsonPropertyName("zombieDie")] public string ZombieDie { get; set; } = "zombiemode.zombie_die_";
    [JsonPropertyName("zombieDieCount")] public int ZombieDieCount { get; set; } = 6;
    [JsonPropertyName("zombieDieVolume")] public double ZombieDieVolume { get; set; } = 1.0;

    /// <summary>
    /// More infected sounds. Attack — the infected's knife swing, from their position, at most once per
    /// `zombieAttackGap` (the infected only has a knife, so any "shot" is a swing). Burn — plays instead of the
    /// pain sound while burning in molotov/incendiary fire, at most once per `zombieBurnGap` (clips are 2–3 s).
    /// Turn — from the position of the player who turned, audible nearby (the first infected is additionally
    /// heard map-wide, see `firstScream`/`firstInfection`). Empty name or count 0 — silent.
    /// </summary>
    [JsonPropertyName("zombieAttack")] public string ZombieAttack { get; set; } = "zombiemode.zombie_attack_";
    [JsonPropertyName("zombieAttackCount")] public int ZombieAttackCount { get; set; } = 0;   // 0 — knife swings are silent by default
    [JsonPropertyName("zombieAttackGap")] public double ZombieAttackGap { get; set; } = 0.5;
    [JsonPropertyName("zombieAttackVolume")] public double ZombieAttackVolume { get; set; } = 0.8;
    [JsonPropertyName("zombieBurn")] public string ZombieBurn { get; set; } = "zombiemode.zombie_burn_";
    [JsonPropertyName("zombieBurnCount")] public int ZombieBurnCount { get; set; } = 6;
    [JsonPropertyName("zombieBurnGap")] public double ZombieBurnGap { get; set; } = 2.0;
    [JsonPropertyName("zombieBurnVolume")] public double ZombieBurnVolume { get; set; } = 0.9;
    [JsonPropertyName("zombieTurn")] public string ZombieTurn { get; set; } = "zombiemode.zombie_turn_";
    [JsonPropertyName("zombieTurnCount")] public int ZombieTurnCount { get; set; } = 3;
    [JsonPropertyName("zombieTurnVolume")] public double ZombieTurnVolume { get; set; } = 0.9;

    /// <summary>Scream of the first infected on turning — to everyone on the map, equally loud. Empty — no scream.</summary>
    [JsonPropertyName("firstScream")] public string FirstScream { get; set; } = "";   // empty by default; the first infection is announced by `firstInfection`
    [JsonPropertyName("firstScreamVolume")] public double FirstScreamVolume { get; set; } = 1.0;

    /// <summary>
    /// First infected stinger — to everyone on the map, on top of the scream. Events `prefix` + 1…count, picked at
    /// random; empty name — scream only.
    /// </summary>
    [JsonPropertyName("firstInfection")] public string FirstInfection { get; set; } = "zombiemode.first_infection_";
    [JsonPropertyName("firstInfectionCount")] public int FirstInfectionCount { get; set; } = 2;
    [JsonPropertyName("firstInfectionVolume")] public double FirstInfectionVolume { get; set; } = 0.9;

    /// <summary>
    /// Round end music — to everyone, depending on the winner. Events `winHuman`/`winZombie` + 1…count.
    /// Note: if the tracks are longer than the delay between rounds (`mp_round_restart_delay`), the tail keeps
    /// playing under the next round's music — there is no way to stop it. Put the events in the Music mixer group.
    /// </summary>
    [JsonPropertyName("winHuman")] public string WinHuman { get; set; } = "zombiemode.win_human_";
    [JsonPropertyName("winHumanCount")] public int WinHumanCount { get; set; } = 6;
    [JsonPropertyName("winZombie")] public string WinZombie { get; set; } = "zombiemode.win_zombie_";
    [JsonPropertyName("winZombieCount")] public int WinZombieCount { get; set; } = 4;
    [JsonPropertyName("winVolume")] public double WinVolume { get; set; } = 1.0;   // loudness is baked into the event (see roundStartVolume)

    /// <summary>
    /// Round ambience: a loop (`ambienceSeconds` long) to everyone, restarted by a global plugin timer that is not
    /// tied to rounds — so exactly one copy plays at any time. Put the event in the Music mixer group so the
    /// music slider affects it.
    /// </summary>
    [JsonPropertyName("ambience")] public string Ambience { get; set; } = "zombiemode.ambience";   // empty — no ambience
    [JsonPropertyName("ambienceSeconds")] public double AmbienceSeconds { get; set; } = 60.1;
    [JsonPropertyName("ambienceVolume")] public double AmbienceVolume { get; set; } = 1.0;   // loudness is baked into the event (see roundStartVolume)

    /// <summary>
    /// Occasional music theme (`themeSeconds` long). It should not play every round or it gets old: it plays every
    /// `themeEveryRounds`-th round, to everyone, `themeDelay` seconds after round start — after the round start
    /// music ends. The ambience is not restarted while the theme plays. The track cannot be stopped: if the round
    /// ends early, the tail plays under the next round start. 0 rounds — never play. Put the event in the Music
    /// mixer group.
    /// </summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "";
    [JsonPropertyName("themeEveryRounds")] public int ThemeEveryRounds { get; set; } = 4;
    [JsonPropertyName("themeDelay")] public double ThemeDelay { get; set; } = 20.0;
    [JsonPropertyName("themeSeconds")] public double ThemeSeconds { get; set; } = 134.1;
    [JsonPropertyName("themeVolume")] public double ThemeVolume { get; set; } = 1.0;   // loudness is baked into the event (see roundStartVolume)
}
