using System.Numerics;
using Engine.Core.Animation;
using Engine.Core.Assets;

namespace Engine.Core.Scene;

// How the sculpting brush changes the ground.
public enum TerrainBrush
{
    Raise,     // lifts the ground under the brush
    Lower,     // digs it down
    Smooth,    // evens out bumps
    Flatten,   // pulls the ground toward one height
    Paint,     // paints the chosen layer's color onto the ground
    Erase,     // removes paint, so the ground goes back to its base color
}

// A color that can be painted onto a terrain: "Dirt", "Road", "Snow"...
public sealed record TerrainLayer(string Name, Vector3 Color);

// Ground made from a grid of heights, for hills, valleys and plains. The terrain is centered on its object: it spans
// Width along X and Length along Z, and rises from the object's position up to Height. Unpainted ground has the terrain's
// Base Color; on top of that you paint layers, each a color of your choosing, onto specific spots with the Paint brush.
// It also acts as a collider, so rigidbodies stand on it and raycasts hit it. Sculpt it in the Inspector, or from a script
// with Sculpt, SetHeight and the generators; scripts can read it with HeightAt and NormalAt.
public sealed class Terrain : Collider
{
    public const int MinResolution = 17;
    public const int MaxResolution = 513;
    public static readonly int[] Resolutions = { 33, 65, 129, 257, 513 };

    public const int MaxLayers = 8;

    private float _width = 60f, _length = 60f, _height = 12f;
    private int _resolution = 129;
    private ushort[] _heights;
    private Vector3 _baseColor = new(0.30f, 0.50f, 0.20f);

    public Terrain() => _heights = new ushort[_resolution * _resolution];

    // How far the terrain reaches along X and Z, and how high its highest possible point is above the object (local units).
    public float Width { get => _width; set { _width = MathF.Max(value, 0.1f); Touch(); } }
    public float Length { get => _length; set { _length = MathF.Max(value, 0.1f); Touch(); } }
    public float Height { get => _height; set { _height = MathF.Max(value, 0.01f); Touch(); } }

    // Grid points along each side. More points hold finer detail and cost more to draw. Changing it resamples the ground.
    public int Resolution
    {
        get => _resolution;
        set => Resize(value);
    }

    // The color of ground that has no paint on it.
    public Vector3 BaseColor { get => _baseColor; set { _baseColor = value; TouchColors(); } }

    // False makes the terrain scenery only: nothing collides with it and raycasts pass through.
    public bool Collide { get; set; } = true;

    // Counts every change to the ground (and its size), so caches know when to rebuild. Not saved.
    public int Version { get; private set; }

    // Counts every change to the colors (painting, layers, the base color). Not saved.
    public int ColorVersion { get; private set; }

    // ---- reading the ground ----------------------------------------------------------------------

    // Distance between neighboring grid points, in local units.
    public float CellX => Width / (Resolution - 1);
    public float CellZ => Length / (Resolution - 1);

    // The height of the grid point at column x and row z (0 to Resolution - 1), 0 to 1 of the terrain's Height.
    public float GetHeight01(int x, int z) => _heights[Math.Clamp(z, 0, _resolution - 1) * _resolution + Math.Clamp(x, 0, _resolution - 1)] / 65535f;

