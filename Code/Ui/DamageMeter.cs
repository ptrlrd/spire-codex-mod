using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using SpireCodex.Producer;

namespace SpireCodex.Ui;

// A small live damage readout in the corner during combat, fed by DamageTracker through the
// snapshot's combat block. Styled to match the F5 companion panel (DeckImagePanel): the same
// dark card chrome and gold / text / muted palette. The player can drag it anywhere; the
// position persists across sessions (damage-meter-pos.json under %APPDATA%/SpireCodex). Until
// it's been moved it defaults to the bottom-left corner. Hidden when there's no live combat, or
// when the Damage meter setting is off. Toggle: ShowDamageMeter.
public partial class DamageMeter : Node
{
    // F5 overlay design tokens (src/windows/in_game/overlay.css :root), mirrored here so the
    // meter matches the companion panel.
    private static readonly Color Bg = Color.FromHtml("16181d");
    private static readonly Color Border = Color.FromHtml("2c313c");
    private const string Accent = "#d7a84a"; // brand gold
    private const string Text = "#e6e6e6";
    private const string Muted = "#c8ccd5";

    private double _accum;
    private CanvasLayer _layer = null!;
    private RichTextLabel? _panel;

    // Overwolf coexistence: cached "is the Overwolf overlay up?" check, re-read ~1x/sec.
    private DateTimeOffset _owChecked = DateTimeOffset.MinValue;
    private bool _owActive;

