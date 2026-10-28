namespace Compression.Core.FileSystems.Cvf;

/// <summary>The two Microsoft LZ77 token grammars of <c>docs/CVF-CODECS-ON-DISK.md</c>.</summary>
internal enum MsdblDialect {
  /// <summary>DoubleSpace DS-0-x: two-bit literal tags, matches copy one byte less than their length code.</summary>
  Ds,
  /// <summary>DriveSpace JM-0-x: one-bit tag for low literals, matches copy their full length code.</summary>
  Jm,
}

/// <summary>
/// The DS-0-x / JM-0-x token stream (without the 4-byte method header): bits LSB-first from
/// consecutive bytes, literals and back-references of up to 4414 bytes distance, and the sync marker
/// (distance 4415) that closes every cluster.
/// </summary>
internal static class MsdblLzCodec {
  /// <summary>The all-ones 12-bit long distance, which is the sync marker rather than a match.</summary>
  public const int SyncDistance = 320 + 0xFFF;

  /// <summary>The farthest real match.</summary>
  public const int MaxDistance = SyncDistance - 1;

  /// <summary>The longest length code: nine-step prefix, <c>2 + 2^8 + 255</c>.</summary>
  private const int MaxLengthCode = 513;

  private const int MaxLengthPrefix = 8;

  /// <summary>Decoded bytes between two sync markers in an encoded cluster: one sector.</summary>
  private const int SyncInterval = 512;

  // ── decoding ────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes the token stream in <paramref name="stream"/> until <paramref name="output"/> is full.
  /// </summary>
  /// <param name="stream">The token stream, starting right after the method header.</param>
  /// <param name="output">The cluster; bytes not written stay as they are (zero).</param>
  /// <param name="dialect">DS or JM.</param>
  /// <param name="stopAtEndOfInput">
  /// Stacker's use of the DS grammar: a stream that runs out (or reaches its zero padding) before the
  /// cluster is full ends there instead of being an error.
  /// </param>
  /// <returns>The number of bytes decoded.</returns>
  /// <exception cref="InvalidDataException">The stream is malformed or ends early.</exception>
  public static int Decode(ReadOnlySpan<byte> stream, Span<byte> output, MsdblDialect dialect, bool stopAtEndOfInput) {
    var reader = new LsbBitReader(stream);
    var position = 0;
    while (position < output.Length) {
      var token = dialect == MsdblDialect.Ds ? ReadDsToken(ref reader) : ReadJmToken(ref reader);
      if (reader.Overran) {
        if (stopAtEndOfInput) break;
        throw new InvalidDataException($"{dialect}: token stream ends after {position} of {output.Length} bytes.");
      }

      if (token.Distance == 0) {                 // literal (a real match never has distance 0)
        if (token.Length != 0) {
          if (stopAtEndOfInput) break;           // zero padding read as a distance-0 match
          throw new InvalidDataException($"{dialect}: match with distance 0.");
        }
        output[position++] = token.Literal;
        continue;
      }

      if (token.Distance == SyncDistance) {
        // A cluster whose data ends before its nominal size is closed by a sync with nothing but
        // padding behind it; the remainder of the cluster reads as zero.
        if (reader.RestIsZero) break;
        continue;
      }

      var length = ReadLengthCode(ref reader);
      if (reader.Overran) {
        if (stopAtEndOfInput) break;
        throw new InvalidDataException($"{dialect}: token stream ends inside a match.");
      }
      if (length < 0) throw new InvalidDataException($"{dialect}: length code has more than {MaxLengthPrefix} leading zeros.");
      if (dialect == MsdblDialect.Ds) --length;
      if (token.Distance > position) throw new InvalidDataException($"{dialect}: match reaches before the start of the cluster.");

      length = Math.Min(length, output.Length - position);
      for (var end = position + length; position < end; ++position)
        output[position] = output[position - token.Distance];
    }
    return position;
  }

  /// <summary>A literal (<see cref="Distance"/> 0, <see cref="Length"/> 0) or the distance of a match/sync.</summary>
  private readonly record struct Token(byte Literal, int Distance, int Length = 0);

