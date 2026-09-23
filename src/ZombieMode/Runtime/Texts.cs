using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using Microsoft.Extensions.Localization;
using ZombieMode.Api;

namespace ZombieMode.Runtime;

/// <summary>
/// Player-facing text. Strings live in <c>lang/&lt;language&gt;.json</c> next to the plugin and are picked by
/// each player's game language, so a translation is a new json file and nothing else.
/// Where the text is shown is up to <see cref="Sink"/>: a centre-screen alert by default, or your own HUD.
/// </summary>
public static class Texts
{
    public static IStringLocalizer? Localizer { get; set; }

    public static INoticeSink Sink { get; set; } = new CenterAlertSink();

    public static string For(CCSPlayerController player, string key, params object[] args)
    {
        if (Localizer is null) return key;
        try { return Localizer.ForPlayer(player, key, args); }
        catch { return key; }
    }

    public static void To(CCSPlayerController? player, string key, params object[] args)
    {
        if (player is null || !player.IsValid || player.IsBot) return;
        Show(player, For(player, key, args));
    }

    public static void ToAll(string key, params object[] args)
    {
        foreach (var player in Utilities.GetPlayers())
            To(player, key, args);
    }

    /// <summary>Show an already built text. Empty text is skipped: it would leave the previous one hanging.</summary>
    public static void Show(CCSPlayerController player, string text)
    {
        if (!player.IsValid || player.IsBot || string.IsNullOrWhiteSpace(text)) return;
        try { Sink.Show(player, text); }
        catch
        {
            try { player.PrintToCenterAlert(text); } catch { /* a notice is never worth a crash */ }
        }
    }
}

/// <summary>Default notice sink: the engine's centre-screen alert.</summary>
public sealed class CenterAlertSink : INoticeSink
{
    public void Show(CCSPlayerController player, string text) => player.PrintToCenterAlert(text);
}
