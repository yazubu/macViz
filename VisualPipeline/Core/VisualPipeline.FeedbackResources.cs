using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class PingPongFramebufferPair : IDisposable
    {
        private readonly int[] _textures = new int[2];
        private readonly int[] _fbos = new int[2];

        private int _width;
        private int _height;

        public int WriteIndex { get; private set; }
        public bool HasHistory { get; private set; }

        public int ReadTexture => _textures[1 - WriteIndex];
        public int WriteTexture => _textures[WriteIndex];
        public int WriteFbo => _fbos[WriteIndex];

        public void EnsureCreated()
        {
            for (var i = 0; i < 2; i++)
            {
                if (_textures[i] == 0)
                {
                    _textures[i] = GL.GenTexture();
                }

                if (_fbos[i] == 0)
                {
                    _fbos[i] = GL.GenFramebuffer();
                }
            }
        }

        public void Resize(int width, int height, string debugName)
        {
            EnsureCreated();

            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (width == _width && height == _height)
            {
                return;
            }

            _width = width;
            _height = height;

            for (var i = 0; i < 2; i++)
            {
                GL.BindTexture(TextureTarget.Texture2D, _textures[i]);
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba,
                    width,
                    height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbos[i]);
                GL.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D,
                    _textures[i],
                    0);

                var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (status != FramebufferErrorCode.FramebufferComplete)
                {
                    throw new InvalidOperationException($"{debugName} framebuffer incomplete: {status}");
                }
            }

            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            WriteIndex = 0;
            HasHistory = false;
        }

        public void SeedFromTexture(VisualPipeline host, int sourceTexture)
        {
            if (sourceTexture == 0)
            {
                return;
            }

            host.CopyTexture(sourceTexture, _textures[0]);
            host.CopyTexture(sourceTexture, _textures[1]);
            WriteIndex = 0;
            HasHistory = true;
        }

        public void Advance()
        {
            WriteIndex = 1 - WriteIndex;
            HasHistory = true;
        }

        public void Dispose()
        {
            for (var i = 0; i < 2; i++)
            {
                if (_fbos[i] != 0)
                {
                    GL.DeleteFramebuffer(_fbos[i]);
                    _fbos[i] = 0;
                }

                if (_textures[i] != 0)
                {
                    GL.DeleteTexture(_textures[i]);
                    _textures[i] = 0;
                }
            }

            _width = 0;
            _height = 0;
            WriteIndex = 0;
            HasHistory = false;
        }
    }

    private sealed class HistoryTexture2D : IDisposable
    {
        public int TextureId { get; private set; }
        public bool HasData { get; private set; }

        private int _width;
        private int _height;

        public void EnsureCreated()
        {
            if (TextureId == 0)
            {
                TextureId = GL.GenTexture();
            }
        }

        public void Resize(int width, int height)
        {
            EnsureCreated();

            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (width == _width && height == _height)
            {
                return;
            }

            _width = width;
            _height = height;

            GL.BindTexture(TextureTarget.Texture2D, TextureId);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba,
                width,
                height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            HasData = false;
        }

        public void CopyFrom(VisualPipeline host, int sourceTexture)
        {
            if (sourceTexture == 0 || TextureId == 0)
            {
                return;
            }

            host.CopyTexture(sourceTexture, TextureId);
            HasData = true;
        }

        public void Dispose()
        {
            if (TextureId != 0)
            {
                GL.DeleteTexture(TextureId);
                TextureId = 0;
            }

            _width = 0;
            _height = 0;
            HasData = false;
        }
    }
}
