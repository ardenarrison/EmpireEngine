using Engine.Brutal;
using Engine.Core.Assets;
using Engine.Core.Serialization;

if (args.Length < 1)
{
    Console.WriteLine("usage: Probe <scene.empirescene> [contentRoot]");
    return 1;
}

var scenePath = Path.GetFullPath(args[0]);
var contentRoot = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.GetDirectoryName(Path.GetDirectoryName(scenePath))!;

var scene = SceneSerializer.Load(scenePath);
var player = new BrutalPlayer(new AssetCache(contentRoot));
player.Run(scene, scene.Name);
return 0;
