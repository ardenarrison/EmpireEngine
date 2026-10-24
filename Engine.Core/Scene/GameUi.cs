using System.Numerics;
using Engine.Core.Scripting;

namespace Engine.Core.Scene;

// The game's UI while it plays: which element the mouse is over, which button is held, which text field has the
// keyboard, which dropdown is open. SceneUpdate runs it every frame after reading the mouse and keyboard and before
// scripts, so the editor's play mode and built games behave the same. It sets each element's Clicked, Changed and
// Submitted flags for the frame and calls the handlers scripts added (Button.OnClick and the like).
//
// Positions are in the game view's points, as Input.mousePosition is, and the view's size is Input.screenSize.
// Drawing is separate (Engine.Brutal's UiRenderer), and reads the state here to show hover, presses and the caret.
public static class GameUi
{
    public const int MaxTextLength = 256;

    private const float RepeatDelay = 0.45f;     // holding Backspace starts repeating after this long
    private const float RepeatInterval = 0.035f;

    // The element under the mouse (null when none, or while the cursor is locked).
    public static UiElement? Hovered { get; private set; }

    // The element the left button went down on, while it is held.
    public static UiElement? Pressed { get; private set; }

    // The text field being typed in.
    public static UiElement? Focused { get; private set; }

    // The dropdown whose list of options is open, and the option the mouse is over in it (-1 for none).
    public static UiElement? OpenDropdown { get; private set; }
    public static int HoveredOption { get; private set; } = -1;
    private static int _pressedOption = -1;

    // True while the mouse is over a visible element or an open dropdown list. A script can check it so a click on a
    // button doesn't also fire a weapon: if (Input.GetMouseButtonDown(0) && !GameUi.PointerOverUi) Shoot();
    public static bool PointerOverUi { get; private set; }

    // True while a text field has the keyboard, so movement keys can be ignored while the player types a name.
    public static bool IsTyping => Focused is not null;

    // Whether the text caret is showing this frame: it blinks, and stays on for a moment after each key.
    public static bool CaretVisible => _caretClock % 1.0f < 0.6f;

    private static float _caretClock;
    private static float _repeatTimer;

    // Forgets everything, for a game that is about to start.
    public static void Reset()
    {
        Hovered = Pressed = Focused = OpenDropdown = null;
        HoveredOption = _pressedOption = -1;
        PointerOverUi = false;
        _caretClock = _repeatTimer = 0f;
    }

    // An element's rectangle in the game view's points: x, y, width, height.
    public static Vector4 PixelRect(UiElement ui, Vector2 viewSize) =>
        new(ui.X * viewSize.X, ui.Y * viewSize.Y, ui.W * viewSize.X, ui.H * viewSize.Y);

    // An open dropdown's option row, as tall as the dropdown itself. The list opens downward, or upward when it would
    // run off the bottom of the view.
    public static Vector4 OptionRect(UiElement ui, int index, Vector2 viewSize)
    {
        var r = PixelRect(ui, viewSize);
        float listH = r.W * ui.Options.Count;
        float top = r.Y + r.W + listH > viewSize.Y && r.Y - listH >= 0f ? r.Y - listH : r.Y + r.W;
        return new Vector4(r.X, top + index * r.W, r.Z, r.W);
    }

    private static bool Contains(Vector4 r, Vector2 p) => p.X >= r.X && p.X < r.X + r.Z && p.Y >= r.Y && p.Y < r.Y + r.W;

    // The UI elements in drawing order: hierarchy order, so later ones are drawn on top and take clicks first.
    public static List<(SceneObject Object, UiElement Element)> Elements(Scene scene)
    {
        var list = new List<(SceneObject, UiElement)>();
        foreach (var obj in scene.InHierarchyOrder)
            foreach (var ui in obj.Components.OfType<UiElement>())
                list.Add((obj, ui));
        return list;
    }

    // One frame of UI input. Called by SceneUpdate.
    internal static void Update(Scene scene)
    {
        var elements = Elements(scene);
        foreach (var (_, ui) in elements)
        {
            ui.Clicked = false;
            ui.Changed = false;
            ui.Submitted = false;
        }

        // Anything that disappeared, was hidden or was disabled since last frame lets go of its state.
        bool Usable(UiElement? ui) => ui is not null && ui.Visible && ui.Interactable && elements.Any(e => e.Element == ui);
        if (!Usable(Pressed)) Pressed = null;
        if (!Usable(Focused) || Focused!.Kind != UiKind.TextField) Focused = null;
        if (!Usable(OpenDropdown) || OpenDropdown!.Kind != UiKind.Dropdown) OpenDropdown = null;
        SceneObject? ObjectOf(UiElement ui) => elements.First(e => e.Element == ui).Object;

        _caretClock += Time.unscaledDeltaTime;
        UpdatePointer(elements, ObjectOf);
        UpdateTyping(ObjectOf);
    }

