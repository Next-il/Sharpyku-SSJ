using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using PanoramaManager;
using PlayerSettings = SSJPlugin.SSJPlugin.PlayerSettings;

namespace SSJPlugin.Panels;

/// <summary>One jump's numbers, the same ones the chat line prints.</summary>
internal readonly record struct JumpStat(int Jump, int Speed, double GainPct, int Sync, int Strafes);

/// <summary>
/// The SSJ card (hud/panorama/layout/custom_game/ssj_hud.xml): one row per jump of the run, PRE in the head, the
/// run's average sync and the speed gained since PRE in the foot. It replaces the per-jump chat line; chat stays
/// for players who pick it in !ssj and for anyone the panel cannot reach.
///
/// <para><b>Who draws what.</b> A runner's numbers live in a <see cref="Run"/> per runner slot, recorded for every
/// player SSJ tracks. Each viewer's card shows one run: their own while alive, the watched player's while
/// spectating, a sample while !ssj is open and nothing else is on it. What a viewer's panel holds is cached in its
/// <see cref="PanelWriter"/>, so only a change is ever written.</para>
///
/// <para><b>Rates.</b> Nothing is written per tick. A jump redraws its runner's card at once; a pass 8 times a
/// second handles the rest (the card leaving after the player's hold time, a spectator switching targets, a
/// panel that could not be opened yet) and writes only what changed.</para>
/// </summary>
internal sealed class SsjHud : IDisposable
{
    public const int Rows = PlayerSettings.MaxJumps;
    private const string Layout = "panorama/layout/custom_game/ssj_hud.vxml_c";
    private const string Root = "SsjHudRoot";
    private const string Card = "sj";

    private static readonly string[] SpotClasses = PlayerSettings.Spots.Select(s => "p-" + s).ToArray();
    private static readonly string[] SizeClasses = PlayerSettings.Sizes.Select(s => "sz-" + s).ToArray();
    private static readonly string[] AccentClasses = PlayerSettings.Accents.Select(a => "ac-" + a).ToArray();
    private static readonly string[] GainClasses = ["g-up", "g-flat", "g-dn"];
    private static readonly string[] SyncClasses = ["y-a", "y-b", "y-c", "y-d"];
    private static readonly string[] AvgClasses = ["ay-a", "ay-b", "ay-c", "ay-d"];
    private static readonly string[] TotalClasses = ["tg-up", "tg-dn"];

    private sealed class Run
    {
        public int Pre;
        public readonly JumpStat?[] Jumps = new JumpStat?[Rows];
        public int Latest = -1;

        /// <summary>A run is on the card, from its first takeoff.</summary>
        public bool Started;

        /// <summary>When its last jump came in or the chain broke; -1 while it is still going.</summary>
        public float EndedAt = -1;

        /// <summary>Bumped on every change, so a viewer redraws the rows only when they changed.</summary>
        public int Version;

        public void Clear()
        {
            Pre = 0;
            Array.Clear(Jumps);
            Latest = -1;
            Started = false;
            EndedAt = -1;
            Version++;
        }
    }

    private sealed class Viewer
    {
        public readonly PanelWriter W = new();

        /// <summary>The run whose rows the panel holds: a runner slot, <see cref="SampleSource"/>, or -2 for none.</summary>
        public int Source = -2;
        public int Version = -1;
        public int Mode = -1;
        public bool Preview;
        public float RetryAt;
    }

    private const int SampleSource = -1;

    private readonly SSJPlugin _plugin;
    private readonly Dictionary<int, Run> _runs = new();
    private readonly Dictionary<int, Viewer> _viewers = new();
    private readonly Run _sample = BuildSample();
    private PanelHandle? _panel;

    public SsjHud(SSJPlugin plugin) => _plugin = plugin;

    public bool Enabled => _panel != null;

