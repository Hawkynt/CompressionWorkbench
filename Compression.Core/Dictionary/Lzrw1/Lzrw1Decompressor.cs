namespace Compression.Core.Dictionary.Lzrw1;

/// <summary>
/// LZRW1 decompressor, converted from Ross N. Williams' public-domain reference
/// <c>lzrw1.c</c>. See <see cref="Lzrw1Compressor"/> for the stream layout.
/// </summary>
public static class Lzrw1Decompressor {
  private const int FlagBytes = 4;
  private const byte FlagCompress = 0;
  private const byte FlagCopy = 1;

  /// <summary>Decompresses an LZRW1 stream.</summary>
  /// <param name="data">The whole stream, flag bytes included.</param>
  /// <returns>The decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The stream is truncated, has an unknown flag, or
  /// copies from before the start of the output.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < FlagBytes)
      throw new InvalidDataException("LZRW1 stream is shorter than its flag bytes.");
    if (data[0] == FlagCopy)
      return data[FlagBytes..].ToArray();
    if (data[0] != FlagCompress)
      throw new InvalidDataException($"LZRW1 flag {data[0]} is neither compressed (0) nor copied (1).");

    // At most 16 output bytes per two input bytes.
    var output = new byte[Math.Max(16, (data.Length - FlagBytes) * 8)];
    var outPos = 0;
    var inPos = FlagBytes;
    var control = 0;
    var controlBits = 0;
    while (inPos < data.Length) {
      if (controlBits == 0) {
        if (inPos + 2 > data.Length)
          throw new InvalidDataException("LZRW1 stream ends inside a control word.");
        control = data[inPos] | data[inPos + 1] << 8;
        inPos += 2;
        controlBits = 16;
        if (inPos == data.Length)
          throw new InvalidDataException("LZRW1 control word is not followed by an item.");
      }

      if ((control & 1) != 0) {
        if (inPos + 2 > data.Length)
          throw new InvalidDataException("LZRW1 stream ends inside a copy item.");
        var offset = (data[inPos] & 0xF0) << 4 | data[inPos + 1];
        var length = 1 + (data[inPos] & 0xF);
        inPos += 2;
        if (offset == 0 || offset > outPos)
          throw new InvalidDataException("LZRW1 copy item reaches before the start of the output.");
        for (var from = outPos - offset; length-- > 0;)
          output[outPos++] = output[from++];
      } else
        output[outPos++] = data[inPos++];

      control >>= 1;
      --controlBits;
    }

    return output.AsSpan(0, outPos).ToArray();
  }
}
