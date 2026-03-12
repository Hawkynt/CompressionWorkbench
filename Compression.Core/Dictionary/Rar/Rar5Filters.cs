using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Built-in post-processing filters for RAR5 decompression.
/// Filters transform a block of decompressed data before final output.
/// </summary>
internal static class Rar5Filters {
  /// <summary>
  /// Represents a pending filter to apply to decompressed output.
  /// </summary>
  internal readonly record struct PendingFilter(
    int Type,
    int BlockStart,
    int BlockLength,
    int Channels);

  /// <summary>
  /// Applies a RAR5 filter to the given data block.
  /// </summary>
  /// <param name="type">The filter type (<see cref="Rar5Constants"/>).</param>
  /// <param name="data">The data to transform.</param>
  /// <param name="channels">Number of channels (for delta filter).</param>
  /// <returns>The transformed data.</returns>
  public static byte[] Apply(int type, ReadOnlySpan<byte> data, int channels = 1) {
    return type switch {
      Rar5Constants.FilterDelta => ApplyDelta(data, channels),
      Rar5Constants.FilterE8E9 => ApplyE8E9(data),
      Rar5Constants.FilterArm => ApplyArm(data),
      _ => data.ToArray() // Unknown filter type — pass through
    };
  }

  /// <summary>
  /// Applies the RAR5 delta filter. The data is organized as interleaved channels.
  /// Each channel is stored as deltas; this filter accumulates them.
  /// </summary>
  private static byte[] ApplyDelta(ReadOnlySpan<byte> data, int channels) {
    if (channels <= 0)
      channels = 1;

    var length = data.Length;
    var result = new byte[length];

    // Data layout: first chunk of (length/channels) bytes is channel 0 deltas,
    // next chunk is channel 1 deltas, etc.
    var channelSize = length / channels;

    for (var ch = 0; ch < channels; ++ch) {
      byte prev = 0;
      var srcOffset = ch * channelSize;
      for (var i = 0; i < channelSize; ++i) {
        prev = unchecked((byte)(prev + data[srcOffset + i]));
        result[i * channels + ch] = prev;
      }
    }

    // Handle remaining bytes (if length is not evenly divisible by channels)
    var remainder = length - channels * channelSize;
    if (remainder > 0)
      data[(channels * channelSize)..].CopyTo(result.AsSpan(channels * channelSize));

    return result;
  }

  /// <summary>
  /// Applies the RAR5 E8/E9 (x86 call/jump) filter.
  /// Converts absolute call/jump addresses back to relative.
  /// </summary>
  private static byte[] ApplyE8E9(ReadOnlySpan<byte> data) {
    if (data.Length < 5)
      return data.ToArray();

    var result = data.ToArray();
    var limit = result.Length - 4;

    for (var i = 0; i <= limit;) {
      var b = result[i];
      if (b is 0xE8 or 0xE9) {
        var addr = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(i + 1));
        // RAR5 E8E9 filter: convert absolute back to relative
        // The filter uses file size as the base, but for RAR5 the block size
        // is used. The address is relative to position (i + 5) in the block.
        addr -= i + 5;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(i + 1), addr);
        i += 5;
      } else
        ++i;
    }

    return result;
  }

  /// <summary>
  /// Applies the RAR5 ARM filter.
  /// Converts absolute BL addresses back to relative.
  /// </summary>
  private static byte[] ApplyArm(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();

    for (var i = 0; i + 3 < result.Length; i += 4) {
      if (result[i + 3] != 0xEB)
        continue;

      // Extract 24-bit offset (little-endian in ARM encoding)
      var offset = result[i] | (result[i + 1] << 8) | (result[i + 2] << 16);
      // Sign-extend from 24 bits
      if ((offset & 0x800000) != 0)
        offset |= unchecked((int)0xFF000000);

      // Convert absolute to relative
      var wordAddr = i >> 2;
      offset -= wordAddr;

      // Write back lower 24 bits
      result[i]     = (byte)(offset & 0xFF);
      result[i + 1] = (byte)((offset >> 8) & 0xFF);
      result[i + 2] = (byte)((offset >> 16) & 0xFF);
    }

    return result;
  }
}
