#pragma warning disable CS1591

namespace FileFormat.Gif;

/// <summary>
/// Builds the CompressionWorkbench animation view from PNGCrushCS' GIF parser.
/// PNGCrushCS owns GIF block parsing, de-interlacing and LZW decoding; this class
/// only performs the Workbench-specific full-canvas composition required by the
/// colorspace pseudo-archive.
/// </summary>
public sealed class GifPixelDecoder {

  /// <summary>One composed frame as RGBA32 pixels with width/height.</summary>
  public readonly record struct DecodedFrame(int Width, int Height, byte[] Rgba32, int DelayMs);

  /// <summary>Decodes <paramref name="data"/> and composes frame rectangles onto the logical canvas.</summary>
  public List<DecodedFrame> Decode(ReadOnlySpan<byte> data) {
    var gif = GifReader.FromSpan(data);
    var canvasWidth = gif.LogicalScreenDescriptor.Width;
    var canvasHeight = gif.LogicalScreenDescriptor.Height;
    if (canvasWidth <= 0 || canvasHeight <= 0 || gif.Frames.Count == 0)
      throw new InvalidDataException("GIF contains no displayable image frames.");

    var canvas = new byte[checked(canvasWidth * canvasHeight * 4)];
    FillBackground(canvas, canvasWidth, canvasHeight, gif.GlobalColorTable, gif.BackgroundColorIndex);

    var result = new List<DecodedFrame>(gif.Frames.Count);
    foreach (var frame in gif.Frames) {
      var previous = frame.DisposalMethod == FrameDisposalMethod.RestoreToPrevious
        ? (byte[])canvas.Clone()
        : null;

      var palette = frame.LocalColorTable ?? gif.GlobalColorTable;
      if (palette == null || palette.Length < 3)
        throw new InvalidDataException("GIF frame has no usable color table.");

      Composite(canvas, canvasWidth, canvasHeight, frame, palette);
      var delayMs = frame.Delay.TotalMilliseconds <= 0
        ? 0
        : (int)Math.Min(int.MaxValue, frame.Delay.TotalMilliseconds);
      result.Add(new DecodedFrame(canvasWidth, canvasHeight, (byte[])canvas.Clone(), delayMs));

      switch (frame.DisposalMethod) {
        case FrameDisposalMethod.RestoreToBackground:
          ClearFrameArea(canvas, canvasWidth, canvasHeight, frame, gif.GlobalColorTable, gif.BackgroundColorIndex);
          break;
        case FrameDisposalMethod.RestoreToPrevious when previous != null:
          Array.Copy(previous, canvas, canvas.Length);
          break;
      }
    }

    return result;
  }

  private static void Composite(byte[] canvas, int canvasWidth, int canvasHeight, Frame frame, byte[] palette) {
    var transparent = frame.TransparentColorIndex;
    for (var y = 0; y < frame.Height; ++y) {
      for (var x = 0; x < frame.Width; ++x) {
        var sourceOffset = y * frame.Width + x;
        if ((uint)sourceOffset >= (uint)frame.PixelData.Length)
          continue;

        var paletteIndex = frame.PixelData[sourceOffset];
        if (transparent.HasValue && paletteIndex == transparent.Value)
          continue;

        var paletteOffset = paletteIndex * 3;
        if (paletteOffset + 2 >= palette.Length)
          continue;

        var targetX = frame.Left + x;
        var targetY = frame.Top + y;
        if ((uint)targetX >= (uint)canvasWidth || (uint)targetY >= (uint)canvasHeight)
          continue;

        var targetOffset = (targetY * canvasWidth + targetX) * 4;
        canvas[targetOffset] = palette[paletteOffset];
        canvas[targetOffset + 1] = palette[paletteOffset + 1];
        canvas[targetOffset + 2] = palette[paletteOffset + 2];
        canvas[targetOffset + 3] = 255;
      }
    }
  }

  private static void FillBackground(byte[] canvas, int width, int height, byte[]? palette, byte backgroundIndex) {
    var (r, g, b) = BackgroundColor(palette, backgroundIndex);
    for (var i = 0; i < width * height; ++i) {
      var offset = i * 4;
      canvas[offset] = r;
      canvas[offset + 1] = g;
      canvas[offset + 2] = b;
      canvas[offset + 3] = 0;
    }
  }

  private static void ClearFrameArea(
    byte[] canvas, int canvasWidth, int canvasHeight, Frame frame, byte[]? palette, byte backgroundIndex) {
    var (r, g, b) = BackgroundColor(palette, backgroundIndex);
    for (var y = 0; y < frame.Height; ++y) {
      for (var x = 0; x < frame.Width; ++x) {
        var targetX = frame.Left + x;
        var targetY = frame.Top + y;
        if ((uint)targetX >= (uint)canvasWidth || (uint)targetY >= (uint)canvasHeight)
          continue;
        var offset = (targetY * canvasWidth + targetX) * 4;
        canvas[offset] = r;
        canvas[offset + 1] = g;
        canvas[offset + 2] = b;
        canvas[offset + 3] = 0;
      }
    }
  }

  private static (byte R, byte G, byte B) BackgroundColor(byte[]? palette, byte backgroundIndex) {
    var offset = backgroundIndex * 3;
    return palette != null && offset + 2 < palette.Length
      ? (palette[offset], palette[offset + 1], palette[offset + 2])
      : ((byte)0, (byte)0, (byte)0);
  }
}
