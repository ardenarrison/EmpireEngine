using System.Reflection;

namespace Engine.Brutal.Rendering;

// The shaders of the two post-process anti-aliasing methods. Both are full-screen triangles that read the finished scene from a
// texture and write the smoothed picture into the swapchain image.
//
// FXAA is one pass: it finds edges from the brightness around each pixel and blurs along them (the short form of Timothy Lottes'
// FXAA). SMAA is three: find edges, work out how much of each neighbor to blend in (using the two lookup textures from the SMAA
// reference), then blend. The SMAA code itself is the reference implementation from github.com/iryoku/smaa (MIT license), kept
// as it is in Smaa/SMAA.glsl (only the authors' contact emails are removed from its header), and the shaders below only supply what it asks the host program for.
internal static class AntiAliasingShaders
{
    // The size of the target in pixels as (1 / width, 1 / height, width, height), passed to every pass as a push constant.
    private const string Header = """
        #version 450
        layout(push_constant) uniform PC { vec4 metrics; } pc;
        """;

    private const string SmaaDefines = """
        #define SMAA_RT_METRICS pc.metrics
        #define SMAA_GLSL_4
        #define SMAA_PRESET_HIGH
        """;

    // A full-screen triangle; the texture coordinate runs from (0, 0) at the top left to (1, 1) at the bottom right.
    private const string TriangleBody = """
        vec2 corner = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
        vTex = corner;
        gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        """;

    public static string FxaaVertex => Header + """

        layout(location = 0) out vec2 vTex;
        void main() {
        """ + TriangleBody + "}\n";

    public static string FxaaFragment => Header + """

        layout(set = 0, binding = 0) uniform sampler2D sceneTex;
        layout(location = 0) in vec2 vTex;
        layout(location = 0) out vec4 outColor;

        const float REDUCE_MIN = 1.0 / 128.0;
        const float REDUCE_MUL = 1.0 / 8.0;
        const float SPAN_MAX = 8.0;

        void main() {
            vec2 px = pc.metrics.xy;
            vec3 rgbNW = texture(sceneTex, vTex + vec2(-1.0, -1.0) * px).rgb;
            vec3 rgbNE = texture(sceneTex, vTex + vec2(1.0, -1.0) * px).rgb;
            vec3 rgbSW = texture(sceneTex, vTex + vec2(-1.0, 1.0) * px).rgb;
            vec3 rgbSE = texture(sceneTex, vTex + vec2(1.0, 1.0) * px).rgb;
            vec4 center = texture(sceneTex, vTex);
            vec3 rgbM = center.rgb;

            const vec3 luma = vec3(0.299, 0.587, 0.114);
            float lumaNW = dot(rgbNW, luma);
            float lumaNE = dot(rgbNE, luma);
            float lumaSW = dot(rgbSW, luma);
            float lumaSE = dot(rgbSE, luma);
            float lumaM = dot(rgbM, luma);
            float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
            float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

            // The direction the edge runs in, from how the brightness changes across the pixel.
            vec2 dir;
            dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
            dir.y = (lumaNW + lumaSW) - (lumaNE + lumaSE);
            float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * REDUCE_MUL), REDUCE_MIN);
            float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
            dir = min(vec2(SPAN_MAX), max(vec2(-SPAN_MAX), dir * rcpDirMin)) * px;

            vec3 rgbA = 0.5 * (texture(sceneTex, vTex + dir * (1.0 / 3.0 - 0.5)).rgb +
                               texture(sceneTex, vTex + dir * (2.0 / 3.0 - 0.5)).rgb);
            vec3 rgbB = rgbA * 0.5 + 0.25 * (texture(sceneTex, vTex + dir * -0.5).rgb +
                                             texture(sceneTex, vTex + dir * 0.5).rgb);
            float lumaB = dot(rgbB, luma);
            // The wide blur is kept only when it doesn't leave the range of brightness around the pixel.
            outColor = vec4((lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB, center.a);
        }
        """;

