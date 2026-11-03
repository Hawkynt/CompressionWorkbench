namespace Compression.Core.Dictionary.PkwareDcl;

/// <summary>
/// Encodes the PKWARE Data Compression Library "implode" format, readable by every "explode"
/// (StormLib, Storm.dll, blast.c).
/// </summary>
/// <remarks>
/// The token coding and its rules follow StormLib's <c>pklib/implode.c</c> (Ladislav Zezula, MIT;
/// see <c>THIRD-PARTY-NOTICE.StormLib.txt</c>): repetitions of 2 to 516 bytes, a two-byte
/// repetition only when it lies within 256 bytes, a one-byte look-ahead when a short match might
/// give way to a longer one, and the end marker after the last token. The match finder is a hash
/// chain of its own, so the output is a valid stream but not byte-identical to pklib's.
/// </remarks>
public static class PkwareDclEncoder {

  private const int MaxRepLength = 0x204;
  private const int MaxChainSteps = 512;

  /// <summary>Compresses <paramref name="input"/> into a complete stream with header and end marker.</summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="mode">Literal coding to use.</param>
  /// <param name="dictionarySize">1024, 2048 or 4096 bytes.</param>
  /// <returns>The compressed stream.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The dictionary size or mode is not one the format defines.</exception>
  public static byte[] Compress(ReadOnlySpan<byte> input, PkwareDclMode mode = PkwareDclMode.Binary, int dictionarySize = 4096) {
    var dictionaryBits = dictionarySize switch {
      1024 => 4,
      2048 => 5,
      4096 => 6,
      _ => throw new ArgumentOutOfRangeException(nameof(dictionarySize), dictionarySize, "PKWARE DCL dictionaries are 1024, 2048 or 4096 bytes."),
    };
    if (mode is not (PkwareDclMode.Binary or PkwareDclMode.Ascii))
      throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown PKWARE DCL literal mode.");

    var writer = new BitWriter(input.Length / 2 + 16);
    writer.WriteByte((byte)mode);
    writer.WriteByte((byte)dictionaryBits);

    var finder = new MatchFinder(input, dictionarySize);
    var position = 0;
    while (position < input.Length) {
      var (length, distance) = finder.Find(position);
      if (length >= 2 && length < 8 && position + 1 < input.Length) {
        // A longer repetition one byte later is worth a literal now (pklib's rule).
        var (nextLength, _) = finder.Find(position + 1);
        if (nextLength > length && (nextLength > length + 1 || distance > 0x80)) {
          WriteLiteral(ref writer, mode, input[position]);
          finder.Insert(position);
          ++position;
          continue;
        }
      }

      if (length >= 2) {
        WriteRepetition(ref writer, length, distance - 1, dictionaryBits);
        for (var i = 0; i < length; ++i)
          finder.Insert(position + i);
        position += length;
      } else {
        WriteLiteral(ref writer, mode, input[position]);
        finder.Insert(position);
        ++position;
      }
    }

    WriteLength(ref writer, PkwareDclTables.EndOfStreamLength);
    return writer.ToArray();
  }

  private static void WriteLiteral(ref BitWriter writer, PkwareDclMode mode, byte value) {
    writer.WriteBits(0, 1);
    if (mode == PkwareDclMode.Binary)
      writer.WriteBits(value, 8);
    else
      writer.WriteBits(PkwareDclTables.ChCodeAsc[value], PkwareDclTables.ChBitsAsc[value]);
  }

  /// <summary>Writes the 1-flag, length code and extra bits for a length value (repetition minus 2).</summary>
  private static void WriteLength(ref BitWriter writer, int lengthValue) {
    var code = 15;
    while (PkwareDclTables.LenBase[code] > lengthValue)
      --code;
    writer.WriteBits(1, 1);
    writer.WriteBits(PkwareDclTables.LenCode[code], PkwareDclTables.LenBits[code]);
    if (PkwareDclTables.ExLenBits[code] != 0)
      writer.WriteBits(lengthValue - PkwareDclTables.LenBase[code], PkwareDclTables.ExLenBits[code]);
  }

