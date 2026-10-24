using System.Numerics;
using Brutal.Numerics;
using Engine.Core.Scene;

namespace Engine.Brutal.Ui;

// Draws the game's UI elements with Canvas2D, over the scene. The built game and the editor's play mode both use it,
// so a menu looks the same in both. Input is handled by GameUi (in Engine.Core); this only reads its state to show
// which element is hovered or pressed, the text caret and the open dropdown.
public static class UiRenderer
{
    private static readonly float4 PanelColor = new(0.12f, 0.13f, 0.16f, 0.92f);
    private static readonly float4 HoverColor = new(0.19f, 0.21f, 0.26f, 0.95f);
    private static readonly float4 PressColor = new(0.15f, 0.40f, 0.72f, 1f);
    private static readonly float4 BorderColor = new(1f, 1f, 1f, 0.16f);
    private static readonly float4 FocusColor = new(0.30f, 0.58f, 0.96f, 1f);
    private static readonly float4 TextColor = new(0.95f, 0.95f, 0.97f, 1f);
    private static readonly float4 HintColor = new(0.58f, 0.60f, 0.65f, 1f);
    private static readonly float4 FieldColor = new(0.06f, 0.07f, 0.09f, 0.92f);
    private static readonly float4 AccentColor = new(0.30f, 0.58f, 0.96f, 1f);
    private static readonly float4 TrackColor = new(0.08f, 0.08f, 0.11f, 0.88f);
    private static readonly float4 FillColor = new(0.30f, 0.80f, 0.45f, 1f);

    private const float Pad = 8f;

    // area is the game view in canvas pixels: x, y, width, height. live is false for a still preview (the editor's
    // UI mode), which ignores hover, presses, focus and open dropdowns.
    public static void Draw(Scene scene, Canvas2D canvas, float4 area, bool live = true)
    {
        var view = new Vector2(area.Z, area.W);
        int layer = canvas.Layer;
        var clip = canvas.Clip;
        UiElement? openList = null;

        foreach (var (_, ui) in GameUi.Elements(scene))
        {
            if (!ui.Visible) continue;
            var r = GameUi.PixelRect(ui, view);
            var box = new Box(area.X + r.X, area.Y + r.Y, r.Z, r.W);
            if (box.W < 1f || box.H < 1f) continue;
            canvas.Layer = Canvas2D.PanelLayer;
            canvas.Clip = null;
            DrawElement(canvas, ui, box, live);
            if (live && GameUi.OpenDropdown == ui) openList = ui;
        }

        // The open list goes over everything else.
        if (openList is { } list)
        {
            canvas.Layer = Canvas2D.PopupLayer;
            canvas.Clip = null;
            for (int i = 0; i < list.Options.Count; i++)
            {
                var r = GameUi.OptionRect(list, i, view);
                var row = new Box(area.X + r.X, area.Y + r.Y, r.Z, r.W);
                var color = i == GameUi.HoveredOption ? PressColor : i == list.Selected ? HoverColor : PanelColor;
                canvas.Rect(row.X, row.Y, row.W, row.H, Opaque(color));
                canvas.Outline(row.X, row.Y, row.W, row.H, 1f, BorderColor);
                Label(canvas, list.Options[i], row, TextColor, center: false);
            }
        }

        canvas.Layer = layer;
        canvas.Clip = clip;
    }

