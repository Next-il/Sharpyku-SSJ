using CounterStrikeSharp.API.Core;
using PanoramaManager;

namespace SSJPlugin.Panels;

/// <summary>
/// What one viewer's panel holds, so only a change is ever written: a dialog variable or class that already has the
/// value is skipped. The same cache as the SharpTimer HUD's (src/Hud/HudLayout.cs). <see cref="Reset"/> it whenever
/// the panel was rebuilt or reopened, since the client no longer has what the cache says.
/// </summary>
internal sealed class PanelWriter
{
    private readonly Dictionary<string, string> _vars = new();
    private readonly Dictionary<(string, string), bool> _classes = new();
    private readonly Dictionary<(string, string), string?> _oneOf = new();

    public void Reset()
    {
        _vars.Clear();
        _classes.Clear();
        _oneOf.Clear();
    }

    public void Var(PanelHandle panel, CCSPlayerController player, string name, string value)
    {
        if (_vars.TryGetValue(name, out var old) && old == value) return;
        _vars[name] = value;
        panel.SetVariableFor(player, name, value);
    }

    public void Cls(PanelHandle panel, CCSPlayerController player, string panelId, string className, bool on)
    {
        var key = (panelId, className);
        if (_classes.TryGetValue(key, out var old) && old == on) return;
        _classes[key] = on;
        panel.SetClassFor(player, panelId, className, on);
    }

    /// <summary>
    /// One class out of a group on a panel (null: none of them). A panel can carry several groups (a spot and a
    /// size, a gain and a sync tier); the group is told apart by its first option. While the cache knows what is on,
    /// only the difference is written; after <see cref="Reset"/> every option is written once.
    /// </summary>
    public void OneOf(PanelHandle panel, CCSPlayerController player, string panelId, string? className, IReadOnlyList<string> options)
    {
        var key = (panelId, options.Count > 0 ? options[0] : "");
        if (_oneOf.TryGetValue(key, out var old))
        {
            if (old == className) return;
            if (old != null) panel.SetClassFor(player, panelId, old, false);
        }
        else
        {
            foreach (var option in options)
                if (option != className) panel.SetClassFor(player, panelId, option, false);
        }

        if (className != null) panel.SetClassFor(player, panelId, className, true);
        _oneOf[key] = className;
    }
}
