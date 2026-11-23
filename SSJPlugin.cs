using System.Collections.Concurrent;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using PanoramaManager;
using SharpTimerAPI;
using SharpTimerAPI.Events;
using SSJPlugin.Panels;

namespace SSJPlugin;

public partial class SSJPlugin : BasePlugin
{
    public override string ModuleAuthor => "EliteGames, Next-il";
    public override string ModuleName => "SSJ-Plugin";
    public override string ModuleVersion => "2.2.0-next";

    // ─── Sync algorithm constants (from FL-StrafeMaster) ───
    private const float  AccelDeadzone          = 0.20f;
    private const float  TurnDeadzoneRad        = 0.0035f;
    private const float  JumpImpulseDelta       = 150f;
    private const float  FallingVzThreshold     = -20f;

    private readonly ConcurrentDictionary<int, PlayerData> _players = new();
    private readonly ConcurrentDictionary<ulong, PlayerSettings> _settings = new();
    private ISharpTimerEventSender? _stEventSender;
    private string? _dbConnectionString;
    private PluginConfig _config = new();

    // Next-il: the Panorama HUD (Panels/). Null when Hud.Enabled is false or the layout could not be spawned; every
    // jump then goes to chat as before.
    private SsjHud? _hud;
    private SsjMenu? _menu;

    /// <summary>The table check at load. Loads and saves wait for it, so they know whether the Hud column is there.</summary>
    private Task _schemaReady = Task.CompletedTask;

    /// <summary>ssj_settings has the Hud column (ssj-hud-settings.sql). Without it the HUD choices last a session.</summary>
    private volatile bool _hudColumn;

    /// <summary>SteamIDs with a save queued; settings are written 2s after the last change, or on disconnect.</summary>
    private readonly HashSet<ulong> _pendingSaves = new();

    // ─── Plugin Config (auto-generated JSON) ───
    public class PluginConfig
    {
        public string ChatTag { get; set; } = "[SSJ]";
        public string ChatTagColor { get; set; } = "DarkBlue";
        public bool StartzoneOnly { get; set; } = true;
        public int DefaultMaxJumps { get; set; } = 6;
        public bool DefaultEnabled { get; set; } = true;
        public bool DefaultRepeat { get; set; } = true;
        public int MinAirTicksToReport { get; set; } = 2;
        public int PrintThrottleTicks { get; set; } = 8;
        public int GroundSettleTicksAir { get; set; } = 3;
        public int ChainResetGroundTicks { get; set; } = 24;
        public DatabaseConfig Database { get; set; } = new();
        public HudConfig Hud { get; set; } = new();
    }

    public class DatabaseConfig
    {
        public bool Enabled { get; set; } = false;
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 3306;
        public string Database { get; set; } = "BhopServer";
        public string User { get; set; } = "root";
        public string Password { get; set; } = "";
        public string TableName { get; set; } = "ssj_settings";
    }

    /// <summary>"Hud" in config.json. Every key is optional: a config without the section gets these defaults.</summary>
    public class HudConfig
    {
        /// <summary>The Panorama card and the clickable !ssj menu. False brings back chat only, with !ssj taking
        /// chat arguments. Takes effect on a plugin load.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Where a player's stats go until they pick: hud, chat or both.</summary>
        public string DefaultOutput { get; set; } = "hud";

        /// <summary>xl (left of the crosshair), xr (right of it), xa (above it), tc (top center), rm (right side)
        /// or br (next to the timer card).</summary>
        public string DefaultSpot { get; set; } = "xl";

        /// <summary>s, m or l.</summary>
        public string DefaultSize { get; set; } = "m";

        /// <summary>Seconds the card stays after a run's last jump: 3, 6 or 10, or 0 for until the next run.</summary>
        public int DefaultHoldSeconds { get; set; } = 6;

        /// <summary>ice, orange, pink, lime or white: the timer HUD's accents.</summary>
        public string DefaultAccent { get; set; } = "ice";
    }

