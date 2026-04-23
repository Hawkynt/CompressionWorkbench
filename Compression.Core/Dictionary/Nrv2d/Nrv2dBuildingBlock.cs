using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Nrv2d;

/// <summary>
/// UCL reference NRV2D LE32 — Markus Oberhumer's UCL-family compression as used
/// at the core of UPX (compression methods 3 / 5 / 7 are the LE32 / LE16 / 8-bit
/// variants of NRV2D respectively; this implementation is LE32).
/// </summary>
/// <remarks>
/// <para>
/// On-disk layout per UCL <c>ucl/src/n2d_d.c</c>: bits are packed into 32-bit
/// little-endian words and consumed MSB-first; literal and match-offset bytes
/// are inlined into the byte stream between bit-word refills, in the order the
/// decoder consumes them. The decoder cursor advances by 4 on each bit-word
/// refill and by 1 on each literal/offset byte read.
/// </para>
/// <para>
/// NRV2D shares NRV2B's literal/match flag (control bit <c>1</c> ⇒ literal),
/// 32-bit refill, "reuse last offset" sentinel (varint value <c>2</c>), and the
/// <c>(value-3)*256 + byte + 1</c> offset reconstruction. Differences:
/// </para>
/// <list type="bullet">
///   <item>The offset varint loop reads <em>three</em> bits per iteration (data, continue, extra-data) instead of NRV2B's two — every non-final iteration appends an extra data bit to the offset value via <c>m_off = (m_off-1)*2 + bit</c>. This removes NRV2B's m_len=3 gap by giving the encoder an extra bit per offset iteration.</item>
///   <item>The low bit of the reconstructed offset (after the byte read) doubles as the first bit of the length encoding: <c>m_len_initial = ~m_off &amp; 1</c>, <c>m_off &gt;&gt;= 1</c>, then <c>m_off++</c>. The encoder folds the desired length-initial bit into the inverted low bit of the offset byte.</item>
///   <item>Length encoding: read one more bit X to form <c>m_len = m_len_initial*2 + X</c>. If 0 ⇒ NRV2B-style varint with <c>m_len += 2</c> (≥ 4). Else m_len ∈ {1, 2, 3}. Final emit count is <c>m_len + 1 + (m_off &gt; 0x500 ? 1 : 0)</c>.</item>
///   <item>Offset-bump threshold is <c>0x500</c> (NRV2B uses <c>0xD00</c>).</item>
/// </list>
/// <para>
/// Our encoder uses greedy hash-chain LZ77 match finding so its output is not
/// byte-for-byte identical to UPX's optimal-parsing reference encoder, but the
/// decoder accepts any valid NRV2D LE32 stream including UPX's own output.
/// </para>
/// </remarks>
public sealed class Nrv2dBuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
  public string Id => "BB_Nrv2d";
  /// <inheritdoc/>
  public string DisplayName => "NRV2D";
  /// <inheritdoc/>
  public string Description => "UCL NRV2D LE32 — LZ77 + interleaved variable-length integer bit stream (UPX core, method 3)";
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
  /// Compresses <paramref name="data"/> as a bare NRV2D stream (no 4-byte size
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
    var enc = new Nrv2dEncoder(output, refillWidthBytes);

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

      // NRV2D length encoding is gap-free. Min emitted length is 2 for
      // small offsets and 3 for offsets > 0x500. Reject too-short matches
      // for far offsets so we don't try to encode an unrepresentable length.
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
    if (data.Length < 4) throw new InvalidDataException("NRV2D: input smaller than 4-byte header.");
    var targetSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (targetSize < 0) throw new InvalidDataException("NRV2D: negative decompressed size.");
    if (targetSize == 0) return [];
    return DecompressCore(data[4..], targetSize);
  }

  /// <summary>
  /// Decompresses a bare NRV2D LE32 stream (no 4-byte size prefix) into a
  /// freshly-allocated buffer of exactly <paramref name="exactOutputSize"/> bytes.
  /// Exposed for callers parsing UPX binaries or other embedded NRV2D streams.
  /// </summary>
  public static byte[] DecompressRaw(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, refillWidthBytes: 4);

  /// <summary>
  /// Decompresses a bare NRV2D LE16 stream (no 4-byte size prefix). UPX
  /// compression method 5 uses this width — bits are packed into 16-bit
  /// little-endian words and consumed MSB-first.
  /// </summary>
  public static byte[] DecompressRawLe16(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, refillWidthBytes: 2);

  /// <summary>
  /// Decompresses a bare NRV2D 8-bit stream (no 4-byte size prefix). UPX
  /// compression method 7 uses this width — bits are packed into single bytes
  /// and consumed MSB-first.
  /// </summary>
  public static byte[] DecompressRawByte(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, refillWidthBytes: 1);

  private static byte[] DecompressCore(ReadOnlySpan<byte> compressed, int targetSize, int refillWidthBytes = 4) {
    var output = new byte[targetSize];
    var reader = new Nrv2dDecoder(compressed, refillWidthBytes);
    uint lastMatchOffset = 1; // UCL initialises last_m_off = 1 (matches reference).
    var op = 0;

    while (op < output.Length) {
      while (reader.ReadBit() == 1) {
        output[op++] = reader.ReadByte();
        if (op >= output.Length) return output;
      }

      // NRV2D offset varint: reads (data, continue, [extra-data]) per iteration.
      uint mOff = 1;
      while (true) {
        mOff = (mOff << 1) | reader.ReadBit();
        if (mOff > 0xFFFFFFu + 3) throw new InvalidDataException("NRV2D: lookbehind overrun.");
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
        // Low bit of raw becomes m_len's first bit (inverted).
        mLen = (raw ^ 0xFFFFFFFFu) & 1;
        raw >>= 1;
        finalOff = raw + 1;
        lastMatchOffset = finalOff;
      }

      // Read the length's second bit and combine with the initial bit.
      mLen = (mLen << 1) | reader.ReadBit();
      if (mLen == 0) {
        // NRV2B-style varint inflates m_len ≥ 4.
        mLen = 1;
        do {
          mLen = (mLen << 1) | reader.ReadBit();
        } while (reader.ReadBit() == 0);
        mLen += 2;
      }

      if (finalOff > OffsetLargeThreshold) mLen++;

      if (finalOff > (uint)op) throw new InvalidDataException("NRV2D: offset points before start of output.");
      var src = op - (int)finalOff;
      // UCL emits 1 + m_len bytes (one outside the do-while, then m_len more).
      var totalToEmit = (int)mLen + 1;
      for (var i = 0; i < totalToEmit && op < output.Length; i++)
        output[op++] = output[src + i];
    }

    return output;
  }

  private static int Hash(ReadOnlySpan<byte> d, int pos)
    => ((d[pos] << 8) ^ (d[pos + 1] << 4) ^ d[pos + 2]) & 0xFFFF;

  // ── Encoder ──────────────────────────────────────────────────────────────
  //
  // Critical invariant: a literal/offset byte must occupy the file position the
  // decoder will be at when it calls ReadByte. The decoder reads the offset
  // byte AFTER the offset varint's final break bit. To keep the byte's epoch
  // matched to the bit-word containing that break bit, the encoder queues the
  // byte BEFORE writing the break bit (so any triggered FlushWord includes it).

  private sealed class Nrv2dEncoder {

    private readonly Stream _output;
    private readonly int _widthBytes;
    private readonly int _widthBits;
    private readonly List<byte> _pendingBytes = [];
    private uint _bitWord;
    private int _bitsUsed;

    public Nrv2dEncoder(Stream output) : this(output, 4) { }

    public Nrv2dEncoder(Stream output, int widthBytes) {
      if (widthBytes is not (1 or 2 or 4))
        throw new ArgumentOutOfRangeException(nameof(widthBytes), "NRV2D: bit-word width must be 1, 2, or 4 bytes.");
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

      // Compute final m_len (after the offset-bump compensation).
      var bumpedLen = length - 1; // UCL emits (m_len + 1) bytes, so m_len = length - 1 base.
      if (offset > OffsetLargeThreshold) bumpedLen--; // The decoder bumps m_len by 1 for far offsets.
      if (bumpedLen < 1) throw new InvalidOperationException("NRV2D: match too short to encode.");
      var mLen = (uint)bumpedLen;

      // Decide the two leading bits of m_len encoding.
      // m_len=1 → bits 01, m_len=2 → 10, m_len=3 → 11, m_len≥4 → 00 + varint(m_len-2).
      uint mLenInitial, mLenSecond;
      bool useVarint;
      uint varintValue = 0;
      switch (mLen) {
        case 1:
          mLenInitial = 0; mLenSecond = 1; useVarint = false;
          break;
        case 2:
          mLenInitial = 1; mLenSecond = 0; useVarint = false;
          break;
        case 3:
          mLenInitial = 1; mLenSecond = 1; useVarint = false;
          break;
        default:
          mLenInitial = 0; mLenSecond = 0; useVarint = true;
          varintValue = mLen - 2; // varint produces V ≥ 2 → m_len = V + 2 ≥ 4.
          break;
      }

      if (reuseLast) {
        // Offset varint emits value 2 (bits: A=0, B=1).
        this.WriteBit(0); // A
        this.WriteBit(1); // B (break)
        // Reuse path reads m_len_initial as a fresh bit then proceeds.
        this.WriteBit((int)mLenInitial);
      } else {
        // Non-reuse: pre-shift raw = (offset-1)*2 + (1 - mLenInitial).
        var rawPre = ((offset - 1) << 1) | (1u - mLenInitial);
        var byteVal = (byte)(rawPre & 0xFF);
        var varintForOff = (rawPre >> 8) + 3; // What the decoder sees as `m_off` after the loop.
        this.EmitOffsetVarint(varintForOff, byteVal);
      }

      // Common tail: m_len second bit, then optional NRV2B-style varint.
      this.WriteBit((int)mLenSecond);
      if (useVarint) this.WriteVarInt(varintValue);
    }

    /// <summary>
    /// Emits the NRV2D offset-varint bit pattern that decodes to <paramref name="targetVarintValue"/>,
    /// queuing <paramref name="offsetByte"/> immediately before the final break bit so that the
    /// byte's word-epoch matches the bit-word containing the break bit (the bit AFTER which the
    /// decoder calls ReadByte for the offset byte).
    /// </summary>
    private void EmitOffsetVarint(uint targetVarintValue, byte offsetByte) {
      if (targetVarintValue < 2)
        throw new ArgumentOutOfRangeException(nameof(targetVarintValue), "NRV2D offset varint requires ≥ 2.");

      // Walk back from target to recover the per-iteration bit triples.
      // Iter k (break): A_k = T & 1, B_k = 1, m_off_pre_iter_k = T >> 1.
      // Iter (k-1) no-break: C_{k-1} = m_off_pre_iter_k & 1,
      //   m_off_iter_kminus1_after_A = (m_off_pre_iter_k >> 1) + 1,
      //   A_{k-1} = m_off_iter_kminus1_after_A & 1, B_{k-1} = 0,
      //   m_off_pre_iter_kminus1 = m_off_iter_kminus1_after_A >> 1.
      // Stop when m_off_pre_iter_1 = 1.
      // Within an iteration the emit order is (A, B [, C if not breaking]).
      // Iterations are emitted iter 1 first, then iter 2, ..., iter k.

      // Collect iterations in reverse-discovery order, then reverse to emit order.
      var iterations = new List<(int A, int B, int? C)> {
        ((int)(targetVarintValue & 1), 1, null) // iter k (break)
      };
      var mOffPre = targetVarintValue >> 1;
      while (mOffPre > 1) {
        var c = (int)(mOffPre & 1);
        var mOffAfterA = (mOffPre >> 1) + 1;
        var a = (int)(mOffAfterA & 1);
        iterations.Add((a, 0, c)); // earlier iteration (no-break)
        mOffPre = mOffAfterA >> 1;
      }
      if (mOffPre != 1)
        throw new InvalidOperationException("NRV2D varint encoder: failed to walk back to initial m_off=1.");

      iterations.Reverse(); // now iter 1 .. iter k

      // Flatten to a flat bit list to make the "emit all except final break bit" trivial.
      var bits = new List<int>(iterations.Count * 3);
      foreach (var (a, b, c) in iterations) {
        bits.Add(a);
        bits.Add(b);
        if (c.HasValue) bits.Add(c.Value);
      }

      // Emit all bits except the final break bit (B_k), queue the byte, then emit the break bit.
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

    /// <summary>
    /// NRV2B-style varint: for value V ≥ 2, emit (data, continue=0) pairs from MSB-1 down to bit 0,
    /// with the trailing continue bit set to 1 to terminate.
    /// </summary>
    private void WriteVarInt(uint value) {
      if (value < 2) throw new ArgumentOutOfRangeException(nameof(value), "NRV2D varint requires ≥ 2.");
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

  private ref struct Nrv2dDecoder {
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _widthBytes;
    private readonly int _widthBits;
    private int _pos;
    private uint _bitWord;
    private int _bitsLeft;

    public Nrv2dDecoder(ReadOnlySpan<byte> data, int widthBytes) {
      if (widthBytes is not (1 or 2 or 4))
        throw new ArgumentOutOfRangeException(nameof(widthBytes), "NRV2D: refill width must be 1, 2, or 4 bytes.");
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
        throw new InvalidDataException("NRV2D: unexpected end of byte stream.");
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