    // Sets a grid point's height, 0 to 1 of the terrain's Height.
    public void SetHeight01(int x, int z, float value)
    {
        if (x < 0 || z < 0 || x >= _resolution || z >= _resolution) return;
        _heights[z * _resolution + x] = (ushort)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 65535f), 0, 65535);
        Touch();
    }

    // Local position of a grid point (on the ground).
    public Vector3 PointAt(int x, int z) =>
        new(-Width / 2f + x * CellX, GetHeight01(x, z) * Height, -Length / 2f + z * CellZ);

    // Height of the ground above the object at a local position, in local units. Outside the terrain it is the edge's height.
    public float HeightAt(float localX, float localZ) => Sample01(localX, localZ) * Height;

    // The height above the object at a world position (as in terrain.GetHeight(player.Position)): the ground's world
    // height there, for placing things on it. Takes the object's position, rotation and scale into account.
    public float GetHeight(SceneObject owner, Vector3 worldPosition)
    {
        if (!Matrix4x4.Invert(owner.WorldMatrix, out var inverse)) return 0f;
        var local = Vector3.Transform(worldPosition, inverse);
        var onGround = Vector3.Transform(new Vector3(local.X, HeightAt(local.X, local.Z), local.Z), owner.WorldMatrix);
        return onGround.Y;
    }

    // The ground's direction at a local position (unit length, pointing up out of the ground).
    public Vector3 NormalAt(float localX, float localZ)
    {
        float dx = CellX, dz = CellZ;
        float hx = HeightAt(localX + dx, localZ) - HeightAt(localX - dx, localZ);
        float hz = HeightAt(localX, localZ + dz) - HeightAt(localX, localZ - dz);
        return Vector3.Normalize(new Vector3(-hx / (2f * dx), 1f, -hz / (2f * dz)));
    }

    private float Sample01(float localX, float localZ)
    {
        float fx = Math.Clamp((localX + Width / 2f) / Width, 0f, 1f) * (_resolution - 1);
        float fz = Math.Clamp((localZ + Length / 2f) / Length, 0f, 1f) * (_resolution - 1);
        int x0 = Math.Min((int)fx, _resolution - 2), z0 = Math.Min((int)fz, _resolution - 2);
        float tx = fx - x0, tz = fz - z0;
        float h00 = GetHeight01(x0, z0), h10 = GetHeight01(x0 + 1, z0), h01 = GetHeight01(x0, z0 + 1), h11 = GetHeight01(x0 + 1, z0 + 1);
        // Each square is split into the two triangles the mesh uses, so this matches the drawn surface exactly.
        return tx + tz <= 1f
            ? h00 + (h10 - h00) * tx + (h01 - h00) * tz
            : h11 + (h01 - h11) * (1f - tx) + (h10 - h11) * (1f - tz);
    }

    // Where a ray (in local space) first meets the ground, as a distance along it. For aiming the sculpting brush.
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out float distance)
    {
        distance = 0f;
        float length = direction.Length();
        if (length < 1e-9f) return false;
        var dir = direction / length;
        float step = MathF.Min(CellX, CellZ) * 0.5f;
        float t = 0f;
        float previousAbove = 0f;
        bool wasAbove = false;
        while (t <= maxDistance)
        {
            var p = origin + dir * t;
            bool inside = p.X >= -Width / 2f && p.X <= Width / 2f && p.Z >= -Length / 2f && p.Z <= Length / 2f;
            float gap = p.Y - HeightAt(p.X, p.Z);
            if (inside && gap <= 0f && wasAbove)
            {
                // Between two steps the ray went from above to below the ground: narrow it down.
                float lo = previousAbove, hi = t;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) / 2f;
                    var q = origin + dir * mid;
                    if (q.Y - HeightAt(q.X, q.Z) > 0f) lo = mid; else hi = mid;
                }
                distance = (lo + hi) / 2f;
                return true;
            }
            if (inside && gap > 0f) { wasAbove = true; previousAbove = t; }
            else if (!inside) wasAbove = false;
            t += step;
        }
        return false;
    }

    // ---- paint layers ----------------------------------------------------------------------------

    private readonly List<TerrainLayer> _layers = new();
    private readonly List<byte[]> _weights = new();       // per layer, a byte (0 to 255) per grid point: how much of it is there
    private readonly List<string?> _weightsText = new();  // the saved form of each, kept until it is painted on

    // The layers that can be painted, in the order they were added.
    public IReadOnlyList<TerrainLayer> Layers => _layers;

    // Adds a layer and returns its index, or -1 when there are already MaxLayers.
    public int AddLayer(string name, Vector3 color)
    {
        if (_layers.Count >= MaxLayers) return -1;
        _layers.Add(new TerrainLayer(name, color));
        _weights.Add(new byte[_resolution * _resolution]);
        _weightsText.Add(null);
        TouchColors();
        return _layers.Count - 1;
    }

    // Takes a layer away; whatever was painted with it goes back to the base color.
    public void RemoveLayer(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        _layers.RemoveAt(index);
        _weights.RemoveAt(index);
        _weightsText.RemoveAt(index);
        TouchColors();
    }

    // Renames a layer and changes its color. Paint already on the ground takes the new color.
    public void SetLayer(int index, string name, Vector3 color)
    {
        if (index < 0 || index >= _layers.Count) return;
        _layers[index] = new TerrainLayer(name, color);
        TouchColors();
    }

    // Paints a layer over the whole terrain.
    public void FillLayer(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        for (int l = 0; l < _layers.Count; l++)
        {
            Array.Fill(_weights[l], l == index ? (byte)255 : (byte)0);
            _weightsText[l] = null;
        }
        TouchColors();
    }

    // Removes all paint, so the whole terrain goes back to its base color.
    public void ClearPaint()
    {
        for (int l = 0; l < _layers.Count; l++)
        {
            Array.Clear(_weights[l]);
            _weightsText[l] = null;
        }
        TouchColors();
    }

    // How much of a layer is painted at a grid point, 0 to 1.
    public float GetLayerWeight(int layer, int x, int z) =>
        layer < 0 || layer >= _layers.Count ? 0f
            : _weights[layer][Math.Clamp(z, 0, _resolution - 1) * _resolution + Math.Clamp(x, 0, _resolution - 1)] / 255f;

    // The layer painted most strongly at a local position, or -1 where there is no paint (just the base color).
    // For scripts that need to know what ground something stands on, like picking a footstep sound.
    public int LayerAt(float localX, float localZ)
    {
        int x = (int)MathF.Round(Math.Clamp((localX + Width / 2f) / Width, 0f, 1f) * (_resolution - 1));
        int z = (int)MathF.Round(Math.Clamp((localZ + Length / 2f) / Length, 0f, 1f) * (_resolution - 1));
        int best = -1;
        float bestWeight = 0.5f;   // paint under half strength still looks mostly like the ground below it
        for (int l = 0; l < _layers.Count; l++)
        {
            float w = _weights[l][z * _resolution + x] / 255f;
            if (w > bestWeight) { bestWeight = w; best = l; }
        }
        return best;
    }

    // Paints amount (0 to 1) of a layer at one grid point and lowers the other layers to make room.
    private void PaintPoint(int index, int layer, float amount)
    {
        float target = Math.Min(1f, _weights[layer][index] / 255f + amount);
        float others = 0f;
        for (int l = 0; l < _layers.Count; l++)
            if (l != layer) others += _weights[l][index] / 255f;
        float room = 1f - target;
        float scale = others > room && others > 0f ? room / others : 1f;
        _weights[layer][index] = (byte)Math.Clamp((int)MathF.Round(target * 255f), 0, 255);
        if (scale < 1f)
            for (int l = 0; l < _layers.Count; l++)
                if (l != layer) _weights[l][index] = (byte)(_weights[l][index] * scale);
        for (int l = 0; l < _layers.Count; l++) _weightsText[l] = null;
    }

    private void ErasePoint(int index, float amount)
    {
        float keep = 1f - Math.Clamp(amount, 0f, 1f);
        for (int l = 0; l < _layers.Count; l++)
        {
            _weights[l][index] = (byte)MathF.Round(_weights[l][index] * keep);
            _weightsText[l] = null;
        }
    }

    // ---- changing the ground ---------------------------------------------------------------------

    // Applies the brush at a local position. radius is in local units. strength is how far the ground moves, in local
    // units for Raise and Lower and as a 0 to 1 blend for Smooth, Flatten, Paint and Erase; scale it by the frame time for
    // a steady effect. For Flatten, level is the height to flatten toward, in local units above the object. For Paint,
    // layer is the index of the layer to paint with.
    public void Sculpt(TerrainBrush brush, float localX, float localZ, float radius, float strength, float level = 0f, int layer = 0)
    {
        if (radius <= 0f || strength == 0f) return;
        int x0 = Math.Max(0, (int)MathF.Floor((localX - radius + Width / 2f) / CellX));
        int x1 = Math.Min(_resolution - 1, (int)MathF.Ceiling((localX + radius + Width / 2f) / CellX));
        int z0 = Math.Max(0, (int)MathF.Floor((localZ - radius + Length / 2f) / CellZ));
        int z1 = Math.Min(_resolution - 1, (int)MathF.Ceiling((localZ + radius + Length / 2f) / CellZ));

        // Smoothing reads the neighbors as they were before the brush, so the result doesn't depend on the order.
        float[]? before = null;
        if (brush == TerrainBrush.Smooth)
        {
            before = new float[(x1 - x0 + 3) * (z1 - z0 + 3)];
            for (int z = z0 - 1; z <= z1 + 1; z++)
                for (int x = x0 - 1; x <= x1 + 1; x++)
                    before[(z - z0 + 1) * (x1 - x0 + 3) + (x - x0 + 1)] = GetHeight01(x, z);
        }

        if (brush == TerrainBrush.Paint && (layer < 0 || layer >= _layers.Count)) return;
        bool changed = false, painted = false;
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                float px = -Width / 2f + x * CellX - localX, pz = -Length / 2f + z * CellZ - localZ;
                float d = MathF.Sqrt(px * px + pz * pz) / radius;
                if (d >= 1f) continue;
                float falloff = 0.5f + 0.5f * MathF.Cos(d * MathF.PI);   // 1 in the middle, 0 at the edge, smooth between
                float h = GetHeight01(x, z);
                switch (brush)
                {
                    case TerrainBrush.Raise: h += strength * falloff / Height; break;
                    case TerrainBrush.Lower: h -= strength * falloff / Height; break;
                    case TerrainBrush.Smooth:
                    {
                        int w = x1 - x0 + 3;
                        float Get(int gx, int gz) => before![(gz - z0 + 1) * w + (gx - x0 + 1)];
                        float average = (Get(x - 1, z) + Get(x + 1, z) + Get(x, z - 1) + Get(x, z + 1) + Get(x, z) * 2f) / 6f;
                        h += (average - h) * Math.Clamp(strength * falloff, 0f, 1f);
                        break;
                    }
                    case TerrainBrush.Flatten:
                        h += (level / Height - h) * Math.Clamp(strength * falloff, 0f, 1f);
                        break;
                    case TerrainBrush.Paint:
                        PaintPoint(z * _resolution + x, layer, Math.Clamp(strength * falloff, 0f, 1f));
                        painted = true;
                        continue;
                    case TerrainBrush.Erase:
                        ErasePoint(z * _resolution + x, Math.Clamp(strength * falloff, 0f, 1f));
                        painted = true;
                        continue;
                }
                ushort value = (ushort)Math.Clamp((int)MathF.Round(Math.Clamp(h, 0f, 1f) * 65535f), 0, 65535);
                if (value != _heights[z * _resolution + x]) { _heights[z * _resolution + x] = value; changed = true; }
            }
        if (changed) Touch();
        if (painted) TouchColors();
    }

    // Makes all the ground one height, in local units above the object.
    public void Flatten(float level)
    {
        ushort value = (ushort)Math.Clamp((int)MathF.Round(Math.Clamp(level / Height, 0f, 1f) * 65535f), 0, 65535);
        Array.Fill(_heights, value);
        Touch();
    }

    // Evens out the whole terrain, the given number of times.
    public void SmoothAll(int passes = 1)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            var copy = (ushort[])_heights.Clone();
            for (int z = 0; z < _resolution; z++)
                for (int x = 0; x < _resolution; x++)
                {
                    int Get(int gx, int gz) => copy[Math.Clamp(gz, 0, _resolution - 1) * _resolution + Math.Clamp(gx, 0, _resolution - 1)];
                    _heights[z * _resolution + x] = (ushort)((Get(x - 1, z) + Get(x + 1, z) + Get(x, z - 1) + Get(x, z + 1) + Get(x, z) * 2 + 3) / 6);
                }
        }
        Touch();
    }

    // Fills the terrain with hills made from layered noise. Same settings, same hills.
    // featureSize is the width of the biggest hills in local units, octaves adds finer detail on top, roughness (0 to 1) is
    // how strong that detail is, and amount (0 to 1) is how much of Height the hills use.
    public void Generate(int seed, float featureSize = 30f, int octaves = 5, float roughness = 0.5f, float amount = 0.8f)
    {
        octaves = Math.Clamp(octaves, 1, 8);
        featureSize = MathF.Max(featureSize, 0.5f);
        var values = new float[_heights.Length];
        float min = float.MaxValue, max = float.MinValue;
        for (int z = 0; z < _resolution; z++)
            for (int x = 0; x < _resolution; x++)
            {
                float wx = x * CellX, wz = z * CellZ;
                float sum = 0f, amplitude = 1f, total = 0f, frequency = 1f / featureSize;
                for (int o = 0; o < octaves; o++)
                {
                    sum += ValueNoise(wx * frequency, wz * frequency, seed + o * 101) * amplitude;
                    total += amplitude;
                    amplitude *= Math.Clamp(roughness, 0f, 1f);
                    frequency *= 2f;
                }
                float v = sum / MathF.Max(total, 1e-6f);
                values[z * _resolution + x] = v;
                min = MathF.Min(min, v);
                max = MathF.Max(max, v);
            }
        float range = MathF.Max(max - min, 1e-6f);
        for (int i = 0; i < values.Length; i++)
        {
            float v = (values[i] - min) / range;
            v = v * v * (3f - 2f * v);   // flatter valleys and tops, steeper between
            _heights[i] = (ushort)Math.Clamp((int)MathF.Round(v * Math.Clamp(amount, 0f, 1f) * 65535f), 0, 65535);
        }
        Touch();
    }

    // Changes the number of grid points per side, keeping the shape of the ground.
    public void Resize(int resolution)
    {
        resolution = Math.Clamp(resolution, MinResolution, MaxResolution);
        if (resolution == _resolution) return;
        var fresh = new ushort[resolution * resolution];
        for (int z = 0; z < resolution; z++)
            for (int x = 0; x < resolution; x++)
            {
                float u = (float)x / (resolution - 1), v = (float)z / (resolution - 1);
                fresh[z * resolution + x] = (ushort)Math.Clamp((int)MathF.Round(Sample01((u - 0.5f) * Width, (v - 0.5f) * Length) * 65535f), 0, 65535);
            }
        // Paint is resampled to the new grid the same way (nearest point: it is a blend, not a height).
        for (int l = 0; l < _weights.Count; l++)
        {
            var oldWeights = _weights[l];
            var resampled = new byte[resolution * resolution];
            for (int z = 0; z < resolution; z++)
                for (int x = 0; x < resolution; x++)
                {
                    int sx = (int)MathF.Round((float)x / (resolution - 1) * (_resolution - 1));
                    int sz = (int)MathF.Round((float)z / (resolution - 1) * (_resolution - 1));
                    resampled[z * resolution + x] = oldWeights[sz * _resolution + sx];
                }
            _weights[l] = resampled;
            _weightsText[l] = null;
        }
        _resolution = resolution;
        _heights = fresh;
        Touch();
        TouchColors();
    }

    private static float ValueNoise(float x, float y, int seed)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float tx = x - xi, ty = y - yi;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
    }

    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 362437);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    // ---- saving ---------------------------------------------------------------------------------

    private string? _heightsText;

    // The heights as text, the way scene files keep them: the high bytes of every point, then the low bytes, squeezed
    // with deflate (smooth ground shrinks a lot) and written in base64. Kept until the ground changes, because saving
    // happens often in the editor (for undo) and the ground rarely changes between saves.
    public string HeightsData
    {
        get
        {
            if (_heightsText is null)
            {
                var planar = new byte[_heights.Length * 2];
                for (int i = 0; i < _heights.Length; i++)
                {
                    planar[i] = (byte)(_heights[i] >> 8);
                    planar[_heights.Length + i] = (byte)(_heights[i] & 0xFF);
                }
                using var packed = new MemoryStream();
                using (var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    deflate.Write(planar, 0, planar.Length);
                _heightsText = Convert.ToBase64String(packed.ToArray());
            }
            return _heightsText;
        }
    }

    // Loads heights saved by HeightsData for a terrain of this resolution. Bad data is ignored and the ground stays flat.
    public void LoadHeights(int resolution, string? data)
    {
        resolution = Math.Clamp(resolution, MinResolution, MaxResolution);
        _resolution = resolution;
        _heights = new ushort[resolution * resolution];
        for (int l = 0; l < _weights.Count; l++)
        {
            _weights[l] = new byte[resolution * resolution];
            _weightsText[l] = null;
        }
        if (!string.IsNullOrEmpty(data))
        {
            try
            {
                using var packed = new MemoryStream(Convert.FromBase64String(data));
                using var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionMode.Decompress);
                var planar = new byte[_heights.Length * 2];
                int read = 0;
                while (read < planar.Length)
                {
                    int n = deflate.Read(planar, read, planar.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read == planar.Length)
                    for (int i = 0; i < _heights.Length; i++)
                        _heights[i] = (ushort)((planar[i] << 8) | planar[_heights.Length + i]);
                else
                    Console.WriteLine("A terrain's saved heights are the wrong size for its resolution, so it starts flat.");
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
            {
                Console.WriteLine("A terrain's saved heights could not be read, so it starts flat.");
            }
        }
        Touch();
    }

    // The saved form of a layer's paint: its bytes, squeezed with deflate, in base64. Empty when nothing is painted.
    public string LayerWeightsData(int layer)
    {
        if (layer < 0 || layer >= _layers.Count) return "";
        if (_weightsText[layer] is { } cached) return cached;
        string text = "";
        if (_weights[layer].Any(w => w != 0))
        {
            using var packed = new MemoryStream();
            using (var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(_weights[layer], 0, _weights[layer].Length);
            text = Convert.ToBase64String(packed.ToArray());
        }
        return _weightsText[layer] = text;
    }

    // Adds a layer with its saved paint (see LayerWeightsData). Call after the heights are loaded, so the sizes match.
    public void LoadLayer(string name, Vector3 color, string? weights)
    {
        int index = AddLayer(name, color);
        if (index < 0 || string.IsNullOrEmpty(weights)) return;
        try
        {
            using var packed = new MemoryStream(Convert.FromBase64String(weights));
            using var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionMode.Decompress);
            var bytes = _weights[index];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = deflate.Read(bytes, read, bytes.Length - read);
                if (n == 0) break;
                read += n;
            }
            _weightsText[index] = null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
        {
            Console.WriteLine($"The paint of terrain layer '{name}' could not be read, so it starts empty.");
        }
        TouchColors();
    }

    private void Touch()
    {
        Version++;
        _heightsText = null;
    }

    private void TouchColors() => ColorVersion++;

    // ---- what the renderer and physics use ----------------------------------------------------------

    private GlbModel? _renderModel;
    private ModelPose? _pose;
    private float[] _positions = [], _normals = [], _uvs = [], _colors = [];
    private int _builtStructure = int.MinValue, _builtVersion = -1, _builtColorVersion = -1;

    // Changes when the drawn mesh has to be made again from scratch (resolution), not when only the ground or its
    // colors changed.
    public int StructureKey => _resolution;

    // The model to upload for this terrain, and the pose with its current vertices. Positions, normals and colors are
    // refreshed whenever the ground or its colors have changed since the last call. Build the model again (a new
    // instance) when StructureKey changes.
    public (GlbModel Model, ModelPose Pose) Prepare()
    {
        int structure = StructureKey;
        if (_renderModel is null || structure != _builtStructure)
        {
            int n = _resolution * _resolution;
            _positions = new float[n * 3];
            _normals = new float[n * 3];
            _uvs = new float[n * 2];
            _colors = new float[n * 4];
            var indices = new uint[(_resolution - 1) * (_resolution - 1) * 6];
            int k = 0;
            for (int z = 0; z < _resolution - 1; z++)
                for (int x = 0; x < _resolution - 1; x++)
                {
                    uint a = (uint)(z * _resolution + x), b = a + (uint)_resolution, c = a + 1, d = b + 1;
                    indices[k++] = a; indices[k++] = b; indices[k++] = c;
                    indices[k++] = c; indices[k++] = b; indices[k++] = d;
                }
            for (int z = 0; z < _resolution; z++)
                for (int x = 0; x < _resolution; x++)
                {
                    _uvs[(z * _resolution + x) * 2] = (float)x / (_resolution - 1);
                    _uvs[(z * _resolution + x) * 2 + 1] = (float)z / (_resolution - 1);
                }
            RefreshVertices();
            var material = new MaterialData([1, 1, 1, 1], 0.95f, 0f, 1.5f, 0.25f, [1, 1, 1], [0, 0, 0]);
            var mesh = new MeshData("Terrain", _positions, _normals, _uvs, indices, material) { Colors = _colors };
            _renderModel = new GlbModel
            {
                Name = "Terrain",
                Meshes = [mesh],
                BoundsMin = new Vector3(-Width / 2f, 0f, -Length / 2f),
                BoundsMax = new Vector3(Width / 2f, Height, Length / 2f),
            };
            _pose = new ModelPose
            {
                Model = _renderModel,
                MeshTransforms = [Matrix4x4.Identity],
                Positions = [_positions],
                Normals = [_normals],
                TexCoords = [_uvs],
                Colors = [_colors],
            };
            _builtStructure = structure;
            _builtVersion = Version;
            _builtColorVersion = ColorVersion;
        }
        else if (_builtVersion != Version)
        {
            RefreshVertices();
            _builtVersion = Version;
            _builtColorVersion = ColorVersion;
        }
        else if (_builtColorVersion != ColorVersion)
        {
            RefreshColors();
            _builtColorVersion = ColorVersion;
        }
        return (_renderModel, _pose!);
    }

    // Recomputes every vertex from the heights: where it is and which way it faces, then its color.
    private void RefreshVertices()
    {
        float cx = CellX, cz = CellZ;
        for (int z = 0; z < _resolution; z++)
            for (int x = 0; x < _resolution; x++)
            {
                int i = z * _resolution + x;
                _positions[i * 3] = -Width / 2f + x * cx;
                _positions[i * 3 + 1] = GetHeight01(x, z) * Height;
                _positions[i * 3 + 2] = -Length / 2f + z * cz;

                float hx = (GetHeight01(x + 1, z) - GetHeight01(x - 1, z)) * Height / ((Math.Min(x + 1, _resolution - 1) - Math.Max(x - 1, 0)) * cx);
                float hz = (GetHeight01(x, z + 1) - GetHeight01(x, z - 1)) * Height / ((Math.Min(z + 1, _resolution - 1) - Math.Max(z - 1, 0)) * cz);
                var normal = Vector3.Normalize(new Vector3(-hx, 1f, -hz));
                _normals[i * 3] = normal.X; _normals[i * 3 + 1] = normal.Y; _normals[i * 3 + 2] = normal.Z;
            }
        RefreshColors();
    }

    // Recomputes every vertex color: the base color, with the painted layers over it.
    private void RefreshColors()
    {
        int layerCount = _layers.Count;
        for (int i = 0; i < _resolution * _resolution; i++)
        {
            var color = _baseColor;

            if (layerCount > 0)
            {
                float painted = 0f;
                var layerColor = Vector3.Zero;
                for (int l = 0; l < layerCount; l++)
                {
                    float w = _weights[l][i] / 255f;
                    if (w <= 0f) continue;
                    painted += w;
                    layerColor += _layers[l].Color * w;
                }
                if (painted > 0f)
                {
                    if (painted > 1f) { layerColor /= painted; painted = 1f; }
                    color = color * (1f - painted) + layerColor;
                }
            }
            _colors[i * 4] = color.X; _colors[i * 4 + 1] = color.Y; _colors[i * 4 + 2] = color.Z; _colors[i * 4 + 3] = 1f;
        }
    }

    private GlbModel? _collisionModel;
    private int _collisionVersion = -1;

    // The ground's triangles for physics. A new model only when the ground has changed, so physics keeps its search
    // structure between steps.
    public GlbModel CollisionModel
    {
        get
        {
            if (_collisionModel is null || _collisionVersion != Version)
            {
                int n = _resolution * _resolution;
                var positions = new float[n * 3];
                for (int z = 0; z < _resolution; z++)
                    for (int x = 0; x < _resolution; x++)
                    {
                        int i = z * _resolution + x;
                        positions[i * 3] = -Width / 2f + x * CellX;
                        positions[i * 3 + 1] = GetHeight01(x, z) * Height;
                        positions[i * 3 + 2] = -Length / 2f + z * CellZ;
                    }
                var indices = new uint[(_resolution - 1) * (_resolution - 1) * 6];
                int k = 0;
                for (int z = 0; z < _resolution - 1; z++)
                    for (int x = 0; x < _resolution - 1; x++)
                    {
                        uint a = (uint)(z * _resolution + x), b = a + (uint)_resolution, c = a + 1, d = b + 1;
                        indices[k++] = a; indices[k++] = b; indices[k++] = c;
                        indices[k++] = c; indices[k++] = b; indices[k++] = d;
                    }
                var material = new MaterialData([1, 1, 1, 1], 1f, 0f, 1.5f, 0f, [1, 1, 1], [0, 0, 0]);
                _collisionModel = new GlbModel
                {
                    Name = "Terrain",
                    Meshes = [new MeshData("Terrain", positions, new float[n * 3], new float[n * 2], indices, material)],
                    BoundsMin = new Vector3(-Width / 2f, 0f, -Length / 2f),
                    BoundsMax = new Vector3(Width / 2f, Height, Length / 2f),
                };
                _collisionVersion = Version;
            }
            return _collisionModel;
        }
    }
}
