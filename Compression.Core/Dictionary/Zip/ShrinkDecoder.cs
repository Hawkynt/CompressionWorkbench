namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Decodes ZIP Shrink (method 1): LZW with 9- to 13-bit codes, where control code 256 followed by
/// 1 widens the codes and followed by 2 partially clears the table (frees every code that is not a
/// prefix of another).
/// </summary>
/// <remarks>
/// Converted from hwunshrink in hwzip 2.4's shrink.c (Hans Wennborg, public domain), see
/// THIRD-PARTY-NOTICE.hwzip.txt. That includes PKZIP's quirks: after a partial clear a code can be
/// added whose prefix is free, and its string stays undetermined until the prefix is reused.
/// </remarks>
public static class ShrinkDecoder {
  internal const int MinCodeSize = 9;
  internal const int MaxCodeSize = 13;
  internal const int MaxCode = (1 << MaxCodeSize) - 1;
  internal const int ControlCode = 256;
  internal const int IncreaseCodeSize = 1;
  internal const int PartialClear = 2;
  internal const ushort InvalidCode = ushort.MaxValue;
  private const ushort UnknownLength = ushort.MaxValue;

  /// <summary>
  /// Decompresses ZIP Shrink data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(byte[] compressed, int originalSize) => Decode(compressed.AsSpan(), originalSize);

  /// <summary>
  /// Decompresses ZIP Shrink data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">The data is not a valid Shrink stream of that size.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);
    var dst = new byte[originalSize];
    if (originalSize == 0)
      return dst;

    var prefix = new ushort[MaxCode + 1];
    var extension = new byte[MaxCode + 1];
    var length = new ushort[MaxCode + 1];
    var lastPos = new int[MaxCode + 1];
    for (var i = 0; i <= 255; ++i) {
      prefix[i] = (ushort)i;
      extension[i] = (byte)i;
      length[i] = 1;
    }

    for (var i = 256; i <= MaxCode; ++i)
      prefix[i] = InvalidCode;

    var queue = new CodeQueue();
    var reader = new ZipLegacyBitReader(compressed);
    var codeSize = MinCodeSize;
    var dstPos = 0;

    if (!ReadCode(ref reader, ref codeSize, prefix, queue, out var current))
      throw new InvalidDataException("Shrink data ended before any output.");
    if (current > 255)
      throw new InvalidDataException("Shrink data must start with a literal code.");

    var firstByte = (byte)current;
    dst[dstPos] = firstByte;
    lastPos[current] = dstPos++;
    var previous = current;

    while (dstPos < originalSize && ReadCode(ref reader, ref codeSize, prefix, queue, out current)) {
      if (current == InvalidCode)
        throw new InvalidDataException("Invalid Shrink control code.");

      // KwKwK: the code about to be added is used before it exists.
      if (current == queue.Next) {
        if (prefix[previous] == InvalidCode)
          throw new InvalidDataException("Shrink code refers to a cleared prefix.");

        prefix[current] = previous;
        extension[current] = firstByte;
        length[current] = (ushort)(length[previous] + 1);
        lastPos[current] = lastPos[previous];
        dst[dstPos] = firstByte;
      }

      var produced = OutputCode(current, dst, dstPos, previous, prefix, extension, length, lastPos, queue, ref firstByte);

      var newCode = queue.RemoveNext();
      if (newCode != InvalidCode) {
        prefix[newCode] = previous;
        extension[newCode] = firstByte;
        length[newCode] = (ushort)(length[previous] + 1);
        lastPos[newCode] = lastPos[previous];
        // A prefix freed by a partial clear leaves the new string undetermined until it is reused.
        if (prefix[previous] == InvalidCode)
          length[newCode] = UnknownLength;
      }

      lastPos[current] = dstPos;
      dstPos += produced;
      previous = current;
    }

    if (dstPos < originalSize)
      throw new InvalidDataException($"Shrink data ended after {dstPos} of {originalSize} bytes.");

