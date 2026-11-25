using Godot;

namespace SpireCodex.Ui;

public partial class FeatureThumb : Control
{
    public enum Kind { Plates, Hover, Bracket, Map, Events, Meter }

    private readonly Kind _kind;

    public FeatureThumb(Kind kind)
    {
        _kind = kind;
        CustomMinimumSize = new Vector2(54, 38);
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public FeatureThumb() : this(Kind.Plates) { }

    public override void _Draw()
    {
        var frame = new Rect2(Vector2.Zero, new Vector2(54, 38));
        DrawRect(frame, Skin.Field);
        DrawRect(frame, Skin.Line, false, 1f);
        var ink = Skin.TextMuted;
        var gold = Skin.Accent;
        switch (_kind)
        {
            case Kind.Plates:
                DrawRect(new Rect2(11, 5, 14, 20), ink, false, 1.4f);
                DrawRect(new Rect2(29, 5, 14, 20), ink, false, 1.4f);
                DrawRect(new Rect2(12, 28, 12, 4), gold);
                DrawRect(new Rect2(30, 28, 12, 4), gold);
                break;
            case Kind.Hover:
                DrawRect(new Rect2(9, 11, 13, 19), ink, false, 1.4f);
                DrawRect(new Rect2(25, 7, 21, 15), Skin.Bg);
                DrawRect(new Rect2(25, 7, 21, 15), gold, false, 1.4f);
                DrawLine(new Vector2(29, 12), new Vector2(42, 12), gold, 1.4f);
                DrawLine(new Vector2(29, 17), new Vector2(38, 17), gold, 1.4f);
                break;
            case Kind.Bracket:
                DrawRect(new Rect2(9, 14, 36, 11), gold, false, 1.4f);
                DrawLine(new Vector2(14, 19.5f), new Vector2(22, 19.5f), gold, 1.6f);
                DrawLine(new Vector2(26, 19.5f), new Vector2(40, 19.5f), ink, 1.6f);
                break;
            case Kind.Map:
                DrawLine(new Vector2(13, 30), new Vector2(27, 19), ink, 1.4f);
                DrawLine(new Vector2(27, 19), new Vector2(15, 8), ink, 1.4f);
                DrawLine(new Vector2(27, 19), new Vector2(41, 9), ink, 1.4f);
                DrawCircle(new Vector2(13, 30), 3f, ink);
                DrawCircle(new Vector2(27, 19), 3.4f, Skin.Danger);
                DrawCircle(new Vector2(15, 8), 3f, ink);
                DrawCircle(new Vector2(41, 9), 3f, Skin.Good);
                break;
            case Kind.Events:
                DrawRect(new Rect2(10, 8, 34, 6), gold, false, 1.4f);
                DrawRect(new Rect2(10, 17, 34, 6), ink, false, 1.4f);
                DrawRect(new Rect2(10, 26, 34, 6), ink, false, 1.4f);
                break;
            default:
                DrawRect(new Rect2(10, 8, 34, 5), gold);
                DrawRect(new Rect2(10, 17, 24, 5), Skin.Link);
                DrawRect(new Rect2(10, 26, 13, 5), ink);
                break;
        }
    }
}
