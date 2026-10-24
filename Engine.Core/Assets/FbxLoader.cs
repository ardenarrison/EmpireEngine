using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Engine.Core.Assets;

// Reads binary FBX (version 7.x, which Blender, Maya, 3ds Max and most asset stores use). Meshes are placed with
// their node transforms and converted to Y-up meters using the file's axis and unit settings, so a model looks
// the same as its glTF export. Each material on a mesh becomes its own MeshData.
// Read from materials: diffuse color, opacity, emission, and roughness from shininess, plus the diffuse (base color),
// normal and emissive texture maps, either embedded in the file or found next to it. Not handled: ASCII FBX,
// skinning, blend shapes, animation.
public static class FbxLoader
{
    private const string Magic = "Kaydara FBX Binary  ";

    private sealed class Node(string name)
    {
        public string Name { get; } = name;
        public List<object> Props { get; } = new();
        public List<Node> Children { get; } = new();

        public Node? Child(string name) => Children.Find(c => c.Name == name);
        public IEnumerable<Node> All(string name) => Children.Where(c => c.Name == name);
        public long Id => Props.Count > 0 && Props[0] is long id ? id : 0;
        public string Class => Props.Count > 2 && Props[2] is string s ? s : "";
        public string ObjectName => Props.Count > 1 && Props[1] is string s ? s.Split('\0')[0] : "";
    }

    public static GlbModel Load(string path)
    {
        var root = Parse(File.ReadAllBytes(path));
        var objects = root.Child("Objects") ?? throw new InvalidDataException("The FBX file has no Objects section.");

        var byId = new Dictionary<long, Node>();
        foreach (var node in objects.Children)
            byId.TryAdd(node.Id, node);

        // Object-to-object links, child first. A material's position among a model's links is its material index.
        var links = root.Child("Connections")?.All("C")
            .Where(c => c.Props.Count >= 3 && c.Props[0] is "OO" && c.Props[1] is long && c.Props[2] is long)
            .Select(c => (Child: (long)c.Props[1], Parent: (long)c.Props[2]))
            .ToList() ?? new();

        // Links from a texture to the material property it feeds, e.g. (texture, material, "DiffuseColor").
        var propertyLinks = root.Child("Connections")?.All("C")
            .Where(c => c.Props.Count >= 4 && c.Props[0] is "OP" && c.Props[1] is long && c.Props[2] is long && c.Props[3] is string)
            .Select(c => (Child: (long)c.Props[1], Parent: (long)c.Props[2], Property: (string)c.Props[3]))
            .ToList() ?? new();

        bool IsA(long id, string name) => byId.TryGetValue(id, out var n) && n.Name == name;
        string modelDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var textureImages = new Dictionary<long, ImageData?>();
        var parentModel = new Dictionary<long, long>();
        foreach (var (child, parent) in links)
            if (IsA(child, "Model") && IsA(parent, "Model"))
                parentModel.TryAdd(child, parent);

        var settings = root.Child("GlobalSettings");
        var axes = AxisConversion(settings);
        float unitScale = (float)(Number(settings, "UnitScaleFactor", 1) / 100);   // FBX units are centimeters
        var toEngine = axes * Matrix4x4.CreateScale(unitScale);

        var meshes = new List<MeshData>();
        foreach (var (geometryId, modelId) in links)
        {
            if (!IsA(geometryId, "Geometry") || !IsA(modelId, "Model")) continue;
            var geometry = byId[geometryId];
            var model = byId[modelId];
            if (geometry.Class != "Mesh") continue;

            var world = Matrix4x4.Identity;
            long current = modelId;
            for (int depth = 0; depth < 256; depth++)
            {
                world *= LocalTransform(byId[current]);
                if (!parentModel.TryGetValue(current, out current)) break;
            }

            var transform = GeometricTransform(model) * world * toEngine;
            var materials = links.Where(l => l.Parent == modelId && IsA(l.Child, "Material"))
                .Select(l => ReadMaterial(byId[l.Child], MapsOf(l.Child)))
                .ToList();
            AddGeometry(meshes, geometry, model.ObjectName, transform, materials);
        }

        return MeshBuilder.Model(path, meshes);

        // The base color, normal and emissive maps feeding a material, from the property each texture is linked to.
        (ImageData? Base, ImageData? Normal, ImageData? Emissive) MapsOf(long materialId)
        {
            ImageData? baseImage = null, normalImage = null, emissiveImage = null;
            foreach (var (textureId, parent, property) in propertyLinks)
            {
                if (parent != materialId || !IsA(textureId, "Texture")) continue;
                string key = property.ToLowerInvariant();
                if (key.Contains("emiss")) emissiveImage ??= TextureImage(textureId);
                else if (key.Contains("normal")) normalImage ??= TextureImage(textureId);
                else if (key.Contains("diffuse") || key.Contains("basecolor") || key.Contains("base_color")) baseImage ??= TextureImage(textureId);
            }
            return (baseImage, normalImage, emissiveImage);
        }

        // A texture's picture: the bytes embedded in its linked Video node, else the file it names.
        ImageData? TextureImage(long textureId)
        {
            if (textureImages.TryGetValue(textureId, out var cached)) return cached;
            ImageData? image = null;
            var texture = byId[textureId];
            var names = new List<string>();
            foreach (var owner in links.Where(l => l.Parent == textureId && IsA(l.Child, "Video")).Select(l => byId[l.Child]).Prepend(texture))
            {
                if (owner.Name == "Video" && owner.Child("Content")?.Props.FirstOrDefault() is byte[] { Length: > 0 } embedded)
                {
                    try
                    {
                        image = ImageData.Decode(embedded, Path.GetFileName(Text(owner, "RelativeFilename")));
                        break;
                    }
                    catch (InvalidDataException ex)
                    {
                        Console.WriteLine($"Could not read an embedded texture: {ex.Message}");
                    }
                }
                names.Add(Text(owner, "RelativeFilename"));
                names.Add(Text(owner, "FileName"));
            }

            if (image is null)
            {
                // The names are paths from the machine that exported the file, so fall back to the bare file name.
                foreach (var name in names.Where(n => n.Length > 0).Select(n => n.Replace('\\', '/')))
                {
                    string fileName = Path.GetFileName(name);
                    var file = new[] { Path.Combine(modelDir, name), Path.Combine(modelDir, fileName),
                                       Path.Combine(modelDir, "textures", fileName), Path.Combine(modelDir, "Textures", fileName) }
                        .FirstOrDefault(File.Exists);
                    if (file is null) continue;
                    try
                    {
                        image = ImageData.Load(file);
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException)
                    {
                        Console.WriteLine($"Could not read texture {fileName}: {ex.Message}");
                    }
                }
                if (image is null && names.Any(n => n.Length > 0))
                    Console.WriteLine($"Texture {Path.GetFileName(names.First(n => n.Length > 0).Replace('\\', '/'))} was not found next to the model.");
            }
            return textureImages[textureId] = image;
        }
    }