    public override void Load(bool hotReload)
    {
        LoadConfig();
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnMapStart>(_ => _hud?.MapStart());
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

        HudLoad();

        if (hotReload)
        {
            foreach (var p in Utilities.GetPlayers())
            {
                if (p is not { IsValid: true, IsBot: false, IsHLTV: false }) continue;
                _players[p.Slot] = new PlayerData();
                if (p.SteamID != 0)
                {
                    var steamId = p.SteamID;
                    _ = Task.Run(async () => await LoadPlayerSettingsAsync(steamId));
                }
                _hud?.Attach(p);
            }
        }
    }

    public override void Unload(bool hotReload)
    {
        foreach (var steamId in _pendingSaves.ToList())
            FlushSave(steamId);

        _menu?.Dispose();
        _hud?.Dispose();
        _menu = null;
        _hud = null;
        try { Panorama.Shutdown(); } catch { /* never started, or already down */ }
    }

    private void HudLoad()
    {
        if (!_config.Hud.Enabled)
        {
            Logger.LogInformation("[SSJ] HUD off (config.json Hud.Enabled = false), stats go to chat.");
            return;
        }

        try
        {
            Panorama.Init(this);
            _hud = new SsjHud(this);
            _hud.Init();
            if (!_hud.Enabled)
            {
                _hud = null;
                return;
            }

            _menu = new SsjMenu(this, _hud);
            _menu.Init();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[SSJ] Panorama failed to start, stats go to chat.");
            _hud = null;
            _menu = null;
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            _stEventSender = new PluginCapability<ISharpTimerEventSender>("sharptimer:event_sender").Get();
            if (_stEventSender != null)
            {
                _stEventSender.STEventSender += OnSharpTimerEvent;
            }
        }
        catch { Logger.LogWarning("[SSJ] SharpTimer API not found. SSJ will track all jumps."); }
    }