  private static Token ReadDsToken(ref LsbBitReader reader) {
    var first = reader.Read(1);
    var second = reader.Read(1);
    return (first, second) switch {
      (0, 0) => Match(reader.Read(6)),
      (1, 0) => new((byte)(0x80 | reader.Read(7)), 0),
      (0, 1) => new((byte)reader.Read(7), 0),
      _ => reader.Read(1) == 0 ? Match(64 + reader.Read(8)) : Match(320 + reader.Read(12)),
    };
  }

  private static Token ReadJmToken(ref LsbBitReader reader) {
    if (reader.Read(1) == 0) return new((byte)reader.Read(7), 0);
    if (reader.Read(1) == 1) return new((byte)(0x80 | reader.Read(7)), 0);
    if (reader.Read(1) == 0) return Match(reader.Read(6));
    return reader.Read(1) == 0 ? Match(64 + reader.Read(8)) : Match(320 + reader.Read(12));
  }

  // Distance 0 cannot be a literal here: flag it with a non-zero length so the caller rejects it.
  private static Token Match(int distance) => new(0, distance, distance == 0 ? 1 : 0);

  /// <summary>Reads <c>z</c> zeros, a one, then <c>z</c> bits: <c>2 + 2^z + v</c>; -1 for nine zeros.</summary>
  private static int ReadLengthCode(ref LsbBitReader reader) {
    var prefix = 0;
    while (reader.Read(1) == 0) {
      if (++prefix > MaxLengthPrefix) return -1;
      if (reader.Overran) return 0;
    }
    return 2 + (1 << prefix) + reader.Read(prefix);
  }

  // ── encoding ────────────────────────────────────────────────────────────

  /// <summary>
  /// Encodes <paramref name="data"/> as a token stream with a sync marker after every 512 decoded
  /// bytes and after the last one, appended to <paramref name="writer"/>. As in the streams the dmsdos
  /// compressors write, no match runs across a 512-byte boundary (distances still reach back across
  /// them), so every sector of the cluster ends on a sync.
  /// </summary>
  public static void Encode(ReadOnlySpan<byte> data, LsbBitWriter writer, MsdblDialect dialect, int level) {
    // A DS match copies one byte less than its length code, so its shortest copy is two bytes and its
    // longest 512; the finder reports matches of three bytes and more.
    var maxCopy = dialect == MsdblDialect.Ds ? MaxLengthCode - 1 : MaxLengthCode;
    var copyBias = dialect == MsdblDialect.Ds ? 1 : 0;
    var bytes = data.ToArray();
    var finder = new ClusterMatchFinder(bytes, MaxDistance, maxCopy, level);

    var sectorStart = 0;
    do {
      var sectorEnd = Math.Min(sectorStart + SyncInterval, bytes.Length);
      var position = sectorStart;
      var pending = finder.Find(position, sectorEnd);
      while (position < sectorEnd) {
        var (length, distance) = pending;
        if (length == 0) {
          WriteLiteral(writer, dialect, bytes[position]);
          pending = ++position < sectorEnd ? finder.Find(position, sectorEnd) : default;
          continue;
        }

        // One step of lazy evaluation: a longer match starting at the next byte wins over this one.
        var next = position + 1 < sectorEnd ? finder.Find(position + 1, sectorEnd) : default;
        if (next.Length > length) {
          WriteLiteral(writer, dialect, bytes[position]);
          ++position;
          pending = next;
          continue;
        }

        WriteMatch(writer, dialect, distance, length + copyBias);
        position += length;
        pending = position < sectorEnd ? finder.Find(position, sectorEnd) : default;
      }
      WriteSync(writer, dialect);
      sectorStart = sectorEnd;
    } while (sectorStart < bytes.Length);
  }

  private static void WriteLiteral(LsbBitWriter writer, MsdblDialect dialect, byte value) {
    var high = value >= 0x80;
    if (dialect == MsdblDialect.Ds)
      writer.Write(high ? 0b01 : 0b10, 2);       // read order: "1 0" high, "0 1" low
    else if (high)
      writer.Write(0b11, 2);
    else
      writer.Write(0, 1);
    writer.Write(value & 0x7F, 7);
  }

