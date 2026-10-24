using System.Numerics;
using Brutal.Numerics;
using Engine.Core.Scene;

namespace Editor;

// The editor's UI mode: every game UI element is outlined over the scene with its kind and name, and clicking one
// selects it. The elements themselves are drawn by UiRenderer, the same as in play mode and built games.
public sealed class UiOverlay
{
    private static readonly float4 OutlineColor = new(0.6f, 0.8f, 1f, 0.9f);
    private static readonly float4 SelectedColor = new(1f, 0.85f, 0.2f, 1f);

    // Draws the outlines on the canvas's top layer and returns the element clicked this frame, if any. sceneArea is the
    // game view in canvas pixels; uiWantsMouse is true while an editor panel has the mouse.
    public Guid? Draw(Scene scene, Canvas2D canvas, float4 sceneArea, Guid? selected, Ui.Gui gui, bool uiWantsMouse)
    {
        int layer = canvas.Layer;
        canvas.Layer = Canvas2D.TopLayer;
        canvas.Clip = null;
        var view = new Vector2(sceneArea.Z, sceneArea.W);
        Guid? clicked = null;

        foreach (var (obj, ui) in GameUi.Elements(scene))
        {
            var r = GameUi.PixelRect(ui, view);
            var box = new Box(sceneArea.X + r.X, sceneArea.Y + r.Y, r.Z, r.W);
            var color = obj.Id == selected ? SelectedColor : ui.Visible ? OutlineColor : OutlineColor with { W = 0.4f };
            canvas.Outline(box.X, box.Y, box.W, box.H, 1.5f, color);
            string tag = SafeLabel.Of($"{ui.Kind}: {obj.Name}{(ui.Visible ? "" : " (hidden)")}", 40);
            canvas.Rect(box.X, box.Y - canvas.FontSize - 4f, canvas.MeasureText(tag) + 8f, canvas.FontSize + 4f, new float4(0f, 0f, 0f, 0.6f));
            canvas.Text(tag, box.X + 4f, box.Y - canvas.FontSize - 3f, color);

            // Later elements are drawn on top, so the last one under the mouse wins.
            if (gui.MousePressed && !uiWantsMouse && box.Contains(gui.Mouse))
                clicked = obj.Id;
        }

        canvas.Layer = layer;
        return clicked;
    }
}