    public static string SmaaEdgeVertex => Header + "\n" + SmaaDefines + "\n#define SMAA_INCLUDE_PS 0\n" + Smaa + """

        layout(location = 0) out vec2 vTex;
        layout(location = 1) out vec4 vOffset[3];
        void main() {
        """ + TriangleBody + """

            SMAAEdgeDetectionVS(vTex, vOffset);
        }
        """;

    public static string SmaaEdgeFragment => Header + "\n" + SmaaDefines + "\n#define SMAA_INCLUDE_VS 0\n" + Smaa + """

        layout(set = 0, binding = 0) uniform sampler2D colorTex;
        layout(location = 0) in vec2 vTex;
        layout(location = 1) in vec4 vOffset[3];
        layout(location = 0) out vec4 outColor;
        void main() {
            outColor = vec4(SMAALumaEdgeDetectionPS(vTex, vOffset, colorTex), 0.0, 0.0);
        }
        """;

    public static string SmaaWeightsVertex => Header + "\n" + SmaaDefines + "\n#define SMAA_INCLUDE_PS 0\n" + Smaa + """

        layout(location = 0) out vec2 vTex;
        layout(location = 1) out vec2 vPix;
        layout(location = 2) out vec4 vOffset[3];
        void main() {
        """ + TriangleBody + """

            SMAABlendingWeightCalculationVS(vTex, vPix, vOffset);
        }
        """;

    public static string SmaaWeightsFragment => Header + "\n" + SmaaDefines + "\n#define SMAA_INCLUDE_VS 0\n" + Smaa + """

        layout(set = 0, binding = 0) uniform sampler2D edgesTex;
        layout(set = 0, binding = 1) uniform sampler2D areaTex;
        layout(set = 0, binding = 2) uniform sampler2D searchTex;
        layout(location = 0) in vec2 vTex;
        layout(location = 1) in vec2 vPix;
        layout(location = 2) in vec4 vOffset[3];
        layout(location = 0) out vec4 outColor;
        void main() {
            outColor = SMAABlendingWeightCalculationPS(vTex, vPix, vOffset, edgesTex, areaTex, searchTex, vec4(0.0));
        }
        """;

    public static string SmaaBlendVertex => Header + "\n" + SmaaDefines + "\n#define SMAA_INCLUDE_PS 0\n" + Smaa + """

        layout(location = 0) out vec2 vTex;
        layout(location = 1) out vec4 vOffset;
        void main() {
        """ + TriangleBody + """

            SMAANeighborhoodBlendingVS(vTex, vOffset);
        }
        """;

    public static string SmaaBlendFragment => Header + "\n" + SmaaDefines + "\n#define SMAA_INCLUDE_VS 0\n" + Smaa + """

        layout(set = 0, binding = 0) uniform sampler2D colorTex;
        layout(set = 0, binding = 1) uniform sampler2D blendTex;
        layout(location = 0) in vec2 vTex;
        layout(location = 1) in vec4 vOffset;
        layout(location = 0) out vec4 outColor;
        void main() {
            outColor = SMAANeighborhoodBlendingPS(vTex, vOffset, colorTex, blendTex);
        }
        """;

    // The SMAA reference source, embedded in the assembly.
    private static string Smaa => _smaa ??= ReadText("SMAA.glsl");
    private static string? _smaa;

    // The two lookup textures SMAA needs: the area texture (160 x 560, two 8-bit channels) and the search texture (64 x 16, one).
    public static byte[] AreaTexture => ReadBytes("AreaTex.bin");
    public static byte[] SearchTexture => ReadBytes("SearchTex.bin");

    private static string ReadText(string name) => System.Text.Encoding.UTF8.GetString(ReadBytes(name));

    private static byte[] ReadBytes(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Engine.Brutal.Rendering.Smaa." + name)
                           ?? throw new InvalidOperationException($"The embedded SMAA file {name} is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