    // ---- geometry --------------------------------------------------------------------------

    private static void AddGeometry(List<MeshData> meshes, Node geometry, string name, Matrix4x4 transform,
        List<MaterialData> materials)
    {
        if (geometry.Child("Vertices")?.Props[0] is not double[] vertices ||
            geometry.Child("PolygonVertexIndex")?.Props[0] is not int[] polygonVertices)
            return;

        var positions = new Vector3[vertices.Length / 3];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = Vector3.Transform(new Vector3((float)vertices[i * 3], (float)vertices[i * 3 + 1], (float)vertices[i * 3 + 2]), transform);

        // Normals move with the inverse transpose, so non-uniform scaling keeps them perpendicular.
        Matrix4x4.Invert(transform with { M41 = 0, M42 = 0, M43 = 0 }, out var inverse);
        var normalTransform = Matrix4x4.Transpose(inverse);
        var normalLayer = ReadLayer(geometry, "LayerElementNormal", "Normals", "NormalsIndex");
        var normals = new Vector3[(normalLayer?.Data.Length ?? 0) / 3];
        for (int i = 0; i < normals.Length; i++)
        {
            var d = normalLayer!.Value.Data;
            var n = Vector3.TransformNormal(new Vector3((float)d[i * 3], (float)d[i * 3 + 1], (float)d[i * 3 + 2]), normalTransform);
            normals[i] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.Zero;
        }

        var uvLayer = ReadLayer(geometry, "LayerElementUV", "UV", "UVIndex");
        var uvs = new Vector2[(uvLayer?.Data.Length ?? 0) / 2];
        for (int i = 0; i < uvs.Length; i++)
            uvs[i] = new((float)uvLayer!.Value.Data[i * 2], 1f - (float)uvLayer.Value.Data[i * 2 + 1]);   // FBX v = 0 is the bottom

        var materialElement = geometry.Child("LayerElementMaterial");
        int[] polygonMaterials = materialElement?.Child("Materials")?.Props[0] as int[] ?? [];
        bool allSame = Text(materialElement, "MappingInformationType") != "ByPolygon";

        // A mirroring transform (negative scale) turns triangles inside out unless the winding is reversed.
        bool flip = transform.GetDeterminant() < 0;
        var builders = new SortedDictionary<int, MeshBuilder>();
        var corners = new List<Corner>();
        int polygon = 0;
        for (int i = 0; i < polygonVertices.Length; i++)
        {
            // The last corner of each polygon is stored as -(index + 1).
            int controlPoint = polygonVertices[i];
            bool last = controlPoint < 0;
            if (last) controlPoint = ~controlPoint;

            int normal = normalLayer?.Layer.Resolve(i, controlPoint, polygon) ?? -1;
            int uv = uvLayer?.Layer.Resolve(i, controlPoint, polygon) ?? -1;
            corners.Add(new Corner(controlPoint, normal, uv));
            if (!last) continue;

            int slot = polygonMaterials.Length == 0 ? 0
                : allSame ? polygonMaterials[0]
                : polygonMaterials[Math.Min(polygon, polygonMaterials.Length - 1)];
            if (!builders.TryGetValue(slot, out var builder))
                builders[slot] = builder = new MeshBuilder(positions, normals, uvs, flip);
            builder.AddPolygon(corners);
            corners.Clear();
            polygon++;
        }

        foreach (var (slot, builder) in builders)
        {
            if (builder.IsEmpty) continue;
            var material = slot >= 0 && slot < materials.Count ? materials[slot] : MeshBuilder.DefaultMaterial();
            meshes.Add(builder.Build(name, material));
        }
    }

