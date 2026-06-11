using System.Numerics;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace macViz;

public sealed class ImGuiController : IDisposable
{
    private readonly GameWindow _window;
    private readonly List<char> _pressedChars = [];

    private int _shader;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _indexBuffer;
    private int _fontTexture;

    private int _vertexBufferSize = 10_000;
    private int _indexBufferSize = 2_000;

    private int _attribLocationTex;
    private int _attribLocationProjMtx;

    private readonly Dictionary<Keys, ImGuiKey> _keyMap = new()
    {
        [Keys.Tab] = ImGuiKey.Tab,
        [Keys.Left] = ImGuiKey.LeftArrow,
        [Keys.Right] = ImGuiKey.RightArrow,
        [Keys.Up] = ImGuiKey.UpArrow,
        [Keys.Down] = ImGuiKey.DownArrow,
        [Keys.PageUp] = ImGuiKey.PageUp,
        [Keys.PageDown] = ImGuiKey.PageDown,
        [Keys.Home] = ImGuiKey.Home,
        [Keys.End] = ImGuiKey.End,
        [Keys.Insert] = ImGuiKey.Insert,
        [Keys.Delete] = ImGuiKey.Delete,
        [Keys.Backspace] = ImGuiKey.Backspace,
        [Keys.Space] = ImGuiKey.Space,
        [Keys.Enter] = ImGuiKey.Enter,
        [Keys.Escape] = ImGuiKey.Escape,
        [Keys.A] = ImGuiKey.A,
        [Keys.C] = ImGuiKey.C,
        [Keys.V] = ImGuiKey.V,
        [Keys.X] = ImGuiKey.X,
        [Keys.Y] = ImGuiKey.Y,
        [Keys.Z] = ImGuiKey.Z,
    };

    public ImGuiController(int width, int height, GameWindow window)
    {
        _window = window;
        _window.TextInput += OnTextInput;

        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        io.DisplaySize = new Vector2(width, height);
        io.Fonts.AddFontDefault();
        io.FontGlobalScale = 2.0f;

        CreateDeviceResources();
        SetPerFrameData(1f / 60f);
    }

    public void WindowResized(int width, int height)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(width, height);

