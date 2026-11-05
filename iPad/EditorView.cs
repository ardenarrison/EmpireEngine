using Brutal.GlfwApi;
using CoreAnimation;
using CoreGraphics;
using Engine.Brutal.Gfx;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace EmpireiPad;

// The view the editor draws in: a CAMetalLayer for MoltenVK, which also turns touches into the mouse the editor expects.
//   One finger        tap to click, drag to drag
//   Hold one finger   right click, for the menus
//   Two fingers       drag to circle the camera (or scroll a panel), pinch to zoom
// A trackpad or mouse is itself: it moves the pointer, clicks, right clicks and scrolls.
//
// It stays first responder so a keyboard always reaches it. It is a UIKeyInput, which would open the on-screen keyboard: an
// empty InputView keeps that closed, and is taken away while the editor expects typing, if the preference allows the keyboard.
public sealed class EditorView : UIView, IUIKeyInput
{
    private const double HoldSeconds = 0.45;
    private const double TapSlopPoints = 10;
    private const string KeyboardSetting = "Keyboard";

    private readonly List<UITouch> _fingers = new();
    private readonly UIView _noKeyboard = new(CGRect.Empty);
    private CGPoint _start, _lastCenter;
    private double _lastSpread;
    private bool _pending;      // one finger is down and it isn't yet known whether it is a tap, a drag or a hold
    private bool _dragging;     // one finger is holding the left button down
    private bool _twoFingers;   // this touch became a two-finger gesture; nothing else happens until all fingers lift
    private NSTimer? _hold;
    private bool _typing;
    private readonly HashSet<int> _held = new();

    public TouchWindow? Window;

    public EditorView()
    {
        ContentScaleFactor = UIScreen.MainScreen.NativeScale;
        MultipleTouchEnabled = true;

        AddGestureRecognizer(new UIHoverGestureRecognizer(g =>
        {
            var at = g.LocationInView(this);
            Window?.PostMove(at.X, at.Y);
        }));
        var scroll = new UIPanGestureRecognizer(g =>
        {
            var moved = g.TranslationInView(this);
            g.SetTranslation(CGPoint.Empty, this);
            Window?.PostWheel(moved.X, moved.Y);
        })
        {
            AllowedScrollTypesMask = UIScrollTypeMask.All,
            AllowedTouchTypes = [],
            CancelsTouchesInView = false,
        };
        AddGestureRecognizer(scroll);
        var pinch = new UIPinchGestureRecognizer(g =>
        {
            Window?.PostPinch(g.Scale);
            g.Scale = 1;
        })
        {
            AllowedTouchTypes = [NSNumber.FromInt64((long)UITouchType.IndirectPointer)],
            CancelsTouchesInView = false,
        };
        AddGestureRecognizer(pinch);
    }

