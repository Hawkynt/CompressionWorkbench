using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzw;

/// <summary>
/// UNIX <c>compress</c> / LZC codec for native <c>.Z</c> streams.
/// </summary>
/// <remarks>
/// LZC uses LSB-first variable-width LZW codes, but unlike a continuous bit stream it packs
/// codes in groups of eight. A code-width change or block CLEAR flushes the entire current
/// group before coding resumes at the new width. The stream carries the traditional
/// <c>1F 9D</c> header and has no embedded uncompressed-length or checksum field.
/// </remarks>
public static class LzcCodec {
  private const byte Magic1 = 0x1F;
  private const byte Magic2 = 0x9D;
  private const byte BlockModeFlag = 0x80;
  private const byte ReservedFlagsMask = 0x60;
  private const byte MaxBitsMask = 0x1F;
  private const int InitialBits = 9;
  private const int ClearCode = 256;

  /// <summary>Compresses data to a native UNIX <c>compress</c> (<c>.Z</c>) stream.</summary>
  /// <param name="data">Uncompressed bytes.</param>
  /// <param name="maxBits">Maximum LZW code width, from 9 through 16.</param>
  /// <param name="blockMode">Whether code 256 is reserved as the block CLEAR code.</param>
  /// <remarks>
  /// The encoder emits a standards-compatible stream without heuristic dictionary clears.
  /// When the dictionary fills it remains fixed at the maximum width, which is valid for both
  /// block and non-block streams. The decoder accepts CLEAR codes produced by adaptive encoders.
  /// </remarks>
  public static byte[] Compress(ReadOnlySpan<byte> data, int maxBits = 16, bool blockMode = true) {
    ValidateMaxBits(maxBits);

    using var output = new MemoryStream();
    output.WriteByte(Magic1);
    output.WriteByte(Magic2);
    output.WriteByte((byte)(maxBits | (blockMode ? BlockModeFlag : 0)));

    if (data.IsEmpty)
      return output.ToArray();

    var firstFreeCode = blockMode ? ClearCode + 1 : ClearCode;
    var maxCodeCount = 1 << maxBits;
    var dictionary = new Dictionary<(int Prefix, byte Suffix), int>();
    var nextEncoderCode = firstFreeCode;

    var writer = new CodeWriter(output, InitialBits);
    var decoderNextCode = firstFreeCode;
    var width = InitialBits;
    var widthMaxCode = (1 << width) - 1;
    var hasPrevious = false;

    void Emit(int code) {
      writer.Write(code);

      // The encoder learns a phrase when it emits its previous phrase, while the decoder
      // can only learn it after seeing the following code. Track decoder state separately
      // so width transitions occur on the same code boundary at both ends.
      if (hasPrevious && decoderNextCode < maxCodeCount)
        ++decoderNextCode;
      hasPrevious = true;

      if (width >= maxBits || decoderNextCode <= widthMaxCode)
        return;

      ++width;
      widthMaxCode = (1 << width) - 1;
      writer.Align(width);
    }

    int currentCode = data[0];
    foreach (var nextByte in data[1..]) {
      var key = (currentCode, nextByte);
      if (dictionary.TryGetValue(key, out var existingCode)) {
        currentCode = existingCode;
        continue;
      }

      Emit(currentCode);
      if (nextEncoderCode < maxCodeCount)
        dictionary[key] = nextEncoderCode++;
      currentCode = nextByte;
    }

    Emit(currentCode);
    writer.Finish();
    return output.ToArray();
  }

  /// <summary>Decompresses a complete native UNIX <c>compress</c> (<c>.Z</c>) stream.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data)
    => DecompressCore(data, null, null);