        var fb = _window.FramebufferSize;
        io.DisplayFramebufferScale = new Vector2(
            width > 0 ? fb.X / (float)width : 1f,
            height > 0 ? fb.Y / (float)height : 1f);
    }

    public void Update(float deltaSeconds)
    {
        var io = ImGui.GetIO();

        if (deltaSeconds <= 0f)
        {
            deltaSeconds = 1f / 60f;
        }

        SetPerFrameData(deltaSeconds);
        UpdateInput(io);
    }

    public void RenderDrawData(ImDrawDataPtr drawData)
    {
        RenderImDrawData(drawData);
    }

    private void SetPerFrameData(float deltaSeconds)
    {
        var io = ImGui.GetIO();
        var client = _window.ClientSize;
        var framebuffer = _window.FramebufferSize;

        io.DisplaySize = new Vector2(client.X, client.Y);
        io.DisplayFramebufferScale = new Vector2(
            client.X > 0 ? framebuffer.X / (float)client.X : 1f,
            client.Y > 0 ? framebuffer.Y / (float)client.Y : 1f);
        io.DeltaTime = deltaSeconds;
    }

    private void UpdateInput(ImGuiIOPtr io)
    {
        var mouse = _window.MouseState;
        var keyboard = _window.KeyboardState;

        io.AddMousePosEvent(mouse.X, mouse.Y);
        io.AddMouseButtonEvent(0, mouse.IsButtonDown(MouseButton.Left));
        io.AddMouseButtonEvent(1, mouse.IsButtonDown(MouseButton.Right));
        io.AddMouseButtonEvent(2, mouse.IsButtonDown(MouseButton.Middle));
        io.AddMouseWheelEvent(mouse.ScrollDelta.X, mouse.ScrollDelta.Y);

        foreach (var pair in _keyMap)
        {
            io.AddKeyEvent(pair.Value, keyboard.IsKeyDown(pair.Key));
        }

        io.KeyCtrl = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
        io.KeyShift = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        io.KeyAlt = keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt);
        io.KeySuper = keyboard.IsKeyDown(Keys.LeftSuper) || keyboard.IsKeyDown(Keys.RightSuper);

        foreach (var c in _pressedChars)
        {
            io.AddInputCharacter(c);
        }

        _pressedChars.Clear();
    }

    private void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.AsString))
        {
            foreach (var c in e.AsString)
            {
                _pressedChars.Add(c);
            }
        }
    }

    private void CreateDeviceResources()
    {
        _vertexBuffer = GL.GenBuffer();
        _indexBuffer = GL.GenBuffer();
        _vertexArray = GL.GenVertexArray();

        GL.BindVertexArray(_vertexArray);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        const int stride = 20;
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);

        CreateShader();
        CreateFontTexture();

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
    }

    private void CreateShader()
    {
        const string vertexSource = """
            #version 330 core
            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec2 aUV;
            layout (location = 2) in vec4 aColor;

            uniform mat4 projection_matrix;

            out vec2 vUV;
            out vec4 vColor;

            void main()
            {
                vUV = aUV;
                vColor = aColor;
                gl_Position = projection_matrix * vec4(aPosition, 0.0, 1.0);
            }
            """;

        const string fragmentSource = """
            #version 330 core
            in vec2 vUV;
            in vec4 vColor;

            uniform sampler2D in_fontTexture;
            out vec4 outputColor;

            void main()
            {
                outputColor = vColor * texture(in_fontTexture, vUV.st);
            }
            """;

        var vertex = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertex, vertexSource);
        GL.CompileShader(vertex);

        var fragment = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragment, fragmentSource);
        GL.CompileShader(fragment);

        _shader = GL.CreateProgram();
        GL.AttachShader(_shader, vertex);
        GL.AttachShader(_shader, fragment);
        GL.LinkProgram(_shader);

        GL.DetachShader(_shader, vertex);
        GL.DetachShader(_shader, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);

        _attribLocationTex = GL.GetUniformLocation(_shader, "in_fontTexture");
        _attribLocationProjMtx = GL.GetUniformLocation(_shader, "projection_matrix");
    }

    private void CreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out var width, out var height, out _);

        _fontTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        io.Fonts.SetTexID((IntPtr)_fontTexture);
        io.Fonts.ClearTexData();
    }

    private void RenderImDrawData(ImDrawDataPtr drawData)
    {
        var fbWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        var fbHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (fbWidth <= 0 || fbHeight <= 0)
        {
            return;
        }

        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ScissorTest);

        GL.Viewport(0, 0, fbWidth, fbHeight);

        var projection = OpenTK.Mathematics.Matrix4.CreateOrthographicOffCenter(0f, drawData.DisplaySize.X, drawData.DisplaySize.Y, 0f, -1f, 1f);
        GL.UseProgram(_shader);
        GL.Uniform1(_attribLocationTex, 0);
        GL.UniformMatrix4(_attribLocationProjMtx, false, ref projection);

        GL.BindVertexArray(_vertexArray);

        drawData.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

        for (var n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];

            var vertexSize = cmdList.VtxBuffer.Size * 20;
            if (vertexSize > _vertexBufferSize)
            {
                while (vertexSize > _vertexBufferSize)
                {
                    _vertexBufferSize *= 2;
                }

                GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
                GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }

            var indexSize = cmdList.IdxBuffer.Size * sizeof(ushort);
            if (indexSize > _indexBufferSize)
            {
                while (indexSize > _indexBufferSize)
                {
                    _indexBufferSize *= 2;
                }

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
                GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertexSize, cmdList.VtxBuffer.Data);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, indexSize, cmdList.IdxBuffer.Data);

            for (var cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex++)
            {
                var cmd = cmdList.CmdBuffer[cmdIndex];

                GL.BindTexture(TextureTarget.Texture2D, (int)cmd.TextureId);
                var clip = cmd.ClipRect;
                GL.Scissor(
                    (int)clip.X,
                    (int)(fbHeight - clip.W),
                    (int)(clip.Z - clip.X),
                    (int)(clip.W - clip.Y));

                GL.DrawElementsBaseVertex(
                    PrimitiveType.Triangles,
                    (int)cmd.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (IntPtr)(cmd.IdxOffset * sizeof(ushort)),
                    (int)cmd.VtxOffset);
            }
        }

        GL.Disable(EnableCap.ScissorTest);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        _window.TextInput -= OnTextInput;

        if (_fontTexture != 0) GL.DeleteTexture(_fontTexture);
        if (_vertexBuffer != 0) GL.DeleteBuffer(_vertexBuffer);
        if (_indexBuffer != 0) GL.DeleteBuffer(_indexBuffer);
        if (_vertexArray != 0) GL.DeleteVertexArray(_vertexArray);
        if (_shader != 0) GL.DeleteProgram(_shader);

        ImGui.DestroyContext();
    }
}
