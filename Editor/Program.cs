using Editor;

if (args.Length < 1)
{
    Console.WriteLine("usage: empire <scene.empirescene> [contentRoot]");
    return 1;
}

var scenePath = Path.GetFullPath(args[0]);
var contentRoot = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.GetDirectoryName(Path.GetDirectoryName(scenePath))!;

new EditorApp(scenePath, contentRoot).Run();
return 0;
