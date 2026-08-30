namespace FileFormat.NuFx;

internal static class NuFxLzcCodec {
  private const byte Magic1 = 0x1F;
  private const byte Magic2 = 0x9D;
  private const byte BlockMode = 0x80;
  private const int Clear = 256;
  private const int First = 257;
  private const int InitialBits = 9;

  internal static byte[] Compress(ReadOnlySpan<byte> input, int maxBits) {
    ValidateMaxBits(maxBits);

    using var output = new MemoryStream();
    output.WriteByte(Magic1);
    output.WriteByte(Magic2);
    output.WriteByte(checked((byte)maxBits));
    if (input.IsEmpty)
      return output.ToArray();

    var dictionary = new Dictionary<(int Prefix, byte Suffix), int>();
    var maxEntries = 1 << maxBits;
    var freeEntry = 256;
    var width = InitialBits;
    var widthLimit = width < maxBits ? (1 << width) + 1 : 1 << width;
    var group = new List<int>(8);

    void FlushGroup(bool padToEightCodes) {
      if (group.Count == 0)
        return;

      ulong bits = 0;
      var bitCount = 0;
      foreach (var code in group) {
        bits |= checked((ulong)code) << bitCount;
        bitCount += width;
        while (bitCount >= 8) {
          output.WriteByte((byte)bits);
          bits >>= 8;
          bitCount -= 8;
        }
      }
      if (bitCount != 0)
        output.WriteByte((byte)bits);

      if (padToEightCodes) {
        var bytesWritten = (group.Count * width + 7) >> 3;
        for (var index = bytesWritten; index < width; index++)
          output.WriteByte(0);
      }
      group.Clear();
    }

    void Emit(int code) {
      if (freeEntry >= widthLimit && width < maxBits) {
        FlushGroup(true);
        width++;
        widthLimit = width < maxBits ? (1 << width) + 1 : 1 << width;
      }

      group.Add(code);
      if (group.Count == 8)
        FlushGroup(false);
    }

    var current = input[0];
    for (var index = 1; index < input.Length; index++) {
      var suffix = input[index];
      if (dictionary.TryGetValue((current, suffix), out var combined)) {
        current = combined;
        continue;
      }

      Emit(current);
      if (freeEntry < maxEntries)
        dictionary[(current, suffix)] = freeEntry++;
      current = suffix;
    }

    Emit(current);
    FlushGroup(false);
    return output.ToArray();
  }

  internal static byte[] Decompress(ReadOnlySpan<byte> input, int expectedMaxBits, int expectedLength) {
    ValidateMaxBits(expectedMaxBits);
    if (expectedLength < 0)
      throw new ArgumentOutOfRangeException(nameof(expectedLength));
    if (input.Length < 3 || input[0] != Magic1 || input[1] != Magic2)
      throw new InvalidDataException("NuFX LZC thread is missing the Unix compress magic header.");

    var flags = input[2];
    if ((flags & 0x60) != 0)
      throw new InvalidDataException($"NuFX LZC header has unsupported flag bits 0x{flags & 0x60:X2}.");
    var maxBits = flags & 0x1F;
    ValidateMaxBits(maxBits);
    if (maxBits != expectedMaxBits)
      throw new InvalidDataException($"NuFX LZC-{expectedMaxBits} thread declares a {maxBits}-bit Unix compress stream.");

    var blockMode = (flags & BlockMode) != 0;
    var maxEntries = 1 << maxBits;
    var prefixes = new int[maxEntries];
    var suffixes = new byte[maxEntries];
    for (var value = 0; value < 256; value++)
      suffixes[value] = (byte)value;

    var freeEntry = blockMode ? First : 256;
    var width = InitialBits;
    var maxCode = width == maxBits ? maxEntries : (1 << width) - 1;
    var bitPosition = 24;
    var groupOrigin = bitPosition;
    var totalBits = checked(input.Length * 8);
    var oldCode = -1;
    var finalByte = 0;
    var stack = new byte[maxEntries];
    using var output = expectedLength > 0 ? new MemoryStream(expectedLength) : new MemoryStream();

    while (true) {
      if (freeEntry > maxCode && width < maxBits) {
        bitPosition = AlignToCodeGroup(bitPosition, width, groupOrigin);
        groupOrigin = bitPosition;
        width++;
        maxCode = width == maxBits ? maxEntries : (1 << width) - 1;
      }

      if (totalBits - bitPosition < width)
        break;
      var code = ReadCode(input, bitPosition, width);
      bitPosition += width;

      if (oldCode < 0) {
        if (code >= 256)
          throw new InvalidDataException("NuFX LZC stream starts with a non-literal code.");
        finalByte = oldCode = code;
        output.WriteByte((byte)code);
        continue;
      }

      if (blockMode && code == Clear) {
        freeEntry = First - 1;
        bitPosition = AlignToCodeGroup(bitPosition, width, groupOrigin);
        groupOrigin = bitPosition;
        width = InitialBits;
        maxCode = width == maxBits ? maxEntries : (1 << width) - 1;
        continue;
      }

      var inputCode = code;
      var stackCount = 0;
      if (code >= freeEntry) {
        if (code > freeEntry)
          throw new InvalidDataException($"NuFX LZC stream references undefined code {code} (next is {freeEntry}).");
        stack[stackCount++] = (byte)finalByte;
        code = oldCode;
      }

      while (code >= 256) {
        if (code >= freeEntry)
          throw new InvalidDataException($"NuFX LZC dictionary chain references undefined code {code}.");
        if (stackCount == stack.Length)
          throw new InvalidDataException("NuFX LZC dictionary chain is cyclic or corrupt.");
        stack[stackCount++] = suffixes[code];
        code = prefixes[code];
      }

      finalByte = suffixes[code];
      stack[stackCount++] = (byte)finalByte;
      while (stackCount != 0)
        output.WriteByte(stack[--stackCount]);

      if (freeEntry < maxEntries) {
        prefixes[freeEntry] = oldCode;
        suffixes[freeEntry] = (byte)finalByte;
        freeEntry++;
      }
      oldCode = inputCode;

      if (output.Length > expectedLength)
        throw new InvalidDataException($"NuFX LZC stream expands beyond the declared {expectedLength}-byte thread length.");
    }

    if (output.Length != expectedLength)
      throw new InvalidDataException($"NuFX LZC stream expanded to {output.Length} bytes, expected {expectedLength}.");
    return output.ToArray();
  }

  private static int ReadCode(ReadOnlySpan<byte> input, int bitPosition, int width) {
    var bytePosition = bitPosition >> 3;
    var shift = bitPosition & 7;
    uint value = input[bytePosition];
    if (bytePosition + 1 < input.Length)
      value |= (uint)input[bytePosition + 1] << 8;
    if (bytePosition + 2 < input.Length)
      value |= (uint)input[bytePosition + 2] << 16;
    return checked((int)((value >> shift) & ((1u << width) - 1)));
  }

  private static int AlignToCodeGroup(int bitPosition, int width, int groupOrigin) {
    var groupBits = checked(width * 8);
    var relative = bitPosition - groupOrigin;
    if (relative <= 0)
      return bitPosition;
    var aligned = checked(((relative + groupBits - 1) / groupBits) * groupBits);
    return checked(groupOrigin + aligned);
  }

  private static void ValidateMaxBits(int maxBits) {
    if (maxBits is < 9 or > 16)
      throw new ArgumentOutOfRangeException(nameof(maxBits), maxBits, "Unix compress code width must be between 9 and 16 bits.");
  }
}
