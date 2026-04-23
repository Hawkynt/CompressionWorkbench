using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Nrv2b;

/// <summary>
/// NRV2B LE32 — Markus Oberhumer's UCL-family compression used at the core of UPX.
/// LZ77-style back-references with an interleaved variable-length integer encoding
/// for match offsets and lengths over a 32-bit little-endian bit stream.
/// </summary>
/// <remarks>
/// <para>
/// On-disk layout per UCL <c>ucl/src/n2b_d.ch</c>: bits are packed into 32-bit
/// little-endian words and consumed MSB-first; literal and match-offset bytes are
/// inlined into the byte stream between bit-word refills, in the order the
/// decoder consumes them. The decoder cursor advances by 4 on each bit-word
/// refill and by 1 on each literal/offset byte read.
/// </para>
/// <para>
/// Encoding scheme:
/// </para>
/// <list type="bullet">
///   <item>Control bit <c>1</c> ⇒ emit literal (next byte from byte stream); control bit <c>0</c> ⇒ decode a match.</item>
///   <item>Variable-length integer (offsets and lengths): start from 1, repeat <c>v = v*2 + data_bit</c> followed by a continue bit (<c>1</c> = stop, <c>0</c> = keep going). Yields values ≥ 2.</item>
///   <item>Offset varint value <c>2</c> means "reuse last offset"; otherwise <c>final = (value - 3) * 256 + byte + 1</c> where <c>byte</c> is read inline from the byte stream.</item>
///   <item>Length: <c>0</c> ⇒ <c>m_len=1</c>; <c>10</c> ⇒ <c>m_len=2</c>; <c>11</c> ⇒ varint+2 ⇒ <c>m_len ≥ 4</c>. Total bytes copied = <c>m_len + 2</c>; an extra <c>+1</c> is added when offset > <c>0xD00</c>.</item>
/// </list>
/// <para>
/// Emitted match sizes are therefore {3, 4, 6, 7, 8, …} for offsets ≤ 0xD00 and
/// {4, 5, 7, 8, 9, …} otherwise — match sizes 5 / 6 fall in an unrepresentable
/// gap and the encoder snaps any candidate match to the next-lower encodable
/// length.
/// </para>
/// <para>
/// Our encoder is spec-faithful and self-consistent but does not match the
/// reference <c>upx</c> tool byte-for-byte (UPX uses an optimal-parsing match
/// picker we don't replicate). The decoder will accept any valid NRV2B LE32
/// stream including UPX's own output.
/// </para>
/// </remarks>
public sealed class Nrv2bBuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
  public string Id => "BB_Nrv2b";
  /// <inheritdoc/>
  public string DisplayName => "NRV2B";
  /// <inheritdoc/>
  public string Description => "UCL NRV2B LE32 — LZ77 + interleaved variable-length integer bit stream (UPX core)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MinEmittedLen = 3;
  private const int MaxOffset = 0xFFFFFF;
  private const int OffsetLargeThreshold = 0xD00;

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
  /// Compresses <paramref name="data"/> as a bare NRV2B stream (no 4-byte size
  /// prefix) at the supplied bit-word refill width (1, 2, or 4 bytes — for the
  /// 8-bit, LE16, or LE32 stream variants respectively). Test-only: production
  /// callers should use <see cref="Compress"/> for LE32 round-tripping; the
  /// LE16/8-bit widths exist so tests can validate the width-aware decoder
  /// helpers against streams produced by a width-matched encoder.
  /// </summary>
  internal static byte[] CompressBare(ReadOnlySpan<byte> data, int refillWidthBytes) {
    using var ms = new MemoryStream();
    if (data.Length > 0) CompressBareInto(ms, data, refillWidthBytes);
    return ms.ToArray();
  }

  private static void CompressBareInto(Stream output, ReadOnlySpan<byte> data, int refillWidthBytes) {
    var enc = new Nrv2bEncoder(output, refillWidthBytes);

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

      // Large offsets require length ≥ 4 because the decoder's offset-threshold length
      // bump steals one byte from the encoded m_len (length 3 would need encoded m_len=0,
      // which isn't part of the encoding). Reject such short matches with far offsets.
      if (bestLen >= MinEmittedLen && !((uint)bestOff > OffsetLargeThreshold && bestLen < 4)) {
        bestLen = SnapToEncodable(bestLen, bestOff);
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
    if (data.Length < 4) throw new InvalidDataException("NRV2B: input smaller than 4-byte header.");
    var targetSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (targetSize < 0) throw new InvalidDataException("NRV2B: negative decompressed size.");
    if (targetSize == 0) return [];
    return DecompressCore(data[4..], targetSize);
  }

  /// <summary>
  /// Decompresses a bare NRV2B LE32 stream (no 4-byte size prefix) into a
  /// freshly-allocated buffer of exactly <paramref name="exactOutputSize"/> bytes.
  /// Exposed for callers parsing UPX binaries or other embedded NRV2B streams.
  /// </summary>
  public static byte[] DecompressRaw(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, refillWidthBytes: 4);

  /// <summary>
  /// Decompresses a bare NRV2B LE16 stream (no 4-byte size prefix). UPX compression
  /// methods 4 and the LE16 variants use this width — bits are packed into 16-bit
  /// little-endian words and consumed MSB-first; the decoder cursor advances by 2
  /// on each bit-word refill.
  /// </summary>
  public static byte[] DecompressRawLe16(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, refillWidthBytes: 2);

  /// <summary>
  /// Decompresses a bare NRV2B 8-bit stream (no 4-byte size prefix). UPX compression
  /// method 6 uses this width — bits are packed into single bytes and consumed
  /// MSB-first; the decoder cursor advances by 1 on each bit-word refill.
  /// </summary>
  public static byte[] DecompressRawByte(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, refillWidthBytes: 1);

  private static byte[] DecompressCore(ReadOnlySpan<byte> compressed, int targetSize, int refillWidthBytes = 4) {
    var output = new byte[targetSize];
    var reader = new Nrv2bDecoder(compressed, refillWidthBytes);
    uint lastMatchOffset = 0;
    var op = 0;

    while (op < output.Length) {
      while (reader.ReadBit() == 1) {
        output[op++] = reader.ReadByte();
        if (op >= output.Length) return output;
      }

      var mOff = reader.ReadVarInt();

      uint finalOff;
      if (mOff == 2) {
        if (lastMatchOffset == 0)
          throw new InvalidDataException("NRV2B: reuse-last-offset before any match emitted.");
        finalOff = lastMatchOffset;
      } else {
        var b = reader.ReadByte();
        var raw = ((mOff - 3) << 8) | b;
        if (raw == 0xFFFFFFFFu) break;
        finalOff = raw + 1;
        lastMatchOffset = finalOff;
      }

      uint mLen;
      if (reader.ReadBit() == 0) mLen = 1;
      else if (reader.ReadBit() == 0) mLen = 2;
      else mLen = reader.ReadVarInt() + 2;

      if (finalOff > OffsetLargeThreshold) mLen++;

      if (finalOff > (uint)op) throw new InvalidDataException("NRV2B: offset points before start of output.");
      var src = op - (int)finalOff;
      var totalToEmit = (int)mLen + 2;
      for (var i = 0; i < totalToEmit && op < output.Length; i++)
        output[op++] = output[src + i];
    }

    return output;
  }

  private static int Hash(ReadOnlySpan<byte> d, int pos)
    => ((d[pos] << 8) ^ (d[pos + 1] << 4) ^ d[pos + 2]) & 0xFFFF;

  /// <summary>
  /// NRV2B length encoding has a gap at <c>m_len=3</c> (emitted size 5 when offset ≤ 0xD00,
  /// 6 otherwise). Snap any proposed length landing in the gap to the next-lower
  /// encodable length so the encoder never tries to emit an unrepresentable size.
  /// </summary>
  private static int SnapToEncodable(int proposed, int offset) {
    var effective = offset > OffsetLargeThreshold ? proposed - 1 : proposed;
    var mLen = effective - 2;
    if (mLen == 3) return offset > OffsetLargeThreshold ? 5 : 4;
    return proposed;
  }

  // ── Encoder ──────────────────────────────────────────────────────────────
  //
  // Critical invariant: a literal/offset byte must occupy the file position the
  // decoder will be at when it calls ReadByte. The decoder reads ReadByte AFTER
  // consuming the bit that precedes it. Since bits are buffered in 32-bit words,
  // the byte's "epoch" (which word's pending list it belongs to) is determined by
  // which bit-word was active when the preceding bit was consumed.
  //
  // To keep the byte in the right epoch, the encoder must queue the byte BEFORE
  // writing any bits whose flush could roll over to the next epoch — which means
  // the byte goes into the pending list FIRST, then the bits follow.

  private sealed class Nrv2bEncoder {

    private readonly Stream _output;
    private readonly int _widthBytes;
    private readonly int _widthBits;
    private readonly List<byte> _pendingBytes = [];
    private uint _bitWord;
    private int _bitsUsed;

    public Nrv2bEncoder(Stream output) : this(output, 4) { }

    public Nrv2bEncoder(Stream output, int widthBytes) {
      if (widthBytes is not (1 or 2 or 4))
        throw new ArgumentOutOfRangeException(nameof(widthBytes), "NRV2B: bit-word width must be 1, 2, or 4 bytes.");
      this._output = output;
      this._widthBytes = widthBytes;
      this._widthBits = widthBytes * 8;
    }

    public void EmitLiteral(byte value) {
      // Byte BEFORE bit so a triggered FlushWord includes the byte in this epoch.
      this._pendingBytes.Add(value);
      this.WriteBit(1);
    }

    public void EmitMatch(uint offset, int length, bool reuseLast) {
      this.WriteBit(0); // match flag

      if (reuseLast) {
        this.WriteVarInt(2);
      } else {
        // Varint + offset byte: emit all varint bits except the final continue bit,
        // queue the offset byte, then emit the final continue bit. This keeps the
        // byte in the same bit-word epoch as the last varint bit (which is when
        // the decoder calls ReadByte for the offset).
        var adjusted = offset - 1;
        var v = (uint)((adjusted >> 8) + 3);
        this.WriteVarIntExceptFinalContinue(v);
        this._pendingBytes.Add((byte)(adjusted & 0xFF));
        this.WriteBit(1); // final continue bit
      }

      // Length encoding.
      var emitted = length;
      if (offset > OffsetLargeThreshold) emitted--;
      var mLen = emitted - 2;
      switch (mLen) {
        case 1:
          this.WriteBit(0);
          break;
        case 2:
          this.WriteBit(1);
          this.WriteBit(0);
          break;
        default:
          if (mLen < 4) throw new InvalidOperationException("NRV2B: unencodable match length 3 (encoder didn't snap).");
          this.WriteBit(1);
          this.WriteBit(1);
          this.WriteVarInt((uint)(mLen - 2));
          break;
      }
    }

    public void Flush() {
      // Pad any leftover bits up to a full word and flush. If pending bytes are
      // queued they'll be written immediately after the (possibly zero-padded) word.
      if (this._bitsUsed > 0) {
        this._bitWord <<= this._widthBits - this._bitsUsed;
        this.FlushWord();
      } else if (this._pendingBytes.Count > 0) {
        // Defensive — should be unreachable since every queued byte is paired
        // with at least one bit, but keeps the stream in a well-defined state.
        this.FlushWord();
      }
    }

    private void WriteBit(int bit) {
      this._bitWord = (this._bitWord << 1) | (uint)(bit & 1);
      this._bitsUsed++;
      if (this._bitsUsed == this._widthBits) this.FlushWord();
    }

    private void WriteVarInt(uint value) {
      if (value < 2) throw new ArgumentOutOfRangeException(nameof(value), "NRV2B varint requires ≥ 2.");
      var msb = 31 - System.Numerics.BitOperations.LeadingZeroCount(value);
      for (var i = msb - 1; i >= 0; i--) {
        this.WriteBit((int)((value >> i) & 1));
        this.WriteBit(i == 0 ? 1 : 0);
      }
    }

    /// <summary>
    /// Writes all varint bits except the final continue bit. The caller is
    /// responsible for writing the trailing <c>1</c> continue bit after any
    /// pending byte has been queued, so that the byte's epoch lines up with the
    /// word containing that final continue bit (which is the word the decoder
    /// is actively consuming when it calls ReadByte for the offset byte).
    /// </summary>
    private void WriteVarIntExceptFinalContinue(uint value) {
      if (value < 2) throw new ArgumentOutOfRangeException(nameof(value), "NRV2B varint requires ≥ 2.");
      var msb = 31 - System.Numerics.BitOperations.LeadingZeroCount(value);
      // Emit all (data, continue=0) pairs except the final data bit and its trailing continue=1.
      for (var i = msb - 1; i >= 1; i--) {
        this.WriteBit((int)((value >> i) & 1));
        this.WriteBit(0);
      }
      // Final data bit (for i=0); caller writes the continue=1 bit afterwards.
      this.WriteBit((int)(value & 1));
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

  private ref struct Nrv2bDecoder {
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _widthBytes;
    private readonly int _widthBits;
    private int _pos;
    private uint _bitWord;
    private int _bitsLeft;

    public Nrv2bDecoder(ReadOnlySpan<byte> data, int widthBytes) {
      if (widthBytes is not (1 or 2 or 4))
        throw new ArgumentOutOfRangeException(nameof(widthBytes), "NRV2B: refill width must be 1, 2, or 4 bytes.");
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
        throw new InvalidDataException("NRV2B: unexpected end of byte stream.");
      return this._data[this._pos++];
    }

    public uint ReadVarInt() {
      uint v = 1;
      while (true) {
        v = (v << 1) | this.ReadBit();
        if (this.ReadBit() == 1) return v;
      }
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
      // Left-align so the MSB of the word sits at bit 31 — keeps ReadBit's
      // "(_bitWord >> 31) & 1" extraction width-agnostic.
      this._bitWord = raw << (32 - this._widthBits);
      this._bitsLeft = this._widthBits;
    }
  }
}
