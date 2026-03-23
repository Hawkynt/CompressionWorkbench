namespace FileFormat.PackBits;

/// <summary>
/// Provides PackBits compression and decompression (Apple MacPaint standard)
/// with a framed container header.
/// </summary>
public static class PackBitsStream {

  /// <summary>Magic bytes: PKBT (0x504B4254).</summary>
  static readonly byte[] Magic = "PKBT"u8.ToArray();

  /// <summary>
  /// Compresses <paramref name="input"/> to <paramref name="output"/> using PackBits encoding.
  /// </summary>
  /// <param name="input">The uncompressed source stream.</param>
  /// <param name="output">The destination stream for compressed data.</param>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read all input bytes so we know the uncompressed size.
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    // Write header: magic + LE uncompressed size.
    output.Write(Magic);
    output.Write(BitConverter.GetBytes((uint)data.Length));

    // Encode PackBits packets.
    var i = 0;
    var literal = new List<byte>(128);

    while (i < data.Length) {
      // Count the run of identical bytes starting at i.
      var runByte = data[i];
      var runLen = 1;
      while (i + runLen < data.Length && data[i + runLen] == runByte && runLen < 128)
        runLen++;

      if (runLen >= 3) {
        // Flush any pending literal run first.
        FlushLiteral(output, literal);
        // Emit repeat packet: header = -(runLen - 1), then the byte.
        output.WriteByte((byte)(unchecked((sbyte)(-(runLen - 1)))));
        output.WriteByte(runByte);
        i += runLen;
      } else if (runLen == 2 && literal.Count == 0) {
        // A run of 2 at the start of a potential literal — emit as repeat.
        output.WriteByte(unchecked((byte)(sbyte)(-1)));
        output.WriteByte(runByte);
        i += 2;
      } else {
        // Accumulate into literal buffer.
        literal.Add(data[i]);
        i++;
        if (literal.Count == 128)
          FlushLiteral(output, literal);
      }
    }

    // Flush remaining literals.
    FlushLiteral(output, literal);
  }

  /// <summary>
  /// Decompresses <paramref name="input"/> to <paramref name="output"/> using PackBits decoding.
  /// </summary>
  /// <param name="input">The compressed source stream.</param>
  /// <param name="output">The destination stream for decompressed data.</param>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Validate magic.
    Span<byte> header = stackalloc byte[8];
    if (input.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
      throw new InvalidDataException("Stream too short for PackBits header.");

    if (header[0] != Magic[0] || header[1] != Magic[1] ||
        header[2] != Magic[2] || header[3] != Magic[3])
      throw new InvalidDataException("Invalid PackBits magic.");

    var uncompressedSize = BitConverter.ToUInt32(header[4..]);
    uint written = 0;

    while (written < uncompressedSize) {
      var nRaw = input.ReadByte();
      if (nRaw < 0)
        throw new InvalidDataException("Unexpected end of PackBits stream.");

      var n = (sbyte)(byte)nRaw;

      if (n == -128) {
        // No-op, skip.
        continue;
      }

      if (n >= 0) {
        // Literal run: copy n+1 bytes.
        var count = n + 1;
        for (var j = 0; j < count; j++) {
          var b = input.ReadByte();
          if (b < 0)
            throw new InvalidDataException("Unexpected end of PackBits stream during literal run.");
          output.WriteByte((byte)b);
          written++;
        }
      } else {
        // Repeat run: read one byte, repeat 1-n times.
        var count = 1 - n;
        var b = input.ReadByte();
        if (b < 0)
          throw new InvalidDataException("Unexpected end of PackBits stream during repeat run.");
        for (var j = 0; j < count; j++) {
          output.WriteByte((byte)b);
          written++;
        }
      }
    }
  }

  static void FlushLiteral(Stream output, List<byte> literal) {
    if (literal.Count == 0)
      return;

    // Header byte = count - 1.
    output.WriteByte((byte)(literal.Count - 1));
    foreach (var b in literal)
      output.WriteByte(b);

    literal.Clear();
  }
}
