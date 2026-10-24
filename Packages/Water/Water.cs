using System.Threading.Tasks;
using Engine.Core.Animation;
using Engine.Core.Assets;
using Engine.Core.Physics;

// How many points the water grid has along each side. More gives smoother waves and costs far more: every point moves
// every frame. Above 1024 x 1024 the game slows to a crawl, and the largest sizes need more memory than most computers
// have (the component then uses the largest size that fits).
public enum WaterResolution
{
    [Label("256 x 256")] Grid256 = 256,
    [Label("512 x 512")] Grid512 = 512,
    [Label("1024 x 1024 (slow on weaker GPUs)")] Grid1024 = 1024,
    [Label("2048 x 2048 (very slow)")] Grid2048 = 2048,
    [Label("4096 x 4096 (unplayable, about 4 GB)")] Grid4096 = 4096,
    [Label("8192 x 8192 (won't fit, about 17 GB)")] Grid8192 = 8192,
    [Label("16384 x 16384 (won't fit, about 67 GB)")] Grid16384 = 16384,
}

// How many waves are added together. More waves give a more natural, less repetitive sea; each one costs a little
// time for every point of the grid, every frame (spread across the CPU's cores).
public enum WaterQuality
{
    [Label("Low (4 waves)")] Low = 4,
    [Label("Medium (8 waves)")] Medium = 8,
    [Label("High (16 waves)")] High = 16,
    [Label("Ultra (32 waves)")] Ultra = 32,
}

// Turns a plane into moving water while the game plays. Put it on an object whose Mesh is a flat plane model;
// the plane is replaced by a finer grid that rolls with waves. Wave sizes are in world units, so
// scaling the object makes more water, not bigger waves. Only planes work for now.
//
// The sea is a sum of Gerstner waves (points travel in small circles, which sharpens the crests), from long swells down
// to short chop, mostly travelling with the wind. Foam forms where crests pinch together, and a scrolling ripple
// texture adds detail finer than the grid.
//
// Part of the Water package: it is an ordinary script, compiled into the project when the package is installed.
public class Water : Component
{
    public float WaveHeight = 0.25f;    // how high the waves rise, overall
    public float WaveLength = 8f;       // distance between the longest waves' crests; shorter waves are added below it
    public float WaveSpeed = 1f;        // 1 moves like real water of those wave lengths; 2 twice as fast
    public float Choppiness = 0.6f;     // 0 gives round swells, 1 sharp crests
    public float WindDirection = 30f;   // the way most waves travel, in degrees around the up axis
    public WaterQuality Quality = WaterQuality.High;
    public int Seed = 1;                // a different number gives a different arrangement of waves
    public WaterResolution Resolution = WaterResolution.Grid256;   // grid points along each side of the plane
    public float Ripples = 0.6f;        // strength of the fine ripples on the surface, 0 to 1
    public float RippleSize = 2f;       // how wide one tile of ripples is, in world units
    public float Smoothness = 0.92f;    // 1 is mirror-like, lower is duller
    public float Foam = 0.6f;           // how much foam forms on pinched crests, 0 to 1
    public float Opacity = 0.85f;       // 1 is solid; lower lets what is under the water show through
    public Vector3 DeepColor = new(0.02f, 0.14f, 0.24f);
    public Vector3 ShallowColor = new(0.05f, 0.34f, 0.44f);
    public Vector3 FoamColor = new(0.88f, 0.94f, 0.98f);

    // One wave, worked out once: its direction, wave number (2 pi / length), height, crest sharpness, speed and phase.
    private struct Wave
    {
        public float DirX, DirZ, K, Amplitude, Q, Omega, Phase;
    }

    private MeshRenderer? _renderer;
    private ModelPose? _pose;
    private Wave[] _waves = [];
    private int _size;                   // grid points per side
    private float[] _rest = [];          // the flat grid: x, y, z per point, in the plane's own space
    private float[] _columnX = [], _rowZ = [];   // the grid's x for each column and z for each row, in the plane's own space

    // One frame's surface: the mesh's vertices and the working values used to fill them. There are two: the one being
    // drawn, and the one the next frame is worked out into in the background, so that work overlaps the drawing.
    private sealed class Surface(int n, int waves)
    {
        public readonly float[] Vertices = new float[n * n * ModelPose.VertexFloats];   // ready for the GPU
        public readonly float[] ColumnSin = new float[waves * n], ColumnCos = new float[waves * n];
        public readonly float[] RowSin = new float[waves * n], RowCos = new float[waves * n];
        public readonly float[] ColumnU = new float[n];
    }

