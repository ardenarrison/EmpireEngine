using System.Globalization;
using System.Numerics;

namespace Engine.Core.Assets;

// Reads Wavefront .obj files and the .mtl material libraries they name. Each object/group and material
// pair becomes one MeshData. Coordinates are used as they are: Y up, one unit per meter (Blender's defaults).
// Read from .mtl: Kd (base color), d/Tr (opacity), Ke (emission), Pr/Pm (roughness/metallic), Ns (roughness
// when Pr is absent), and the maps map_Kd (base color), map_Bump / bump / norm (normal map) and map_Ke (emission),
// found next to the .mtl file or in a textures folder there. Not handled: vertex colors, curves, line continuations.
public static class ObjLoader
{
    private sealed class Group(string name, string material)
    {
        public string Name { get; } = name;
        public string Material { get; } = material;
        public List<Corner[]> Faces { get; } = new();
    }

    public static GlbModel Load(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var materials = new Dictionary<string, MaterialData>();
        var images = new Dictionary<string, ImageData?>(StringComparer.OrdinalIgnoreCase);   // by file, so shared maps decode once
        var groups = new Dictionary<(string, string), Group>();   // in first-use order

        string objectName = Path.GetFileNameWithoutExtension(path);
        string materialName = "";
        Group? group = null;

        int lineNumber = 0;
        foreach (var rawLine in File.ReadLines(path))
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            string rest = line[parts[0].Length..].Trim();
            try
            {
                switch (parts[0])
                {
                    case "v":
                        positions.Add(new(Float(parts[1]), Float(parts[2]), Float(parts[3])));
                        break;
                    case "vn":
                        normals.Add(Vector3.Normalize(new(Float(parts[1]), Float(parts[2]), Float(parts[3]))));
                        break;
                    case "vt":
                        // OBJ puts v = 0 at the bottom of the texture; glTF (and the renderer) at the top.
                        uvs.Add(new(Float(parts[1]), 1f - (parts.Length > 2 ? Float(parts[2]) : 0f)));
                        break;
                    case "o":
                    case "g":
                        objectName = rest.Length > 0 ? rest : objectName;
                        // Blender writes no usemtl for an object without a material, so a new object starts without one.
                        if (parts[0] == "o") materialName = "";
                        group = null;
                        break;
                    case "usemtl":
                        materialName = rest;
                        group = null;
                        break;
                    case "mtllib":
                        LoadMaterialLibrary(dir, rest, materials, images);
                        break;
                    case "f":
                        if (group is null && !groups.TryGetValue((objectName, materialName), out group))
                            groups[(objectName, materialName)] = group = new Group(objectName, materialName);
                        var face = new Corner[parts.Length - 1];
                        for (int i = 1; i < parts.Length; i++)
                            face[i - 1] = ParseCorner(parts[i], positions.Count, uvs.Count, normals.Count);
                        group.Faces.Add(face);
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} line {lineNumber}: could not read \"{line}\".", ex);
            }
        }

        var meshes = new List<MeshData>();
        var positionArray = positions.ToArray();
        var normalArray = normals.ToArray();
        var uvArray = uvs.ToArray();
        foreach (var g in groups.Values)
        {
            var builder = new MeshBuilder(positionArray, normalArray, uvArray, flipWinding: false);
            foreach (var face in g.Faces)
                builder.AddPolygon(face);
            if (builder.IsEmpty) continue;
            var material = materials.TryGetValue(g.Material, out var m) ? m : MeshBuilder.DefaultMaterial();
            meshes.Add(builder.Build(g.Name, material));
        }
        return MeshBuilder.Model(path, meshes);
    }

    // A face corner: "v", "v/vt", "v//vn" or "v/vt/vn". Indices start at 1; negative ones count back from the end.
    private static Corner ParseCorner(string token, int positionCount, int uvCount, int normalCount)
    {
        var fields = token.Split('/');
        int Index(int field, int count)
        {
            if (field >= fields.Length || fields[field].Length == 0) return -1;
            int i = int.Parse(fields[field], CultureInfo.InvariantCulture);
            return i > 0 ? i - 1 : count + i;
        }
        return new Corner(Index(0, positionCount), Index(2, normalCount), Index(1, uvCount));
    }

    private static void LoadMaterialLibrary(string dir, string names, Dictionary<string, MaterialData> materials,
        Dictionary<string, ImageData?> images)
    {
        // Usually one file name, which may contain spaces. Several names separated by spaces are allowed too.
        var files = File.Exists(Path.Combine(dir, names)) ? [names] : names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var file in files)
        {
            string full = Path.Combine(dir, file);
            if (!File.Exists(full))
            {
                Console.WriteLine($"Material library {file} was not found next to the model.");
                continue;
            }
            ReadMtl(full, materials, images);
        }
    }

    private static void ReadMtl(string path, Dictionary<string, MaterialData> materials, Dictionary<string, ImageData?> images)
    {
        string mtlDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string? name = null;
        float[] color = [1, 1, 1, 1];
        float[] emission = [0, 0, 0];
        float? roughness = null, shininess = null;
        float metallic = 0, normalScale = 1;
        ImageData? baseImage = null, normalImage = null, emissiveImage = null;

        void Finish()
        {
            if (name is null) return;
            // Without Pr, roughness comes from Ns (0-1000). This inverts Blender's exporter: Ns = 1000 * (1 - roughness)^2.
            float r = roughness ?? (shininess is { } ns ? 1f - MathF.Sqrt(Math.Clamp(ns, 0, 1000) / 1000f) : 0.5f);
            // A map replaces Kd rather than tinting it (Blender writes a default Kd of 0.8 next to map_Kd), and an
            // emission map with no Ke would otherwise be multiplied by black.
            float[] baseColor = baseImage is null ? color : [1, 1, 1, color[3]];
            float[] glow = emissiveImage is not null && emission.All(e => e == 0) ? [1, 1, 1] : emission;
            materials[name] = MeshBuilder.DefaultMaterial(baseColor, Math.Clamp(r, 0f, 1f), metallic, glow) with
            {
                BaseColorImage = baseImage,
                NormalImage = normalImage,
                EmissiveImage = emissiveImage,
                NormalScale = normalScale,
            };
        }

        ImageData? Image(string line)
        {
            if (TextureFile(line, mtlDir) is not { } file) return null;
            if (images.TryGetValue(file, out var cached)) return cached;
            ImageData? image = null;
            try
            {
                image = ImageData.Load(file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Console.WriteLine($"Could not read texture {Path.GetFileName(file)}: {ex.Message}");
            }
            return images[file] = image;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var parts = rawLine.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].StartsWith('#')) continue;

            // Texture maps: "map_Kd [options] file". They are checked first because the file is not a number.
            if (name is not null && parts[0] is "map_Kd" or "map_Bump" or "map_bump" or "bump" or "norm" or "map_Ke")
            {
                var image = Image(rawLine);
                switch (parts[0])
                {
                    case "map_Kd": baseImage = image; break;
                    case "map_Ke": emissiveImage = image; break;
                    default:
                        normalImage = image;
                        int bm = Array.IndexOf(parts, "-bm");
                        if (bm >= 0 && bm + 1 < parts.Length &&
                            float.TryParse(parts[bm + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float scale))
                            normalScale = scale;
                        break;
                }
                continue;
            }

            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && parts[0] != "newmtl")
                continue;
            float Component(int i) => i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : x;

            switch (parts[0])
            {
                case "newmtl":
                    Finish();
                    name = rawLine.Trim()[6..].Trim();
                    color = [1, 1, 1, 1];
                    emission = [0, 0, 0];
                    roughness = shininess = null;
                    metallic = 0;
                    normalScale = 1;
                    baseImage = normalImage = emissiveImage = null;
                    break;
                case "Kd": color = [x, Component(2), Component(3), color[3]]; break;
                case "d": color[3] = x; break;
                case "Tr": color[3] = 1f - x; break;
                case "Ke": emission = [x, Component(2), Component(3)]; break;
                case "Pr": roughness = x; break;
                case "Pm": metallic = x; break;
                case "Ns": shininess = x; break;
            }
        }
        Finish();
    }

    // The file named by a map line such as "map_Bump -bm 1.0 textures/wall n.png": skips the option flags and
    // their values, then looks for the rest beside the .mtl, in its folder by file name alone, and in a textures folder.
    private static string? TextureFile(string line, string mtlDir)
    {
        var tokens = line.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int i = 1;
        while (i < tokens.Length && tokens[i].StartsWith('-'))
            i += 1 + tokens[i] switch
            {
                "-o" or "-s" or "-t" => 3,
                "-mm" => 2,
                "-bm" or "-blendu" or "-blendv" or "-clamp" or "-imfchan" or "-texres" or "-type" or "-cc" => 1,
                _ => 0,
            };
        if (i >= tokens.Length) return null;

        string relative = string.Join(' ', tokens[i..]).Replace('\\', '/');
        string fileName = Path.GetFileName(relative);
        foreach (var candidate in new[]
                 {
                     Path.Combine(mtlDir, relative),
                     Path.Combine(mtlDir, fileName),
                     Path.Combine(mtlDir, "textures", fileName),
                     Path.Combine(mtlDir, "Textures", fileName),
                 })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        Console.WriteLine($"Texture {relative} was not found next to the material library.");
        return null;
    }

    private static float Float(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