    // How a layer (normals, UVs) maps onto the mesh: per polygon corner, per control point, per polygon, or one
    // value for everything, either directly or through an index array.
    private readonly record struct Layer(string Mapping, string Reference, int[]? Index)
    {
        public int Resolve(int corner, int controlPoint, int polygon)
        {
            int i = Mapping switch
            {
                "ByPolygonVertex" => corner,
                "ByVertice" or "ByVertex" or "ByControlPoint" => controlPoint,
                "ByPolygon" => polygon,
                "AllSame" => 0,
                _ => -1,
            };
            if (i >= 0 && Reference is "IndexToDirect" or "Index")
                i = Index is not null && i < Index.Length ? Index[i] : -1;
            return i;
        }
    }

    private static (Layer Layer, double[] Data)? ReadLayer(Node geometry, string element, string data, string index)
    {
        var layer = geometry.Child(element);
        if (layer?.Child(data)?.Props[0] is not double[] values) return null;
        return (new Layer(Text(layer, "MappingInformationType"), Text(layer, "ReferenceInformationType"),
            layer.Child(index)?.Props[0] as int[]), values);
    }

    // ---- transforms ------------------------------------------------------------------------

    // The FBX node transform, written for row vectors (System.Numerics order):
    // Sp^-1 * S * Sp * Soff * Rp^-1 * Rpost^-1 * R * Rpre * Rp * Roff * T
    private static Matrix4x4 LocalTransform(Node model)
    {
        var scalingPivot = Matrix4x4.CreateTranslation(Vector(model, "ScalingPivot", Vector3.Zero));
        var rotationPivot = Matrix4x4.CreateTranslation(Vector(model, "RotationPivot", Vector3.Zero));
        Matrix4x4.Invert(scalingPivot, out var scalingPivotInverse);
        Matrix4x4.Invert(rotationPivot, out var rotationPivotInverse);

        // Pre- and post-rotation only count when RotationActive is set (Maya sets it; Blender writes neither).
        bool rotationActive = Number(model, "RotationActive", 0) != 0;
        var pre = rotationActive ? Euler(Vector(model, "PreRotation", Vector3.Zero), 0) : Matrix4x4.Identity;
        var post = rotationActive ? Euler(Vector(model, "PostRotation", Vector3.Zero), 0) : Matrix4x4.Identity;
        Matrix4x4.Invert(post, out var postInverse);
        var rotation = Euler(Vector(model, "Lcl Rotation", Vector3.Zero), (int)Number(model, "RotationOrder", 0));

        return scalingPivotInverse
             * Matrix4x4.CreateScale(Vector(model, "Lcl Scaling", Vector3.One))
             * scalingPivot
             * Matrix4x4.CreateTranslation(Vector(model, "ScalingOffset", Vector3.Zero))
             * rotationPivotInverse * postInverse * rotation * pre * rotationPivot
             * Matrix4x4.CreateTranslation(Vector(model, "RotationOffset", Vector3.Zero))
             * Matrix4x4.CreateTranslation(Vector(model, "Lcl Translation", Vector3.Zero));
    }

