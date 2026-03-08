using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

internal sealed class WpfPresentationBackend : IDisposable
{
    private readonly Image _targetImage;
    private readonly BlendEffect _blendEffect = new();

    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _underlayBitmap;
    private ImageBrush? _overlayBrush;
    private ImageBrush? _inputBrush;
    private byte[]? _pixelBuffer;

    public WpfPresentationBackend(Image targetImage)
    {
        _targetImage = targetImage;
    }

    public int PixelWidth => _bitmap?.PixelWidth ?? 0;

    public int PixelHeight => _bitmap?.PixelHeight ?? 0;

    public byte[]? EnsureSurface(int width, int height, bool force)
    {
        if (width <= 0 || height <= 0)
        {
            return _pixelBuffer;
        }

        bool needsBitmap = _bitmap == null || force || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height;
        if (needsBitmap)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _pixelBuffer = new byte[width * height * 4];
            _targetImage.Source = _bitmap;
        }
        else if (_pixelBuffer == null || _pixelBuffer.Length != width * height * 4)
        {
            _pixelBuffer = new byte[width * height * 4];
        }

        if (_underlayBitmap == null || _underlayBitmap.PixelWidth != width || _underlayBitmap.PixelHeight != height || force)
        {
            _underlayBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            EnsureEffectResources();
            if (_overlayBrush != null)
            {
                _overlayBrush.ImageSource = _underlayBitmap;
            }
        }
        else
        {
            EnsureEffectResources();
        }

        return _pixelBuffer;
    }

    public void PresentFrame(byte[] pixelBuffer, int stride)
    {
        if (_bitmap == null)
        {
            return;
        }

        _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), pixelBuffer, stride, 0);
    }

    public void PresentUnderlay(byte[]? underlayBuffer, int stride)
    {
        if (_underlayBitmap == null || underlayBuffer == null)
        {
            return;
        }

        _underlayBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _underlayBitmap.PixelWidth, _underlayBitmap.PixelHeight), underlayBuffer, stride, 0);
    }

    public void UpdateEffectState(bool useOverlay, double blendModeValue)
    {
        EnsureEffectResources();

        _blendEffect.UseOverlay = useOverlay ? 1.0 : 0.0;
        _blendEffect.Mode = blendModeValue;
        if (_inputBrush != null && _bitmap != null)
        {
            _inputBrush.ImageSource = _bitmap;
            _inputBrush.Opacity = 1.0;
        }
    }

    private void EnsureEffectResources()
    {
        if (_overlayBrush == null)
        {
            _overlayBrush = new ImageBrush
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
        }

        if (_inputBrush == null && _bitmap != null)
        {
            _inputBrush = new ImageBrush(_bitmap)
            {
                Stretch = Stretch.Fill,
                Opacity = 1.0
            };
        }

        if (_overlayBrush != null)
        {
            _overlayBrush.ImageSource = _underlayBitmap;
            _blendEffect.Overlay = _overlayBrush;
        }

        if (_inputBrush != null)
        {
            _blendEffect.Input = _inputBrush;
        }

        _targetImage.Effect = _blendEffect;
    }

    public void Dispose()
    {
        _targetImage.Effect = null;
        _targetImage.Source = null;
        _overlayBrush = null;
        _inputBrush = null;
        _bitmap = null;
        _underlayBitmap = null;
        _pixelBuffer = null;
    }
}
