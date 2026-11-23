using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using PanoramaManager;
using PlayerSettings = SSJPlugin.SSJPlugin.PlayerSettings;

namespace SSJPlugin.Panels;

/// <summary>
/// !ssj: the SSJ settings (hud/panorama/layout/custom_game/ssj_menu.xml), clickable, in the timer's !hud look. One
/// page: SSJ on / off, where the stats go (HUD, chat or both), jumps per run, every run or the first only, how long
/// the card stays and its accent on the left; where it goes (a mini map of the screen), its size and the resets on
/// the right. It replaces the CS2MenuManager WASD menu.
///
/// <para>Every click applies at once on the card behind the menu, which shows a sample run while the menu is open,
/// and is saved per player the way the plugin always saved its settings (ssj_settings).</para>
/// </summary>
internal sealed class SsjMenu : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/ssj_menu.vxml_c";
    private const string Root = "SsjMenuRoot";

    private static readonly string[] SpotClasses = PlayerSettings.Spots.Select(s => "p-" + s).ToArray();
    private static readonly string[] SizeClasses = PlayerSettings.Sizes.Select(s => "sz-" + s).ToArray();

    private sealed class Viewer
    {
        public readonly PanelWriter W = new();
        public string Message = "";
    }

    private readonly SSJPlugin _plugin;
    private readonly SsjHud _hud;
    private readonly Dictionary<int, Viewer> _viewers = new();
    private PanelHandle? _panel;

    public SsjMenu(SSJPlugin plugin, SsjHud hud)
    {
        _plugin = plugin;
        _hud = hud;
    }

    public void Init()
    {
        if (_panel != null) return;
        try
        {
            _panel = Panorama.Spawn(Layout, new LayoutContract
            {
                RootPanelId = Root,
                RevealClass = "show",
                CloseButtonId = "sjm_close",
                RowCount = 0,
                CaptureInput = true,
                HideHud = HideHudFlags.None,
                SharedText = false,
            });
            _panel.OnEvent += OnEvent;
        }
        catch (Exception e)
        {
            _plugin.Logger.LogWarning("[SSJ] the !ssj menu failed to start, !ssj takes chat arguments only: {Message}", e.Message);
            _panel = null;
        }
    }

    public void Dispose()
    {
        _panel?.Dispose();
        _panel = null;
        _viewers.Clear();
    }

    public bool IsOpenFor(CCSPlayerController player) => _panel != null && _panel.IsOpenFor(player);

    /// <summary>Opens the settings. False when they could not be shown, so the caller answers in chat.</summary>
    public bool Open(CCSPlayerController player)
    {
        if (_panel == null || player is not { IsValid: true, IsBot: false }) return false;

        _panel.Open(player);
        if (!_panel.IsOpenFor(player)) return false;

        // Open takes off every per-viewer class this handle set, so the cache starts empty too.
        var v = _viewers[player.Slot] = new Viewer();
        Draw(player, v);
        _hud.SetPreview(player, true);
        return true;
    }

    public void Forget(int slot)
    {
        _panel?.Close(slot);
        _viewers.Remove(slot);
    }

    /// <summary>Redraws an open menu after a chat command or a late database load changed the settings.</summary>
    public void Refresh(CCSPlayerController player)
    {
        if (_viewers.TryGetValue(player.Slot, out var v) && IsOpenFor(player)) Draw(player, v);
    }

    // ------------------------------------------------------------------ drawing

    private void Draw(CCSPlayerController player, Viewer v)
    {
        var panel = _panel!;
        var w = v.W;
        var s = _plugin.SettingsFor(player);

        void On(string id, bool on) => w.Cls(panel, player, id, "on", on);

        On("sjm_en_on", s.Enabled);
        On("sjm_en_off", !s.Enabled);

        On("sjm_out_hud", s.Output == SsjOutput.Hud);
        On("sjm_out_chat", s.Output == SsjOutput.Chat);
        On("sjm_out_both", s.Output == SsjOutput.Both);

        for (var n = 1; n <= PlayerSettings.MaxJumps; n++)
            On($"sjm_j_{n}", s.Mode == n);

        On("sjm_rep_on", s.Repeat);
        On("sjm_rep_off", !s.Repeat);

        foreach (var hold in PlayerSettings.Holds)
            On($"sjm_hold_{hold}", s.Hold == hold);

        foreach (var accent in PlayerSettings.Accents)
            On($"sjm_ac_{accent}", s.Accent == accent);

        foreach (var spot in PlayerSettings.Spots)
            w.Cls(panel, player, $"sjm_z_{spot}", "cur", s.Spot == spot);

        foreach (var size in PlayerSettings.Sizes)
            On($"sjm_sz_{size}", s.Size == size);

        // Dimmed, still clickable: what does nothing while SSJ is off, or while the stats only go to chat.
        var off = !s.Enabled;
        var noCard = off || !s.ShowsHud;
        w.Cls(panel, player, "sjm_r_out", "disabled", off);
        w.Cls(panel, player, "sjm_r_jumps", "disabled", off);
        w.Cls(panel, player, "sjm_r_rep", "disabled", off);
        w.Cls(panel, player, "sjm_r_hold", "disabled", noCard);
        w.Cls(panel, player, "sjm_r_ac", "disabled", noCard);
        w.Cls(panel, player, "sjm_wbox", "disabled", noCard);
        w.Cls(panel, player, "sjm_r_size", "disabled", noCard);

        w.OneOf(panel, player, "sjm_mb", "p-" + s.Spot, SpotClasses);
        w.OneOf(panel, player, "sjm_mb", "sz-" + s.Size, SizeClasses);
        w.Cls(panel, player, "sjm_mb", "off", noCard);
        w.Var(panel, player, "sjm_where", PlayerSettings.SpotName(s.Spot));
        w.Var(panel, player, "sjm_msg", v.Message);
    }

    // ------------------------------------------------------------------ input

    private void OnEvent(PanelEvent e)
    {
        if (e.Player is not { IsValid: true } player) return;

        switch (e.Action)
        {
            case PanelAction.Close:
                _viewers.Remove(player.Slot);
                _hud.SetPreview(player, false);
                return;

            case PanelAction.Restored:
                if (!_viewers.TryGetValue(player.Slot, out var restored))
                {
                    _panel?.Close(player);
                    return;
                }

                restored.W.Reset();
                Draw(player, restored);
                return;

            case PanelAction.Button:
                if (_viewers.TryGetValue(player.Slot, out var v)) Click(player, v, e.ElementId);
                return;
        }
    }

    private void Click(CCSPlayerController player, Viewer v, string id)
    {
        if (!id.StartsWith("sjm_", StringComparison.Ordinal)) return;
        var s = _plugin.SettingsFor(player);
        v.Message = "";

        switch (id)
        {
            case "sjm_en_on": _plugin.SetEnabled(player, s, true); break;
            case "sjm_en_off": _plugin.SetEnabled(player, s, false); break;
            case "sjm_out_hud": s.Output = SsjOutput.Hud; break;
            case "sjm_out_chat": s.Output = SsjOutput.Chat; break;
            case "sjm_out_both": s.Output = SsjOutput.Both; break;
            case "sjm_rep_on": s.Repeat = true; break;
            case "sjm_rep_off": s.Repeat = false; break;
            case "sjm_reset":
                _plugin.ResetStats(player, s);
                v.Message = "Stats reset.";
                Draw(player, v);
                return;
            case "sjm_defaults":
                _plugin.ResetSettings(player, s);
                v.Message = "Back to the defaults.";
                break;
            default:
                if (!ApplyValue(s, id)) return;
                break;
        }

        _plugin.SettingsChanged(player, s);
        Draw(player, v);
    }

    /// <summary>The value buttons: jumps, hold, accent, spot and size. False for an id that is none of them.</summary>
    private static bool ApplyValue(PlayerSettings s, string id)
    {
        if (id.StartsWith("sjm_j_", StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(6), out var jumps) && jumps is >= 1 and <= PlayerSettings.MaxJumps)
        {
            s.Mode = jumps;
            return true;
        }

        if (id.StartsWith("sjm_hold_", StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(9), out var hold) && PlayerSettings.Holds.Contains(hold))
        {
            s.Hold = hold;
            return true;
        }

        if (id.StartsWith("sjm_ac_", StringComparison.Ordinal) && PlayerSettings.Accents.Contains(id[7..]))
        {
            s.Accent = id[7..];
            return true;
        }

        if (id.StartsWith("sjm_z_", StringComparison.Ordinal) && PlayerSettings.Spots.Contains(id[6..]))
        {
            s.Spot = id[6..];
            return true;
        }

        if (id.StartsWith("sjm_sz_", StringComparison.Ordinal) && PlayerSettings.Sizes.Contains(id[7..]))
        {
            s.Size = id[7..];
            return true;
        }

        return false;
    }
}