    // An offset applied to the mesh only, not passed on to child nodes.
    private static Matrix4x4 GeometricTransform(Node model) =>
        Matrix4x4.CreateScale(Vector(model, "GeometricScaling", Vector3.One))
        * Euler(Vector(model, "GeometricRotation", Vector3.Zero), 0)
        * Matrix4x4.CreateTranslation(Vector(model, "GeometricTranslation", Vector3.Zero));

    // Euler angles in degrees. Order is FBX's RotationOrder: 0 XYZ (X applied first), 1 XZY, 2 YZX, 3 YXZ, 4 ZXY, 5 ZYX.
    private static Matrix4x4 Euler(Vector3 degrees, int order)
    {
        var x = Matrix4x4.CreateRotationX(float.DegreesToRadians(degrees.X));
        var y = Matrix4x4.CreateRotationY(float.DegreesToRadians(degrees.Y));
        var z = Matrix4x4.CreateRotationZ(float.DegreesToRadians(degrees.Z));
        return order switch
        {
            1 => x * z * y,
            2 => y * z * x,
            3 => y * x * z,
            4 => z * x * y,
            5 => z * y * x,
            _ => x * y * z,
        };
    }

    // Maps the file's right/up/front axes onto the engine's: +X right, +Y up, +Z front (as in glTF).
    // Blender and Maya files are already Y-up; 3ds Max files are Z-up with -Y front.
    private static Matrix4x4 AxisConversion(Node? settings)
    {
        int right = (int)Number(settings, "CoordAxis", 0), up = (int)Number(settings, "UpAxis", 1), front = (int)Number(settings, "FrontAxis", 2);
        if (right is < 0 or > 2 || up is < 0 or > 2 || front is < 0 or > 2 || right == up || up == front || right == front)
            return Matrix4x4.Identity;

        var m = new Matrix4x4 { M44 = 1 };
        Set(ref m, right, 0, Math.Sign(Number(settings, "CoordAxisSign", 1)));
        Set(ref m, up, 1, Math.Sign(Number(settings, "UpAxisSign", 1)));
        Set(ref m, front, 2, Math.Sign(Number(settings, "FrontAxisSign", 1)));
        return m;

        static void Set(ref Matrix4x4 m, int row, int column, float value) => m[row, column] = value == 0 ? 1 : value;
    }

    // ---- materials -------------------------------------------------------------------------

    private static MaterialData ReadMaterial(Node material, (ImageData? Base, ImageData? Normal, ImageData? Emissive) maps)
    {
        var diffuse = Vector(material, "DiffuseColor", Vector(material, "Diffuse", Vector3.One));
        float opacity = (float)Number(material, "Opacity", 1);
        var emission = Vector(material, "EmissiveColor", Vector3.Zero) * (float)Number(material, "EmissiveFactor", 1);

        // Blender writes ShininessExponent = (10 * (1 - roughness))^2; this undoes that.
        float roughness = 0.5f;
        if (Property(material, "ShininessExponent") is not null || Property(material, "Shininess") is not null)
        {
            double shininess = Number(material, "ShininessExponent", Number(material, "Shininess", 25));
            roughness = Math.Clamp(1f - MathF.Sqrt((float)Math.Max(shininess, 0)) / 10f, 0f, 1f);
        }

        // A diffuse texture replaces the diffuse color (exporters leave a default gray next to it), and an emissive
        // texture with no emissive color would otherwise be multiplied by black.
        if (maps.Base is not null) diffuse = Vector3.One;
        if (maps.Emissive is not null && emission == Vector3.Zero) emission = Vector3.One;

        return MeshBuilder.DefaultMaterial([diffuse.X, diffuse.Y, diffuse.Z, opacity], roughness,
            emission: [emission.X, emission.Y, emission.Z]) with
        {
            BaseColorImage = maps.Base,
            NormalImage = maps.Normal,
            EmissiveImage = maps.Emissive,
        };
    }

    // ---- Properties70 values ---------------------------------------------------------------

    // A property is a P node: name, type, label, flags, then the value(s).
    private static Node? Property(Node? owner, string name) =>
        owner?.Child("Properties70")?.All("P").FirstOrDefault(p => p.Props.Count > 0 && p.Props[0] as string == name);

    private static double Number(Node? owner, string name, double fallback) =>
        Property(owner, name) is { Props.Count: >= 5 } p && IsNumber(p.Props[4]) ? Convert.ToDouble(p.Props[4]) : fallback;

