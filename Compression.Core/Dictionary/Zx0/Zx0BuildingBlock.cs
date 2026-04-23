using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Zx0;

/// <summary>
/// ZX0 — Einar Saukas's LZ77-family compressor for the ZX Spectrum and modern
/// demoscene productions. Encodes literal runs and back-references with an
/// interlaced Elias-gamma bit stream packed alongside raw literal/offset bytes.
/// </summary>
/// <remarks>
/// <para>
/// Reference implementation:
/// <c>https://raw.githubusercontent.com/einar-saukas/ZX0/main/src/compress.c</c>
/// (BSD-3-clause). This port is spec-faithful to the ZX0 v2 forward, non-inverted
/// stream — the most common form compatible with Saukas's Z80 decoder. The
/// encoder is greedy, not optimal (ZX0's optimizer is an O(n·max_offset) shortest
/// path search we don't replicate), so our output won't match <c>zx0</c>'s tool
/// byte-for-byte; the decoder is fully spec-compliant and accepts any valid ZX0
/// v2 stream.
/// </para>
/// <para>
/// On-disk layout after the 4-byte little-endian original-size prefix:
/// </para>
/// <list type="bullet">
///   <item>Commands alternate implicitly — the stream always starts with a literal
///         block; after literals the next indicator bit distinguishes rep-match
///         from new-offset-match; after a match the next indicator bit
///         distinguishes literals from a new-offset match.</item>
///   <item>Literal block: <c>elias(length)</c> followed by <c>length</c> raw
///         literal bytes. First block has the indicator bit suppressed (the
///         decoder knows command #0 is literals).</item>
///   <item>Rep-match (reuse last offset): indicator <c>0</c> + <c>elias(length)</c>.</item>
///   <item>New-offset match: indicator <c>1</c> + <c>elias((offset-1)/128+1)</c>
///         (offset high nibble, 1-based) + 1 raw byte carrying
///         <c>(127-((offset-1)&amp;127))&lt;&lt;1</c> in bits 7..1 of the LSB byte and the
///         <b>first bit</b> of the subsequent <c>elias(length-1)</c> in bit 0.</item>
///   <item>End-of-stream: a new-offset match with high-nibble value 256
///         (encoded as Elias-gamma of 256, which overflows the 1..255 range).</item>
/// </list>
/// <para>
/// The "backtrack" mechanism in the reference encoder re-uses bit 0 of the
/// offset LSB byte to carry the first bit of the length Elias-gamma so the
/// decoder can read both in a single fetch. Our port preserves this by
/// reserving one pending bit after the offset byte and patching it into the
/// byte's low bit before any subsequent flag byte is allocated.
/// </para>
/// <para>
/// Elias-gamma encoding (interlaced, non-inverted, forward): emit
/// <c>msb_pos</c> pairs of <c>(control=0, data_bit)</c>, then a final
/// <c>control=1</c> terminator. For <c>value = 1</c> the only bit emitted is
/// the terminator <c>1</c>.
/// </para>
/// </remarks>
public sealed class Zx0BuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
  public string Id => "BB_Zx0";

  /// <inheritdoc/>
  public string DisplayName => "ZX0";

  /// <inheritdoc/>
  public string Description => "Einar Saukas's ZX0 — LZ77 with Elias-gamma coded offsets, used in modern ZX Spectrum + demoscene productions";

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  // ZX0 v2 forward, non-inverted. Salvador uses invert=true; see SalvadorBuildingBlock.
  private const bool InvertMode = false;
  private const int InitialOffset = 1;
  private const int MaxOffset = 0xFFFFFF;  // ZX0's offset field is unbounded in principle; cap for search sanity.
  private const int MinMatchLength = 2;

  /// <summary>Compress <paramref name="data"/> with a 4-byte little-endian original-size prefix followed by a bare ZX0 stream.</summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0) return ms.ToArray();

    var body = CompressBare(data);
    ms.Write(body);
    return ms.ToArray();
  }

  /// <summary>Decompress a 4-byte-prefixed ZX0 payload.</summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4) throw new InvalidDataException("ZX0: input smaller than 4-byte header.");
    var targetSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (targetSize < 0) throw new InvalidDataException("ZX0: negative decompressed size.");
    if (targetSize == 0) return [];
    return DecompressCore(data[4..], targetSize, InvertMode);
  }

  /// <summary>
  /// Decompresses a bare ZX0 stream (no 4-byte size prefix) into a freshly-allocated
  /// buffer of exactly <paramref name="exactOutputSize"/> bytes. Exposed for callers
  /// parsing ZX0-wrapped binaries (crunched ZX Spectrum TAPs, demoscene 4K/64K intros).
  /// </summary>
  public static byte[] DecompressRaw(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    DecompressCore(compressed, exactOutputSize, InvertMode);

  // ── Bare encoder ──────────────────────────────────────────────────────────

  internal static byte[] CompressBare(ReadOnlySpan<byte> data) => CompressBare(data, InvertMode);

  internal static byte[] CompressBare(ReadOnlySpan<byte> data, bool invertMode) {
    var enc = new Zx0Encoder(invertMode);

    // Greedy LZ with hash chain match finder. Rejects matches shorter than
    // MinMatchLength or with offset > MaxOffset, and clamps offset to pos.
    const int HashBits = 16;
    var head = new int[1 << HashBits];
    var prev = data.Length > 0 ? new int[data.Length] : [];
    Array.Fill(head, -1);

    var pos = 0;
    var literalStart = 0;
    var lastOffset = InitialOffset;

    while (pos < data.Length) {
      var bestLen = 0;
      var bestOff = 0;

      if (pos + MinMatchLength <= data.Length) {
        var h = Hash(data, pos);
        var chainLen = 0;
        var minPos = Math.Max(0, pos - MaxOffset);
        var idx = head[h];
        while (idx >= minPos && chainLen < 64) {
          var off = pos - idx;
          if (off >= 1 && off <= MaxOffset && data[idx] == data[pos]) {
            var maxLen = Math.Min(data.Length - pos, 0x10000);
            var len = 0;
            while (len < maxLen && data[idx + len] == data[pos + len]) len++;
            if (len >= MinMatchLength && len > bestLen) {
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

      // Rep-match opportunity: try the last offset — if it gives ≥ MinMatchLength
      // and at least matches the best new-offset match, prefer it (cheaper encoding).
      var repLen = 0;
      if (pos >= lastOffset && lastOffset >= 1) {
        var maxRep = Math.Min(data.Length - pos, 0x10000);
        while (repLen < maxRep && data[pos - lastOffset + repLen] == data[pos + repLen]) repLen++;
      }

      if (repLen >= MinMatchLength && repLen >= bestLen) {
        if (pos > literalStart) {
          enc.EmitLiterals(data, literalStart, pos - literalStart);
          literalStart = pos;
        }
        enc.EmitRepMatch(repLen);
        // Insert into hash for trailing positions so forward hashes stay accurate.
        for (var j = 1; j < repLen && pos + j + MinMatchLength <= data.Length; j++) {
          var h = Hash(data, pos + j);
          prev[pos + j] = head[h];
          head[h] = pos + j;
        }
        pos += repLen;
        literalStart = pos;
      } else if (bestLen >= MinMatchLength) {
        if (pos > literalStart) {
          enc.EmitLiterals(data, literalStart, pos - literalStart);
          literalStart = pos;
        }
        enc.EmitNewOffsetMatch((uint)bestOff, bestLen);
        lastOffset = bestOff;
        for (var j = 1; j < bestLen && pos + j + MinMatchLength <= data.Length; j++) {
          var h = Hash(data, pos + j);
          prev[pos + j] = head[h];
          head[h] = pos + j;
        }
        pos += bestLen;
        literalStart = pos;
      } else {
        pos++;
      }
    }

    if (pos > literalStart) enc.EmitLiterals(data, literalStart, pos - literalStart);
    enc.EmitEnd();
    return enc.ToArray();
  }

  private static int Hash(ReadOnlySpan<byte> d, int pos) {
    if (pos + 1 >= d.Length) return d[pos] & 0xFFFF;
    return ((d[pos] << 8) ^ (d[pos + 1] << 4) ^ (pos + 2 < d.Length ? d[pos + 2] : 0)) & 0xFFFF;
  }

  // ── Decoder (shared by Zx0 + Salvador via invertMode toggle) ──────────────

  internal static byte[] DecompressCore(ReadOnlySpan<byte> compressed, int targetSize, bool invertMode) {
    var output = new byte[targetSize];
    var dec = new Zx0Decoder(compressed);
    var op = 0;
    var lastOffset = InitialOffset;
    var isFirstCommand = true;

    while (op < output.Length) {
      bool isMatchWithOffset;
      if (isFirstCommand) {
        isFirstCommand = false;
        isMatchWithOffset = false;  // first command is always literals.
      } else {
        isMatchWithOffset = dec.ReadBit() != 0;
      }

      if (!isMatchWithOffset) {
        // Literal run.
        var nLiterals = dec.ReadElias(1, invertMode: false);
        for (var i = 0; i < nLiterals; i++) {
          if (op >= output.Length) throw new InvalidDataException("ZX0: literal run exceeds output size.");
          output[op++] = dec.ReadByte();
        }
        if (op >= output.Length) return output;

        // After literals, read match/rep-match bit.
        isMatchWithOffset = dec.ReadBit() != 0;
      }

      int matchLen;
      if (isMatchWithOffset) {
        // New-offset match.
        var hi = dec.ReadElias(1, invertMode);
        if (hi == 256) break;  // end marker.
        hi--;  // 0-based MSB.

        var lo = dec.ReadByte();
        var offset = (hi << 7) | (127 - (lo >> 1));
        offset++;
        if (offset <= 0) throw new InvalidDataException("ZX0: non-positive offset.");

        // Length Elias-gamma starts with lo&1 as its prefix bit.
        matchLen = dec.ReadEliasPrefix(1, invertMode: false, firstBit: (uint)(lo & 1));
        matchLen += 1;  // ref: nMatchLen += (2-1)

        lastOffset = offset;
      } else {
        // Rep-match.
        matchLen = dec.ReadElias(1, invertMode: false);
      }

      if ((uint)lastOffset > (uint)op) throw new InvalidDataException("ZX0: offset points before start of output.");
      var src = op - lastOffset;
      for (var i = 0; i < matchLen && op < output.Length; i++) output[op++] = output[src + i];
    }

    return output;
  }

  // ── Encoder ───────────────────────────────────────────────────────────────
  //
  // Bit-stream invariants (matching the reference):
  //  • Flag bytes hold up to 8 indicator/elias bits MSB-first. A new flag byte
  //    is allocated when the mask rolls to 0, taking the next position in the
  //    output stream.
  //  • Literal and offset-LSB bytes are written inline at the current position.
  //  • The very first indicator bit of the stream is implicit (not written) —
  //    the decoder assumes command #0 is literals. We model this by starting
  //    with a pending "backtrack" target pointing at an unused byte slot.
  //  • After emitting a new-offset match's LSB byte, the first bit of the
  //    subsequent length Elias-gamma is deposited into bit 0 of that same LSB
  //    byte (all other bits of the LSB byte are <<1 shifted so bit 0 is free).
  //    The reference implements this with a post-write "backtrack" one-byte
  //    look-behind; we queue a pending bit and patch the previous byte.

  private sealed class Zx0Encoder {
    private readonly List<byte> _out = [];
    private readonly bool _invertMode;
    private int _bitMask;           // 0 ⇒ no active flag byte; else 128/64/...
    private int _bitIndex;          // index of the active flag byte in _out.
    private bool _backtrack;        // next bit should patch bit 0 of _out[^1] instead of allocating a flag byte.

    public Zx0Encoder(bool invertMode) {
      this._invertMode = invertMode;
      // Matches reference: `backtrack = TRUE` at init causes the first WriteBit
      // to patch the "previous" byte's LSB — but since no byte exists yet, that
      // bit is simply lost. The decoder's `nIsFirstCommand` recovers it
      // (always 0 = literal). Since our first command is always a literal,
      // this produces a spec-compliant first indicator.
      this._backtrack = true;
    }

    public byte[] ToArray() => [.. this._out];

    public void EmitLiterals(ReadOnlySpan<byte> data, int start, int length) {
      if (length <= 0) return;
      this.WriteBit(0);                               // literal indicator
      this.WriteInterlacedEliasGamma(length, false);  // literal count (non-inverted)
      for (var i = 0; i < length; i++) this.WriteByte(data[start + i]);
    }

    public void EmitRepMatch(int length) {
      this.WriteBit(0);                               // rep-match indicator (follows literals)
      this.WriteInterlacedEliasGamma(length, false);
    }

    public void EmitNewOffsetMatch(uint offset, int length) {
      this.WriteBit(1);                               // new-offset indicator
      this.WriteInterlacedEliasGamma((int)((offset - 1) / 128 + 1), this._invertMode);
      // LSB byte: bit 0 reserved for the length Elias-gamma's first bit (patched by backtrack).
      this.WriteByte((byte)((127 - (offset - 1) % 128) << 1));
      this._backtrack = true;
      this.WriteInterlacedEliasGamma(length - 1, false);
    }

    public void EmitEnd() {
      this.WriteBit(1);
      this.WriteInterlacedEliasGamma(256, this._invertMode);
    }

    private void WriteBit(int value) {
      if (this._backtrack) {
        if (value != 0) this._out[^1] |= 1;
        this._backtrack = false;
        return;
      }
      if (this._bitMask == 0) {
        this._bitMask = 128;
        this._bitIndex = this._out.Count;
        this._out.Add(0);
      }
      if (value != 0) this._out[this._bitIndex] |= (byte)this._bitMask;
      this._bitMask >>= 1;
    }

    private void WriteByte(byte value) => this._out.Add(value);

    private void WriteInterlacedEliasGamma(int value, bool invertMode) {
      // Find MSB position: i = largest power of 2 ≤ value.
      var i = 2;
      while (i <= value) i <<= 1;
      i >>= 1;
      // Emit (control=0, data_bit) pairs for each lower bit.
      while ((i >>= 1) != 0) {
        this.WriteBit(0);  // forward mode: control=0 means "more bits coming".
        var dataBit = (value & i) != 0 ? 1 : 0;
        this.WriteBit(invertMode ? 1 - dataBit : dataBit);
      }
      // Terminator.
      this.WriteBit(1);
    }
  }

  // ── Decoder (ref struct, span-based) ──────────────────────────────────────

  private ref struct Zx0Decoder {
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private byte _bits;
    private int _bitMask;  // 0 when no bits buffered; else 128,64,... for the next bit.

    public Zx0Decoder(ReadOnlySpan<byte> data) {
      this._data = data;
      this._pos = 0;
      this._bits = 0;
      this._bitMask = 0;
    }

    public int ReadBit() {
      if (this._bitMask == 0) {
        if (this._pos >= this._data.Length) throw new InvalidDataException("ZX0: unexpected end of bit stream.");
        this._bits = this._data[this._pos++];
        this._bitMask = 128;
      }
      var bit = (this._bits & 128) != 0 ? 1 : 0;
      this._bits <<= 1;
      this._bitMask >>= 1;
      return bit;
    }

    public byte ReadByte() {
      if (this._pos >= this._data.Length) throw new InvalidDataException("ZX0: unexpected end of byte stream.");
      return this._data[this._pos++];
    }

    public int ReadElias(int initial, bool invertMode) {
      var value = initial;
      while (this.ReadBit() == 0) {
        var dataBit = this.ReadBit();
        if (invertMode) dataBit ^= 1;
        value = (value << 1) | dataBit;
      }
      return value;
    }

    /// <summary>Elias-gamma read where the caller supplies the first control bit (usually bit 0 of the offset LSB byte).</summary>
    public int ReadEliasPrefix(int initial, bool invertMode, uint firstBit) {
      var value = initial;
      // If firstBit == 1 (terminator), value stays at initial.
      // If firstBit == 0, read data bit and continue as normal.
      if (firstBit == 0) {
        var dataBit = this.ReadBit();
        if (invertMode) dataBit ^= 1;
        value = (value << 1) | dataBit;
        while (this.ReadBit() == 0) {
          dataBit = this.ReadBit();
          if (invertMode) dataBit ^= 1;
          value = (value << 1) | dataBit;
        }
      }
      return value;
    }
  }
}