    [Export("layerClass")]
    public static Class LayerClass() => new(typeof(CAMetalLayer));

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var scale = ContentScaleFactor;
        ((CAMetalLayer)Layer).DrawableSize = new CGSize(Bounds.Width * scale, Bounds.Height * scale);
        Window?.UpdateSize();
    }

    // ---- touches ---------------------------------------------------------------------------------

    public override void TouchesBegan(NSSet touches, UIEvent? evt)
    {
        foreach (var touch in touches.Cast<UITouch>())
        {
            if (touch.Type == UITouchType.IndirectPointer)
            {
                var at = touch.LocationInView(this);
                Window?.PostMove(at.X, at.Y);
                Window?.PostButton(PointerButton(evt), true);
                continue;
            }
            _fingers.Add(touch);
        }

        if (_fingers.Count == 1 && !_twoFingers && !_dragging)
        {
            _start = _fingers[0].LocationInView(this);
            _pending = true;
            _hold = NSTimer.CreateScheduledTimer(HoldSeconds, _ => Held());
        }
        else if (_fingers.Count >= 2 && !_twoFingers)
        {
            CancelHold();
            if (_dragging) Window?.PostButton(GlfwMouseButton.Left, false);
            _pending = _dragging = false;
            _twoFingers = true;
            (_lastCenter, _lastSpread) = Gesture();
            Window?.PostMove(_lastCenter.X, _lastCenter.Y);
        }
    }

    public override void TouchesMoved(NSSet touches, UIEvent? evt)
    {
        foreach (var touch in touches.Cast<UITouch>().Where(t => t.Type == UITouchType.IndirectPointer))
        {
            var at = touch.LocationInView(this);
            Window?.PostMove(at.X, at.Y);
        }

        if (_twoFingers && _fingers.Count >= 2)
        {
            var (center, spread) = Gesture();
            Window?.PostMove(center.X, center.Y);
            Window?.PostSwipe(center.X - _lastCenter.X, center.Y - _lastCenter.Y);
            if (_lastSpread > 1 && spread > 1) Window?.PostPinch(spread / _lastSpread);
            (_lastCenter, _lastSpread) = (center, spread);
        }
        else if (_fingers.Count == 1 && !_twoFingers)
        {
            var at = _fingers[0].LocationInView(this);
            if (_pending && Distance(at, _start) > TapSlopPoints)
            {
                CancelHold();
                _pending = false;
                _dragging = true;
                Window?.PostMove(_start.X, _start.Y);
                Window?.PostButton(GlfwMouseButton.Left, true);
            }
            if (_dragging) Window?.PostMove(at.X, at.Y);
        }
    }

    public override void TouchesEnded(NSSet touches, UIEvent? evt) => Lifted(touches, evt);
    public override void TouchesCancelled(NSSet touches, UIEvent? evt) => Lifted(touches, evt);

    private void Lifted(NSSet touches, UIEvent? evt)
    {
        foreach (var touch in touches.Cast<UITouch>())
        {
            if (touch.Type == UITouchType.IndirectPointer)
            {
                Window?.PostButton(GlfwMouseButton.Left, false);
                Window?.PostButton(GlfwMouseButton.Right, false);
                continue;
            }
            _fingers.Remove(touch);
        }
        if (_fingers.Count > 0) return;

        CancelHold();
        if (_pending)
        {
            Window?.PostMove(_start.X, _start.Y);
            Window?.PostButton(GlfwMouseButton.Left, true);
            Window?.PostButton(GlfwMouseButton.Left, false);
        }
        else if (_dragging) Window?.PostButton(GlfwMouseButton.Left, false);
        _pending = _dragging = _twoFingers = false;
    }

    // One finger stayed still: a right click, which opens the menus a mouse would.
    private void Held()
    {
        _hold = null;
        if (!_pending) return;
        _pending = false;
        Window?.PostMove(_start.X, _start.Y);
        Window?.PostButton(GlfwMouseButton.Right, true);
        Window?.PostButton(GlfwMouseButton.Right, false);
    }

    private void CancelHold()
    {
        _hold?.Invalidate();
        _hold = null;
    }

    private (CGPoint Center, double Spread) Gesture()
    {
        var a = _fingers[0].LocationInView(this);
        var b = _fingers[1].LocationInView(this);
        return (new CGPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2), Distance(a, b));
    }

    private static double Distance(CGPoint a, CGPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static GlfwMouseButton PointerButton(UIEvent? evt) =>
        evt is not null && evt.ButtonMask.HasFlag(UIEventButtonMask.Secondary) ? GlfwMouseButton.Right : GlfwMouseButton.Left;

    // ---- keyboard ------------------------------------------------------------------------------

    public KeyboardPreference Keyboard
    {
        get => (KeyboardPreference)(int)NSUserDefaults.StandardUserDefaults.IntForKey(KeyboardSetting);
        set
        {
            NSUserDefaults.StandardUserDefaults.SetInt((int)value, KeyboardSetting);
            ReloadInputViews();
        }
    }

    public void SetTyping(bool typing)
    {
        _typing = typing;
        ReloadInputViews();
    }

    public override bool CanBecomeFirstResponder => true;
    public override UIView? InputView => _typing && Keyboard != KeyboardPreference.Connected ? null : _noKeyboard;
    public override UIView? InputAccessoryView => null;

    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        foreach (var press in presses)
            if (press.Key is { } key)
            {
                _held.Add((int)key.KeyCode);
                Window?.PostKey((int)key.KeyCode, true);
            }
        base.PressesBegan(presses, evt);
    }

    public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        Released(presses);
        base.PressesEnded(presses, evt);
    }

    public override void PressesCancelled(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        Released(presses);
        base.PressesCancelled(presses, evt);
    }

    private void Released(NSSet<UIPress> presses)
    {
        foreach (var press in presses)
            if (press.Key is { } key)
            {
                _held.Remove((int)key.KeyCode);
                Window?.PostKey((int)key.KeyCode, false);
            }
    }

    private const int EnterKey = 40, BackspaceKey = 42, TabKey = 43;

    public bool HasText => true;

    // Text from either keyboard. A connected keyboard has already reported Enter, Tab and Backspace as keys; the on-screen one
    // only sends them here, so they become a quick press.
    public void InsertText(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n') Tap(EnterKey);
            else if (rune.Value == '\t') Tap(TabKey);
            else Window?.PostCharacter((uint)rune.Value);
        }
    }

    public void DeleteBackward() => Tap(BackspaceKey);

    private void Tap(int key)
    {
        if (_held.Contains(key)) return;
        Window?.PostKey(key, true);
        Window?.PostKey(key, false);
    }

    [Export("autocorrectionType")] public UITextAutocorrectionType AutocorrectionType => UITextAutocorrectionType.No;
    [Export("autocapitalizationType")] public UITextAutocapitalizationType AutocapitalizationType => UITextAutocapitalizationType.None;
    [Export("spellCheckingType")] public UITextSpellCheckingType SpellCheckingType => UITextSpellCheckingType.No;
    [Export("smartQuotesType")] public UITextSmartQuotesType SmartQuotesType => UITextSmartQuotesType.No;
    [Export("smartDashesType")] public UITextSmartDashesType SmartDashesType => UITextSmartDashesType.No;
}