  /// <param name="writer">Target.</param>
  /// <param name="length">Repetition length, 2..516.</param>
  /// <param name="distanceMinusOne">Backward distance minus one.</param>
  /// <param name="dictionaryBits">4, 5 or 6.</param>
  private static void WriteRepetition(ref BitWriter writer, int length, int distanceMinusOne, int dictionaryBits) {
    WriteLength(ref writer, length - 2);
    if (length == 2) {
      var code = distanceMinusOne >> 2;
      writer.WriteBits(PkwareDclTables.DistCode[code], PkwareDclTables.DistBits[code]);
      writer.WriteBits(distanceMinusOne & 3, 2);
    } else {
      var code = distanceMinusOne >> dictionaryBits;
      writer.WriteBits(PkwareDclTables.DistCode[code], PkwareDclTables.DistBits[code]);
      writer.WriteBits(distanceMinusOne & ((1 << dictionaryBits) - 1), dictionaryBits);
    }
  }

  /// <summary>Hash-chain match finder over two-byte prefixes, bounded by the dictionary size.</summary>
  private ref struct MatchFinder {
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _window;
    private readonly int[] _head;
    private readonly int[] _previous;
    private int _inserted;

    public MatchFinder(ReadOnlySpan<byte> data, int window) {
      this._data = data;
      this._window = window;
      this._head = new int[1 << 16];
      Array.Fill(this._head, -1);
      this._previous = new int[Math.Max(1, data.Length)];
      this._inserted = 0;
    }

    /// <summary>Records <paramref name="position"/> (and any skipped before it) as a match source.</summary>
    public void Insert(int position) {
      for (; this._inserted <= position; ++this._inserted) {
        if (this._inserted + 1 >= this._data.Length) {
          this._previous[this._inserted] = -1;
          continue;
        }
        var key = this._data[this._inserted] | (this._data[this._inserted + 1] << 8);
        this._previous[this._inserted] = this._head[key];
        this._head[key] = this._inserted;
      }
    }

    /// <summary>Longest repetition starting at <paramref name="position"/>, nearest first on ties.</summary>
    public readonly (int Length, int Distance) Find(int position) {
      if (position + 1 >= this._data.Length)
        return (0, 0);
      var maxLength = Math.Min(MaxRepLength, this._data.Length - position);
      var key = this._data[position] | (this._data[position + 1] << 8);
      var candidate = this._head[key];
      // Positions not yet inserted cannot be sources; walk the chain from the newest inserted one.
      while (candidate >= position)
        candidate = this._previous[candidate];

      var bestLength = 0;
      var bestDistance = 0;
      for (var steps = 0; candidate >= 0 && steps < MaxChainSteps; ++steps) {
        var distance = position - candidate;
        if (distance > this._window)
          break;
        var length = 2;
        while (length < maxLength && this._data[candidate + length] == this._data[position + length])
          ++length;
        if (length > bestLength && (length > 2 || distance <= 0x100)) {
          bestLength = length;
          bestDistance = distance;
          if (length == maxLength)
            break;
        }
        candidate = this._previous[candidate];
      }
      return (bestLength, bestDistance);
    }
  }

  /// <summary>LSB-first bit writer.</summary>
  private struct BitWriter(int capacity) {
    private readonly List<byte> _bytes = new(capacity);
    private uint _buffer;
    private int _count;

    public void WriteByte(byte value) => this._bytes.Add(value);

    public void WriteBits(int value, int count) {
      this._buffer |= (uint)(value & ((1 << count) - 1)) << this._count;
      this._count += count;
      while (this._count >= 8) {
        this._bytes.Add((byte)this._buffer);
        this._buffer >>= 8;
        this._count -= 8;
      }
    }

    public readonly byte[] ToArray() {
      var result = new byte[this._bytes.Count + (this._count > 0 ? 1 : 0)];
      this._bytes.CopyTo(result);
      if (this._count > 0)
        result[^1] = (byte)this._buffer;
      return result;
    }
  }
}