    private Surface? _shown, _next;
    private Task? _working;              // the next frame's surface, being worked out
    private readonly System.Collections.Concurrent.ConcurrentBag<RowResults> _rowPool = new();

    public override void Start()
    {
        _renderer = GetComponent<MeshRenderer>();
        if (_renderer is null)
        {
            Console.WriteLine($"Water on '{Object.Name}' needs a Mesh (a plane) on the same object.");
            return;
        }
        var plane = _renderer.Model.Length > 0 ? PhysicsWorld.ModelLoader?.Invoke(_renderer.Model) : null;
        if (plane is null)
        {
            Console.WriteLine($"Water on '{Object.Name}': its Mesh has no model to turn into water.");
            _renderer = null;
            return;
        }
        var size = plane.BoundsMax - plane.BoundsMin;
        if (size.Y > 0.01f * MathF.Max(size.X, size.Z) || size.X <= 0f || size.Z <= 0f)
        {
            Console.WriteLine($"Water on '{Object.Name}' only works on a flat plane, and '{_renderer.Model}' isn't one.");
            _renderer = null;
            return;
        }
        _waves = MakeWaves();
        Build(plane.BoundsMin, plane.BoundsMax);
        Compute(_shown!, Settings(Time.time));   // the first frame is worked out right away
    }