    private static void DrawElement(Canvas2D c, UiElement ui, Box box, bool live)
    {
        bool enabled = ui.Interactable;
        bool hover = live && enabled && GameUi.Hovered == ui;
        bool pressed = live && enabled && GameUi.Pressed == ui && hover;
        float4 Dim(float4 color) => enabled ? color : color with { W = color.W * 0.5f };

        switch (ui.Kind)
        {
            case UiKind.Button:
            {
                var bg = pressed ? PressColor : hover ? HoverColor : PanelColor;
                c.Rect(box.X, box.Y, box.W, box.H, Dim(bg));
                c.Outline(box.X, box.Y, box.W, box.H, 1f, Dim(BorderColor));
                Label(c, ui.Label, box, Dim(TextColor), ui.CenterText);
                break;
            }
            case UiKind.TextField:
            {
                bool focused = live && GameUi.Focused == ui;
                c.Rect(box.X, box.Y, box.W, box.H, Dim(hover && !focused ? HoverColor : FieldColor));
                c.Outline(box.X, box.Y, box.W, box.H, focused ? 2f : 1f, focused ? FocusColor : Dim(BorderColor));
                if (ui.Text.Length == 0 && !focused)
                {
                    Label(c, ui.Label, box, Dim(HintColor), center: false);   // the label is a hint while empty
                    break;
                }
                // While typing, show the end of the text so the caret stays in view.
                string shown = ui.Text;
                float room = box.W - Pad * 2f - 3f;
                if (focused)
                    while (shown.Length > 0 && c.MeasureText(shown) > room) shown = shown[1..];
                else
                    shown = Fit(c, shown, room);
                float top = TextTop(c, box);
                c.Clip = box;
                c.Text(shown, box.X + Pad, top, Dim(TextColor));
                if (focused && GameUi.CaretVisible)
                    c.Rect(box.X + Pad + c.MeasureText(shown) + 1f, top + 1f, 1.5f, c.FontSize, TextColor);
                c.Clip = null;
                break;
            }
            case UiKind.Checkbox:
            {
                if (hover) c.Rect(box.X, box.Y, box.W, box.H, HoverColor with { W = 0.5f });
                float size = MathF.Min(box.H * 0.62f, box.W);
                var tick = new Box(box.X + Pad * 0.5f, box.Y + (box.H - size) / 2f, size, size);
                c.Rect(tick.X, tick.Y, tick.W, tick.H, Dim(pressed ? HoverColor : FieldColor));
                c.Outline(tick.X, tick.Y, tick.W, tick.H, 1.5f, Dim(hover ? FocusColor : BorderColor with { W = 0.5f }));
                if (ui.Checked)
                {
                    float inset = MathF.Max(3f, size * 0.22f);
                    c.Rect(tick.X + inset, tick.Y + inset, tick.W - inset * 2f, tick.H - inset * 2f, Dim(AccentColor));
                }
                var text = new Box(tick.X + tick.W, box.Y, box.X + box.W - tick.X - tick.W, box.H);
                Label(c, ui.Label, text, Dim(TextColor), center: false);
                break;
            }
            case UiKind.Dropdown:
            {
                bool open = live && GameUi.OpenDropdown == ui;
                var bg = open || pressed ? PressColor : hover ? HoverColor : PanelColor;
                c.Rect(box.X, box.Y, box.W, box.H, Dim(bg));
                c.Outline(box.X, box.Y, box.W, box.H, 1f, Dim(BorderColor));
                string shown = ui.Selected >= 0 && ui.Selected < ui.Options.Count ? ui.Options[ui.Selected] : "";
                float arrowW = c.MeasureText("v") + Pad * 1.5f;
                Label(c, shown, box with { W = box.W - arrowW }, Dim(TextColor), ui.CenterText);
                c.Text("v", box.X + box.W - arrowW + Pad * 0.5f, TextTop(c, box), Dim(HintColor));
                break;
            }
            case UiKind.Bar:
            {
                c.Rect(box.X, box.Y, box.W, box.H, Dim(TrackColor));
                float fill = box.W * Math.Clamp(ui.Value, 0f, 1f);
                if (fill > 0f) c.Rect(box.X, box.Y, fill, box.H, Dim(FillColor));
                c.Outline(box.X, box.Y, box.W, box.H, 1f, Dim(BorderColor));
                if (box.H >= c.FontSize) Label(c, ui.Label, box, Dim(TextColor), center: false);
                break;
            }
        }
    }

    // One line of text inside a box, cut short with "..." when it doesn't fit.
    private static void Label(Canvas2D c, string text, Box box, float4 color, bool center)
    {
        if (string.IsNullOrEmpty(text)) return;
        string shown = Fit(c, text, box.W - Pad * 2f);
        float w = c.MeasureText(shown);
        float x = center ? box.X + (box.W - w) / 2f : box.X + Pad;
        c.Clip = box;
        c.Text(shown, x, TextTop(c, box), color);
        c.Clip = null;
    }

    private static float TextTop(Canvas2D c, Box box) => box.Y + (box.H - c.FontSize) / 2f - 1f;

    private static string Fit(Canvas2D c, string text, float maxWidth)
    {
        if (maxWidth <= 0f) return "";
        if (c.MeasureText(text) <= maxWidth) return text;
        while (text.Length > 1 && c.MeasureText(text + "...") > maxWidth) text = text[..^1];
        return text + "...";
    }

    private static float4 Opaque(float4 color) => color with { W = 1f };
}