    private static void UpdatePointer(List<(SceneObject Object, UiElement Element)> elements, Func<UiElement, SceneObject?> objectOf)
    {
        Hovered = null;
        HoveredOption = -1;
        PointerOverUi = false;
        if (Input.cursorLocked)
        {
            // Mouse-look hides the cursor, so nothing is under it.
            Pressed = null;
            OpenDropdown = null;
            return;
        }

        var mouse = Input.mousePosition;
        var view = Input.screenSize;

        if (OpenDropdown is { } open)
            for (int i = 0; i < open.Options.Count; i++)
                if (Contains(OptionRect(open, i, view), mouse))
                    HoveredOption = i;

        if (HoveredOption < 0)
            for (int i = elements.Count - 1; i >= 0; i--)
            {
                var ui = elements[i].Element;
                if (ui.Visible && Contains(PixelRect(ui, view), mouse))
                {
                    Hovered = ui;
                    break;
                }
            }
        PointerOverUi = HoveredOption >= 0 || Hovered is not null;

        if (Input.GetMouseButtonDown(MouseButton.Left))
        {
            _pressedOption = HoveredOption;
            Pressed = HoveredOption < 0 && Hovered is { Interactable: true } ? Hovered : null;
            // A press anywhere else lets go of the text field, and closes an open list (pressing the dropdown itself
            // toggles it on release instead).
            if (Focused is not null && Pressed != Focused) Focused = null;
            if (OpenDropdown is not null && HoveredOption < 0 && Pressed != OpenDropdown) OpenDropdown = null;
        }

        if (!Input.GetMouseButtonUp(MouseButton.Left)) return;

        if (OpenDropdown is { } list && _pressedOption >= 0 && _pressedOption == HoveredOption)
        {
            OpenDropdown = null;
            if (list.Selected != HoveredOption)
            {
                list.Selected = HoveredOption;
                list.Changed = true;
                int picked = HoveredOption;
                Raise(objectOf(list), "OnChanged", list.SelectHandlers, h => h(picked));
            }
        }
        else if (Pressed is { } ui && Hovered == ui)
        {
            Activate(ui, objectOf(ui));
        }
        Pressed = null;
        _pressedOption = -1;
    }

    // A click (press and release over the same element).
    private static void Activate(UiElement ui, SceneObject? obj)
    {
        switch (ui.Kind)
        {
            case UiKind.Button:
                ui.Clicked = true;
                Raise(obj, "OnClick", ui.ClickHandlers, h => h());
                break;
            case UiKind.Checkbox:
                ui.Checked = !ui.Checked;
                ui.Changed = true;
                bool state = ui.Checked;
                Raise(obj, "OnChanged", ui.CheckHandlers, h => h(state));
                break;
            case UiKind.TextField:
                Focused = ui;
                _caretClock = 0f;
                break;
            case UiKind.Dropdown:
                OpenDropdown = OpenDropdown == ui ? null : ui;
                break;
        }
    }

    private static void UpdateTyping(Func<UiElement, SceneObject?> objectOf)
    {
        if (Focused is not { } field) return;
        string before = field.Text;
        var text = new System.Text.StringBuilder(before);

        foreach (char c in Input.inputString)
            if (!char.IsControl(c) && text.Length < MaxTextLength)
                text.Append(c);

        // Backspace deletes one character, then repeats while held.
        if (Input.GetKeyDown(Keys.Backspace))
        {
            if (text.Length > 0) text.Length--;
            _repeatTimer = RepeatDelay;
        }
        else if (Input.GetKey(Keys.Backspace))
        {
            _repeatTimer -= Time.unscaledDeltaTime;
            while (_repeatTimer <= 0f)
            {
                if (text.Length > 0) text.Length--;
                _repeatTimer += RepeatInterval;
            }
        }

        string after = text.ToString();
        if (after != before)
        {
            field.Text = after;
            field.Changed = true;
            _caretClock = 0f;
            Raise(objectOf(field), "OnChanged", field.TextHandlers, h => h(after));
        }

        if (Input.GetKeyDown(Keys.Enter))
        {
            Focused = null;
            field.Submitted = true;
            Raise(objectOf(field), "OnSubmit", field.SubmitHandlers, h => h(after));
        }
        else if (Input.GetKeyDown(Keys.Escape))
        {
            Focused = null;
        }
    }

    // Calls each handler on its own, so one that throws is reported and the rest still run.
    internal static void Raise<T>(SceneObject? obj, string eventName, T? handlers, Action<T> call) where T : Delegate
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                call((T)handler);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UI error in {eventName} of '{obj?.Name}' ({handler.Method.DeclaringType?.Name}.{handler.Method.Name}): {ex.Message}");
            }
        }
    }
}
