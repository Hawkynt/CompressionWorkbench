using Compression.Core.Dictionary.MatchFinders;

namespace FileFormat.PowerPacker;

/// <summary>
/// Compressor and decompressor for the Amiga PowerPacker (PP20) crunched file format.
/// PP20 stores an LZ stream that is consumed from the end of the packed data and
/// reconstructs the output from end to start.
/// </summary>
public static class PowerPackerStream {

  /// <summary>Decompresses a PP20-crunched stream.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    output.Write(DecompressCore(data));
  }

  /// <summary>Compresses with the traditional <see cref="PowerPackerEfficiency.Good"/> preset.</summary>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, PowerPackerEfficiency.Good);

  /// <summary>Compresses using the selected historical efficiency preset.</summary>
  public static void Compress(Stream input, Stream output, PowerPackerEfficiency efficiency) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    output.Write(CompressCore(data, efficiency));
  }

  /// <summary>Tries every historical efficiency preset and writes the smallest result.</summary>
  public static void CompressOptimal(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    output.Write(CompressOptimal(data));
  }

  /// <summary>Decompresses a complete PP20 file.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data) => DecompressCore(data);

  /// <summary>Compresses with the traditional <see cref="PowerPackerEfficiency.Good"/> preset.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> data)
    => CompressCore(data, PowerPackerEfficiency.Good);

  /// <summary>Compresses using the selected historical efficiency preset.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> data, PowerPackerEfficiency efficiency)
    => CompressCore(data, efficiency);

  /// <summary>Tries every historical efficiency preset and returns the smallest result.</summary>
  public static byte[] CompressOptimal(ReadOnlySpan<byte> data) {
    byte[]? best = null;
    foreach (var efficiency in Enum.GetValues<PowerPackerEfficiency>()) {
      var candidate = CompressCore(data, efficiency);
      if (best is null || candidate.Length < best.Length)
        best = candidate;
    }

    return best!;
  }

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < PowerPackerConstants.MinFileSize)
      throw new InvalidDataException("Input is shorter than the minimum PP20 file size.");

    if (!data[..PowerPackerConstants.MagicLength].SequenceEqual(PowerPackerConstants.Magic)) {
      if (data[..PowerPackerConstants.MagicLength].SequenceEqual(PowerPackerConstants.PX20Magic))
        throw new InvalidDataException("Encrypted PowerPacker (PX20) files are not supported.");

      throw new InvalidDataException("Invalid PowerPacker magic bytes.");
    }

    if ((data.Length & 3) != 0)
      throw new InvalidDataException("PP20 file size must be a multiple of four bytes.");

    var efficiency = data.Slice(PowerPackerConstants.EfficiencyTableOffset, PowerPackerConstants.EfficiencyTableSize);
    ValidateEfficiency(efficiency);

    var infoOffset = data.Length - PowerPackerConstants.DecrunchInfoSize;
    var originalSize = (data[infoOffset] << 16) | (data[infoOffset + 1] << 8) | data[infoOffset + 2];
    var skipBits = data[infoOffset + 3];
    if (skipBits > 31)
      throw new InvalidDataException("PP20 skip-bit count must be between 0 and 31.");

    var packed = data.Slice(
      PowerPackerConstants.MagicLength + PowerPackerConstants.EfficiencyTableSize,
      data.Length - PowerPackerConstants.MagicLength - PowerPackerConstants.EfficiencyTableSize - PowerPackerConstants.DecrunchInfoSize);

    if (skipBits > packed.Length * 8)
      throw new InvalidDataException("PP20 skip-bit count exceeds the packed bitstream.");

    if (originalSize == 0)
      return [];

    var output = new byte[originalSize];
    var outPos = originalSize;
    var bits = new ReverseBitReader(packed);
    bits.SkipBits(skipBits);

    while (outPos > 0) {
      var hasLiteralRun = bits.ReadBit() == 0;
      if (hasLiteralRun) {
        var literalCount = 1;
        int part;
        do {
          part = bits.ReadBits(2);
          literalCount = checked(literalCount + part);
        } while (part == 3);

        if (literalCount > outPos)
          throw new InvalidDataException("PP20 literal run exceeds the remaining output size.");

        for (var i = 0; i < literalCount; ++i)
          output[--outPos] = (byte)bits.ReadBits(8);

        if (outPos == 0)
          break;
      }

      var offsetClass = bits.ReadBits(2);
      var offsetBits = efficiency[offsetClass];
      var matchLength = offsetClass + 2;

      if (offsetClass == 3) {
        if (bits.ReadBit() == 0)
          offsetBits = 7;
      }

      var offset = bits.ReadBits(offsetBits);

      if (offsetClass == 3) {
        int part;
        do {
          part = bits.ReadBits(3);
          matchLength = checked(matchLength + part);
        } while (part == 7);
      }

      if (matchLength > outPos)
        throw new InvalidDataException("PP20 match exceeds the remaining output size.");
      if (outPos + offset >= output.Length)
        throw new InvalidDataException("PP20 match offset points outside the already decoded output.");

      for (var i = 0; i < matchLength; ++i) {
        var value = output[outPos + offset];
        output[--outPos] = value;
      }
    }

    return output;
  }

  private static byte[] CompressCore(ReadOnlySpan<byte> input, PowerPackerEfficiency efficiencyPreset) {
    if (input.Length > PowerPackerConstants.MaxOriginalSize)
      throw new ArgumentOutOfRangeException(nameof(input), $"PP20 stores the original size in 24 bits; maximum is {PowerPackerConstants.MaxOriginalSize} bytes.");

    var efficiency = PowerPackerConstants.GetEfficiency(efficiencyPreset);
    if (input.IsEmpty)
      return BuildPp20File(efficiency, new byte[sizeof(uint)], 0, 0);

    var reversed = input.ToArray();
    Array.Reverse(reversed);
    ReadOnlySpan<byte> scanInput = reversed;

    var maxDistance = 1 << efficiency[3];
    var matchFinder = new HashChainMatchFinder(maxDistance);
    var bitWriter = new ReverseBitWriter();
    var pendingLiterals = new List<byte>();
    var pos = 0;

    while (pos < scanInput.Length) {
      var maxLength = Math.Min(scanInput.Length - pos, 256);
      var match = pos >= 2
        ? matchFinder.FindMatch(scanInput, pos, maxDistance, maxLength, 2)
        : default;

      if (TrySelectMatch(match.Distance, match.Length, efficiency, out var offsetClass, out var matchLength)) {
        if (pendingLiterals.Count == 0) {
          bitWriter.WriteBit(1);
        } else {
          bitWriter.WriteBit(0);
          WriteLiteralRun(bitWriter, pendingLiterals);
          pendingLiterals.Clear();
        }

        WriteMatch(bitWriter, offsetClass, match.Distance - 1, matchLength, efficiency);

        for (var i = 1; i < matchLength; ++i)
          matchFinder.InsertPosition(scanInput, pos + i);
        pos += matchLength;
      } else {
        pendingLiterals.Add(scanInput[pos]);
        if (pos < 2)
          matchFinder.InsertPosition(scanInput, pos);
        ++pos;
      }
    }

    if (pendingLiterals.Count > 0) {
      bitWriter.WriteBit(0);
      WriteLiteralRun(bitWriter, pendingLiterals);
    }

    var packed = bitWriter.ToArray(out var skipBits);
    return BuildPp20File(efficiency, packed, input.Length, skipBits);
  }

  private static bool TrySelectMatch(
    int distance,
    int availableLength,
    ReadOnlySpan<byte> efficiency,
    out int offsetClass,
    out int matchLength) {
    offsetClass = 0;
    matchLength = 0;
    if (distance <= 0 || availableLength < 2)
      return false;

    if (availableLength >= 5 && distance <= (1 << efficiency[3])) {
      offsetClass = 3;
      matchLength = availableLength;
      return true;
    }

    for (var candidateClass = 2; candidateClass >= 0; --candidateClass) {
      var length = candidateClass + 2;
      if (availableLength >= length && distance <= (1 << efficiency[candidateClass])) {
        offsetClass = candidateClass;
        matchLength = length;
        return true;
      }
    }

    return false;
  }

  private static void WriteLiteralRun(ReverseBitWriter writer, List<byte> literals) {
    var remaining = literals.Count - 1;
    while (remaining >= 3) {
      writer.WriteBits(3, 2);
      remaining -= 3;
    }
    writer.WriteBits(remaining, 2);

    foreach (var literal in literals)
      writer.WriteBits(literal, 8);
  }

  private static void WriteMatch(
    ReverseBitWriter writer,
    int offsetClass,
    int offset,
    int matchLength,
    ReadOnlySpan<byte> efficiency) {
    writer.WriteBits(offsetClass, 2);

    if (offsetClass == 3) {
      if (offset < 128) {
        writer.WriteBit(0);
        writer.WriteBits(offset, 7);
      } else {
        writer.WriteBit(1);
        writer.WriteBits(offset, efficiency[3]);
      }

      var remaining = matchLength - 5;
      while (remaining >= 7) {
        writer.WriteBits(7, 3);
        remaining -= 7;
      }
      writer.WriteBits(remaining, 3);
      return;
    }

    writer.WriteBits(offset, efficiency[offsetClass]);
  }

  private static byte[] BuildPp20File(ReadOnlySpan<byte> efficiency, byte[] packedData, int originalSize, int skipBits) {
    if (efficiency.Length != PowerPackerConstants.OffsetClasses)
      throw new ArgumentException("PP20 efficiency table must contain four entries.", nameof(efficiency));
    if (packedData.Length < sizeof(uint) || (packedData.Length & 3) != 0)
      throw new ArgumentException("PP20 packed data must contain whole 32-bit words.", nameof(packedData));

    var totalSize = PowerPackerConstants.MagicLength
      + PowerPackerConstants.EfficiencyTableSize
      + packedData.Length
      + PowerPackerConstants.DecrunchInfoSize;

    var result = new byte[totalSize];
    var span = result.AsSpan();
    PowerPackerConstants.Magic.CopyTo(span);
    efficiency.CopyTo(span[PowerPackerConstants.EfficiencyTableOffset..]);
    packedData.CopyTo(span[(PowerPackerConstants.MagicLength + PowerPackerConstants.EfficiencyTableSize)..]);

    var infoOffset = totalSize - PowerPackerConstants.DecrunchInfoSize;
    span[infoOffset] = (byte)(originalSize >> 16);
    span[infoOffset + 1] = (byte)(originalSize >> 8);
    span[infoOffset + 2] = (byte)originalSize;
    span[infoOffset + 3] = (byte)skipBits;
    return result;
  }

  private static void ValidateEfficiency(ReadOnlySpan<byte> efficiency) {
    foreach (var bits in efficiency)
      if (bits is < 9 or > 15)
        throw new InvalidDataException("PP20 efficiency entries must be between 9 and 15 bits.");
  }

  private ref struct ReverseBitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bytePos = data.Length - 1;
    private int _bitPos;
    private int _remainingBits = data.Length * 8;

    public int ReadBit() => this.ReadBits(1);

    public void SkipBits(int count) {
      if ((uint)count > (uint)this._remainingBits)
        throw new InvalidDataException("PP20 packed bitstream is truncated.");

      var absolute = this._bitPos + count;
      this._bytePos -= absolute >> 3;
      this._bitPos = absolute & 7;
      this._remainingBits -= count;
    }

    public int ReadBits(int count) {
      if ((uint)count > (uint)this._remainingBits)
        throw new InvalidDataException("PP20 packed bitstream is truncated.");

      var result = 0;
      for (var i = 0; i < count; ++i) {
        result = (result << 1) | ((this._data[this._bytePos] >> this._bitPos) & 1);
        if (++this._bitPos == 8) {
          this._bitPos = 0;
          --this._bytePos;
        }
      }

      this._remainingBits -= count;
      return result;
    }
  }

  private sealed class ReverseBitWriter {
    private readonly List<byte> _bytes = [];
    private byte _current;
    private int _bitsInCurrent;
    private int _totalBits;

    public void WriteBit(int value) => this.WriteBits(value, 1);

    public void WriteBits(int value, int bitCount) {
      for (var bit = bitCount - 1; bit >= 0; --bit) {
        if (((value >> bit) & 1) != 0)
          this._current |= (byte)(1 << this._bitsInCurrent);

        ++this._bitsInCurrent;
        ++this._totalBits;
        if (this._bitsInCurrent == 8) {
          this._bytes.Add(this._current);
          this._current = 0;
          this._bitsInCurrent = 0;
        }
      }
    }

    public byte[] ToArray(out int skipBits) {
      if (this._totalBits == 0) {
        skipBits = 0;
        return new byte[sizeof(uint)];
      }

      var logicalBytes = new byte[(this._totalBits + 7) >> 3];
      for (var i = 0; i < this._bytes.Count; ++i)
        logicalBytes[i] = this._bytes[i];
      if (this._bitsInCurrent != 0)
        logicalBytes[^1] = this._current;

      var packedBytes = ((this._totalBits + 31) >> 5) << 2;
      skipBits = packedBytes * 8 - this._totalBits;
      var consumptionOrder = new byte[packedBytes];
      var byteShift = skipBits >> 3;
      var bitShift = skipBits & 7;

      for (var i = 0; i < logicalBytes.Length; ++i) {
        var target = i + byteShift;
        consumptionOrder[target] |= (byte)(logicalBytes[i] << bitShift);
        if (bitShift != 0 && target + 1 < consumptionOrder.Length)
          consumptionOrder[target + 1] |= (byte)(logicalBytes[i] >> (8 - bitShift));
      }

      Array.Reverse(consumptionOrder);
      return consumptionOrder;
    }
  }

  private static byte[] ReadAllBytes(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
