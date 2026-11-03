namespace Compression.Core.Dictionary.Lzrw3;

/// <summary>
/// LZRW3 decompressor, converted from Ross N. Williams' public-domain reference
/// <c>lzrw3.c</c>. See <see cref="Lzrw3Compressor"/> for the stream layout.
/// </summary>
public static class Lzrw3Decompressor {
  private const int FlagBytes = 4;
  private const byte FlagCompress = 0;
  private const byte FlagCopy = 1;

  /// <summary>Decompresses an LZRW3 stream.</summary>
  /// <param name="data">The whole stream, flag bytes included.</param>
  /// <returns>The decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The stream is truncated or has an unknown flag.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < FlagBytes)
      throw new InvalidDataException("LZRW3 stream is shorter than its flag bytes.");
    if (data[0] == FlagCopy)
      return data[FlagBytes..].ToArray();
    if (data[0] != FlagCompress)
      throw new InvalidDataException($"LZRW3 flag {data[0]} is neither compressed (0) nor copied (1).");

    // At most 18 output bytes per two input bytes.
    var output = new byte[Math.Max(18, (data.Length - FlagBytes) * 9)];
    var hash = new int[Lzrw3Table.Length];
    hash.AsSpan().Fill(Lzrw3Table.StartString);
    var literals = 0;
    var outPos = 0;
    var inPos = FlagBytes;
    var control = 1u;
    while (inPos < data.Length) {
      if (control == 1) {
        if (inPos + 2 > data.Length)
          throw new InvalidDataException("LZRW3 stream ends inside a control word.");
        control = 0x10000u | data[inPos] | (uint)data[inPos + 1] << 8;
        inPos += 2;
        if (inPos == data.Length)
          throw new InvalidDataException("LZRW3 control word is not followed by an item.");
      }

      if ((control & 1) != 0) {
        if (inPos + 2 > data.Length)
          throw new InvalidDataException("LZRW3 stream ends inside a copy item.");
        var index = (data[inPos] & 0xF0) << 4 | data[inPos + 1];
        var length = (data[inPos] & 0xF) + 3;
        inPos += 2;
        var from = hash[index];
        var ziv = outPos;
        for (var i = 0; i < length; ++i)
          output[outPos++] = from + i < 0 ? Lzrw3Table.At(output, from + i) : output[from + i];

        if (literals > 0) {
          var r = ziv - literals;
          hash[Lzrw3Table.Hash(output, r)] = r;
          if (literals == 2) {
            ++r;
            hash[Lzrw3Table.Hash(output, r)] = r;
          }

          literals = 0;
        }

        hash[index] = ziv;
      } else {
        output[outPos++] = data[inPos++];
        if (++literals == 3) {
          var p = outPos - 3;
          hash[Lzrw3Table.Hash(output, p)] = p;
          literals = 2;
        }
      }

      control >>= 1;
    }

    return output.AsSpan(0, outPos).ToArray();
  }
}