    // Drag + persisted position. _pos is the user-set top-left (null until moved/loaded); _moved
    // gates whether the auto bottom-left default still applies.
    private bool _dragging;
    private Vector2 _dragOffset;
    private Vector2? _pos;
    private bool _moved;

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; damage meter not started");
            return;
        }
        var n = new DamageMeter { Name = "SpireCodexDamageMeter" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, n);
        MainFile.Logger.Info("damage meter started");
    }

    public override void _Ready()
    {
        _layer = new CanvasLayer { Layer = 199 }; // corner readout, below the card plates
        AddChild(_layer);
        LoadPos();
    }

    public override void _Process(double delta)
    {
        _accum += delta;
        if (_accum < 0.15) return;
        _accum = 0;

        // Stand down when the Overwolf overlay is up: it has its own damage meter (fed by this
        // mod's live-state), so showing ours too would stack two meters.
        var combat = (SpireCodexConfig.ShowDamageMeter && !OverwolfActive())
            ? LiveStateProducer.Latest?.Combat : null;
        if (combat == null) { HidePanel(); return; }

        var turn = combat.Turn ?? 0;
        var perTurn = turn > 0 ? combat.DamageDealt / turn : combat.DamageDealt;

        EnsurePanel();
        _panel!.Text =
            $"[color={Accent}][b]Damage[/b][/color]"
            + $"\n[font_size=22][color={Text}]{perTurn}[/color][/font_size] [color={Muted}]dmg / turn[/color]"
            + $"\n[color={Muted}]This turn[/color] [color={Text}]{combat.DamageDealtThisTurn}[/color]"
            + $"   [color={Muted}]Total[/color] [color={Text}]{combat.DamageDealt}[/color]"
            + $"\n[color={Muted}]Biggest[/color] [color={Text}]{combat.BiggestHit}[/color]"
            + $"   [color={Muted}]Taken[/color] [color={Text}]{combat.DamageTaken}[/color]";

        // Position the meter — but never while a drag is in flight (MoveTo owns it then; writing
        // here would race it and snap/flicker). Size is valid after FitContent lays the text out,
        // and the text (so the box) changes each tick, so clamp against the live size every time.
        // When a moved position is re-clamped (resolution/size change), write it back to _pos so
        // the source of truth and the displayed position never diverge.
        if (!_dragging)
        {
            var vp = _panel.GetViewportRect().Size;
            var size = _panel.Size;
            if (_moved && _pos is { } p)
            {
                _pos = ClampToViewport(p, size, vp);
                _panel.Position = _pos.Value;
            }
            else
            {
                _panel.Position = new Vector2(24f, vp.Y - size.Y - 24f);
            }
        }
        _panel.Visible = true;
    }

    // --- drag ------------------------------------------------------------------------

    // Press on the meter starts a drag (only the meter rect sees this — gameplay clicks
    // elsewhere are untouched). Motion + release are tracked in _Input so the cursor can outrun
    // the box. Mirrors DeckImagePanel's header-drag.
    private void OnPanelInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            _dragging = true;
            _dragOffset = _panel!.GetGlobalMousePosition() - _panel.Position;
            _panel.AcceptEvent();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (!_dragging || _panel is null || !_panel.Visible) return;
        if (@event is InputEventMouseMotion)
        {
            MoveTo(_panel!.GetGlobalMousePosition() - _dragOffset);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            _dragging = false;
            GetViewport().SetInputAsHandled();
            if (_pos is { } p) _ = SavePosAsync(p); // fire-and-forget; never blocks the frame
        }
    }

    private void MoveTo(Vector2 pos)
    {
        var vp = _panel!.GetViewportRect().Size;
        _pos = ClampToViewport(pos, _panel.Size, vp);
        _moved = true;
        _panel.Position = _pos.Value;
    }

    private static Vector2 ClampToViewport(Vector2 pos, Vector2 size, Vector2 vp)
    {
        pos.X = Mathf.Clamp(pos.X, 0f, Mathf.Max(0f, vp.X - size.X));
        pos.Y = Mathf.Clamp(pos.Y, 0f, Mathf.Max(0f, vp.Y - size.Y));
        return pos;
    }

    // --- view ------------------------------------------------------------------------

    private void EnsurePanel()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel)) return;

        // The F5 panel's card stylebox: dark fill, subtle border, rounded corners, soft shadow.
        var card = new StyleBoxFlat { BgColor = Bg, BorderColor = Border };
        card.SetBorderWidthAll(1);
        card.SetCornerRadiusAll(8);
        card.ShadowColor = new Color(0, 0, 0, 0.5f);
        card.ShadowSize = 14;
        card.ContentMarginLeft = 12; card.ContentMarginRight = 12;
        card.ContentMarginTop = 9; card.ContentMarginBottom = 9;

        _panel = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Stop, // capture clicks on the meter so it drags
        };
        _panel.AddThemeStyleboxOverride("normal", card);
        _panel.AddThemeFontSizeOverride("normal_font_size", 14);
        if (ThemeDB.GetProjectTheme() is { } theme) _panel.Theme = theme; // match the game/F5 font
        _panel.GuiInput += OnPanelInput;
        _layer.AddChild(_panel);
    }

    private void HidePanel()
    {
        _dragging = false; // a hidden meter must never keep capturing input mid-drag
        if (_panel != null && GodotObject.IsInstanceValid(_panel)) _panel.Visible = false;
    }

    // --- Overwolf coexistence --------------------------------------------------------

    // Match the overlay's own live-state freshness window, so the two sides agree on "live".
    private const int OverwolfFreshMs = 5000;
    private static readonly JsonSerializerOptions MarkerOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record OverlayMarker(long Ts, bool Active);

    // The Overwolf overlay writes this next to live-state.json (same %TEMP% dir) while its in-game
    // window is up: { "ts": <unix ms>, "active": true }. A fresh + active marker means it's showing
    // its own meter, so ours hides. Missing/stale/inactive marker -> we show ours as normal.
    private static string? MarkerPath()
    {
        try
        {
            var dir = Path.GetDirectoryName(Config.LiveStatePath);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "spire-codex-overlay.json");
        }
        catch { return null; }
    }

    // Cached + throttled to ~1x/sec so the marker isn't read on every 0.15s render tick.
    private bool OverwolfActive()
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _owChecked).TotalSeconds >= 1.0)
        {
            _owChecked = now;
            _owActive = MarkerFresh();
        }
        return _owActive;
    }

    private static bool MarkerFresh()
    {
        try
        {
            var path = MarkerPath();
            if (path == null || !File.Exists(path)) return false;
            if (JsonSerializer.Deserialize<OverlayMarker>(File.ReadAllText(path), MarkerOpts)
                is not { Active: true } m) return false;
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - m.Ts;
            return age >= 0 && age < OverwolfFreshMs;
        }
        catch { return false; }
    }

    // --- position persistence --------------------------------------------------------

    private sealed record PosFile(float X, float Y, bool Moved);

    private static string? PosFilePath()
    {
        try
        {
            var dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SpireCodex");
            return Path.Combine(dir, "damage-meter-pos.json");
        }
        catch { return null; }
    }

    private void LoadPos()
    {
        try
        {
            var path = PosFilePath();
            if (path == null || !File.Exists(path)) return;
            if (JsonSerializer.Deserialize<PosFile>(File.ReadAllText(path)) is { Moved: true } p)
            {
                _pos = new Vector2(p.X, p.Y);
                _moved = true;
            }
        }
        catch { /* fall back to the bottom-left default */ }
    }

    private static async Task SavePosAsync(Vector2 pos)
    {
        try
        {
            var path = PosFilePath();
            if (path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new PosFile(pos.X, pos.Y, true));
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }
}
