namespace FileFormat.StuffIt;

/// <summary>
/// StuffIt method 8, "MW": Miller and Wegman's LZ variant, written by the first StuffIt releases.
/// Codes are read least-significant bit first, 9 bits wide at the start of each run.
/// </summary>
/// <remarks>
/// <para>
/// Codes below 256 are bytes. Every code after the first one of a run adds a dictionary entry that
/// joins the previous phrase with the whole current phrase (where LZW adds a single byte), so
/// entry <c>k</c> expands to phrase(<c>dict[k-1]</c>) followed by phrase(<c>dict[k]</c>). The
/// first code of a run is kept in <c>dict[255]</c> so that entry 256 starts with it. The code
/// width grows by one bit whenever the next free entry reaches a power of two.
/// </para>
/// <para>
/// A code equal to the next free entry ends the run and starts a fresh one: the code width goes back
/// to 9 bits and numbering restarts at 256. A code above it ends the stream. Decoding also stops
/// once the fork's declared length has been produced.
/// </para>
/// <para>Converted from XADMaster's XADStuffItOldHandles (libxad's StuffIt client by Dirk Stöcker;
/// LGPL-2.1-or-later).</para>
/// </remarks>
internal static class StuffItMwDecoder {
  private const int FirstEntry = 256;
  private const int DictionarySize = 16385;

  /// <summary>Decodes <paramref name="outputLength"/> bytes from a method-8 fork.</summary>
  /// <exception cref="InvalidDataException">The fork ends early or its dictionary would overflow.</exception>
  public static byte[] Decode(byte[] input, int outputLength) {
    var output = new byte[outputLength];
    if (outputLength == 0) return output;

    var reader = new StuffItBitReader(input);
    var dict = new ushort[DictionarySize];
    var stack = new ushort[DictionarySize];
    var written = 0;

    while (written < outputLength) {
      var next = FirstEntry;
      var limit = next << 1;
      var bits = 9;
      var code = reader.ReadBitsLe(bits);
      if (code < next) {
        dict[FirstEntry - 1] = (ushort)code;
        written = Emit(code, dict, stack, output, written);
        while (written < outputLength && (code = reader.ReadBitsLe(bits)) < next) {
          if (next >= DictionarySize)
            throw new InvalidDataException("StuffIt MW dictionary overflow.");
          dict[next++] = (ushort)code;
          if (next == limit) {
            limit <<= 1;
            ++bits;
          }

          written = Emit(code, dict, stack, output, written);
        }
      }

      if (code > next) break;
    }

    if (written < outputLength)
      throw new InvalidDataException("StuffIt MW fork ended before its declared length.");
    return output;
  }

  /// <summary>Writes the phrase of <paramref name="code"/>, stopping at the end of the output.</summary>
  private static int Emit(int code, ushort[] dict, ushort[] stack, byte[] output, int written) {
    var depth = 0;
    stack[depth++] = (ushort)code;
    while (depth > 0) {
      int ptr = stack[--depth];
      while (ptr >= FirstEntry) {
        if (depth >= stack.Length) throw new InvalidDataException("StuffIt MW phrase is too deep.");
        stack[depth++] = dict[ptr];
        ptr = dict[ptr - 1];
      }

      if (written < output.Length) output[written++] = (byte)ptr;
    }

    return written;
  }
}
