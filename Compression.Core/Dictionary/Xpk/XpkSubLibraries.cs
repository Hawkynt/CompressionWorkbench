namespace Compression.Core.Dictionary.Xpk;

/// <summary>
/// Decoders for XPK sub-library chunks (one packed chunk in, its raw bytes out).
/// </summary>
/// <remarks>
/// Converted from ancient (Teemu Suutari, BSD-2-Clause, see THIRD-PARTY-NOTICE.ancient.txt):
/// <c>RLENDecompressor</c>, <c>SQSHDecompressor</c>, <c>NUKEDecompressor</c>
/// (NUKE/DUKE) and <c>DLTADecode</c>. Code tables are copied exactly.
/// </remarks>
public static class XpkSubLibraries {

  /// <summary>Sub-library ids this class decodes.</summary>
  public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal) {
    "NONE", "RLEN", "SQSH", "NUKE", "DUKE",
  };

  /// <summary>Decodes one packed chunk of sub-library <paramref name="type"/>.</summary>
  /// <exception cref="NotSupportedException">The sub-library is not implemented.</exception>
  /// <exception cref="InvalidDataException">The chunk is malformed.</exception>
  public static byte[] Decode(string type, byte[] chunk, int rawSize) => type switch {
    "NONE" => chunk.Length == rawSize ? chunk : throw new InvalidDataException("XPK-NONE: chunk size differs from its raw size."),
    "RLEN" => DecodeRlen(chunk, rawSize),
    "SQSH" => DecodeSqsh(chunk, rawSize),
    "NUKE" => DecodeNuke(chunk, rawSize, delta: false),
    "DUKE" => DecodeNuke(chunk, rawSize, delta: true),
    _ => throw new NotSupportedException($"XPK sub-library '{type}' is not supported."),
  };

  // ── RLEN ─────────────────────────────────────────────────────────────

  /// <summary>
  /// XPK-RLEN: a count byte below 128 copies that many literals; 128 and above repeats the
  /// next byte <c>256 − count</c> times.
  /// </summary>
  public static byte[] DecodeRlen(byte[] chunk, int rawSize) {
    var output = new byte[rawSize];
    int i = 0, o = 0;
    byte Next() => i < chunk.Length ? chunk[i++] : throw new InvalidDataException("XPK-RLEN: data ends early.");
    while (o < rawSize) {
      int count = Next();
      if (count < 128) {
        if (count == 0) throw new InvalidDataException("XPK-RLEN: zero literal count.");
        for (var k = 0; k < count; ++k) Put(Next());
      } else {
        count = 256 - count;
        var ch = Next();
        for (var k = 0; k < count; ++k) Put(ch);
      }
    }
    return output;

    void Put(byte b) {
      if (o >= rawSize) throw new InvalidDataException("XPK-RLEN: run past the chunk's raw size.");
      output[o++] = b;
    }
  }

  /// <summary>Encodes <paramref name="data"/> as an XPK-RLEN chunk.</summary>
  public static byte[] EncodeRlen(ReadOnlySpan<byte> data) {
    var output = new List<byte>(data.Length + data.Length / 64 + 2);
    var i = 0;
    while (i < data.Length) {
      var run = 1;
      while (i + run < data.Length && run < 127 && data[i + run] == data[i]) ++run;
      if (run >= 3) {
        output.Add((byte)(256 - run));
        output.Add(data[i]);
        i += run;
        continue;
      }
      // Literals up to the next run of three.
      var start = i;
      var count = 0;
      while (i < data.Length && count < 127) {
        if (i + 2 < data.Length && data[i] == data[i + 1] && data[i] == data[i + 2]) break;
        ++i;
        ++count;
      }
      output.Add((byte)count);
      for (var k = 0; k < count; ++k) output.Add(data[start + k]);
    }
    return [.. output];
  }

  // ── SQSH ─────────────────────────────────────────────────────────────

  private static readonly int[][] SqshBitLengths = [
    [2, 3, 4, 5, 6, 7, 8, 0],
    [3, 2, 4, 5, 6, 7, 8, 0],
    [4, 3, 5, 2, 6, 7, 8, 0],
    [5, 4, 6, 2, 3, 7, 8, 0],
    [6, 5, 7, 2, 3, 4, 8, 0],
    [7, 6, 8, 2, 3, 4, 5, 0],
    [8, 7, 6, 2, 3, 4, 5, 0],
  ];

  /// <summary>Prefix code read MSB first: (length, code, value).</summary>
  private static int DecodePrefix(Func<uint> readBit, (int Length, uint Code, int Value)[] codes) {
    uint code = 0;
    for (var len = 1; len <= 32; ++len) {
      code = (code << 1) | readBit();
      foreach (var c in codes)
        if (c.Length == len && c.Code == code) return c.Value;
    }
    throw new InvalidDataException("XPK: invalid prefix code.");
  }

  private static readonly (int, uint, int)[] SqshMod = [(1, 0b1, 0), (2, 0b00, 1), (3, 0b010, 2), (4, 0b0110, 3), (4, 0b0111, 4)];
  private static readonly (int, uint, int)[] SqshLength = [(1, 0b0, 0), (2, 0b10, 1), (3, 0b110, 2), (4, 0b1110, 3), (4, 0b1111, 4)];
  private static readonly (int, uint, int)[] SqshDistance = [(1, 0b1, 1), (2, 0b00, 0), (2, 0b01, 2)];

  /// <summary>XPK-SQSH: delta-coded samples with adaptive bit widths and LZ repeats.</summary>
  public static byte[] DecodeSqsh(byte[] chunk, int rawSize) {
    if (chunk.Length < 3) throw new InvalidDataException("XPK-SQSH: chunk too small.");
    var declared = (chunk[0] << 8) | chunk[1];
    if (declared == 0 || declared != rawSize) throw new InvalidDataException("XPK-SQSH: size field disagrees with the chunk.");
    var input = new XpkForwardStream(chunk, 2, chunk.Length);
    var bits = new XpkMsbBitReader(() => (input.ReadByte(), 8));
    uint ReadBits(int n) => bits.Read(n);
    uint ReadBit() => bits.Read(1);
    int ReadSigned(int n) {
      var v = (int)ReadBits(n);
      if ((v & (1 << (n - 1))) != 0) v |= -1 << n;
      return v;
    }

    var output = new byte[rawSize];
    var o = 0;
    var current = input.ReadByte();
    output[o++] = current;
    uint accum1 = 0, accum2 = 0;
    var prevBits = 0;
    var lengthVlc = new XpkVlc(1, 1, 1, 3, 5);
    var distanceVlc = new XpkVlc(8, 12, 14);

    while (o < rawSize) {
      var width = 0;
      uint count = 0;
      var repeat = false;
      if (accum1 >= 8) {
        void CondCase() {
          if (width == 8) {
            if (accum2 < 20) count = 1;
            else { count = 2; accum2 += 8; }
          } else {
            count = 5;
            accum2 += 8;
          }
        }
        void Table(int newBits) {
          if (prevBits < 2 || newBits == 0) throw new InvalidDataException("XPK-SQSH: invalid width change.");
          width = SqshBitLengths[prevBits - 2][newBits - 1];
          if (width == 0) throw new InvalidDataException("XPK-SQSH: invalid width change.");
          CondCase();
        }
        switch (DecodePrefix(ReadBit, SqshMod)) {
          case 0:
            if (prevBits == 8) { width = 8; CondCase(); }
            else { width = prevBits; count = 5; accum2 += 8; }
            break;
          case 1: repeat = true; break;
          case 2: Table(2); break;
          case 3: Table(3); break;
          case 4: Table((int)ReadBits(2) + 4); break;
        }
      } else if (ReadBit() != 0) {
        repeat = true;
      } else {
        count = 1;
        width = 8;
      }

      if (repeat) {
        count = lengthVlc.Decode(ReadBits, (uint)DecodePrefix(ReadBit, SqshLength)) + 2;
        if (count >= 3) {
          if (accum1 != 0) --accum1;
          if (count > 3 && accum1 != 0) --accum1;
        }
        var distance = distanceVlc.Decode(ReadBits, (uint)DecodePrefix(ReadBit, SqshDistance)) + 1;
        count = Math.Min(count, (uint)(rawSize - o));
        if (distance > o) throw new InvalidDataException("XPK-SQSH: match reaches before the chunk.");
        for (var k = 0; k < count; ++k, ++o) output[o] = output[o - (int)distance];
        current = output[o - 1];
      } else {
        count = Math.Min(count, (uint)(rawSize - o));
        for (var k = 0; k < count; ++k) {
          current = (byte)(current - ReadSigned(width));
          output[o++] = current;
        }
        if (accum1 != 31) ++accum1;
        prevBits = width;
      }
      accum2 -= accum2 >> 3;
    }
    return output;
  }

  // ── NUKE / DUKE ──────────────────────────────────────────────────────

  private static readonly XpkVlc NukeDistances = new(4, 6, 8, 9, -4, 7, 9, 11, 13, 14, -5, 7, 9, 11, 13, 14);

  /// <summary>
  /// XPK-NUKE: LZ77 with literal bytes read backwards from the chunk end and four
  /// interleaved bit readers (1-bit, 2-bit, 4-bit LSB-first and free-width) fed forwards.
  /// DUKE adds a running-sum delta pass.
  /// </summary>
  public static byte[] DecodeNuke(byte[] chunk, int rawSize, bool delta) {
    var forward = new XpkForwardStream(chunk, 0, chunk.Length);
    var backward = new XpkBackwardStream(chunk, 0, chunk.Length);
    forward.Linked = backward;
    backward.Linked = forward;
    var bit1 = new XpkMsbBitReader(() => (forward.ReadBE16(), 16));
    var bit2 = new XpkMsbBitReader(() => (forward.ReadBE16(), 16));
    var bit4 = new XpkLsbBitReader(() => (forward.ReadBE32(), 32));
    var bitX = new XpkMsbBitReader(() => (forward.ReadBE16(), 16));
    uint ReadBits(int n) => bitX.Read(n);

    var output = new byte[rawSize];
    var o = 0;
    while (true) {
      if (bit1.Read(1) == 0) {
        uint count = 0;
        if (bit1.Read(1) != 0) count = 1;
        else {
          uint t;
          do {
            t = bit2.Read(2);
            count += t != 0 ? 5 - t : 3;
          } while (t == 0);
        }
        for (var k = 0; k < count; ++k) {
          if (o >= rawSize) throw new InvalidDataException("XPK-NUKE: literals past the chunk's raw size.");
          output[o++] = backward.ReadByte();
        }
      }
      if (o >= rawSize) break;
      var index = bit4.Read(4);
      var distance = NukeDistances.Decode(ReadBits, index);
      uint length = index < 4 ? 2u : index < 10 ? 3u : 0;
      if (length == 0) {
        length = bit2.Read(2);
        if (length == 0) {
          length = 6;
          uint t;
          do {
            t = bit4.Read(4);
            length += t != 0 ? 16 - t : 15;
          } while (t == 0);
        } else
          length = 7 - length;
      }
      if (distance == 0 || distance > o || o + length > rawSize)
        throw new InvalidDataException("XPK-NUKE: invalid match.");
      for (var k = 0; k < length; ++k, ++o) output[o] = output[o - (int)distance];
    }
    if (delta) {
      byte sum = 0;
      for (var k = 0; k < output.Length; ++k) output[k] = sum += output[k];
    }
    return output;
  }
}
