namespace FileFormat.StuffIt;

/// <summary>
/// StuffIt method 1: RLE90, the BinHex run-length scheme. <c>0x90 0x00</c> is a literal 0x90;
/// <c>0x90 n</c> (n ≥ 2) repeats the previous byte until the run is n long including that byte;
/// <c>0x90 0x01</c> is invalid.
/// </summary>
internal static class StuffItRle90 {
  public static byte[] Decode(ReadOnlySpan<byte> input, int outputLength) {
    var output = new byte[outputLength];
    var o = 0;
    byte last = 0;
    for (var i = 0; i < input.Length && o < outputLength; ++i) {
      var b = input[i];
      if (b != 0x90) {
        output[o++] = last = b;
        continue;
      }
      if (++i >= input.Length) throw new InvalidDataException("StuffIt RLE90 data ends after a marker.");
      var n = input[i];
      if (n == 0) {
        output[o++] = last = 0x90;
        continue;
      }
      if (n == 1) throw new InvalidDataException("StuffIt RLE90 run of length 1 is invalid.");
      for (var k = 1; k < n && o < outputLength; ++k) output[o++] = last;
    }
    if (o != outputLength) throw new InvalidDataException("StuffIt RLE90 data is shorter than the recorded size.");
    return output;
  }

  /// <summary>Encodes so that <see cref="Decode"/> (and every RLE90 reader) restores the input.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var output = new List<byte>(data.Length + 16);
    var i = 0;
    while (i < data.Length) {
      var b = data[i];
      var run = 1;
      while (i + run < data.Length && data[i + run] == b && run < 255) ++run;
      output.Add(b);
      if (b == 0x90) output.Add(0x00);
      if (run >= 3) {
        output.Add(0x90);
        output.Add((byte)run);
      } else {
        for (var k = 1; k < run; ++k) {
          output.Add(b);
          if (b == 0x90) output.Add(0x00);
        }
      }
      i += run;
    }
    return [.. output];
  }
}

/// <summary>
/// StuffIt method 3: a static Huffman code whose tree is serialised at the start of the fork
/// (bit 1 = leaf followed by an 8-bit value, bit 0 = branch: zero subtree then one subtree),
/// all read most-significant bit first.
/// </summary>
internal static class StuffItHuffman {
  public static byte[] Decode(byte[] input, int outputLength) {
    var reader = new StuffItBitReader(input);
    var code = new StuffItPrefixCode();
    int? onlySymbol = null;
    if (reader.ReadBit() == 1) onlySymbol = reader.ReadBits(8);
    else ParseBranch(reader, code, 0, 0);

    var output = new byte[outputLength];
    for (var i = 0; i < outputLength; ++i)
      output[i] = (byte)(onlySymbol ?? code.Decode(reader));
    return output;
  }

  private static void ParseBranch(StuffItBitReader reader, StuffItPrefixCode code, int node, int depth) {
    if (depth > 256) throw new InvalidDataException("StuffIt Huffman tree is too deep.");
    for (var bit = 0; bit < 2; ++bit) {
      if (reader.ReadBit() == 1) {
        code.SetChild(node, bit, StuffItPrefixCode.Leaf(reader.ReadBits(8)));
      } else {
        var child = code.NewNode();
        code.SetChild(node, bit, child);
        ParseBranch(reader, code, child, depth + 1);
      }
    }
  }
}

/// <summary>
/// StuffIt method 2: Unix compress LZW (block mode, up to 14-bit codes) without the 3-byte .Z
/// header. Decoded by the .Z decoder with that header put back.
/// </summary>
internal static class StuffItLzw {
  public static byte[] Decode(byte[] input, int outputLength) {
    var withHeader = new byte[input.Length + 3];
    withHeader[0] = 0x1F;
    withHeader[1] = 0x9D;
    withHeader[2] = 0x8E; // block mode, 14 bits
    input.CopyTo(withHeader, 3);
    using var ms = new MemoryStream(withHeader, writable: false);
    using var z = new FileFormat.Compress.CompressStream(ms, Compression.Core.Streams.CompressionStreamMode.Decompress);
    var output = new byte[outputLength];
    var got = z.ReadAtLeast(output, outputLength, throwOnEndOfStream: false);
    if (got != outputLength) throw new InvalidDataException("StuffIt LZW data is shorter than the recorded size.");
    return output;
  }
}