  /// <summary>Decompresses a native <c>.Z</c> stream and requires an exact expanded length.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int expandedLength) {
    ArgumentOutOfRangeException.ThrowIfNegative(expandedLength);
    return DecompressCore(data, expandedLength, null);
  }

  /// <summary>
  /// Decompresses a native <c>.Z</c> stream and requires both an exact expanded length and
  /// the expected maximum code width from an enclosing format.
  /// </summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int expandedLength, int expectedMaxBits) {
    ArgumentOutOfRangeException.ThrowIfNegative(expandedLength);
    ValidateMaxBits(expectedMaxBits);
    return DecompressCore(data, expandedLength, expectedMaxBits);
  }

  private static byte[] DecompressCore(ReadOnlySpan<byte> data, int? expandedLength, int? expectedMaxBits) {
    if (data.Length < 3)
      throw new InvalidDataException("LZC stream is shorter than its three-byte header.");
    if (data[0] != Magic1 || data[1] != Magic2)
      throw new InvalidDataException("LZC stream has invalid 1F 9D magic bytes.");

    var flags = data[2];
    if ((flags & ReservedFlagsMask) != 0)
      throw new InvalidDataException($"LZC stream uses reserved header flags 0x{flags & ReservedFlagsMask:X2}.");

    var maxBits = flags & MaxBitsMask;
    if (maxBits is < InitialBits or > 16)
      throw new InvalidDataException($"LZC stream declares invalid maximum code width {maxBits}.");
    if (expectedMaxBits is { } expected && maxBits != expected)
      throw new InvalidDataException($"LZC stream declares {maxBits}-bit codes, but the enclosing format requires {expected}-bit LZC.");

    var blockMode = (flags & BlockModeFlag) != 0;
    var firstFreeCode = blockMode ? ClearCode + 1 : ClearCode;
    var maxCodeCount = 1 << maxBits;
    var prefix = new int[maxCodeCount];
    var suffix = new byte[maxCodeCount];
    var reverse = new byte[maxCodeCount];
    using var output = expandedLength is { } length ? new MemoryStream(length) : new MemoryStream();

    var reader = new CodeReader(data[3..], InitialBits);
    var nextCode = firstFreeCode;
    var width = InitialBits;
    var widthMaxCode = (1 << width) - 1;
    var previousCode = -1;

    while (true) {
      if (width < maxBits && nextCode > widthMaxCode) {
        ++width;
        widthMaxCode = (1 << width) - 1;
        reader.Align(width);
      }

      if (!reader.TryRead(out var code))
        break;

      if (blockMode && code == ClearCode) {
        nextCode = firstFreeCode;
        width = InitialBits;
        widthMaxCode = (1 << width) - 1;
        previousCode = -1;
        reader.Align(width);
        continue;
      }

      if (previousCode < 0) {
        if ((uint)code > byte.MaxValue)
          throw new InvalidDataException($"LZC stream starts a dictionary block with non-literal code {code}.");
        EnsureOutputFits(output, expandedLength, 1);
        output.WriteByte((byte)code);
        previousCode = code;
        continue;
      }

      var isKwKwK = code == nextCode && nextCode < maxCodeCount;
      if (code > nextCode || (code == nextCode && !isKwKwK))
        throw new InvalidDataException($"LZC stream references future dictionary code {code} (next {nextCode}).");

      var phraseCode = isKwKwK ? previousCode : code;
      var phraseLength = DecodePhrase(phraseCode, nextCode, prefix, suffix, reverse, out var firstByte);
      var writeLength = checked(phraseLength + (isKwKwK ? 1 : 0));
      EnsureOutputFits(output, expandedLength, writeLength);
      for (var index = phraseLength - 1; index >= 0; --index)
        output.WriteByte(reverse[index]);
      if (isKwKwK)
        output.WriteByte(firstByte);

      if (nextCode < maxCodeCount) {
        prefix[nextCode] = previousCode;
        suffix[nextCode] = firstByte;
        ++nextCode;
      }

      previousCode = code;
    }

    if (expandedLength is { } required && output.Length != required)
      throw new InvalidDataException($"LZC stream expanded to {output.Length} bytes instead of the required {required}.");
    return output.ToArray();
  }

  private static int DecodePhrase(int code, int nextCode, int[] prefix, byte[] suffix,
      Span<byte> reverse, out byte firstByte) {
    var count = 0;
    var current = code;
    while (current >= 256) {
      if (current >= nextCode)
        throw new InvalidDataException($"LZC dictionary chain references undefined code {current}.");
      if (count >= reverse.Length)
        throw new InvalidDataException("LZC dictionary phrase exceeds the maximum representable length.");
      reverse[count++] = suffix[current];
      current = prefix[current];
    }

    if ((uint)current > byte.MaxValue)
      throw new InvalidDataException($"LZC dictionary chain terminates at invalid literal {current}.");
    if (count >= reverse.Length)
      throw new InvalidDataException("LZC dictionary phrase exceeds the maximum representable length.");

    firstByte = (byte)current;
    reverse[count++] = firstByte;
    return count;
  }

  private static void EnsureOutputFits(MemoryStream output, int? expandedLength, int additionalBytes) {
    if (expandedLength is { } limit && output.Length + additionalBytes > limit)
      throw new InvalidDataException($"LZC stream expands beyond the required {limit} bytes.");
  }

  private static void ValidateMaxBits(int maxBits) {
    if (maxBits is < InitialBits or > 16)
      throw new ArgumentOutOfRangeException(nameof(maxBits), maxBits, "LZC maximum code width must be in the range 9..16.");
  }

  private sealed class CodeWriter(Stream output, int width) {
    private readonly Stream _output = output;
    private int _width = width;
    private UInt128 _bits;
    private int _codeCount;

    public void Write(int code) {
      this._bits |= (UInt128)(uint)code << (this._codeCount * this._width);
      if (++this._codeCount == 8)
        this.Flush(fullGroup: true);
    }

    public void Align(int newWidth) {
      if (this._codeCount != 0)
        this.Flush(fullGroup: true);
      this._width = newWidth;
    }

    public void Finish() {
      if (this._codeCount != 0)
        this.Flush(fullGroup: false);
    }

    private void Flush(bool fullGroup) {
      var byteCount = fullGroup ? this._width : (this._codeCount * this._width + 7) >> 3;
      for (var index = 0; index < byteCount; ++index)
        this._output.WriteByte((byte)(this._bits >> (index * 8)));
      this._bits = 0;
      this._codeCount = 0;
    }
  }

  private ref struct CodeReader {
    private readonly ReadOnlySpan<byte> _source;
    private int _sourceOffset;
    private int _width;
    private UInt128 _bits;
    private int _codesRemaining;

    public CodeReader(ReadOnlySpan<byte> source, int width) {
      this._source = source;
      this._sourceOffset = 0;
      this._width = width;
      this._bits = 0;
      this._codesRemaining = 0;
    }

    public void Align(int newWidth) {
      this._bits = 0;
      this._codesRemaining = 0;
      this._width = newWidth;
    }

    public bool TryRead(out int code) {
      if (this._codesRemaining == 0) {
        if (this._sourceOffset >= this._source.Length) {
          code = 0;
          return false;
        }

        var byteCount = Math.Min(this._width, this._source.Length - this._sourceOffset);
        var codeCount = byteCount * 8 / this._width;
        if (codeCount == 0)
          throw new InvalidDataException("LZC stream ends with a truncated code.");

        this._bits = 0;
        for (var index = 0; index < byteCount; ++index)
          this._bits |= (UInt128)this._source[this._sourceOffset + index] << (index * 8);
        this._sourceOffset += byteCount;
        this._codesRemaining = codeCount;
      }

      var mask = ((UInt128)1 << this._width) - 1;
      code = (int)(this._bits & mask);
      this._bits >>= this._width;
      --this._codesRemaining;
      return true;
    }
  }
}

/// <summary>Benchmarkable UNIX <c>compress</c> / LZC building block.</summary>
/// <remarks>
/// Native <c>.Z</c> streams do not carry their expanded length, so this building-block envelope
/// prefixes a little-endian 32-bit length before a native 16-bit block-mode stream.
/// </remarks>
public sealed class LzcBuildingBlock : IBuildingBlock {
  /// <inheritdoc />
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Lzc";
  /// <inheritdoc />
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZC (UNIX compress)";
  /// <inheritdoc />
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "UNIX compress LZW with 9-16 bit codes and eight-code width-transition packing";
  /// <inheritdoc />
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc />
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    var native = LzcCodec.Compress(data);
    var result = new byte[checked(4 + native.Length)];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    native.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc />
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("LZC building-block envelope is truncated.");
    var length = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (length < 0)
      throw new InvalidDataException("LZC building-block envelope has a negative expanded length.");
    return LzcCodec.Decompress(data[4..], length, 16);
  }
}
