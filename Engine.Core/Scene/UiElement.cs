namespace Engine.Core.Scene;

public enum UiKind { Button, TextField, Checkbox, Dropdown, Bar }

// A 2D element drawn over the scene while the game plays, in the editor and in built games. X and Y are the top-left
// corner and W and H the size, as fractions (0 to 1) of the game view. GameUi handles the mouse and keyboard each
// frame before scripts run, sets Clicked, Changed and Submitted, and calls the handlers scripts added through a
// Button, TextField, Checkbox or Dropdown field (see UiControls.cs).
public sealed class UiElement : Component
{
    public UiKind Kind { get; set; } = UiKind.Button;
    public string Label { get; set; } = "Button";     // button text, checkbox text, or a text field's hint when empty
    public float X { get; set; } = 0.4f;
    public float Y { get; set; } = 0.45f;
    public float W { get; set; } = 0.2f;
    public float H { get; set; } = 0.07f;
    public string Text { get; set; } = "";            // text field value
    public bool Checked { get; set; }                  // checkbox state
    public List<string> Options { get; set; } = new() { "Option 1", "Option 2" };
    public int Selected { get; set; }                 // dropdown index
    public float Value { get; set; } = 0.5f;          // bar fill, 0 to 1

    public bool CenterText { get; set; } = true;     // button and dropdown text centered by default
    public bool Visible { get; set; } = true;        // false hides it; hidden elements take no clicks
    public bool Interactable { get; set; } = true;   // false shows it dimmed and ignores clicks and typing

    public bool Clicked { get; set; }                 // button pressed this frame
    public bool Changed { get; set; }                 // checkbox, text field, or dropdown changed this frame
    public bool Submitted { get; set; }               // Enter pressed in this text field this frame

    // The handlers scripts added. They live on the element, so every script that holds a control for this element
    // shares them. Runtime only: never saved.
    internal Action? ClickHandlers;
    internal Action<string>? TextHandlers;
    internal Action<string>? SubmitHandlers;
    internal Action<bool>? CheckHandlers;
    internal Action<int>? SelectHandlers;
}