    /// <summary>Spawns the layout once. A second Spawn would orphan the first entity, so this is guarded.</summary>
    public void Init()
    {
        if (_panel != null) return;
        try
        {
            _panel = Panorama.Spawn(Layout, new LayoutContract
            {
                // Unique server-wide: dialog variables are keyed by the root panel's id.
                RootPanelId = Root,
                RevealClass = "show",
                RowCount = 0,
                CaptureInput = false,
                HideHud = HideHudFlags.None,
                SharedText = false,
            });
            _panel.OnEvent += OnPanelEvent;
        }
        catch (Exception e)
        {
            // SSJ works without its HUD: every jump falls back to the chat line while Enabled is false.
            _plugin.Logger.LogWarning("[SSJ] the HUD failed to start, using chat instead: {Message}", e.Message);
            _panel = null;
        }
    }

    public void Dispose()
    {
        _panel?.Dispose();
        _panel = null;
        _viewers.Clear();
        _runs.Clear();
    }

    private void OnPanelEvent(PanelEvent e)
    {
        if (e.Action != PanelAction.Restored || e.Player is not { IsValid: true } player) return;
        if (!_viewers.TryGetValue(player.Slot, out var v)) return;
        Invalidate(v);
        Draw(player, v);
    }

    /// <summary>Everything this viewer's panel holds is unknown now: the next draw writes it all.</summary>
    private static void Invalidate(Viewer v)
    {
        v.W.Reset();
        v.Source = -2;
        v.Version = -1;
        v.Mode = -1;
    }

    // ================================================================== viewers

    private static bool IsHuman(CCSPlayerController? p) =>
        p is { IsValid: true, IsBot: false, IsHLTV: false } && p.Connected == PlayerConnectedState.Connected;

    public bool Reaches(CCSPlayerController? player) =>
        _panel != null && player is { IsValid: true } && _panel.IsOpenFor(player);

    /// <summary>Opens the player's card session. Safe to repeat; a player it cannot reach is retried every 3s.</summary>
    public void Attach(CCSPlayerController player)
    {
        if (_panel == null || !IsHuman(player)) return;

        if (!_viewers.TryGetValue(player.Slot, out var v))
        {
            v = new Viewer();
            _viewers[player.Slot] = v;
        }

        if (_panel.IsOpenFor(player)) return;
        if (Server.CurrentTime < v.RetryAt) return;
        v.RetryAt = Server.CurrentTime + 3f;

        _panel.Open(player);
        // Open takes off every per-viewer class this handle set for the slot.
        Invalidate(v);
        if (_panel.IsOpenFor(player)) Draw(player, v);
    }

    public void Forget(int slot)
    {
        _viewers.Remove(slot);
        _runs.Remove(slot);
        _panel?.Close(slot);
    }

    /// <summary>New map: server time starts over, so every stored time is meaningless.</summary>
    public void MapStart()
    {
        _runs.Clear();
        foreach (var v in _viewers.Values)
        {
            Invalidate(v);
            v.RetryAt = 0;
        }
    }

    /// <summary>The player changed something in !ssj: redraw their card now.</summary>
    public void SettingsChanged(CCSPlayerController player)
    {
        if (_viewers.TryGetValue(player.Slot, out var v) && Reaches(player)) Draw(player, v);
    }

    /// <summary>While !ssj is open the card shows a sample run when nothing real is on it, so the spot, size, accent
    /// and jump count can be seen.</summary>
    public void SetPreview(CCSPlayerController player, bool on)
    {
        if (!_viewers.TryGetValue(player.Slot, out var v) || v.Preview == on) return;
        v.Preview = on;
        if (Reaches(player)) Draw(player, v);
    }

    // ================================================================== the runner's side

    private Run RunOf(int slot)
    {
        if (!_runs.TryGetValue(slot, out var r))
        {
            r = new Run();
            _runs[slot] = r;
        }

        return r;
    }

    /// <summary>The first jump of a run took off: a fresh card with PRE and empty rows.</summary>
    public void Begin(CCSPlayerController runner, int pre)
    {
        var r = RunOf(runner.Slot);
        r.Clear();
        r.Started = true;
        r.Pre = pre;
        Redraw(runner.Slot);
    }

    /// <summary>A jump landed and was reported.</summary>
    public void Add(CCSPlayerController runner, JumpStat stat)
    {
        if (stat.Jump < 1 || stat.Jump > Rows) return;
        var r = RunOf(runner.Slot);
        if (!r.Started)
        {
            r.Clear();
            r.Started = true;
        }

        r.Jumps[stat.Jump - 1] = stat;
        r.Latest = stat.Jump - 1;
        r.EndedAt = -1;
        r.Version++;
        Redraw(runner.Slot);
    }