    private void OnSharpTimerEvent(object? sender, ISharpTimerPlayerEvent e)
    {
        if (e is StartTimerEvent startEvent && startEvent.Player != null && startEvent.Player.IsValid)
        {
            var player = startEvent.Player;
            var pd = _players.GetOrAdd(player.Slot, _ => new PlayerData());
            CloseRun(player, pd);
            pd.TimerStarted = true;
            pd.ResetChain();
        }
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && !player.IsBot)
        {
            var pd = _players.GetOrAdd(player.Slot, _ => new PlayerData());
            CloseRun(player, pd);
            pd.TimerStarted = false;
            pd.ResetChain();
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && !player.IsBot)
        {
            ulong steamId = player.SteamID;
            if (steamId != 0)
                _ = Task.Run(async () => await LoadPlayerSettingsAsync(steamId));
            _hud?.Attach(player);
        }
        return HookResult.Continue;
    }

    private void OnClientDisconnect(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is { IsValid: true } && player.SteamID != 0)
            FlushSave(player.SteamID);

        _players.TryRemove(slot, out _);
        _menu?.Forget(slot);
        _hud?.Forget(slot);
    }

    /// <summary>
    /// A run is over before its chain broke (a timer restart or a respawn): its card goes, and with Repeat off a run
    /// that showed anything counts as the one shown.
    /// </summary>
    private void CloseRun(CCSPlayerController player, PlayerData pd)
    {
        if (pd.ShownInChain && player.SteamID != 0 && _settings.TryGetValue(player.SteamID, out var cfg))
            cfg.AlreadyShown = true;
        pd.ShownInChain = false;
        _hud?.Clear(player.Slot);
    }

    // ─── Config Loading ───
    private void LoadConfig()
    {
        string cfgDir = Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "SSJ-Plugin");
        string cfgPath = Path.Combine(cfgDir, "config.json");

        // Remove old database.json if it exists
        string oldDbPath = Path.Combine(cfgDir, "database.json");
        if (File.Exists(oldDbPath) && !File.Exists(cfgPath))
        {
            try { File.Delete(oldDbPath); } catch { }
        }

        if (!File.Exists(cfgPath))
        {
            Directory.CreateDirectory(cfgDir);
            _config = new PluginConfig();
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
            Logger.LogInformation("[SSJ] Created default config.json at {Path}", cfgPath);
        }
        else
        {
            // Read only. A config.json without the Hud section (every server before 2.2.0) gets its defaults, and the
            // file is never rewritten: it can sit on a mount shared by many servers.
            try
            {
                _config = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(cfgPath)) ?? new PluginConfig();
                _config.Database ??= new DatabaseConfig();
                _config.Hud ??= new HudConfig();
                Logger.LogInformation("[SSJ] Config loaded.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[SSJ] Failed to load config.json, using defaults.");
                _config = new PluginConfig();
            }
        }

        // Setup database if enabled
        if (_config.Database.Enabled)
        {
            var db = _config.Database;
            _dbConnectionString = $"Server={db.Host};Port={db.Port};Database={db.Database};User={db.User};Password={db.Password};";
            _schemaReady = Task.Run(EnsureTableAsync);
        }
    }

    private string GetTagColor()
    {
        return _config.ChatTagColor.ToLower() switch
        {
            "darkblue" => ChatColors.DarkBlue.ToString()!,
            "blue" => ChatColors.Blue.ToString()!,
            "lightblue" => ChatColors.LightBlue.ToString()!,
            "red" => ChatColors.Red.ToString()!,
            "darkred" => ChatColors.DarkRed.ToString()!,
            "green" => ChatColors.Green.ToString()!,
            "lime" => ChatColors.Lime.ToString()!,
            "gold" => ChatColors.Gold.ToString()!,
            "yellow" => ChatColors.Yellow.ToString()!,
            "purple" => ChatColors.Purple.ToString()!,
            "magenta" => ChatColors.Magenta.ToString()!,
            "olive" => ChatColors.Olive.ToString()!,
            "orange" => ChatColors.Orange.ToString()!,
            "grey" => ChatColors.Grey.ToString()!,
            "lightred" => ChatColors.LightRed.ToString()!,
            "lightpurple" => ChatColors.LightPurple.ToString()!,
            _ => ChatColors.DarkBlue.ToString()!
        };
    }

    private string Tag => $"{GetTagColor()}{_config.ChatTag}{ChatColors.Default}";

    private void Reply(CCSPlayerController player, string message) => player.PrintToChat($" {Tag} {message}");

    // ─── DB Table Creation ───
    private async Task EnsureTableAsync()
    {
        if (_dbConnectionString == null) return;
        try
        {
            await using var conn = new MySqlConnection(_dbConnectionString);
            await conn.OpenAsync();
            await using (var cmd = conn.CreateCommand())
            {
                // A fresh install gets the Hud column here. An existing table keeps its shape: the column is added
                // by hand with ssj-hud-settings.sql, never by the plugin.
                cmd.CommandText = $@"
                    CREATE TABLE IF NOT EXISTS {_config.Database.TableName} (
                        SteamID BIGINT UNSIGNED PRIMARY KEY,
                        Enabled TINYINT NOT NULL DEFAULT 1,
                        RepeatMode TINYINT NOT NULL DEFAULT 1,
                        MaxJumps INT NOT NULL DEFAULT 6,
                        Hud VARCHAR(128) NOT NULL DEFAULT ''
                    );";
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT COUNT(*) FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = 'Hud'";
                cmd.Parameters.AddWithValue("@t", _config.Database.TableName);
                _hudColumn = Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
            }

            if (_hudColumn)
                Logger.LogInformation("[SSJ] Database table ready.");
            else
                Logger.LogWarning("[SSJ] {Table} has no Hud column: SSJ on/off, repeat and jumps are saved, the HUD " +
                                  "choices (chat or HUD, spot, size, accent, hold) last until the player leaves. " +
                                  "Run ssj-hud-settings.sql to keep them.", _config.Database.TableName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[SSJ] Failed to create ssj_settings table.");
        }
    }

    // ─── Load Player Settings from DB ───
    private async Task LoadPlayerSettingsAsync(ulong steamId)
    {
        if (_dbConnectionString == null) return;
        try
        {
            await _schemaReady;
            var hudColumn = _hudColumn;

            await using var conn = new MySqlConnection(_dbConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = hudColumn
                ? $"SELECT Enabled, RepeatMode, MaxJumps, Hud FROM {_config.Database.TableName} WHERE SteamID = @sid"
                : $"SELECT Enabled, RepeatMode, MaxJumps FROM {_config.Database.TableName} WHERE SteamID = @sid";
            cmd.Parameters.AddWithValue("@sid", steamId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return;

            var enabled = reader.GetInt32(0) == 1;
            var repeat = reader.GetInt32(1) == 1;
            var mode = reader.GetInt32(2);
            var hud = hudColumn && !reader.IsDBNull(3) ? reader.GetString(3) : null;

            // Settings are only ever touched on the game thread.
            Server.NextFrame(() =>
            {
                var settings = _settings.GetOrAdd(steamId, _ => PlayerSettings.Defaults(_config));
                if (settings.Touched) return;   // changed in !ssj before the row arrived: that choice wins

                settings.Enabled = enabled;
                settings.Repeat = repeat;
                settings.Mode = Math.Clamp(mode, 1, PlayerSettings.MaxJumps);
                if (hudColumn) settings.UnpackHud(hud, _config);

                var player = Utilities.GetPlayers().FirstOrDefault(p => p is { IsValid: true } && p.SteamID == steamId);
                if (player == null) return;
                _hud?.SettingsChanged(player);
                _menu?.Refresh(player);
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[SSJ] Failed to load settings for {SteamId}", steamId);
        }
    }

    private sealed record SaveRow(ulong SteamId, bool Enabled, bool Repeat, int Mode, string Hud);

    /// <summary>Saves the player's settings 2s after the last change, so clicking through the menu is one write.</summary>
    private void QueueSave(ulong steamId)
    {
        if (_dbConnectionString == null || steamId == 0 || !_pendingSaves.Add(steamId)) return;
        AddTimer(2f, () => FlushSave(steamId));
    }

    private void FlushSave(ulong steamId)
    {
        if (!_pendingSaves.Remove(steamId) || !_settings.TryGetValue(steamId, out var s)) return;
        var row = new SaveRow(steamId, s.Enabled, s.Repeat, s.Mode, s.PackHud(_config));
        _ = Task.Run(async () => await SavePlayerSettingsAsync(row));
    }

    // ─── Save Player Settings to DB ───
    private async Task SavePlayerSettingsAsync(SaveRow row)
    {
        if (_dbConnectionString == null) return;
        try
        {
            await _schemaReady;
            var hudColumn = _hudColumn;

            await using var conn = new MySqlConnection(_dbConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = hudColumn
                ? $@"
                    INSERT INTO {_config.Database.TableName} (SteamID, Enabled, RepeatMode, MaxJumps, Hud)
                    VALUES (@sid, @en, @rep, @mj, @hud)
                    ON DUPLICATE KEY UPDATE Enabled=@en, RepeatMode=@rep, MaxJumps=@mj, Hud=@hud"
                : $@"
                    INSERT INTO {_config.Database.TableName} (SteamID, Enabled, RepeatMode, MaxJumps)
                    VALUES (@sid, @en, @rep, @mj)
                    ON DUPLICATE KEY UPDATE Enabled=@en, RepeatMode=@rep, MaxJumps=@mj";
            cmd.Parameters.AddWithValue("@sid", row.SteamId);
            cmd.Parameters.AddWithValue("@en", row.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@rep", row.Repeat ? 1 : 0);
            cmd.Parameters.AddWithValue("@mj", row.Mode);
            if (hudColumn) cmd.Parameters.AddWithValue("@hud", row.Hud);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[SSJ] Failed to save settings for {SteamId}", row.SteamId);
        }
    }

    // ─── Settings, for the plugin, the HUD and the menu ───

    /// <summary>The player's settings, or the server's defaults until their row loads.</summary>
    internal PlayerSettings SettingsFor(CCSPlayerController player) =>
        _settings.GetOrAdd(player.SteamID, _ => PlayerSettings.Defaults(_config));

    /// <summary>Something in the player's settings changed: save it and redraw their card and menu.</summary>
    internal void SettingsChanged(CCSPlayerController player, PlayerSettings settings)
    {
        settings.Touched = true;
        QueueSave(player.SteamID);
        _hud?.SettingsChanged(player);
        _menu?.Refresh(player);
    }

    /// <summary>SSJ on or off. Off also ends the run on the card and the chain being tracked.</summary>
    internal void SetEnabled(CCSPlayerController player, PlayerSettings settings, bool on)
    {
        if (settings.Enabled == on) return;
        settings.Enabled = on;
        if (!on)
        {
            if (_players.TryGetValue(player.Slot, out var pd))
                pd.ResetChain();
            _hud?.Clear(player.Slot);
        }
    }

    /// <summary>The old menu's Reset Stats: the current chain and the card start over, and Repeat off shows again.</summary>
    internal void ResetStats(CCSPlayerController player, PlayerSettings settings)
    {
        if (_players.TryGetValue(player.Slot, out var data))
            data.ResetChain();
        settings.AlreadyShown = false;
        _hud?.Clear(player.Slot);
    }

    /// <summary>Every setting back to config.json's defaults.</summary>
    internal void ResetSettings(CCSPlayerController player, PlayerSettings settings)
    {
        var wasOn = settings.Enabled;
        settings.ResetToDefaults(_config);
        if (wasOn && !settings.Enabled)
        {
            if (_players.TryGetValue(player.Slot, out var pd))
                pd.ResetChain();
            _hud?.Clear(player.Slot);
        }
    }

    // ─── Air State: per-jump frame data (velocity-based sync à la FL-StrafeMaster) ───
    public class AirState
    {
        public bool  InAir;
        public int   StableAirCount;
        public int   StableGroundCount;
        public int   TotalAirTicks;       // Total frames in air
        public int   GoodTicks;           // Frames where turn direction == accel direction
        public float StartHorizSpeed;     // Speed at takeoff
        public float LastVx, LastVy, LastVz;
        public float LastHeadingDeg;      // Heading in degrees for strafe projection
        public int   StrafeCount;         // Number of strafe direction changes
        public int   LastTurnSign;        // Last turn direction (+1 left, -1 right)
        public long  LastPrintTick;

        public void Reset()
        {
            InAir = false;
            TotalAirTicks = 0;
            GoodTicks = 0;
            StartHorizSpeed = 0f;
            StrafeCount = 0;
            LastTurnSign = 0;
        }
    }

    // ─── Player Data: bhop chain tracking ───
    public class PlayerData
    {
        public AirState Air { get; set; } = new();
        public int   JumpNumber { get; set; }
        public float PreSpeed { get; set; }          // Speed before first jump in chain
        public float PrevTakeoffSpeed { get; set; }  // Takeoff speed of previous jump
        public float LastTakeoffSpeed { get; set; }  // Takeoff speed of current jump
        public bool  TimerStarted { get; set; }      // Only track SSJ after leaving startzone
        public bool  ShownInChain { get; set; }      // A jump of this chain went to the HUD or chat

        public void ResetChain()
        {
            JumpNumber = 0;
            PreSpeed = 0f;
            PrevTakeoffSpeed = 0f;
            LastTakeoffSpeed = 0f;
            Air.Reset();
        }
    }

    // ─── Main Tick: velocity cross/dot sync + ground entity landing detection ───
    private void OnTick()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is null || !player.IsValid || player.IsBot || player.IsHLTV || !player.PawnIsAlive)
                continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn is null || !pawn.IsValid) continue;

            ulong steamId = player.SteamID;
            if (steamId == 0) continue;

            var cfg = _settings.GetOrAdd(steamId, _ => PlayerSettings.Defaults(_config));
            if (!cfg.Enabled) continue;

            var pd = _players.GetOrAdd(player.Slot, _ => new PlayerData());

            // Only track SSJ after timer started (player left startzone)
            if (_config.StartzoneOnly && _stEventSender != null && !pd.TimerStarted) continue;
            var st = pd.Air;

            var v = pawn.AbsVelocity;
            float vx = v.X, vy = v.Y, vz = v.Z;
            float horizSpeed = MathF.Sqrt(vx * vx + vy * vy);

            // Ground detection via GroundEntity (more reliable than FL_ONGROUND for autobhop)
            bool rawOnGround = false;
            try { rawOnGround = pawn.GroundEntity.Value is not null; } catch { }

            // ─── Ground state machine ───
            if (rawOnGround)
            {
                st.StableGroundCount++;
                st.StableAirCount = 0;

                if (st.InAir && st.StableGroundCount >= 1)
                {
                    // LANDING confirmed
                    float endHoriz = MathF.Sqrt(st.LastVx * st.LastVx + st.LastVy * st.LastVy);
                    HandleLanding(player, pd, cfg, st, endHoriz);
                    st.Reset();
                }

                // Chain reset if on ground too long — SSJ stops until next startzone
                if (st.StableGroundCount > _config.ChainResetGroundTicks && pd.JumpNumber > 0)
                {
                    // Next-il: the run on the card ends here and starts its hold time. Repeat off: a run that
                    // showed anything is the one shown (it used to be cleared again on the next chain's first jump,
                    // so Repeat off did nothing).
                    if (pd.ShownInChain) cfg.AlreadyShown = true;
                    pd.ShownInChain = false;
                    _hud?.End(player);

                    pd.ResetChain();
                    pd.TimerStarted = false;
                }
            }
            else
            {
                st.StableAirCount++;
                st.StableGroundCount = 0;

                if (!st.InAir && st.StableAirCount >= _config.GroundSettleTicksAir)
                {
                    // TAKEOFF confirmed
                    st.InAir = true;
                    st.TotalAirTicks = 0;
                    st.GoodTicks = 0;
                    st.StartHorizSpeed = horizSpeed;
                    st.StrafeCount = 0;
                    st.LastTurnSign = 0;
                    st.LastVx = vx; st.LastVy = vy; st.LastVz = vz;
                    st.LastHeadingDeg = SafeHeadingDeg(vx, vy);

                    // Track bhop chain
                    bool isBhop = pd.JumpNumber > 0; // if already in chain, it's a bhop (ground settle was short)
                    if (!isBhop && pd.JumpNumber > 0)
                        pd.ResetChain();

                    pd.PrevTakeoffSpeed = pd.LastTakeoffSpeed;
                    pd.LastTakeoffSpeed = horizSpeed;
                    pd.JumpNumber++;

                    if (pd.JumpNumber == 1)
                    {
                        pd.PreSpeed = horizSpeed;
                        pd.ShownInChain = false;
                        if (cfg.Repeat) cfg.AlreadyShown = false;

                        // A new run on the card: PRE and empty rows, filled as the jumps land.
                        if (!cfg.AlreadyShown)
                            _hud?.Begin(player, RoundSpeed(pd.PreSpeed));
                    }
                }
            }

            // ─── Autobhop re-jump detection (in-air landing+jump in same tick) ───
            if (st.InAir)
            {
                float dvz = vz - st.LastVz;
                bool wasFalling = st.LastVz <= FallingVzThreshold;

                if (wasFalling && dvz >= JumpImpulseDelta)
                {
                    // Player landed and re-jumped in same tick (autobhop)
                    float endHoriz = MathF.Sqrt(st.LastVx * st.LastVx + st.LastVy * st.LastVy);
                    HandleLanding(player, pd, cfg, st, endHoriz);

                    // Immediately start a new jump
                    st.InAir = true;
                    st.TotalAirTicks = 0;
                    st.GoodTicks = 0;
                    st.StartHorizSpeed = horizSpeed;
                    st.StrafeCount = 0;
                    st.LastTurnSign = 0;
                    st.StableAirCount = _config.GroundSettleTicksAir;
                    st.StableGroundCount = 0;
                    st.LastVx = vx; st.LastVy = vy; st.LastVz = vz;
                    st.LastHeadingDeg = SafeHeadingDeg(vx, vy);

                    pd.PrevTakeoffSpeed = pd.LastTakeoffSpeed;
                    pd.LastTakeoffSpeed = horizSpeed;
                    pd.JumpNumber++;
                }
            }

            // ─── In-air sync tracking (cross/dot product on velocity vectors) ───
            if (st.InAir)
            {
                st.TotalAirTicks++;

                float pvx = st.LastVx, pvy = st.LastVy;
                float cvx = vx, cvy = vy;

                // Cross/dot product to get rotation angle between velocity frames
                float dot   = pvx * cvx + pvy * cvy;
                float cross = pvx * cvy - pvy * cvx;
                float ang   = MathF.Atan2(cross, dot);

                // Determine turn direction
                int turnSign = 0;
                if      (ang >  TurnDeadzoneRad) turnSign = +1;  // Turning left
                else if (ang < -TurnDeadzoneRad) turnSign = -1;  // Turning right

                // Project acceleration onto perpendicular axis (left of heading)
                const float Deg2Rad = (float)(Math.PI / 180.0);
                float hPrevRad = st.LastHeadingDeg * Deg2Rad;
                float leftPX   = -MathF.Sin(hPrevRad);
                float leftPY   =  MathF.Cos(hPrevRad);

                float dvx      = cvx - pvx;
                float dvy      = cvy - pvy;
                float projLeft = dvx * leftPX + dvy * leftPY;

                // Determine strafe acceleration direction
                int sideSign = 0;
                if      (projLeft >  AccelDeadzone) sideSign = +1;
                else if (projLeft < -AccelDeadzone) sideSign = -1;

                // SYNC: turn direction matches strafe acceleration direction
                if (turnSign != 0 && sideSign != 0 && turnSign == sideSign)
                    st.GoodTicks++;

                // Count strafe direction changes
                if (turnSign != 0 && turnSign != st.LastTurnSign)
                {
                    st.StrafeCount++;
                    st.LastTurnSign = turnSign;
                }

                st.LastVx = cvx; st.LastVy = cvy; st.LastVz = vz;
                st.LastHeadingDeg = SafeHeadingDeg(cvx, cvy);
            }
            else
            {
                st.LastVz = vz;
            }
        }

        // The card's timed steps (hold time, spectators, panels not open yet), 8 times a second.
        if (_hud != null && Server.TickCount % 8 == 0)
            _hud.Tick();
    }

    private static int RoundSpeed(float speed) => (int)MathF.Round(speed, MidpointRounding.AwayFromZero);

    // ─── Handle Landing: report SSJ stats (HUD card and/or chat) ───
    private void HandleLanding(CCSPlayerController player, PlayerData pd, PlayerSettings cfg, AirState st, float endHorizSpeed)
    {
        if (st.TotalAirTicks < _config.MinAirTicksToReport) return;
        if (pd.JumpNumber <= 0 || pd.JumpNumber > cfg.Mode) return;
        if (!cfg.Repeat && cfg.AlreadyShown) return;

        long tick = Server.TickCount;
        if (tick - st.LastPrintTick < _config.PrintThrottleTicks) return;

        int sync = st.TotalAirTicks > 0
            ? (int)MathF.Round(100f * st.GoodTicks / st.TotalAirTicks)
            : 0;

        float gain = endHorizSpeed - st.StartHorizSpeed;
        double gainPct = st.StartHorizSpeed > 0
            ? Math.Round((double)gain / st.StartHorizSpeed * 100.0, 1)
            : 0;

        // Next-il: the card gets every reported jump (spectators see it too); chat when the player keeps it, or
        // when the card cannot reach them.
        _hud?.Add(player, new JumpStat(pd.JumpNumber, RoundSpeed(endHorizSpeed), gainPct, sync, st.StrafeCount));
        if (cfg.ShowsChat || _hud == null || !_hud.Reaches(player))
            PrintJumpToChat(player, pd, st, endHorizSpeed, sync, gainPct);

        st.LastPrintTick = tick;
        pd.ShownInChain = true;

        if (pd.JumpNumber >= cfg.Mode)
        {
            cfg.AlreadyShown = true;
            _hud?.End(player);
        }
    }

    /// <summary>The original chat line, unchanged.</summary>
    private void PrintJumpToChat(CCSPlayerController player, PlayerData pd, AirState st, float endHorizSpeed, int sync, double gainPct)
    {
        string syncColor = sync switch
        {
            >= 80 => ChatColors.Green.ToString()!,
            >= 60 => ChatColors.Yellow.ToString()!,
            >= 40 => ChatColors.Gold.ToString()!,
            _     => ChatColors.Red.ToString()!
        };

        string gainColor = gainPct switch
        {
            > 0  => ChatColors.Green.ToString()!,
            0    => ChatColors.Yellow.ToString()!,
            _    => ChatColors.Red.ToString()!
        };

        string gainSign = gainPct >= 0 ? "+" : "";

        string tag = Tag;

        if (pd.JumpNumber == 1)
        {
            player.PrintToChat(
                $" {tag} Jump {ChatColors.LightBlue}{pd.JumpNumber}{ChatColors.Default}" +
                $" | Pre: {ChatColors.Gold}{pd.PreSpeed:F0}{ChatColors.Default}" +
                $" | Speed: {ChatColors.LightBlue}{endHorizSpeed:F0}{ChatColors.Default}" +
                $" | Gain: {gainColor}{gainSign}{gainPct}%{ChatColors.Default}" +
                $" | Sync: {syncColor}{sync}%{ChatColors.Default}" +
                $" | Strafes: {ChatColors.Purple}{st.StrafeCount}{ChatColors.Default}");
        }
        else
        {
            player.PrintToChat(
                $" {tag} Jump {ChatColors.LightBlue}{pd.JumpNumber}{ChatColors.Default}" +
                $" | Speed: {ChatColors.LightBlue}{endHorizSpeed:F0}{ChatColors.Default}" +
                $" | Gain: {gainColor}{gainSign}{gainPct}%{ChatColors.Default}" +
                $" | Sync: {syncColor}{sync}%{ChatColors.Default}" +
                $" | Strafes: {ChatColors.Purple}{st.StrafeCount}{ChatColors.Default}");
        }
    }

    // ─── Heading from velocity vector ───
    private static float SafeHeadingDeg(float x, float y)
    {
        if (x == 0f && y == 0f) return 0f;
        return MathF.Atan2(y, x) * (180f / MathF.PI);
    }

    // ─── !ssj: the HUD settings menu, or chat arguments ───
    // Next-il fork: the CS2MenuManager WASD menu is gone. !ssj opens the clickable Panorama menu (Panels/SsjMenu.cs);
    // the arguments below work with or without it, and are the only way in when the HUD is off.
    [ConsoleCommand("css_ssj", "SSJ settings: !ssj, or !ssj on|off|1-10|hud|chat|both|repeat|reset")]
    public void OnSSJCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot) return;

        var settings = SettingsFor(player);
        var arg = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : "";

        if (arg.Length == 0)
        {
            if (_menu != null && _menu.Open(player)) return;
            Reply(player, Status(settings));
            Reply(player, $"{ChatColors.Grey}!ssj on|off · !ssj 1-10 (jumps) · !ssj hud|chat|both · !ssj repeat · !ssj reset");
            return;
        }

        switch (arg)
        {
            case "on":
            case "off":
                SetEnabled(player, settings, arg == "on");
                Reply(player, $"SSJ {(settings.Enabled ? $"{ChatColors.Green}on" : $"{ChatColors.Red}off")}{ChatColors.Default}.");
                break;

            case "toggle":
                SetEnabled(player, settings, !settings.Enabled);
                Reply(player, $"SSJ {(settings.Enabled ? $"{ChatColors.Green}on" : $"{ChatColors.Red}off")}{ChatColors.Default}.");
                break;

            case "repeat":
                settings.Repeat = !settings.Repeat;
                Reply(player, settings.Repeat
                    ? $"Every run: {ChatColors.Green}on{ChatColors.Default}."
                    : $"Every run: {ChatColors.Red}off{ChatColors.Default}, your first run only until !ssj reset.");
                break;

            case "reset":
                ResetStats(player, settings);
                Reply(player, "Stats reset.");
                _menu?.Refresh(player);
                return;

            default:
                if (int.TryParse(arg, out var jumps) && jumps is >= 1 and <= PlayerSettings.MaxJumps)
                {
                    settings.Mode = jumps;
                    Reply(player, $"Showing {ChatColors.LightBlue}{jumps}{ChatColors.Default} jumps.");
                    break;
                }

                if (PlayerSettings.ParseOutput(arg) is { } output)
                {
                    settings.Output = output;
                    Reply(player, output switch
                    {
                        SsjOutput.Chat => "SSJ stats go to chat.",
                        SsjOutput.Both => "SSJ stats go to the HUD and chat.",
                        _ => "SSJ stats go to the HUD.",
                    });
                    break;
                }

                Reply(player, $"{ChatColors.Grey}!ssj on|off · !ssj 1-10 (jumps) · !ssj hud|chat|both · !ssj repeat · !ssj reset");
                return;
        }

        SettingsChanged(player, settings);
    }

    private static string Status(PlayerSettings s) =>
        $"SSJ {(s.Enabled ? $"{ChatColors.Green}on" : $"{ChatColors.Red}off")}{ChatColors.Default}" +
        $" · {ChatColors.LightBlue}{s.Mode}{ChatColors.Default} jumps" +
        $" · {PlayerSettings.OutputName(s.Output)}" +
        $" · every run {(s.Repeat ? "on" : "off")}";
}
