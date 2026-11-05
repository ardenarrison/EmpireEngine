using Editor;
using Editor.Ui;

// The look is chosen by --lightmode or --darkmode anywhere in the arguments; without either, the system's setting is used.
Theme.Light = ThemeChoice.IsLight(ThemeChoice.FromArguments(args, out args));

if (args.Length < 1)
{
    Console.WriteLine("usage: empire [--lightmode | --darkmode] <scene.empirescene> [contentRoot]");
    return 1;
}

var scenePath = Path.GetFullPath(args[0]);
var contentRoot = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.GetDirectoryName(Path.GetDirectoryName(scenePath))!;

var editor = new EditorApp(scenePath, contentRoot);
editor.Run();

// File > Change Project: a fresh editor on the new project, which also ends everything that belonged to this one (the terminal's
// shell, say). The Hub remembers it as the last one opened.
if (editor.NextProject is { } next && EmpireHub.EmpireProject.FindStartScene(next) is { } nextScene)
{
    ProjectList.Remember(next);
    var restart = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    restart.ArgumentList.Add(Theme.Light ? "--lightmode" : "--darkmode");
    restart.ArgumentList.Add(nextScene);
    restart.ArgumentList.Add(next);
    System.Diagnostics.Process.Start(restart);
}
return 0;
