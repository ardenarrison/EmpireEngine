namespace Engine.Core.Scene;

// What scripts use to work with the game's UI. Declare a public field of one of these types and drag a UI object from
// the Hierarchy onto it in the Inspector; when play starts the field holds that object's control:
//
//     public class Menu : Behaviour
//     {
//         public Button play;          // only objects with a UI Button can be dropped here
//         public TextField playerName;
//
//         public override void Start()
//         {
//             play.OnClick += StartGame;
//             playerName.OnSubmit += name => Console.WriteLine("Hello " + name);
//         }
//
//         private void StartGame() => SceneManager.LoadScene("Level1");
//     }
//
// Handlers run during the frame, before scripts' Update, on the frame the player clicks or types. A control can also be
// looked up from code with UiControl.From<Button>(someObject).
public abstract class UiControl
{
    // The object the control is on, and its UI element.
    public SceneObject Object { get; }
    public UiElement Element { get; }

    private protected UiControl(SceneObject obj, UiElement element)
    {
        Object = obj;
        Element = element;
    }

    // Button text, checkbox text, or a text field's hint (shown while it is empty).
    public string Label
    {
        get => Element.Label;
        set => Element.Label = value ?? "";
    }

    // False hides it, and hidden controls take no clicks.
    public bool Visible
    {
        get => Element.Visible;
        set => Element.Visible = value;
    }

    // False shows it dimmed and it ignores the mouse and keyboard.
    public bool Interactable
    {
        get => Element.Interactable;
        set => Element.Interactable = value;
    }

    // Where it is, as fractions (0 to 1) of the game view: X and Y are the top-left corner.
    public float X { get => Element.X; set => Element.X = value; }
    public float Y { get => Element.Y; set => Element.Y = value; }
    public float Width { get => Element.W; set => Element.W = value; }
    public float Height { get => Element.H; set => Element.H = value; }

    // The kind of UI element each control type needs.
    public static UiKind KindOf(Type controlType) =>
        controlType == typeof(Button) ? UiKind.Button :
        controlType == typeof(TextField) ? UiKind.TextField :
        controlType == typeof(Checkbox) ? UiKind.Checkbox :
        controlType == typeof(Dropdown) ? UiKind.Dropdown :
        controlType == typeof(Bar) ? UiKind.Bar :
        throw new ArgumentException($"{controlType.Name} is not a UI control type.");

    // True for Button, TextField, Checkbox, Dropdown and Bar.
    public static bool IsControlType(Type type) =>
        type == typeof(Button) || type == typeof(TextField) || type == typeof(Checkbox) ||
        type == typeof(Dropdown) || type == typeof(Bar);

    // The control of this type on an object, or null when the object is null or has no UI element of that kind.
    public static T? From<T>(SceneObject? obj) where T : UiControl => (T?)From(typeof(T), obj);

    public static UiControl? From(Type controlType, SceneObject? obj)
    {
        var kind = KindOf(controlType);
        if (obj?.Components.OfType<UiElement>().FirstOrDefault(e => e.Kind == kind) is not { } element) return null;
        return kind switch
        {
            UiKind.Button => new Button(obj, element),
            UiKind.TextField => new TextField(obj, element),
            UiKind.Checkbox => new Checkbox(obj, element),
            UiKind.Dropdown => new Dropdown(obj, element),
            _ => new Bar(obj, element),
        };
    }

    public override string ToString() => $"{GetType().Name} '{Object.Name}'";
}

public sealed class Button : UiControl
{
    internal Button(SceneObject obj, UiElement element) : base(obj, element) { }

    // Runs when the button is clicked: pressed and let go over it.
    public event Action? OnClick
    {
        add => Element.ClickHandlers += value;
        remove => Element.ClickHandlers -= value;
    }

    // True on the frame the button was clicked.
    public bool Clicked => Element.Clicked;

    // Clicks the button from code: runs the OnClick handlers now.
    public void Click() => GameUi.Raise(Object, "OnClick", Element.ClickHandlers, h => h());

    public void RemoveAllListeners() => Element.ClickHandlers = null;
}

public sealed class TextField : UiControl
{
    internal TextField(SceneObject obj, UiElement element) : base(obj, element) { }

    // The text in the field. Setting it does not run OnChanged.
    public string Text
    {
        get => Element.Text;
        set => Element.Text = value ?? "";
    }

    // Runs with the new text each time the player types or deletes a character.
    public event Action<string>? OnChanged
    {
        add => Element.TextHandlers += value;
        remove => Element.TextHandlers -= value;
    }

    // Runs with the text when the player presses Enter in the field.
    public event Action<string>? OnSubmit
    {
        add => Element.SubmitHandlers += value;
        remove => Element.SubmitHandlers -= value;
    }

    // True on the frame the text changed, or Enter was pressed.
    public bool Changed => Element.Changed;
    public bool Submitted => Element.Submitted;

    // True while the player is typing in this field.
    public bool IsFocused => GameUi.Focused == Element;

    public void RemoveAllListeners()
    {
        Element.TextHandlers = null;
        Element.SubmitHandlers = null;
    }
}

public sealed class Checkbox : UiControl
{
    internal Checkbox(SceneObject obj, UiElement element) : base(obj, element) { }

    // Whether it is ticked. Setting it does not run OnChanged.
    public bool IsChecked
    {
        get => Element.Checked;
        set => Element.Checked = value;
    }

    // Runs with the new state when the player ticks or unticks it.
    public event Action<bool>? OnChanged
    {
        add => Element.CheckHandlers += value;
        remove => Element.CheckHandlers -= value;
    }

    public bool Changed => Element.Changed;

    public void RemoveAllListeners() => Element.CheckHandlers = null;
}

public sealed class Dropdown : UiControl
{
    internal Dropdown(SceneObject obj, UiElement element) : base(obj, element) { }

    // The index of the chosen option. Setting it does not run OnChanged.
    public int Selected
    {
        get => Element.Selected;
        set => Element.Selected = value;
    }

    // The chosen option's text, or "" when there are no options.
    public string SelectedText =>
        Element.Selected >= 0 && Element.Selected < Element.Options.Count ? Element.Options[Element.Selected] : "";

    // The choices, top to bottom. Change the list to change what the dropdown offers.
    public List<string> Options
    {
        get => Element.Options;
        set => Element.Options = value ?? new List<string>();
    }

    // Runs with the new index when the player picks a different option.
    public event Action<int>? OnChanged
    {
        add => Element.SelectHandlers += value;
        remove => Element.SelectHandlers -= value;
    }

    public bool Changed => Element.Changed;

    // True while its list of options is open.
    public bool IsOpen => GameUi.OpenDropdown == Element;

    public void RemoveAllListeners() => Element.SelectHandlers = null;
}

// A bar that fills from the left, for health, loading and the like. It takes no input.
public sealed class Bar : UiControl
{
    internal Bar(SceneObject obj, UiElement element) : base(obj, element) { }

    // How full it is, 0 to 1.
    public float Value
    {
        get => Element.Value;
        set => Element.Value = Math.Clamp(value, 0f, 1f);
    }
}
