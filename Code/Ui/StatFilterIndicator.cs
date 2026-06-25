using Godot;
using SpireCodex.Api;

namespace SpireCodex.Ui;

// A small persistent pill that shows the active stat filter ("Stats: Ascension 10") whenever
// it is narrowed past the all-runs default, so the player always knows the community numbers
// on plates/hover-tips are filtered. Hidden on "All runs". Chosen in the mod Settings (the
// Stats dropdown). Same lightweight pattern as MapDangerHints' corner banner.
public partial class StatFilterIndicator : Node
{
    private double _accum;
    private CanvasLayer _layer = null!;
    private RichTextLabel? _pill;
    private string _shown = "";

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; stat-filter indicator not started");
            return;
        }
        var n = new StatFilterIndicator { Name = "SpireCodexStatFilter" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, n);
        MainFile.Logger.Info("stat-filter indicator started");
    }

    public override void _Ready()
    {
        _layer = new CanvasLayer { Layer = 198 }; // below the map hints / card plates
        AddChild(_layer);
    }

    public override void _Process(double delta)
    {
        _accum += delta;
        if (_accum < 0.25) return;
        _accum = 0;

        if (CodexScores.CurrentFilter == StatFilter.DefaultKey) { HidePill(); return; }

        var label = CodexScores.CurrentFilterLabel;
        EnsurePill();
        if (label != _shown)
        {
            _pill!.Text = $"[color=#d7a84a][b]Stats[/b][/color] [color=#e8e8e8]{label}[/color]";
            _shown = label;
        }

        // Top-right corner (clear of the bottom-corner damage meter / event banner).
        var vp = _pill!.GetViewportRect().Size;
        var size = _pill.Size;
        _pill.Position = new Vector2(vp.X - size.X - 24f, 24f);
        _pill.Visible = true;
    }

    private void EnsurePill()
    {
        if (_pill != null && GodotObject.IsInstanceValid(_pill)) return;
        var style = new StyleBoxFlat { BgColor = new Color(0.06f, 0.07f, 0.09f, 0.88f) };
        style.SetCornerRadiusAll(5);
        style.ContentMarginLeft = 10; style.ContentMarginRight = 10;
        style.ContentMarginTop = 5; style.ContentMarginBottom = 5;
        _pill = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _pill.AddThemeStyleboxOverride("normal", style);
        _pill.AddThemeFontSizeOverride("normal_font_size", 13);
        _layer.AddChild(_pill);
        _shown = ""; // force a text refresh
    }

    private void HidePill()
    {
        if (_pill != null && GodotObject.IsInstanceValid(_pill)) _pill.Visible = false;
    }
}
