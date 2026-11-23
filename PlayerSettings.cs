using System.Globalization;

namespace SSJPlugin;

/// <summary>Where a player's SSJ stats go.</summary>
public enum SsjOutput { Hud, Chat, Both }

public partial class SSJPlugin
{
    /// <summary>
    /// One player's SSJ settings, by SteamID. Enabled, Repeat and Mode (jumps per run) are the plugin's own columns in
    /// ssj_settings, as before. The HUD's choices (where the stats go, the card's spot, size, accent and how long it
    /// stays) are packed into its Hud column as "o=chat;p=xr;s=l", holding only what differs from the server's
    /// defaults in config.json, so a default changed there reaches everyone who never picked that one.
    /// </summary>
    public sealed class PlayerSettings
    {
        public bool Enabled { get; set; }
        public bool Repeat { get; set; }

        /// <summary>Jumps per run, 1..10.</summary>
        public int Mode { get; set; }

        public SsjOutput Output { get; set; }
        public string Spot { get; set; } = "xl";
        public string Size { get; set; } = "m";

        /// <summary>Seconds the card stays after the run's last jump; 0 keeps it until the next run.</summary>
        public int Hold { get; set; } = 6;

        public string Accent { get; set; } = "ice";

        /// <summary>Repeat off: a run was shown since joining or the last reset, so no more are.</summary>
        public bool AlreadyShown { get; set; }

        /// <summary>The player changed something this session: a database row arriving late must not undo it.</summary>
        public bool Touched { get; set; }

        public bool ShowsHud => Output != SsjOutput.Chat;
        public bool ShowsChat => Output != SsjOutput.Hud;

        /// <summary>The spots, in the order the menu and the layouts list them (ssj_hud.css p-*).</summary>
        public static readonly string[] Spots = ["xl", "xr", "xa", "tc", "rm", "br"];
        public static readonly string[] Sizes = ["s", "m", "l"];
        public static readonly int[] Holds = [3, 6, 10, 0];
        public static readonly string[] Accents = ["ice", "orange", "pink", "lime", "white"];

        public const int MaxJumps = 10;

        public static string SpotName(string spot) => spot switch
        {
            "xl" => "Left of the crosshair",
            "xr" => "Right of the crosshair",
            "xa" => "Above the crosshair",
            "tc" => "Top center",
            "rm" => "Right side",
            "br" => "Next to the timer",
            _ => spot,
        };

        public static string OutputName(SsjOutput output) => output switch
        {
            SsjOutput.Chat => "chat",
            SsjOutput.Both => "both",
            _ => "hud",
        };

        public static SsjOutput? ParseOutput(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "hud" => SsjOutput.Hud,
            "chat" => SsjOutput.Chat,
            "both" or "hud+chat" => SsjOutput.Both,
            _ => null,
        };

        public static PlayerSettings Defaults(PluginConfig cfg)
        {
            var s = new PlayerSettings();
            s.ResetToDefaults(cfg);
            return s;
        }

        /// <summary>Every setting back to the server's defaults (the !ssj menu's DEFAULTS).</summary>
        public void ResetToDefaults(PluginConfig cfg)
        {
            var hud = cfg.Hud;
            Enabled = cfg.DefaultEnabled;
            Repeat = cfg.DefaultRepeat;
            Mode = Math.Clamp(cfg.DefaultMaxJumps, 1, MaxJumps);
            Output = ParseOutput(hud.DefaultOutput) ?? SsjOutput.Hud;
            Spot = Pick(hud.DefaultSpot, Spots, "xl");
            Size = Pick(hud.DefaultSize, Sizes, "m");
            Hold = Holds.Contains(hud.DefaultHoldSeconds) ? hud.DefaultHoldSeconds : 6;
            Accent = Pick(hud.DefaultAccent, Accents, "ice");
        }

        /// <summary>The Hud column: only what differs from <paramref name="cfg"/>'s defaults.</summary>
        public string PackHud(PluginConfig cfg)
        {
            var d = Defaults(cfg);
            var parts = new List<string>(5);
            if (Output != d.Output) parts.Add("o=" + OutputName(Output));
            if (Spot != d.Spot) parts.Add("p=" + Spot);
            if (Size != d.Size) parts.Add("s=" + Size);
            if (Hold != d.Hold) parts.Add("h=" + Hold.ToString(CultureInfo.InvariantCulture));
            if (Accent != d.Accent) parts.Add("a=" + Accent);
            return string.Join(';', parts);
        }

        /// <summary>Applies a Hud column on top of the defaults. Unknown keys and values are skipped.</summary>
        public void UnpackHud(string? packed, PluginConfig cfg)
        {
            var d = Defaults(cfg);
            Output = d.Output;
            Spot = d.Spot;
            Size = d.Size;
            Hold = d.Hold;
            Accent = d.Accent;
            if (string.IsNullOrWhiteSpace(packed)) return;

            foreach (var part in packed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part[..eq];
                var value = part[(eq + 1)..];
                switch (key)
                {
                    case "o": Output = ParseOutput(value) ?? Output; break;
                    case "p": Spot = Pick(value, Spots, Spot); break;
                    case "s": Size = Pick(value, Sizes, Size); break;
                    case "h":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && Holds.Contains(h)) Hold = h;
                        break;
                    case "a": Accent = Pick(value, Accents, Accent); break;
                }
            }
        }

        private static string Pick(string? value, string[] options, string fallback)
        {
            var v = value?.Trim().ToLowerInvariant();
            return v != null && options.Contains(v) ? v : fallback;
        }
    }
}