    return dst;
  }

  private static int OutputCode(ushort code, byte[] dst, int dstPos, ushort previous, ushort[] prefix, byte[] extension, ushort[] length, int[] lastPos, CodeQueue queue, ref byte firstByte) {
    if (code <= 255) {
      firstByte = (byte)code;
      dst[dstPos] = (byte)code;
      return 1;
    }

    if (prefix[code] == InvalidCode || prefix[code] == code)
      throw new InvalidDataException("Shrink code is not in the table.");

    if (length[code] != UnknownLength) {
      if (dst.Length - dstPos < length[code])
        throw new InvalidDataException("Shrink data decodes past the expected size.");

      CopyFromPrevious(dst, lastPos[code], dstPos, length[code]);
      firstByte = dst[dstPos];
      return length[code];
    }

    // A string whose prefix was free when it was added: the prefix has been reused since.
    var prefixCode = prefix[code];
    if (prefixCode == queue.Next) {
      if (prefix[previous] == InvalidCode)
        throw new InvalidDataException("Shrink code refers to a cleared prefix.");

      prefix[prefixCode] = previous;
      extension[prefixCode] = firstByte;
      length[prefixCode] = (ushort)(length[previous] + 1);
      lastPos[prefixCode] = lastPos[previous];
      dst[dstPos] = firstByte;
    } else if (prefix[prefixCode] == InvalidCode)
      throw new InvalidDataException("Shrink code refers to a cleared prefix.");

    var produced = length[prefixCode] + 1;
    if (dst.Length - dstPos < produced)
      throw new InvalidDataException("Shrink data decodes past the expected size.");

    CopyFromPrevious(dst, lastPos[prefixCode], dstPos, length[prefixCode]);
    dst[dstPos + produced - 1] = extension[code];
    firstByte = dst[dstPos];
    length[code] = (ushort)produced;
    lastPos[code] = dstPos;
    return produced;
  }

  private static void CopyFromPrevious(byte[] dst, int from, int to, int count) {
    for (var i = 0; i < count; ++i)
      dst[to + i] = dst[from + i];
  }

  private static bool ReadCode(ref ZipLegacyBitReader reader, ref int codeSize, ushort[] prefix, CodeQueue queue, out ushort code) {
    for (;;) {
      code = (ushort)ZipLegacyBitReader.Low(reader.Bits(), codeSize);
      if (!reader.Advance(codeSize))
        return false;

      if (code != ControlCode)
        return true;

      var control = (int)ZipLegacyBitReader.Low(reader.Bits(), codeSize);
      if (!reader.Advance(codeSize)) {
        code = InvalidCode;
        return true;
      }

      if (control == IncreaseCodeSize && codeSize < MaxCodeSize) {
        ++codeSize;
        continue;
      }

      if (control == PartialClear) {
        UnshrinkPartialClear(prefix, queue);
        continue;
      }

      code = InvalidCode;
      return true;
    }
  }

  private static void UnshrinkPartialClear(ushort[] prefix, CodeQueue queue) {
    var isPrefix = new bool[MaxCode + 1];
    for (var i = ControlCode + 1; i <= MaxCode; ++i)
      if (prefix[i] != InvalidCode)
        isPrefix[prefix[i]] = true;

    queue.Reset();
    for (var i = ControlCode + 1; i <= MaxCode; ++i)
      if (!isPrefix[i]) {
        prefix[i] = InvalidCode;
        queue.Add((ushort)i);
      }
  }

  /// <summary>The free codes, handed out lowest first.</summary>
  internal sealed class CodeQueue {
    private readonly ushort[] _codes = new ushort[MaxCode - ControlCode + 1];
    private int _count;
    private int _next;

    public CodeQueue() {
      for (var code = ControlCode + 1; code <= MaxCode; ++code)
        this._codes[this._count++] = (ushort)code;
    }

    public ushort Next => this._next < this._count ? this._codes[this._next] : InvalidCode;

    public ushort RemoveNext() {
      var code = this.Next;
      if (code != InvalidCode)
        ++this._next;

      return code;
    }

    public void Reset() {
      this._count = 0;
      this._next = 0;
    }

    public void Add(ushort code) => this._codes[this._count++] = code;
  }
}
