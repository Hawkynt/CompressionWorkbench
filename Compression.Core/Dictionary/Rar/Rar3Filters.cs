namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Standard RAR3 filters for post-processing decompressed data.
/// RAR3 uses a VM for filters, but in practice only a few standard filters are used.
/// </summary>
internal static class Rar3Filters {
  /// <summary>
  /// Applies the E8/E9 (x86 call/jump) filter to the data.
  /// Converts relative addresses back to absolute form.
  /// </summary>
  /// <param name="data">The data buffer to filter in-place.</param>
  /// <param name="offset">Start offset within the buffer.</param>
  /// <param name="length">Number of bytes to process.</param>
  /// <param name="fileOffset">The current file offset (for address calculations).</param>
  public static void ApplyE8E9(byte[] data, int offset, int length, int fileOffset) {
    for (var i = offset; i < offset + length - 4; ++i) {
      if (data[i] != 0xE8 && data[i] != 0xE9)
        continue;

      var addr = data[i + 1] | (data[i + 2] << 8) | (data[i + 3] << 16) | (data[i + 4] << 24);
      var absAddr = addr + fileOffset + i - offset;

      // Only convert if the absolute address looks like a valid code reference
      if (absAddr >= 0) {
        data[i + 1] = (byte)absAddr;
        data[i + 2] = (byte)(absAddr >> 8);
        data[i + 3] = (byte)(absAddr >> 16);
        data[i + 4] = (byte)(absAddr >> 24);
      }

      i += 4;
    }
  }

  /// <summary>
  /// Applies the delta filter: reverses delta encoding where each byte stores
  /// the difference from the previous byte at the same channel position.
  /// </summary>
  /// <param name="data">The data buffer to filter in-place.</param>
  /// <param name="offset">Start offset within the buffer.</param>
  /// <param name="length">Number of bytes to process.</param>
  /// <param name="numChannels">Number of interleaved channels.</param>
  public static void ApplyDelta(byte[] data, int offset, int length, int numChannels) {
    for (var ch = 0; ch < numChannels; ++ch) {
      byte prev = 0;
      for (var i = ch + offset; i < offset + length; i += numChannels) {
        prev = (byte)(prev + data[i]);
        data[i] = prev;
      }
    }
  }

  /// <summary>
  /// Applies the audio prediction filter: reverses simple linear prediction
  /// on each channel. Used for uncompressed audio data.
  /// </summary>
  /// <param name="data">The data buffer to filter in-place.</param>
  /// <param name="offset">Start offset within the buffer.</param>
  /// <param name="length">Number of bytes to process.</param>
  /// <param name="numChannels">Number of audio channels.</param>
  public static void ApplyAudio(byte[] data, int offset, int length, int numChannels) {
    for (var ch = 0; ch < numChannels; ++ch) {
      var prev1 = 0;
      var prev2 = 0;
      for (var i = ch + offset; i < offset + length; i += numChannels) {
        var predicted = 2 * prev1 - prev2;
        prev2 = prev1;
        prev1 = data[i] + predicted;
        data[i] = (byte)prev1;
      }
    }
  }

  /// <summary>
  /// Applies the RGB filter: reverses prediction on 3-channel (RGB) image data.
  /// Each pixel's green channel is stored as-is, while red and blue are delta-coded
  /// against green.
  /// </summary>
  /// <param name="data">The data buffer to filter in-place.</param>
  /// <param name="offset">Start offset within the buffer.</param>
  /// <param name="length">Number of bytes to process.</param>
  /// <param name="width">The image width in pixels.</param>
  /// <param name="posR">Byte position of red channel within pixel (0, 1, or 2).</param>
  public static void ApplyRgb(byte[] data, int offset, int length, int width, int posR) {
    var stride = width * 3;
    for (var row = 0; row < length / stride; ++row) {
      var rowOffset = offset + row * stride;
      for (var x = 0; x < width; ++x) {
        var pixOffset = rowOffset + x * 3;
        if (pixOffset + 2 >= data.Length) break;

        var g = data[pixOffset + 1];
        data[pixOffset + posR] = (byte)(data[pixOffset + posR] + g);
        data[pixOffset + 2 - posR] = (byte)(data[pixOffset + 2 - posR] + g);
      }
    }
  }

  /// <summary>
  /// Applies the IA-64 (Itanium) BCJ filter: converts absolute branch targets
  /// back to relative addresses within 128-bit instruction bundles.
  /// </summary>
  /// <param name="data">The data buffer to filter in-place.</param>
  /// <param name="offset">Start offset within the buffer.</param>
  /// <param name="length">Number of bytes to process.</param>
  public static void ApplyItanium(byte[] data, int offset, int length) {
    var slice = data.AsSpan(offset, length);
    var decoded = Transforms.BcjFilter.DecodeIA64(slice, 0);
    decoded.CopyTo(data, offset);
  }
}
