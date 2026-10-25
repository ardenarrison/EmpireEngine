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

new EditorApp(scenePath, contentRoot).Run();
return 0;
