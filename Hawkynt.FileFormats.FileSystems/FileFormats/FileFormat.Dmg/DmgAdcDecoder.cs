namespace FileFormat.Dmg;

/// <summary>
/// Decodes the legacy Apple Data Compression stream used by UDIF block type
/// <c>0x80000004</c>. ADC has no framing of its own: a stream is a sequence of
/// literal or back-reference tokens and the surrounding blkx entry supplies the
/// exact compressed and uncompressed lengths.
/// </summary>
/// <remarks>
/// Wire grammar cross-checked against the historical ADC description used by
/// dmg2img and compcol: 1-bit literal tokens, 10-bit short-distance matches and
/// 16-bit long-distance matches. This implementation is independent managed C#.
/// </remarks>
internal static class DmgAdcDecoder {
  public static void Decode(ReadOnlySpan<byte> source, Span<byte> destination) {
    var input = 0;
    var output = 0;

    while (output < destination.Length) {
      if (input >= source.Length)
        throw new InvalidDataException($"DMG ADC stream ended after {output} of {destination.Length} output bytes.");

      var tag = source[input];
      if ((tag & 0x80) != 0) {
        var literalLength = (tag & 0x7F) + 1;
        if (source.Length - input - 1 < literalLength)
          throw new InvalidDataException("DMG ADC literal token is truncated.");
        if (destination.Length - output < literalLength)
          throw new InvalidDataException("DMG ADC literal token exceeds the declared output range.");

        source.Slice(input + 1, literalLength).CopyTo(destination[output..]);
        input += literalLength + 1;
        output += literalLength;
        continue;
      }

      int length;
      int encodedOffset;
      if ((tag & 0x40) != 0) {
        if (source.Length - input < 3)
          throw new InvalidDataException("DMG ADC long-match token is truncated.");
        length = (tag & 0x3F) + 4;
        encodedOffset = (source[input + 1] << 8) | source[input + 2];
        input += 3;
      } else {
        if (source.Length - input < 2)
          throw new InvalidDataException("DMG ADC short-match token is truncated.");
        length = ((tag & 0x3C) >> 2) + 3;
        encodedOffset = ((tag & 0x03) << 8) | source[input + 1];
        input += 2;
      }

      var distance = encodedOffset + 1;
      if (distance > output)
        throw new InvalidDataException($"DMG ADC match distance {distance} exceeds the {output}-byte history.");
      if (destination.Length - output < length)
        throw new InvalidDataException("DMG ADC match token exceeds the declared output range.");

      // Deliberately copy byte-by-byte: ADC back-references may overlap, and
      // the newly produced bytes are part of the history for the same token.
      for (var i = 0; i < length; ++i) {
        destination[output] = destination[output - distance];
        ++output;
      }
    }

    if (input != source.Length)
      throw new InvalidDataException($"DMG ADC stream has {source.Length - input} trailing compressed bytes after the declared output was reconstructed.");
  }
}
