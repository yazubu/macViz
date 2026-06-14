using OpenTK.Graphics.OpenGL4;
using StbImageSharp;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class StaticImageSourceStage : PipelineStage
    {
        public const string TypeIdValue = "source.staticImages";

        private enum SwitchAlgorithm
        {
            Order = 0,
            OrderInv = 1,
            Random = 2,
            RandomEx = 3
        }

        private sealed class LoadedImage
        {
            public required string Path { get; init; }
            public required int Width { get; init; }
            public required int Height { get; init; }
            public required byte[] PixelDataRgba { get; set; }
            public int TextureId { get; set; }
        }

        private readonly Parameter<int> _algorithm = new("Static Images / Algorithm (0 Order,1 Order Inv,2 Random,3 Random EX)", 0, 3, 0);
        private readonly Parameter<float> _trigger = new("Static Images / Trigger", 0f, 1f, 0f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private readonly List<LoadedImage> _images = [];
        private readonly Random _random = new(1977);

        private int _selectedImageIndex;
        private bool _previousTriggerHigh;
        private string _status = "No images loaded";

        public StaticImageSourceStage()
        {
            _parameters = [_algorithm, _trigger];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Static Images";
        public override IReadOnlyList<IParameter> Parameters => _parameters;
        public override bool IsSourceStage => true;

        public IReadOnlyList<string> ImagePaths => _images.Select(x => x.Path).ToList();
        public string Status => _status;
        public int SelectedImageIndex => _selectedImageIndex;

        public int AddImagesFromPath(string pathOrDirectory)
        {
            if (string.IsNullOrWhiteSpace(pathOrDirectory))
            {
                _status = "Path is empty";
                return 0;
            }

            var trimmed = pathOrDirectory.Trim();
            if (Directory.Exists(trimmed))
            {
                var files = Directory
                    .EnumerateFiles(trimmed)
                    .Where(IsSupportedImagePath)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (files.Count == 0)
                {
                    _status = "No supported images found in folder";
                    return 0;
                }

                var added = 0;
                foreach (var file in files)
                {
                    if (AddSingleImage(file))
                    {
                        added++;
                    }
                }

                if (added > 0)
                {
                    _status = $"Loaded {added} image(s)";
                }

                return added;
            }

            return AddSingleImage(trimmed) ? 1 : 0;
        }

        public void SetImagePaths(IEnumerable<string> paths)
        {
            ClearImages();
            foreach (var path in paths)
            {
                AddImagesFromPath(path);
            }

            if (_images.Count == 0)
            {
                _status = "No images loaded";
            }
        }

        public void SetSelectedImageIndex(int index)
        {
            if (_images.Count == 0)
            {
                _selectedImageIndex = 0;
                return;
            }

            _selectedImageIndex = Math.Clamp(index, 0, _images.Count - 1);
        }

        public void RemoveImageAt(int index)
        {
            if (index < 0 || index >= _images.Count)
            {
                return;
            }

            var image = _images[index];
            if (image.TextureId != 0)
            {
                GL.DeleteTexture(image.TextureId);
                image.TextureId = 0;
            }

            _images.RemoveAt(index);
            if (_selectedImageIndex >= _images.Count)
            {
                _selectedImageIndex = Math.Max(0, _images.Count - 1);
            }

            _status = _images.Count == 0 ? "No images loaded" : $"{_images.Count} image(s) loaded";
        }

        public void ClearImages()
        {
            foreach (var image in _images)
            {
                if (image.TextureId != 0)
                {
                    GL.DeleteTexture(image.TextureId);
                    image.TextureId = 0;
                }
            }

            _images.Clear();
            _selectedImageIndex = 0;
            _previousTriggerHigh = _trigger.CurrentValue >= 0.5f;
            _status = "No images loaded";
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            if (_images.Count == 0)
            {
                host.DrawFullscreen(host._blitProgram, 0);
                return;
            }

            if (_selectedImageIndex >= _images.Count)
            {
                _selectedImageIndex = 0;
            }

            var triggerHigh = _trigger.CurrentValue >= 0.5f;
            if (triggerHigh && !_previousTriggerHigh)
            {
                Advance(_images.Count);
            }

            _previousTriggerHigh = triggerHigh;

            var selectedImage = _images[_selectedImageIndex];
            if (selectedImage.TextureId == 0)
            {
                EnsureTextureUploaded(selectedImage);
            }

            host.DrawFullscreen(host._blitProgramFlipY, selectedImage.TextureId);
        }

        public override void Dispose()
        {
            ClearImages();
        }

        private bool AddSingleImage(string path)
        {
            if (!File.Exists(path))
            {
                _status = $"File not found: {path}";
                return false;
            }

            if (!IsSupportedImagePath(path))
            {
                _status = "Unsupported file extension";
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            if (_images.Any(x => string.Equals(x.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                _status = $"Already loaded: {Path.GetFileName(fullPath)}";
                return false;
            }

            try
            {
                using var stream = File.OpenRead(fullPath);
                var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                if (image is null || image.Data is null || image.Width <= 0 || image.Height <= 0)
                {
                    _status = $"Failed to decode image: {Path.GetFileName(fullPath)}";
                    return false;
                }

                var pixels = image.Data;
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }

                _images.Add(new LoadedImage
                {
                    Path = fullPath,
                    Width = image.Width,
                    Height = image.Height,
                    PixelDataRgba = pixels,
                    TextureId = 0
                });

                _status = $"Loaded: {Path.GetFileName(fullPath)}";
                return true;
            }
            catch (Exception ex)
            {
                _status = $"Load failed: {ex.Message}";
                return false;
            }
        }

        private void EnsureTextureUploaded(LoadedImage image)
        {
            if (image.TextureId != 0)
            {
                return;
            }

            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba,
                image.Width,
                image.Height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                image.PixelDataRgba);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

            GL.BindTexture(TextureTarget.Texture2D, 0);

            image.TextureId = texture;
            image.PixelDataRgba = [];
        }

        private void Advance(int activeCount)
        {
            if (activeCount <= 0)
            {
                _selectedImageIndex = 0;
                return;
            }

            var algorithm = (SwitchAlgorithm)Math.Clamp(_algorithm.CurrentValue, 0, 3);
            switch (algorithm)
            {
                case SwitchAlgorithm.Order:
                    _selectedImageIndex = (_selectedImageIndex + 1) % activeCount;
                    break;
                case SwitchAlgorithm.OrderInv:
                    _selectedImageIndex = (_selectedImageIndex - 1 + activeCount) % activeCount;
                    break;
                case SwitchAlgorithm.Random:
                    _selectedImageIndex = _random.Next(activeCount);
                    break;
                case SwitchAlgorithm.RandomEx:
                    if (activeCount <= 1)
                    {
                        _selectedImageIndex = 0;
                        break;
                    }

                    var next = _selectedImageIndex;
                    while (next == _selectedImageIndex)
                    {
                        next = _random.Next(activeCount);
                    }

                    _selectedImageIndex = next;
                    break;
            }
        }

        private static bool IsSupportedImagePath(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
        }
    }
}
