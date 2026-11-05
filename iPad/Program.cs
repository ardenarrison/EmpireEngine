using Foundation;
using UIKit;

namespace EmpireiPad;

public static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;
}

[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    private EditorController? _editor;

    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        _editor = new EditorController();
        Window = new UIWindow(windowScene) { RootViewController = _editor };
        Window.MakeKeyAndVisible();
    }

    // Metal may not draw while the app is in the background.
    [Export("sceneDidEnterBackground:")]
    public void DidEnterBackground(UIScene scene) => _editor?.SetActive(false);

    [Export("sceneWillEnterForeground:")]
    public void WillEnterForeground(UIScene scene) => _editor?.SetActive(true);
}
