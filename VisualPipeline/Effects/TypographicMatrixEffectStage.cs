using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class TypographicMatrixEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.typographicMatrix";

        private const string CharacterRamp = " .:-=+*%@#";
        private const int MaxCharacters = 10;
        private const int GlyphWidth = 16;
        private const int GlyphHeight = 16;

        private readonly Parameter<float> _cellSize = new("Typographic Matrix / Cell Size (px)", 4f, 64f, 16f);
        private readonly Parameter<int> _densityGroups = new("Typographic Matrix / Density Groups", 1, MaxCharacters, 5);
        private readonly Parameter<float> _mix = new("Typographic Matrix / Mix", 0f, 1f, 1f);

        private readonly Parameter<float>[] _groupBrightness = new Parameter<float>[MaxCharacters];
        private readonly Parameter<float>[] _groupSize = new Parameter<float>[MaxCharacters];
        private readonly Parameter<float>[] _groupColorR = new Parameter<float>[MaxCharacters];
        private readonly Parameter<float>[] _groupColorG = new Parameter<float>[MaxCharacters];
        private readonly Parameter<float>[] _groupColorB = new Parameter<float>[MaxCharacters];

        private readonly List<IParameter> _parameters = [];
        private int _activeDensityGroups;

        private int _program;
        private int _atlasTexture;

        private int _uTexture;
        private int _uAtlasTexture;
        private int _uCellSize;
        private int _uCharCount;
        private int _uGroupCount;
        private int _uMix;

        private readonly int[] _uGroupBrightness = new int[MaxCharacters];
        private readonly int[] _uGroupSize = new int[MaxCharacters];
        private readonly int[] _uGroupColor = new int[MaxCharacters];

        public TypographicMatrixEffectStage()
        {
            for (var i = 0; i < MaxCharacters; i++)
            {
                var groupIndex = i + 1;
                var t = i / (float)(MaxCharacters - 1);

                _groupBrightness[i] = new Parameter<float>($"Typographic Matrix / Group {groupIndex} Brightness", 0f, 4f, 1f);
                _groupSize[i] = new Parameter<float>($"Typographic Matrix / Group {groupIndex} Size", 0.2f, 2.5f, 1f);
                _groupColorR[i] = new Parameter<float>($"Typographic Matrix / Group {groupIndex} Color R", 0f, 1f, 0.05f + 0.15f * t);
                _groupColorG[i] = new Parameter<float>($"Typographic Matrix / Group {groupIndex} Color G", 0f, 1f, 0.55f + 0.45f * t);
                _groupColorB[i] = new Parameter<float>($"Typographic Matrix / Group {groupIndex} Color B", 0f, 1f, 0.10f + 0.10f * t);
            }

            RebuildDynamicParameterList(Math.Clamp(_densityGroups.Value, 1, MaxCharacters));
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Typographic Matrix";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override bool RefreshDynamicParameters()
        {
            var targetGroups = Math.Clamp(_densityGroups.Value, 1, MaxCharacters);
            if (targetGroups == _activeDensityGroups)
            {
                return false;
            }

            RebuildDynamicParameterList(targetGroups);
            return true;
        }

        private void RebuildDynamicParameterList(int groups)
        {
            _activeDensityGroups = Math.Clamp(groups, 1, MaxCharacters);

            _parameters.Clear();
            _parameters.Add(_cellSize);
            _parameters.Add(_densityGroups);
            _parameters.Add(_mix);

            for (var i = 0; i < _activeDensityGroups; i++)
            {
                _parameters.Add(_groupBrightness[i]);
                _parameters.Add(_groupSize[i]);
                _parameters.Add(_groupColorR[i]);
                _parameters.Add(_groupColorG[i]);
                _parameters.Add(_groupColorB[i]);
            }
        }

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
                #version 330 core
                layout (location = 0) in vec2 aPosition;
                layout (location = 1) in vec2 aUv;
                out vec2 vUv;

                void main()
                {
                    vUv = aUv;
                    gl_Position = vec4(aPosition, 0.0, 1.0);
                }
                """;

            var fragment = BuildFragmentShader();

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uAtlasTexture = GL.GetUniformLocation(_program, "uAtlasTexture");
            _uCellSize = GL.GetUniformLocation(_program, "uCellSize");
            _uCharCount = GL.GetUniformLocation(_program, "uCharCount");
            _uGroupCount = GL.GetUniformLocation(_program, "uGroupCount");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            for (var i = 0; i < MaxCharacters; i++)
            {
                _uGroupBrightness[i] = GL.GetUniformLocation(_program, $"uGroupBrightness{i}");
                _uGroupSize[i] = GL.GetUniformLocation(_program, $"uGroupSize{i}");
                _uGroupColor[i] = GL.GetUniformLocation(_program, $"uGroupColor{i}");
            }

            _atlasTexture = CreateFontAtlasTexture();

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.Uniform1(_uAtlasTexture, 1);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            if (_program == 0 || _atlasTexture == 0 || inputTexture == 0)
            {
                host.DrawFullscreen(host._blitProgram, inputTexture);
                return;
            }

            GL.UseProgram(_program);
            GL.Uniform1(_uCellSize, _cellSize.CurrentValue);
            GL.Uniform1(_uCharCount, CharacterRamp.Length);
            GL.Uniform1(_uGroupCount, Math.Clamp(_densityGroups.CurrentValue, 1, CharacterRamp.Length));
            GL.Uniform1(_uMix, _mix.CurrentValue);

            for (var i = 0; i < MaxCharacters; i++)
            {
                GL.Uniform1(_uGroupBrightness[i], _groupBrightness[i].CurrentValue);
                GL.Uniform1(_uGroupSize[i], _groupSize[i].CurrentValue);
                GL.Uniform3(_uGroupColor[i], _groupColorR[i].CurrentValue, _groupColorG[i].CurrentValue, _groupColorB[i].CurrentValue);
            }

            host.DrawFullscreenWithTextures(
                _program,
                (0, inputTexture),
                (1, _atlasTexture));
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }

            if (_atlasTexture != 0)
            {
                GL.DeleteTexture(_atlasTexture);
                _atlasTexture = 0;
            }
        }

        private static string BuildFragmentShader()
        {
            var sb = new StringBuilder();

            sb.AppendLine("#version 330 core");
            sb.AppendLine("in vec2 vUv;");
            sb.AppendLine("out vec4 fragColor;");
            sb.AppendLine();
            sb.AppendLine("uniform sampler2D uTexture;");
            sb.AppendLine("uniform sampler2D uAtlasTexture;");
            sb.AppendLine("uniform float uCellSize;");
            sb.AppendLine("uniform int uCharCount;");
            sb.AppendLine("uniform int uGroupCount;");
            sb.AppendLine("uniform float uMix;");

            for (var i = 0; i < MaxCharacters; i++)
            {
                sb.AppendLine($"uniform float uGroupBrightness{i};");
                sb.AppendLine($"uniform float uGroupSize{i};");
                sb.AppendLine($"uniform vec3 uGroupColor{i};");
            }

            sb.AppendLine();
            sb.AppendLine("float luma(vec3 c)");
            sb.AppendLine("{");
            sb.AppendLine("    return dot(c, vec3(0.299, 0.587, 0.114));");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("float getGroupBrightness(int g)");
            sb.AppendLine("{");
            sb.AppendLine("    if (g <= 0) return uGroupBrightness0;");
            for (var i = 1; i < MaxCharacters; i++)
            {
                sb.AppendLine($"    if (g == {i}) return uGroupBrightness{i};");
            }

            sb.AppendLine($"    return uGroupBrightness{MaxCharacters - 1};");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("float getGroupSize(int g)");
            sb.AppendLine("{");
            sb.AppendLine("    if (g <= 0) return uGroupSize0;");
            for (var i = 1; i < MaxCharacters; i++)
            {
                sb.AppendLine($"    if (g == {i}) return uGroupSize{i};");
            }

            sb.AppendLine($"    return uGroupSize{MaxCharacters - 1};");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("vec3 getGroupColor(int g)");
            sb.AppendLine("{");
            sb.AppendLine("    if (g <= 0) return uGroupColor0;");
            for (var i = 1; i < MaxCharacters; i++)
            {
                sb.AppendLine($"    if (g == {i}) return uGroupColor{i};");
            }

            sb.AppendLine($"    return uGroupColor{MaxCharacters - 1};");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("void main()");
            sb.AppendLine("{");
            sb.AppendLine("    vec2 texSize = vec2(textureSize(uTexture, 0));");
            sb.AppendLine("    vec2 safeTexSize = max(texSize, vec2(1.0));");
            sb.AppendLine("    float cellSize = max(uCellSize, 1.0);");
            sb.AppendLine();
            sb.AppendLine("    vec2 pixel = vUv * safeTexSize;");
            sb.AppendLine("    vec2 cellId = floor(pixel / cellSize);");
            sb.AppendLine("    vec2 cellOrigin = cellId * cellSize;");
            sb.AppendLine();
            sb.AppendLine("    const int sampleCount = 4;");
            sb.AppendLine("    float avgLum = 0.0;");
            sb.AppendLine("    for (int y = 0; y < sampleCount; y++)");
            sb.AppendLine("    {");
            sb.AppendLine("        for (int x = 0; x < sampleCount; x++)");
            sb.AppendLine("        {");
            sb.AppendLine("            vec2 o = (vec2(float(x) + 0.5, float(y) + 0.5) / float(sampleCount)) * cellSize;");
            sb.AppendLine("            vec2 suv = clamp((cellOrigin + o) / safeTexSize, vec2(0.0), vec2(1.0));");
            sb.AppendLine("            avgLum += luma(texture(uTexture, suv).rgb);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    avgLum /= float(sampleCount * sampleCount);");
            sb.AppendLine();
            sb.AppendLine("    int charCount = max(uCharCount, 1);");
            sb.AppendLine("    int charIndex = int(floor(clamp(avgLum, 0.0, 0.99999) * float(charCount)));");
            sb.AppendLine("    charIndex = clamp(charIndex, 0, charCount - 1);");
            sb.AppendLine();
            sb.AppendLine("    int groupCount = clamp(uGroupCount, 1, charCount);");
            sb.AppendLine("    int group = int(floor(float(charIndex) * float(groupCount) / float(charCount))); ");
            sb.AppendLine("    group = clamp(group, 0, groupCount - 1);");
            sb.AppendLine();
            sb.AppendLine("    vec3 original = texture(uTexture, vUv).rgb;");
            sb.AppendLine("    vec2 local = fract(pixel / cellSize);");
            sb.AppendLine("    float size = max(getGroupSize(group), 0.05);");
            sb.AppendLine("    vec2 glyphUv = (local - vec2(0.5)) / size + vec2(0.5);");
            sb.AppendLine();
            sb.AppendLine("    float glyph = 0.0;");
            sb.AppendLine("    if (glyphUv.x >= 0.0 && glyphUv.x <= 1.0 && glyphUv.y >= 0.0 && glyphUv.y <= 1.0)");
            sb.AppendLine("    {");
            sb.AppendLine("        float charSpan = 1.0 / float(charCount);");
            sb.AppendLine("        float atlasX = (float(charIndex) + glyphUv.x) * charSpan;");
            sb.AppendLine("        vec2 atlasUv = vec2(atlasX, glyphUv.y);");
            sb.AppendLine("        glyph = texture(uAtlasTexture, atlasUv).r;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    float brightness = max(getGroupBrightness(group), 0.0);");
            sb.AppendLine("    vec3 matrixColor = getGroupColor(group) * brightness * avgLum * glyph;");
            sb.AppendLine("    vec3 finalColor = mix(original, matrixColor, clamp(uMix, 0.0, 1.0));");
            sb.AppendLine("    fragColor = vec4(finalColor, 1.0);");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static int CreateFontAtlasTexture()
        {
            var charCount = CharacterRamp.Length;
            var width = GlyphWidth * charCount;
            var height = GlyphHeight;
            var pixels = new byte[width * height * 4];

            for (var i = 0; i < charCount; i++)
            {
                DrawGlyph(pixels, width, i, CharacterRamp[i]);
            }

            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            var handle = Marshal.AllocHGlobal(pixels.Length);
            try
            {
                Marshal.Copy(pixels, 0, handle, pixels.Length);
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba,
                    width,
                    height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    handle);
            }
            finally
            {
                Marshal.FreeHGlobal(handle);
            }

            GL.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private static void DrawGlyph(byte[] pixels, int atlasWidth, int glyphIndex, char c)
        {
            var xOffset = glyphIndex * GlyphWidth;

            static void Put(byte[] target, int width, int x, int y, byte value)
            {
                if (x < 0 || y < 0)
                {
                    return;
                }

                var height = target.Length / 4 / width;
                if (x >= width || y >= height)
                {
                    return;
                }

                var idx = (y * width + x) * 4;
                target[idx + 0] = value;
                target[idx + 1] = value;
                target[idx + 2] = value;
                target[idx + 3] = 255;
            }

            void HLine(int y, int x0, int x1, byte value)
            {
                var ys = y + 0;
                for (var x = x0; x <= x1; x++)
                {
                    Put(pixels, atlasWidth, xOffset + x, ys, value);
                }
            }

            void VLine(int x, int y0, int y1, byte value)
            {
                for (var y = y0; y <= y1; y++)
                {
                    Put(pixels, atlasWidth, xOffset + x, y, value);
                }
            }

            void Dot(int cx, int cy, int radius, byte value)
            {
                for (var y = cy - radius; y <= cy + radius; y++)
                {
                    for (var x = cx - radius; x <= cx + radius; x++)
                    {
                        var dx = x - cx;
                        var dy = y - cy;
                        if (dx * dx + dy * dy <= radius * radius)
                        {
                            Put(pixels, atlasWidth, xOffset + x, y, value);
                        }
                    }
                }
            }

            void Diag(bool reverse, int thickness, byte value)
            {
                for (var y = 2; y < GlyphHeight - 2; y++)
                {
                    var t = (y - 2) / (float)(GlyphHeight - 5);
                    var centerX = reverse
                        ? (int)MathF.Round((GlyphWidth - 3) - t * (GlyphWidth - 5))
                        : (int)MathF.Round(2 + t * (GlyphWidth - 5));

                    for (var k = -thickness; k <= thickness; k++)
                    {
                        Put(pixels, atlasWidth, xOffset + centerX + k, y, value);
                    }
                }
            }

            switch (c)
            {
                case ' ':
                    break;

                case '.':
                    Dot(8, 12, 1, 255);
                    break;

                case ':':
                    Dot(8, 5, 1, 255);
                    Dot(8, 11, 1, 255);
                    break;

                case '-':
                    HLine(8, 4, 11, 255);
                    break;

                case '=':
                    HLine(6, 4, 11, 255);
                    HLine(10, 4, 11, 255);
                    break;

                case '+':
                    HLine(8, 4, 11, 255);
                    VLine(8, 4, 11, 255);
                    break;

                case '*':
                    HLine(8, 5, 10, 255);
                    VLine(8, 5, 10, 255);
                    Diag(false, 0, 220);
                    Diag(true, 0, 220);
                    break;

                case '%':
                    Dot(5, 5, 2, 235);
                    Dot(10, 10, 2, 235);
                    Diag(false, 0, 255);
                    break;

                case '@':
                    Dot(8, 8, 5, 180);
                    Dot(8, 8, 3, 0);
                    Dot(8, 8, 2, 255);
                    HLine(8, 8, 12, 255);
                    VLine(12, 6, 9, 255);
                    break;

                case '#':
                    HLine(5, 3, 12, 255);
                    HLine(10, 3, 12, 255);
                    VLine(5, 3, 12, 255);
                    VLine(10, 3, 12, 255);
                    break;

                default:
                    HLine(8, 4, 11, 255);
                    break;
            }
        }
    }
}