    /// <summary>The run's last jump is in, or the chain broke: the card starts its hold time.</summary>
    public void End(CCSPlayerController runner)
    {
        if (!_runs.TryGetValue(runner.Slot, out var r) || !r.Started || r.EndedAt >= 0) return;
        r.EndedAt = Server.CurrentTime;
        Redraw(runner.Slot);
    }

    /// <summary>The run is over and off the card: a timer restart, a respawn, SSJ turned off or its stats reset.</summary>
    public void Clear(int slot)
    {
        if (!_runs.TryGetValue(slot, out var r) || (!r.Started && r.Latest < 0)) return;
        r.Clear();
        Redraw(slot);
    }

    /// <summary>Redraws the runner's own card and every spectator's card that shows this run.</summary>
    private void Redraw(int runnerSlot)
    {
        if (_panel == null) return;
        foreach (var (slot, v) in _viewers)
        {
            if (slot != runnerSlot && v.Source != runnerSlot) continue;
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true } && _panel.IsOpenFor(player)) Draw(player, v);
        }
    }

    /// <summary>8 times a second: attaches players the panel does not reach yet, ends cards whose hold ran out and
    /// follows spectators to their new target. Writes nothing that did not change.</summary>
    public void Tick()
    {
        if (_panel == null) return;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsHuman(player)) continue;
            if (!_viewers.TryGetValue(player.Slot, out var v) || !_panel.IsOpenFor(player))
            {
                Attach(player);
                continue;
            }

            Draw(player, v);
        }
    }

    // ================================================================== drawing

    /// <summary>The player whose run this viewer sees: themselves while alive, the watched player while dead.</summary>
    private static CCSPlayerController? SourceOf(CCSPlayerController viewer)
    {
        if (viewer.PawnIsAlive) return viewer;

        var target = viewer.Pawn.Value?.ObserverServices?.ObserverTarget;
        if (target is not { IsValid: true }) return null;
        var index = target.Index;

        foreach (var p in Utilities.GetPlayers())
            if (p is { IsValid: true } && p.Slot != viewer.Slot && p.PawnIsAlive && p.Pawn.Index == index)
                return p;

        return null;
    }

    private static bool OnCard(Run r, int hold, float now) =>
        r.Started && (r.EndedAt < 0 || hold == 0 || now < r.EndedAt + hold);

    private void Draw(CCSPlayerController player, Viewer v)
    {
        var panel = _panel!;
        var w = v.W;
        var s = _plugin.SettingsFor(player);

        w.OneOf(panel, player, "sj_skin", "ac-" + s.Accent, AccentClasses);
        w.OneOf(panel, player, "sj_pos", "p-" + s.Spot, SpotClasses);
        w.OneOf(panel, player, "sj_sz", "sz-" + s.Size, SizeClasses);

        Run? run = null;
        var source = -2;
        var who = "";

        if (s.Enabled && s.ShowsHud)
        {
            var target = SourceOf(player);
            if (target != null && _runs.TryGetValue(target.Slot, out var r) && OnCard(r, s.Hold, Server.CurrentTime))
            {
                run = r;
                source = target.Slot;
                if (target.Slot != player.Slot) who = target.PlayerName;
            }
            else if (v.Preview)
            {
                run = _sample;
                source = SampleSource;
            }
        }

        if (run == null)
        {
            w.Cls(panel, player, Card, "show", false);
            return;
        }

        var mode = Math.Clamp(s.Mode, 1, Rows);
        if (v.Source != source || v.Version != run.Version || v.Mode != mode)
        {
            DrawRun(player, w, run, mode);
            v.Source = source;
            v.Version = run.Version;
            v.Mode = mode;
        }

        w.Var(panel, player, "sj_who", who);
        w.Cls(panel, player, Card, "spec", who.Length > 0);
        w.Cls(panel, player, Card, "show", true);
    }

    private void DrawRun(CCSPlayerController player, PanelWriter w, Run run, int mode)
    {
        var panel = _panel!;
        w.Var(panel, player, "sj_pre", run.Pre > 0 ? Num(run.Pre) : "-");

        var filled = 0;
        var syncSum = 0;
        var lastSpeed = 0;

        for (var i = 0; i < Rows; i++)
        {
            var row = $"sjr{i}";
            var shown = i < mode;
            w.Cls(panel, player, row, "hidden", !shown);

            if (shown && run.Jumps[i] is { } j)
            {
                w.Var(panel, player, row + "_sp", Num(j.Speed));
                w.Var(panel, player, row + "_g", Gain(j.GainPct));
                w.Var(panel, player, row + "_sy", Num(j.Sync) + "%");
                w.Var(panel, player, row + "_st", Num(j.Strafes));
                w.Cls(panel, player, row, "f", true);
                w.OneOf(panel, player, row, GainClass(j.GainPct), GainClasses);
                w.OneOf(panel, player, row, SyncClass(j.Sync, SyncClasses), SyncClasses);
                filled++;
                syncSum += j.Sync;
                lastSpeed = j.Speed;
            }
            else
            {
                w.Var(panel, player, row + "_sp", "-");
                w.Var(panel, player, row + "_g", "-");
                w.Var(panel, player, row + "_sy", "-");
                w.Var(panel, player, row + "_st", "-");
                w.Cls(panel, player, row, "f", false);
                w.OneOf(panel, player, row, null, GainClasses);
                w.OneOf(panel, player, row, null, SyncClasses);
            }

            w.Cls(panel, player, row, "new", shown && i == run.Latest);
        }

        if (filled > 0)
        {
            var avg = (int)Math.Round(syncSum / (double)filled, MidpointRounding.AwayFromZero);
            w.Var(panel, player, "sj_avg", Num(avg) + "%");
            w.OneOf(panel, player, Card, SyncClass(avg, AvgClasses), AvgClasses);
        }
        else
        {
            w.Var(panel, player, "sj_avg", "-");
            w.OneOf(panel, player, Card, null, AvgClasses);
        }

        if (filled > 0 && run.Pre > 0)
        {
            var gained = lastSpeed - run.Pre;
            w.Var(panel, player, "sj_tot", Signed(gained));
            w.OneOf(panel, player, Card, gained > 0 ? "tg-up" : gained < 0 ? "tg-dn" : null, TotalClasses);
        }
        else
        {
            w.Var(panel, player, "sj_tot", "-");
            w.OneOf(panel, player, Card, null, TotalClasses);
        }
    }

    // ================================================================== format

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>"+9.1%", "−2.4%", "0.0%". A real minus sign: the hyphen reads as a dash at this size.</summary>
    private static string Gain(double pct)
    {
        var text = Math.Abs(pct).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        return pct > 0 ? "+" + text : pct < 0 ? "−" + text : text;
    }

    private static string Signed(int value) =>
        value > 0 ? "+" + Num(value) : value < 0 ? "−" + Num(-value) : "0";

    /// <summary>The chat line's colours: green up, yellow flat, red down.</summary>
    private static string GainClass(double pct) => pct > 0 ? "g-up" : pct < 0 ? "g-dn" : "g-flat";

    /// <summary>The chat line's sync tiers: 80+, 60+, 40+, under 40.</summary>
    private static string SyncClass(int sync, string[] tiers) =>
        sync >= 80 ? tiers[0] : sync >= 60 ? tiers[1] : sync >= 40 ? tiers[2] : tiers[3];

    /// <summary>The run !ssj shows behind the menu when the player has none on their card.</summary>
    private static Run BuildSample()
    {
        var r = new Run { Pre = 276, Started = true };
        (int Speed, double Gain, int Sync, int Strafes)[] rows =
        [
            (302, 9.4, 88, 4), (331, 9.6, 85, 5), (357, 7.9, 81, 5), (380, 6.4, 77, 6), (401, 5.5, 72, 6),
            (419, 4.5, 68, 7), (436, 4.1, 70, 7), (452, 3.7, 64, 8), (466, 3.1, 61, 8), (480, 3.0, 58, 9),
        ];
        for (var i = 0; i < rows.Length; i++)
            r.Jumps[i] = new JumpStat(i + 1, rows[i].Speed, rows[i].Gain, rows[i].Sync, rows[i].Strafes);
        r.Latest = rows.Length - 1;
        return r;
    }
}