  private static void WriteMatch(LsbBitWriter writer, MsdblDialect dialect, int distance, int lengthCode) {
    WriteDistance(writer, dialect, distance);
    var prefix = 31 - int.LeadingZeroCount(lengthCode - 2);
    writer.Write(1 << prefix, prefix + 1);       // `prefix` zeros, then a one
    writer.Write(lengthCode - 2 - (1 << prefix), prefix);
  }

  private static void WriteSync(LsbBitWriter writer, MsdblDialect dialect) => WriteDistance(writer, dialect, SyncDistance);

  private static void WriteDistance(LsbBitWriter writer, MsdblDialect dialect, int distance) {
    if (dialect == MsdblDialect.Ds) {
      switch (distance) {
        case < 64: writer.Write(0b00, 2); writer.Write(distance, 6); break;
        case < 320: writer.Write(0b011, 3); writer.Write(distance - 64, 8); break;
        default: writer.Write(0b111, 3); writer.Write(distance - 320, 12); break;
      }
      return;
    }

    writer.Write(0b01, 2);                       // read order: "1 0"
    switch (distance) {
      case < 64: writer.Write(0, 1); writer.Write(distance, 6); break;
      case < 320: writer.Write(0b01, 2); writer.Write(distance - 64, 8); break;
      default: writer.Write(0b11, 2); writer.Write(distance - 320, 12); break;
    }
  }
}

/// <summary>Reads bits LSB-first from consecutive bytes; past the end it yields zeros and records the overrun.</summary>
internal ref struct LsbBitReader {
  private readonly ReadOnlySpan<byte> _data;
  private int _next;
  private ulong _buffer;
  private int _count;

  public LsbBitReader(ReadOnlySpan<byte> data) => this._data = data;

  /// <summary>True once a read has needed bits beyond the end of the data.</summary>
  public readonly bool Overran => (long)this._next * 8 - this._count > (long)this._data.Length * 8;

  /// <summary>True when every bit not yet read is zero.</summary>
  public readonly bool RestIsZero => this._buffer == 0 && (this._next >= this._data.Length || !this._data[this._next..].ContainsAnyExcept((byte)0));

  /// <summary>Reads <paramref name="count"/> (at most 24) bits, the first read becoming the least significant.</summary>
  public int Read(int count) {
    while (this._count < count) {
      if (this._next < this._data.Length) this._buffer |= (ulong)this._data[this._next] << this._count;
      ++this._next;
      this._count += 8;
    }
    var value = (int)(this._buffer & ((1UL << count) - 1));
    this._buffer >>= count;
    this._count -= count;
    return value;
  }
}

/// <summary>Writes bits LSB-first into consecutive bytes.</summary>
internal sealed class LsbBitWriter {
  private byte[] _bytes = new byte[256];
  private int _length;
  private ulong _accumulator;
  private int _count;

  /// <summary>Appends a whole byte; only valid at a byte boundary.</summary>
  public void WriteByte(byte value) => this.Write(value, 8);

  /// <summary>Appends the low <paramref name="count"/> (at most 32) bits of <paramref name="value"/>, least significant first.</summary>
  public void Write(int value, int count) {
    this._accumulator |= ((ulong)(uint)value & ((1UL << count) - 1)) << this._count;
    this._count += count;
    while (this._count >= 8) {
      this.Push((byte)this._accumulator);
      this._accumulator >>= 8;
      this._count -= 8;
    }
  }

  /// <summary>Flushes the partial byte and pads with zero bytes to a multiple of <paramref name="alignment"/>.</summary>
  public byte[] ToArray(int alignment) {
    if (this._count > 0) this.Write(0, 8 - this._count);
    while (this._length % alignment != 0) this.Push(0);
    return this._bytes.AsSpan(0, this._length).ToArray();
  }

  private void Push(byte value) {
    if (this._length == this._bytes.Length) Array.Resize(ref this._bytes, this._bytes.Length * 2);
    this._bytes[this._length++] = value;
  }
}