    // The set of waves: lengths spread from WaveLength down to a sixteenth of it, longer ones taller and closer to the
    // wind's direction, shorter ones lower and more scattered. Heights are scaled so they add up to about twice
    // WaveHeight, and each wave's sharpness is limited so crests never loop over themselves.
    private Wave[] MakeWaves()
    {
        int count = Math.Clamp((int)Quality, 1, 64);
        var random = new Random(Seed);
        float wind = WindDirection * MathF.PI / 180f;
        float longest = MathF.Max(WaveLength, 0.05f);
        var waves = new Wave[count];
        float totalWeight = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0f : i / (count - 1f);
            float length = longest * MathF.Pow(1f / 16f, t) * (0.85f + 0.3f * (float)random.NextDouble());
            float spread = (0.35f + 0.9f * t) * ((float)random.NextDouble() * 2f - 1f);   // radians either side of the wind
            float angle = wind + spread;
            float k = MathF.Tau / length;
            float weight = MathF.Pow(length / longest, 0.8f);
            totalWeight += weight;
            waves[i] = new Wave
            {
                DirX = MathF.Cos(angle),
                DirZ = MathF.Sin(angle),
                K = k,
                Amplitude = weight,
                Omega = MathF.Sqrt(9.81f * k),   // deep water: longer waves travel faster
                Phase = (float)random.NextDouble() * MathF.Tau,
            };
        }
        float steepness = Math.Clamp(Choppiness, 0f, 1f);
        for (int i = 0; i < count; i++)
        {
            waves[i].Amplitude *= 2f * WaveHeight / totalWeight;
            waves[i].Q = steepness / (waves[i].K * MathF.Max(MathF.Abs(waves[i].Amplitude), 1e-6f) * count);
        }
        return waves;
    }

    // A grid of Resolution x Resolution points covering the plane, drawn in its place.
    private void Build(Vector3 min, Vector3 max)
    {
        int n = Fit((int)Resolution);
        _size = n;
        int count = n * n;
        _rest = new float[count * 3];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int i = z * n + x;
                float u = x / (n - 1f), v = z / (n - 1f);
                _rest[i * 3] = min.X + (max.X - min.X) * u;
                _rest[i * 3 + 1] = (min.Y + max.Y) * 0.5f;
                _rest[i * 3 + 2] = min.Z + (max.Z - min.Z) * v;
            }
        _columnX = new float[n];
        _rowZ = new float[n];
        for (int i = 0; i < n; i++)
        {
            _columnX[i] = _rest[i * 3];
            _rowZ[i] = _rest[i * n * 3 + 2];
        }
        _shown = new Surface(n, _waves.Length);
        _next = new Surface(n, _waves.Length);

        // The flat plane, for the mesh's first upload. After that each frame's vertices come ready-made from a Surface.
        var positions = (float[])_rest.Clone();
        var normals = new float[count * 3];
        for (int i = 0; i < count; i++) normals[i * 3 + 1] = 1f;
        var colors = new float[count * 4];
        var uvs = new float[count * 2];

        var indices = new uint[(n - 1) * (n - 1) * 6];
        int k = 0;
        for (int z = 0; z < n - 1; z++)
            for (int x = 0; x < n - 1; x++)
            {
                uint a = (uint)(z * n + x), b = a + (uint)n, c = a + 1, d = b + 1;
                indices[k++] = a; indices[k++] = b; indices[k++] = c;
                indices[k++] = c; indices[k++] = b; indices[k++] = d;
            }

        // A clean, glassy surface: the sky's reflection and the sun's highlight do most of the work. The ripple texture
        // bends the surface's normal for detail the grid is too coarse to carry.
        float roughness = Math.Clamp(1f - Smoothness, 0.02f, 1f);
        float opacity = Math.Clamp(Opacity, 0f, 1f);
        var material = new MaterialData([1, 1, 1, opacity], roughness, 0f, 1.33f, 1f, [1, 1, 1], [0, 0, 0],
            NormalImage: Ripples > 0f ? RippleTexture() : null, NormalScale: Math.Clamp(Ripples, 0f, 1f),
            AlphaBlend: opacity < 1f);
        var mesh = new MeshData("Water", positions, normals, uvs, indices, material) { Colors = colors };
        float rise = MathF.Abs(WaveHeight) * 2.5f;
        var model = new GlbModel
        {
            Name = "Water",
            Meshes = [mesh],
            BoundsMin = min - new Vector3(0, rise, 0),
            BoundsMax = max + new Vector3(0, rise, 0),
        };
        _pose = new ModelPose
        {
            Model = model,
            MeshTransforms = [Matrix4x4.Identity],
            Positions = [positions],
            Normals = [normals],
            Colors = [colors],
            TexCoords = [uvs],
            Vertices = [_shown.Vertices],
        };
        _renderer!.Mesh = model;
        _renderer.Pose = _pose;
    }

    // A tiling normal map of small criss-crossing ripples. Every ripple fits the tile a whole number of times, so the
    // edges meet without a seam.
    private ImageData RippleTexture()
    {
        const int size = 256;
        var random = new Random(Seed * 7919 + 17);
        var ripples = new (int Fx, int Fz, float Amp, float Phase)[12];
        for (int i = 0; i < ripples.Length; i++)
        {
            int fx = random.Next(-9, 10), fz = random.Next(-9, 10);
            if (fx == 0 && fz == 0) fx = 3;
            ripples[i] = (fx, fz, 1f / MathF.Sqrt(fx * fx + fz * fz), (float)random.NextDouble() * MathF.Tau);
        }

        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                float dx = 0f, dy = 0f;
                foreach (var (fx, fz, amp, phase) in ripples)
                {
                    float c = MathF.Cos(MathF.Tau * (fx * u + fz * v) + phase) * amp;
                    dx += c * fx;
                    dy += c * fz;
                }
                var n = Vector3.Normalize(new Vector3(-dx * 0.08f, -dy * 0.08f, 1f));   // tangent space: z is "up"
                int o = (y * size + x) * 4;
                pixels[o] = (byte)Math.Clamp((n.X * 0.5f + 0.5f) * 255f, 0f, 255f);
                pixels[o + 1] = (byte)Math.Clamp((n.Y * 0.5f + 0.5f) * 255f, 0f, 255f);
                pixels[o + 2] = (byte)Math.Clamp((n.Z * 0.5f + 0.5f) * 255f, 0f, 255f);
                pixels[o + 3] = 255;
            }
        return new ImageData { Name = "Water ripples", Width = size, Height = size, Pixels = pixels };
    }

    // Memory per grid point: the component's own copies (about 92 bytes) plus the renderer's (about 170 bytes).
    private const long BytesPerPoint = 264;

    // The requested size, or the largest smaller one that fits in half of the memory the program may use, so a size
    // that can't work slows the game down instead of crashing it. Sizes above 1024 get a warning.
    private int Fit(int requested)
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        int n = Math.Clamp(requested, 256, 16384);
        while (n > 256 && (long)n * n * BytesPerPoint > available / 2)
            n /= 2;
        if (n < requested)
            Console.WriteLine($"Water on '{Object.Name}': {requested} x {requested} needs about {(long)requested * requested * BytesPerPoint >> 30} GB, " +
                              $"more than this computer can spare, so {n} x {n} is used instead.");
        if (n > 1024)
            Console.WriteLine($"Water on '{Object.Name}': {n} x {n} is {(long)n * n:N0} points moving every frame. Expect the game to run very slowly.");
        return n;
    }

    // What one frame's surface is worked out from, read on the game's thread so the background work never touches
    // the component's fields or the scene while the game changes them.
    private readonly record struct FrameSettings(float Sx, float Sy, float Sz, float Time, float WaveSpeed, float Foam,
        float Choppiness, float WindDirection, float RippleSize, Vector3 Deep, Vector3 Shallow, Vector3 FoamColor);

    private FrameSettings Settings(float time)
    {
        var scale = Object.Scale;
        return new FrameSettings(MathF.Max(MathF.Abs(scale.X), 1e-4f), MathF.Max(MathF.Abs(scale.Y), 1e-4f),
            MathF.Max(MathF.Abs(scale.Z), 1e-4f), time, WaveSpeed, Foam, Choppiness, WindDirection, RippleSize,
            DeepColor, ShallowColor, FoamColor);
    }

    // Each frame: show the surface worked out in the background during the last frame (it was worked out for this
    // moment), and start on the next one. The water is drawn one frame behind, which can't be seen, and its work
    // happens while the game draws and waits for the screen instead of adding to the frame.
    public override void Update()
    {
        if (_renderer is null || _pose is null || _shown is null || _next is null) return;
        if (_working is null)
        {
            _working = StartNext();   // the first frame shows what Start worked out
            return;
        }
        _working.Wait();
        (_shown, _next) = (_next, _shown);
        _pose.Vertices![0] = _shown.Vertices;
        _working = StartNext();
    }

    // Works out the next frame's surface on other threads, for the time it will be when that frame is shown.
    private Task StartNext()
    {
        var settings = Settings(Time.time + MathF.Max(Time.deltaTime, 1f / 120f));
        var target = _next!;
        return Task.Run(() => Compute(target, settings));
    }

    // One wave's numbers for this frame, multiplied out ahead so the inner loop only multiplies and adds.
    private struct WaveTerms
    {
        public float QaDx, QaDz, Amplitude, KaDx, KaDz, Qka, Pxx, Pzz, Pxz;
    }

    // One row's results before they go into the mesh, one value per column. Each worker thread keeps its own set.
    private sealed class RowResults(int n)
    {
        public readonly float[] Dx = new float[n], Dy = new float[n], Dz = new float[n];
        public readonly float[] Nx = new float[n], Ny = new float[n], Nz = new float[n];
        public readonly float[] R = new float[n], G = new float[n], B = new float[n];
    }

    // Moves every point with the waves, works out which way the surface faces and how pinched it is (for foam), colors
    // it, and slides the ripple texture along with the wind, into one frame's surface.
    //
    // Speed: a wave's phase at a point is (part from the column) + (part from the row), so its sine and cosine come from
    // sin(a + b) = sin a cos b + cos a sin b. Sines and cosines are worked out once per column and once per row
    // (thousands per frame instead of millions). Each point then only multiplies and adds: several columns at a time
    // with SIMD vectors, every wave added while the running totals stay in the CPU's registers, and the rows shared out
    // across the CPU's cores.
    private void Compute(Surface surface, FrameSettings f)
    {
        // Waves are measured in world units, so the object's scale is taken out of the plane's own coordinates.
        float sx = f.Sx, sy = f.Sy, sz = f.Sz;
        float time = f.Time * f.WaveSpeed;
        var waves = _waves;
        int n = _size, count = waves.Length;

        float squares = 0f;
        var terms = new WaveTerms[count];
        for (int wi = 0; wi < count; wi++)
        {
            var w = waves[wi];
            squares += w.Amplitude * w.Amplitude;
            float qa = w.Q * w.Amplitude, ka = w.K * w.Amplitude, qka = w.Q * ka;
            terms[wi] = new WaveTerms
            {
                QaDx = qa * w.DirX, QaDz = qa * w.DirZ, Amplitude = w.Amplitude,
                KaDx = ka * w.DirX, KaDz = ka * w.DirZ, Qka = qka,
                Pxx = qka * w.DirX * w.DirX, Pzz = qka * w.DirZ * w.DirZ, Pxz = qka * w.DirX * w.DirZ,
            };
            for (int i = 0; i < n; i++)
            {
                (surface.ColumnSin[wi * n + i], surface.ColumnCos[wi * n + i]) = MathF.SinCos(w.K * w.DirX * _columnX[i] * sx);
                // Row values are stored row by row, so one row's waves sit together in memory.
                (surface.RowSin[i * count + wi], surface.RowCos[i * count + wi]) = MathF.SinCos(w.K * w.DirZ * _rowZ[i] * sz - w.Omega * time + w.Phase);
            }
        }
        float rms = MathF.Sqrt(squares / 2f);   // the typical height of the surface

        // Foam is measured against what this set of waves typically does, so the Foam setting means the same thing
        // whatever the wave count or choppiness: at 0 there is none, around 0.6 the sharpest crests carry it, at 1
        // it streaks across much of the sea. How far a crest pinches grows with Choppiness and shrinks as more waves
        // share it; pinchScale turns it into a number around 1 on the sharpest few crests.
        float foamAmount = Math.Clamp(f.Foam, 0f, 1f);
        float pinchScale = 1f / (MathF.Max(Math.Clamp(f.Choppiness, 0f, 1f), 0.05f) * 1.3f / MathF.Sqrt(count));
        float pinchStart = 1.4f - 1.1f * foamAmount;        // pinch (scaled) where foam begins
        float heightStart = 2.6f - 1.2f * foamAmount;       // height (in typical heights) where crest foam begins

        float wind = f.WindDirection * MathF.PI / 180f;
        float rippleSize = MathF.Max(f.RippleSize, 0.05f);
        float driftU = MathF.Cos(wind) * f.Time * 0.03f, driftV = MathF.Sin(wind) * f.Time * 0.03f;
        var deep = f.Deep;
        var shallow = f.Shallow;
        var foamColor = f.FoamColor;
        int lanes = Vector<float>.Count;
        float invSx = 1f / sx, invSy = 1f / sy, invSz = 1f / sz;
        float invRms = rms > 0f ? 1f / rms : 0f;
        float crestScale = rms > 0f ? 0.5f / (rms * 2.5f) : 0f;   // height to a 0..1 trough-to-crest value, around 0.5
        var columnU = surface.ColumnU;
        for (int c = 0; c < n; c++) columnU[c] = _columnX[c] * sx / rippleSize + driftU;

        var columnSin = surface.ColumnSin; var columnCos = surface.ColumnCos;
        var rowSin = surface.RowSin; var rowCos = surface.RowCos;
        var vertices = surface.Vertices;
        Parallel.For(0, n, () => _rowPool.TryTake(out var pooled) && pooled.Dx.Length == n ? pooled : new RowResults(n), (row, _, results) =>
        {
            int rowOffset = row * count;
            int col = 0;
            for (; col <= n - lanes; col += lanes)
            {
                var dx = Vector<float>.Zero; var dy = Vector<float>.Zero; var dz = Vector<float>.Zero;
                var nx = Vector<float>.Zero; var ny = Vector<float>.One; var nz = Vector<float>.Zero;
                var jxx = Vector<float>.One; var jzz = Vector<float>.One; var jxz = Vector<float>.Zero;
                for (int wi = 0; wi < count; wi++)
                {
                    ref readonly var t = ref terms[wi];
                    float sb = rowSin[rowOffset + wi], cb = rowCos[rowOffset + wi];
                    var sa = new Vector<float>(columnSin, wi * n + col);
                    var ca = new Vector<float>(columnCos, wi * n + col);
                    var sin = sa * cb + ca * sb;
                    var cos = ca * cb - sa * sb;
                    dx += cos * t.QaDx; dz += cos * t.QaDz; dy += sin * t.Amplitude;
                    nx -= cos * t.KaDx; nz -= cos * t.KaDz; ny -= sin * t.Qka;
                    jxx -= sin * t.Pxx; jzz -= sin * t.Pzz; jxz -= sin * t.Pxz;
                }

                dx.CopyTo(results.Dx, col); dy.CopyTo(results.Dy, col); dz.CopyTo(results.Dz, col);
                nx *= invSx; ny *= invSy; nz *= invSz;   // the renderer turns normals by the object's scale; undo it
                var invLength = Vector<float>.One / Vector.SquareRoot(nx * nx + ny * ny + nz * nz);
                (nx * invLength).CopyTo(results.Nx, col);
                (ny * invLength).CopyTo(results.Ny, col);
                (nz * invLength).CopyTo(results.Nz, col);

                // Darker in the troughs, lighter toward the crests. Foam where the surface pinches (it would fold over
                // if the waves were any sharper), and on the tallest crests.
                var crest = Vector.Min(Vector.Max(dy * crestScale + new Vector<float>(0.5f), Vector<float>.Zero), Vector<float>.One);
                var r = new Vector<float>(deep.X) + crest * (shallow.X - deep.X);
                var g = new Vector<float>(deep.Y) + crest * (shallow.Y - deep.Y);
                var b = new Vector<float>(deep.Z) + crest * (shallow.Z - deep.Z);
                if (foamAmount > 0f)
                {
                    var pinch = (Vector<float>.One - (jxx * jzz - jxz * jxz)) * pinchScale;
                    var foam = Vector.Max(SmoothVector(pinchStart, pinchStart + 0.5f, pinch),
                        SmoothVector(heightStart, heightStart + 0.6f, dy * invRms) * 0.8f);
                    r += (new Vector<float>(foamColor.X) - r) * foam;
                    g += (new Vector<float>(foamColor.Y) - g) * foam;
                    b += (new Vector<float>(foamColor.Z) - b) * foam;
                }
                r.CopyTo(results.R, col); g.CopyTo(results.G, col); b.CopyTo(results.B, col);
            }
            for (; col < n; col++)   // columns left over when n isn't a multiple of the vector width
            {
                float dx = 0f, dy = 0f, dz = 0f, nx = 0f, ny = 1f, nz = 0f, jxx = 1f, jzz = 1f, jxz = 0f;
                for (int wi = 0; wi < count; wi++)
                {
                    var t = terms[wi];
                    float sb = rowSin[rowOffset + wi], cb = rowCos[rowOffset + wi];
                    float sa = columnSin[wi * n + col], ca = columnCos[wi * n + col];
                    float sin = sa * cb + ca * sb, cos = ca * cb - sa * sb;
                    dx += cos * t.QaDx; dz += cos * t.QaDz; dy += sin * t.Amplitude;
                    nx -= cos * t.KaDx; nz -= cos * t.KaDz; ny -= sin * t.Qka;
                    jxx -= sin * t.Pxx; jzz -= sin * t.Pzz; jxz -= sin * t.Pxz;
                }
                var normal = Vector3.Normalize(new Vector3(nx * invSx, ny * invSy, nz * invSz));
                var color = Vector3.Lerp(deep, shallow, Math.Clamp(dy * crestScale + 0.5f, 0f, 1f));
                if (foamAmount > 0f)
                {
                    float pinch = (1f - (jxx * jzz - jxz * jxz)) * pinchScale;
                    float foam = MathF.Max(Smooth(pinchStart, pinchStart + 0.5f, pinch), Smooth(heightStart, heightStart + 0.6f, dy * invRms) * 0.8f);
                    color = Vector3.Lerp(color, foamColor, foam);
                }
                results.Dx[col] = dx; results.Dy[col] = dy; results.Dz[col] = dz;
                results.Nx[col] = normal.X; results.Ny[col] = normal.Y; results.Nz[col] = normal.Z;
                results.R[col] = color.X; results.G[col] = color.Y; results.B[col] = color.Z;
            }

            // Into the vertices in the GPU's layout: position, normal, texture coordinates, color.
            float rowV = _rowZ[row] * sz / rippleSize + driftV;
            for (int column = 0; column < n; column++)
            {
                int i = row * n + column, v = i * ModelPose.VertexFloats;
                vertices[v] = _rest[i * 3] + results.Dx[column] * invSx;
                vertices[v + 1] = _rest[i * 3 + 1] + results.Dy[column] * invSy;
                vertices[v + 2] = _rest[i * 3 + 2] + results.Dz[column] * invSz;
                vertices[v + 3] = results.Nx[column]; vertices[v + 4] = results.Ny[column]; vertices[v + 5] = results.Nz[column];
                vertices[v + 6] = columnU[column]; vertices[v + 7] = rowV;
                vertices[v + 8] = results.R[column]; vertices[v + 9] = results.G[column]; vertices[v + 10] = results.B[column]; vertices[v + 11] = 1f;
            }
            return results;
        }, results => _rowPool.Add(results));   // kept for the next frame, so nothing is allocated per frame
    }

    private static Vector<float> SmoothVector(float edge0, float edge1, Vector<float> x)
    {
        var t = Vector.Min(Vector.Max((x - new Vector<float>(edge0)) * (1f / (edge1 - edge0)), Vector<float>.Zero), Vector<float>.One);
        return t * t * (new Vector<float>(3f) - t * 2f);
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
