using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Nrv2e;

/// <summary>
/// UCL reference NRV2E LE32 — Markus Oberhumer's UCL-family compression as used
/// at the core of UPX (compression methods 8 / 9 / 10 are the LE32 / LE16 / 8-bit
/// variants of NRV2E respectively; this implementation is LE32).
/// </summary>
/// <remarks>
/// <para>
/// On-disk layout per UCL <c>ucl/src/n2e_d.c</c>: bits are packed into 32-bit
/// little-endian words and consumed MSB-first; literal and match-offset bytes
/// are inlined into the byte stream between bit-word refills, in the order the
/// decoder consumes them. The decoder cursor advances by 4 on each bit-word
/// refill and by 1 on each literal/offset byte read.
/// </para>
/// <para>
/// NRV2E is structurally identical to NRV2D in its offset encoding (three-bit-
/// per-iteration varint loop, length-bit folded into the offset's low bit) and
/// in the <c>0x500</c> offset-bump threshold. The difference is the length
/// suffix encoding:
/// </para>
/// <list type="bullet">
///   <item>If m_len_initial = 1: read X ⇒ <c>m_len = 1 + X</c> ∈ {1, 2}.</item>
///   <item>Else if next bit = 1: read Z ⇒ <c>m_len = 3 + Z</c> ∈ {3, 4}.</item>
///   <item>Else: NRV2B-style varint, then <c>m_len += 3</c> (≥ 5).</item>
/// </list>
/// <para>
/// Final emit count is <c>m_len + 1 + (m_off &gt; 0x500 ? 1 : 0)</c>, gap-free
/// for both small and large offsets.
/// </para>
/// <para>
/// Our encoder uses greedy hash-chain LZ77 match finding so its output is not
/// byte-for-byte identical to UPX's optimal-parsing reference encoder, but the
/// decoder accepts any valid NRV2E LE32 stream including UPX's own output.
/// </para>
/// </remarks>
public sealed class Nrv2eBuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
  public string Id => "BB_Nrv2e";
  /// <inheritdoc/>
  public string DisplayName => "NRV2E";
  /// <inheritdoc/>
  public string Description => "UCL NRV2E LE32 — LZ77 + interleaved variable-length integer bit stream (UPX core, method 8)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MinEmittedLen = 3;
  private const int MaxOffset = 0xFFFFFF;
  private const int OffsetLargeThreshold = 0x500;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);
    if (data.Length == 0) return ms.ToArray();

    CompressBareInto(ms, data, refillWidthBytes: 4);
    return ms.ToArray();
  }

  /// <summary>
  /// Compresses <paramref name="data"/> as a bare NRV2E stream (no 4-byte size
  /// prefix) at the supplied bit-word refill width (1, 2, or 4 bytes — for the
  /// 8-bit, LE16, or LE32 stream variants respectively). Test-only helper —
  /// production callers should use <see cref="Compress"/> for LE32 round-tripping.
  /// </summary>
  internal static byte[] CompressBare(ReadOnlySpan<byte> data, int refillWidthBytes) {
    using var ms = new MemoryStream();
    if (data.Length > 0) CompressBareInto(ms, data, refillWidthBytes);
    return ms.ToArray();
  }

  private static void CompressBareInto(Stream output, ReadOnlySpan<byte> data, int refillWidthBytes) {
    var enc = new Nrv2eEncoder(output, refillWidthBytes);

    const int HashBits = 16;
    var head = new int[1 << HashBits];
    var prev = new int[data.Length];
    Array.Fill(head, -1);

    uint lastMatchOffset = 0;
    var pos = 0;
    while (pos < data.Length) {
      var bestLen = 0;
      var bestOff = 0;

      if (pos + MinEmittedLen <= data.Length) {
        var h = Hash(data, pos);
        var chainLen = 0;
        var minPos = Math.Max(0, pos - MaxOffset);
        var idx = head[h];
        while (idx >= minPos && chainLen < 64) {
          var off = pos - idx;
          if (off <= MaxOffset && data[idx] == data[pos]) {
            var maxLen = Math.Min(data.Length - pos, 1024);
            var len = 0;
            while (len < maxLen && data[idx + len] == data[pos + len]) len++;
            if (len >= MinEmittedLen && len > bestLen) {
              bestLen = len;
              bestOff = off;
            }
          }
          idx = prev[idx];
          chainLen++;
        }
        prev[pos] = head[h];
        head[h] = pos;
      }

      // NRV2E length encoding is gap-free. Min emitted length is 2 for small
      // offsets and 3 for offsets > 0x500. Reject too-short matches for far
      // offsets so we don't try to encode an unrepresentable length.
      if (bestLen >= MinEmittedLen && !((uint)bestOff > OffsetLargeThreshold && bestLen < 3)) {
        var reuseLast = (uint)bestOff == lastMatchOffset;
        enc.EmitMatch((uint)bestOff, bestLen, reuseLast);
        lastMatchOffset = (uint)bestOff;

        for (var j = 1; j < bestLen && pos + j + MinEmittedLen <= data.Length; j++) {
          var h = Hash(data, pos + j);
          prev[pos + j] = head[h];
          head[h] = pos + j;
        }
        pos += bestLen;
      } else {
        enc.EmitLiteral(data[pos]);
        pos++;
      }
    }

    enc.Flush();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4) throw new InvalidDataException("NRV2E: input smaller than 4-byte header.");
    var targetSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (targetSize < 0) throw new InvalidDataException("NRV2E: negative decompressed size.");
    if (targetSize == 0) return [];
    return DecompressCore(data[4..], targetSize);
  }

  /// <summary>
  /// Decompresses a bare NRV2E LE32 stream (no 4-byte size prefix) into a
  /// freshly-allocated buffer of exactly <paramref name="exactOutputSize"/> bytes.
  /// Exposed for callers parsing UPX binaries or other embedded NRV2E streams.
  /// </summary>
  public static byte[] DecompressRaw(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, refillWidthBytes: 4);

  /// <summary>
  /// Decompresses a bare NRV2E LE16 stream (no 4-byte size prefix). UPX
  /// compression method 9 uses this width — bits are packed into 16-bit
  /// little-endian words and consumed MSB-first.
  /// </summary>
  public static byte[] DecompressRawLe16(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, refillWidthBytes: 2);

  /// <summary>
  /// Decompresses a bare NRV2E 8-bit stream (no 4-byte size prefix). UPX
  /// compression method 10 uses this width — bits are packed into single bytes
  /// and consumed MSB-first.
  /// </summary>
  public static byte[] DecompressRawByte(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, refillWidthBytes: 1);

  private static byte[] DecompressCore(ReadOnlySpan<byte> compressed, int targetSize, int refillWidthBytes = 4) {
    var output = new byte[targetSize];
    var reader = new Nrv2eDecoder(compressed, refillWidthBytes);
    uint lastMatchOffset = 1; // UCL initialises last_m_off = 1.
    var op = 0;

    while (op < output.Length) {
      while (reader.ReadBit() == 1) {
        output[op++] = reader.ReadByte();
        if (op >= output.Length) return output;
      }

      // NRV2E shares the NRV2D offset varint: (data, continue, [extra-data]) per iter.
      uint mOff = 1;
      while (true) {
        mOff = (mOff << 1) | reader.ReadBit();
        if (mOff > 0xFFFFFFu + 3) throw new InvalidDataException("NRV2E: lookbehind overrun.");
        if (reader.ReadBit() == 1) break;
        mOff = ((mOff - 1) << 1) | reader.ReadBit();
      }

      uint finalOff;
      uint mLen;
      if (mOff == 2) {
        finalOff = lastMatchOffset;
        mLen = reader.ReadBit();
      } else {
        var b = reader.ReadByte();
        var raw = ((mOff - 3) << 8) | b;
        if (raw == 0xFFFFFFFFu) break;
        mLen = (raw ^ 0xFFFFFFFFu) & 1;
        raw >>= 1;
        finalOff = raw + 1;
        lastMatchOffset = finalOff;
      }

      // NRV2E length: m_len ∈ {1,2} via 1+X; {3,4} via 3+Z; or varint+3 for ≥ 5.
      if (mLen != 0) {
        mLen = 1 + reader.ReadBit();
      } else if (reader.ReadBit() == 1) {
        mLen = 3 + reader.ReadBit();
      } else {
        mLen = 1;
        do {
          mLen = (mLen << 1) | reader.ReadBit();
        } while (reader.ReadBit() == 0);
        mLen += 3;
      }

      if (finalOff > OffsetLargeThreshold) mLen++;

      if (finalOff > (uint)op) throw new InvalidDataException("NRV2E: offset points before start of output.");
      var src = op - (int)finalOff;
      var totalToEmit = (int)mLen + 1; // UCL emits 1 + m_len bytes per match.
      for (var i = 0; i < totalToEmit && op < output.Length; i++)
        output[op++] = output[src + i];
    }

    return output;
  }

  private static int Hash(ReadOnlySpan<byte> d, int pos)
    => ((d[pos] << 8) ^ (d[pos + 1] << 4) ^ d[pos + 2]) & 0xFFFF;

  // ── Encoder ──────────────────────────────────────────────────────────────

  private sealed class Nrv2eEncoder {

    private readonly Stream _output;
    private readonly int _widthBytes;
    private readonly int _widthBits;
    private readonly List<byte> _pendingBytes = [];
    private uint _bitWord;
    private int _bitsUsed;

    public Nrv2eEncoder(Stream output) : this(output, 4) { }

    public Nrv2eEncoder(Stream output, int widthBytes) {
      if (widthBytes is not (1 or 2 or 4))
        throw new ArgumentOutOfRangeException(nameof(widthBytes), "NRV2E: bit-word width must be 1, 2, or 4 bytes.");
      this._output = output;
      this._widthBytes = widthBytes;
      this._widthBits = widthBytes * 8;
    }

    public void EmitLiteral(byte value) {
      this._pendingBytes.Add(value);
      this.WriteBit(1);
    }

    public void EmitMatch(uint offset, int length, bool reuseLast) {
      this.WriteBit(0); // match flag

      // UCL emits (m_len + 1) bytes; +1 more if offset > 0x500.
      var bumpedLen = length - 1;
      if (offset > OffsetLargeThreshold) bumpedLen--;
      if (bumpedLen < 1) throw new InvalidOperationException("NRV2E: match too short to encode.");
      var mLen = (uint)bumpedLen;

      // Decide initial bit and length-suffix bit pattern.
      // m_len=1 → init=1, X=0 ("10")
      // m_len=2 → init=1, X=1 ("11")
      // m_len=3 → init=0, Y=1, Z=0 ("010")
      // m_len=4 → init=0, Y=1, Z=1 ("011")
      // m_len≥5 → init=0, Y=0, varint(m_len-3)
      uint mLenInitial;
      var suffixBits = new List<int>();
      uint? varintValue = null;
      if (mLen == 1) {
        mLenInitial = 1; suffixBits.Add(0);
      } else if (mLen == 2) {
        mLenInitial = 1; suffixBits.Add(1);
      } else if (mLen == 3) {
        mLenInitial = 0; suffixBits.Add(1); suffixBits.Add(0);
      } else if (mLen == 4) {
        mLenInitial = 0; suffixBits.Add(1); suffixBits.Add(1);
      } else {
        mLenInitial = 0; suffixBits.Add(0);
        varintValue = mLen - 3;
      }

      if (reuseLast) {
        // Offset varint emits value 2 (bits A=0, B=1).
        this.WriteBit(0);
        this.WriteBit(1);
        // Reuse path: m_len_initial is read as a fresh bit.
        this.WriteBit((int)mLenInitial);
      } else {
        var rawPre = ((offset - 1) << 1) | (1u - mLenInitial);
        var byteVal = (byte)(rawPre & 0xFF);
        var varintForOff = (rawPre >> 8) + 3;
        this.EmitOffsetVarint(varintForOff, byteVal);
      }

      foreach (var bit in suffixBits) this.WriteBit(bit);
      if (varintValue is { } v) this.WriteVarInt(v);
    }

    /// <summary>
    /// Emits the NRV2D/E offset-varint bit pattern that decodes to <paramref name="targetVarintValue"/>,
    /// queuing <paramref name="offsetByte"/> immediately before the final break bit so that the
    /// byte's word-epoch matches the bit-word containing the break bit.
    /// </summary>
    private void EmitOffsetVarint(uint targetVarintValue, byte offsetByte) {
      if (targetVarintValue < 2)
        throw new ArgumentOutOfRangeException(nameof(targetVarintValue), "NRV2E offset varint requires ≥ 2.");

      var iterations = new List<(int A, int B, int? C)> {
        ((int)(targetVarintValue & 1), 1, null) // iter k (break)
      };
      var mOffPre = targetVarintValue >> 1;
      while (mOffPre > 1) {
        var c = (int)(mOffPre & 1);
        var mOffAfterA = (mOffPre >> 1) + 1;
        var a = (int)(mOffAfterA & 1);
        iterations.Add((a, 0, c));
        mOffPre = mOffAfterA >> 1;
      }
      if (mOffPre != 1)
        throw new InvalidOperationException("NRV2E varint encoder: failed to walk back to initial m_off=1.");

      iterations.Reverse();

      var bits = new List<int>(iterations.Count * 3);
      foreach (var (a, b, c) in iterations) {
        bits.Add(a);
        bits.Add(b);
        if (c.HasValue) bits.Add(c.Value);
      }

      for (var i = 0; i < bits.Count - 1; i++) this.WriteBit(bits[i]);
      this._pendingBytes.Add(offsetByte);
      this.WriteBit(bits[^1]);
    }

    public void Flush() {
      if (this._bitsUsed > 0) {
        this._bitWord <<= this._widthBits - this._bitsUsed;
        this.FlushWord();
      } else if (this._pendingBytes.Count > 0) {
        this.FlushWord();
      }
    }

    private void WriteBit(int bit) {
      this._bitWord = (this._bitWord << 1) | (uint)(bit & 1);
      this._bitsUsed++;
      if (this._bitsUsed == this._widthBits) this.FlushWord();
    }

    private void WriteVarInt(uint value) {
      if (value < 2) throw new ArgumentOutOfRangeException(nameof(value), "NRV2E varint requires ≥ 2.");
      var msb = 31 - System.Numerics.BitOperations.LeadingZeroCount(value);
      for (var i = msb - 1; i >= 0; i--) {
        this.WriteBit((int)((value >> i) & 1));
        this.WriteBit(i == 0 ? 1 : 0);
      }
    }

    private void FlushWord() {
      Span<byte> buf = stackalloc byte[4];
      switch (this._widthBytes) {
        case 4:
          BinaryPrimitives.WriteUInt32LittleEndian(buf, this._bitWord);
          break;
        case 2:
          BinaryPrimitives.WriteUInt16LittleEndian(buf[..2], (ushort)this._bitWord);
          break;
        default:
          buf[0] = (byte)this._bitWord;
          break;
      }
      this._output.Write(buf[..this._widthBytes]);
      foreach (var b in this._pendingBytes) this._output.WriteByte(b);
      this._pendingBytes.Clear();
      this._bitWord = 0;
      this._bitsUsed = 0;
    }
  }

  // ── Decoder ──────────────────────────────────────────────────────────────

  private ref struct Nrv2eDecoder {
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _widthBytes;
    private readonly int _widthBits;
    private int _pos;
    private uint _bitWord;
    private int _bitsLeft;

    public Nrv2eDecoder(ReadOnlySpan<byte> data, int widthBytes) {
      if (widthBytes is not (1 or 2 or 4))
        throw new ArgumentOutOfRangeException(nameof(widthBytes), "NRV2E: refill width must be 1, 2, or 4 bytes.");
      this._data = data;
      this._widthBytes = widthBytes;
      this._widthBits = widthBytes * 8;
      this._pos = 0;
      this._bitWord = 0;
      this._bitsLeft = 0;
      this.RefillWord();
    }

    public uint ReadBit() {
      if (this._bitsLeft == 0) this.RefillWord();
      var bit = (this._bitWord >> 31) & 1;
      this._bitWord <<= 1;
      this._bitsLeft--;
      return bit;
    }

    public byte ReadByte() {
      if (this._pos >= this._data.Length)
        throw new InvalidDataException("NRV2E: unexpected end of byte stream.");
      return this._data[this._pos++];
    }

    private void RefillWord() {
      Span<byte> pad = stackalloc byte[4];
      var take = Math.Min(this._widthBytes, this._data.Length - this._pos);
      if (take > 0) this._data.Slice(this._pos, take).CopyTo(pad);
      this._pos += take;
      uint raw = this._widthBytes switch {
        4 => BinaryPrimitives.ReadUInt32LittleEndian(pad),
        2 => BinaryPrimitives.ReadUInt16LittleEndian(pad[..2]),
        _ => pad[0],
      };
      this._bitWord = raw << (32 - this._widthBits);
      this._bitsLeft = this._widthBits;
    }
  }
}