    private static Vector3 Vector(Node? owner, string name, Vector3 fallback) =>
        Property(owner, name) is { Props.Count: >= 7 } p && IsNumber(p.Props[4]) && IsNumber(p.Props[5]) && IsNumber(p.Props[6])
            ? new((float)Convert.ToDouble(p.Props[4]), (float)Convert.ToDouble(p.Props[5]), (float)Convert.ToDouble(p.Props[6]))
            : fallback;

    private static bool IsNumber(object value) => value is short or int or long or float or double or bool;

    private static string Text(Node? owner, string child) => owner?.Child(child)?.Props.FirstOrDefault() as string ?? "";

    // ---- binary parsing --------------------------------------------------------------------

    private static Node Parse(byte[] file)
    {
        if (file.Length < 27 || Encoding.ASCII.GetString(file, 0, Magic.Length) != Magic)
        {
            bool ascii = Encoding.ASCII.GetString(file, 0, Math.Min(file.Length, 256)).Contains("FBX", StringComparison.Ordinal);
            throw new InvalidDataException(ascii
                ? "This is an ASCII FBX file, which is not supported. Export it as binary FBX instead."
                : "This is not an FBX file.");
        }

        using var reader = new BinaryReader(new MemoryStream(file));
        reader.BaseStream.Position = 23;
        uint version = reader.ReadUInt32();
        if (version < 7000)
            throw new InvalidDataException($"FBX version {version / 1000.0:0.0} is too old. Export it as FBX 7.4 or newer.");

        var root = new Node("");
        try
        {
            while (ReadNode(reader, wide: version >= 7500) is { } node)
                root.Children.Add(node);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The FBX file is cut short.", ex);
        }
        return root;
    }

    // A node record: end offset, property count, property bytes, name, properties, then child nodes up to the
    // end offset. Version 7.5+ files use 64-bit offsets. A record of all zeros ends a list of nodes.
    private static Node? ReadNode(BinaryReader reader, bool wide)
    {
        int headerSize = wide ? 25 : 13;
        if (reader.BaseStream.Position + headerSize > reader.BaseStream.Length) return null;

        long end = wide ? (long)reader.ReadUInt64() : reader.ReadUInt32();
        long propertyCount = wide ? (long)reader.ReadUInt64() : reader.ReadUInt32();
        _ = wide ? reader.ReadUInt64() : reader.ReadUInt32();
        int nameLength = reader.ReadByte();
        if (end == 0) return null;
        if (end > reader.BaseStream.Length || end < reader.BaseStream.Position)
            throw new InvalidDataException("The FBX file is damaged.");

        var node = new Node(Encoding.UTF8.GetString(reader.ReadBytes(nameLength)));
        for (long i = 0; i < propertyCount; i++)
            node.Props.Add(ReadProperty(reader));
        while (reader.BaseStream.Position < end && ReadNode(reader, wide) is { } child)
            node.Children.Add(child);

        reader.BaseStream.Position = end;
        return node;
    }

    private static object ReadProperty(BinaryReader reader)
    {
        char type = (char)reader.ReadByte();
        return type switch
        {
            'Y' => reader.ReadInt16(),
            'C' => reader.ReadByte() != 0,
            'I' => reader.ReadInt32(),
            'F' => reader.ReadSingle(),
            'D' => reader.ReadDouble(),
            'L' => reader.ReadInt64(),
            'S' => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())),
            'R' => reader.ReadBytes(reader.ReadInt32()),
            'd' => MemoryMarshal.Cast<byte, double>(ReadArray(reader, 8)).ToArray(),
            'f' => Array.ConvertAll(MemoryMarshal.Cast<byte, float>(ReadArray(reader, 4)).ToArray(), v => (double)v),
            'i' => MemoryMarshal.Cast<byte, int>(ReadArray(reader, 4)).ToArray(),
            'l' => MemoryMarshal.Cast<byte, long>(ReadArray(reader, 8)).ToArray(),
            'b' => Array.ConvertAll(ReadArray(reader, 1), v => v != 0),
            _ => throw new InvalidDataException($"The FBX file has an unknown property type '{type}'."),
        };
    }

    // Array properties: element count, encoding (0 raw, 1 zlib), stored byte length, then the data.
    private static byte[] ReadArray(BinaryReader reader, int elementSize)
    {
        int count = reader.ReadInt32();
        int encoding = reader.ReadInt32();
        int storedLength = reader.ReadInt32();
        byte[] stored = reader.ReadBytes(storedLength);
        if (encoding == 0)
            return stored;

        var data = new byte[checked(count * elementSize)];
        using var zlib = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress);
        zlib.ReadExactly(data);
        return data;
    }
}
