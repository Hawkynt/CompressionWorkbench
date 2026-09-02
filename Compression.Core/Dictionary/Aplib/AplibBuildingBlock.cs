using System.Buffers.Binary;
using System.Numerics;
using Compression.Registry;

namespace Compression.Core.Dictionary.Aplib;

/// <summary>
/// Which aPLib bit-stream dialect a decoder should expect.
/// </summary>
public enum AplibDialect {
  /// <summary>
  /// Ibsen's aPLib as documented: the "10" token's γ-coded offset is biased by the
  /// last-was-match flag (−3 after a literal, −2 after a match) and the previous
  /// offset is reused only when the flag is clear and the γ value is exactly 2.
  /// </summary>
  Standard = 0,

  /// <summary>
  /// The simplified dialect emitted by packers whose in-stub depacker never tracks
  /// last-was-match: the γ offset is always biased by −3 and the reuse case always
  /// triggers at γ = 2. Streams differ from <see cref="Standard"/> the moment a
  /// normal match directly follows another match, so the two are not interchangeable.
  /// Observed in JDPack 1.x stubs (bit layout read off the packed samples' own
  /// depacker; see <c>JdpackExecutablePackerHandler</c>).
  /// </summary>
  NoLastWasMatch = 1,
}

/// <summary>
/// aPLib — Jørgen Ibsen's byte-oriented LZ77 with an interleaved single-bit tag
/// stream, used as the compression core of numerous Win32 PE packers
/// (FSG 2.0, PECompact 2, RLPack, and others; ASPack is commonly listed here
/// too but uses a Huffman-coded stream of its own).
/// </summary>
/// <remarks>
/// <para>
/// The decoder is a clean-room port of the public <c>aP_depack</c> algorithm
/// (aPLib "safe" depacker), reconstructed from the documented bit-stream layout
/// rather than any Ibsen source. Tag bits are consumed MSB-first, eight per tag
/// byte, and the tag byte is read lazily from the single interleaved source
/// cursor the moment the decoder runs out of buffered bits; literal bytes and
/// match low-order offset bytes are read from that same cursor as they occur.
/// </para>
/// <para>
/// Token grammar (each leading path is a tag-bit prefix):
/// </para>
/// <list type="bullet">
///   <item><c>0</c> — literal: copy one verbatim byte; clears "last-was-match".</item>
///   <item><c>10</c> — normal match: γ-coded offset high part plus one inlined
///     low byte, γ-coded length with offset-dependent bumps
///     (<c>+1</c> at offset ≥ 1280, another <c>+1</c> at ≥ 32000, <c>+2</c> at
///     offset &lt; 128). When "last-was-match" is clear and the γ offset decodes
///     to 2, the previous offset is reused with a fresh γ length.</item>
///   <item><c>110</c> — short match: a single inlined byte encodes a 7-bit offset
///     (<c>byte &gt;&gt; 1</c>) and a length of <c>2 + (byte &amp; 1)</c>; a zero
///     offset is the end-of-stream marker.</item>
///   <item><c>111</c> — single byte: a 4-bit offset copies one byte, or offset 0
///     emits a literal <c>0x00</c>.</item>
/// </list>
/// <para>
/// The interlaced Elias-γ helper reconstructs values ≥ 2: <c>v = 1</c>, then
/// repeatedly <c>v = v*2 + dataBit</c> while the following continue bit is set.
/// </para>
/// <para>
/// Our encoder is a spec-faithful greedy LZ that emits literals, normal matches,
/// and the end marker; it produces valid aPLib streams our own decoder (and the
/// reference depacker) round-trips, but it does not replicate aPLib's optimal
/// parser and is not byte-identical to the reference packer. The decoder accepts
/// any valid aPLib stream, including output from real packers.
/// </para>
/// </remarks>
public sealed class AplibBuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Aplib";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "aPLib";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "aPLib (Ibsen) — byte-oriented LZ77 with interleaved tag-bit stream, the core of FSG/PECompact/RLPack";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MinNormalMatch = 2;
  private const int MaxChain = 64;
  private const int MaxMatch = 0x10000;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
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

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4) throw new InvalidDataException("aPLib: input smaller than 4-byte header.");
    var targetSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (targetSize < 0) throw new InvalidDataException("aPLib: negative decompressed size.");
    if (targetSize == 0) return [];
    return DecompressRaw(data[4..], targetSize);
  }

  /// <summary>
  /// Decodes a bare aPLib stream (no size prefix) into at most
  /// <paramref name="maxOutputSize"/> bytes, stopping at the end-of-stream marker.
  /// Exposed for packer handlers that carve an aPLib payload out of a PE/ELF image
  /// and know the original size (or an upper bound) from the packer header.
  /// </summary>
  public static byte[] DecompressRaw(ReadOnlySpan<byte> compressed, int maxOutputSize) =>
    DecompressRaw(compressed, maxOutputSize, out _, out _);

  /// <summary>
  /// As <see cref="DecompressRaw(ReadOnlySpan{byte},int)"/>, additionally reporting
  /// whether decoding stopped at a genuine end-of-stream marker
  /// (<paramref name="endMarkerHit"/>) versus running into the
  /// <paramref name="maxOutputSize"/> cap, and how many input bytes were consumed
  /// (<paramref name="inputConsumed"/>). Packer handlers that carve a payload at a
  /// guessed offset use the end-marker flag to reject false positives: a bare
  /// aPLib stream that terminates cleanly and consumes most of its input is far
  /// more likely to be a real payload than random section bytes that happen to
  /// decode without throwing.
  /// </summary>
  public static byte[] DecompressRaw(ReadOnlySpan<byte> compressed, int maxOutputSize, out bool endMarkerHit, out int inputConsumed) =>
    DecompressRaw(compressed, maxOutputSize, AplibDialect.Standard, out endMarkerHit, out inputConsumed);

  /// <summary>
  /// As <see cref="DecompressRaw(ReadOnlySpan{byte},int,out bool,out int)"/>, decoding the
  /// requested <paramref name="dialect"/>. Packers that embed a hand-written aPLib depacker
  /// sometimes ship a simplified one; see <see cref="AplibDialect"/>.
  /// </summary>
  public static byte[] DecompressRaw(ReadOnlySpan<byte> compressed, int maxOutputSize, AplibDialect dialect, out bool endMarkerHit, out int inputConsumed) {
    if (maxOutputSize < 0) throw new ArgumentOutOfRangeException(nameof(maxOutputSize));
    var trackLastWasMatch = dialect == AplibDialect.Standard;
    endMarkerHit = false;
    inputConsumed = 0;
    if (compressed.Length == 0 || maxOutputSize == 0) return [];

    // The output grows on demand rather than being allocated at maxOutputSize up
    // front. Packer handlers probe candidate payload offsets with a deliberately
    // loose bound (a section length times the maximum aPLib expansion ratio), and
    // a stream that is not aPLib at all aborts within a few tokens — so eagerly
    // allocating that bound turned every rejected offset into a multi-megabyte
    // zeroing, which dominated the cost of a payload scan.
    var output = new byte[Math.Min(maxOutputSize, InitialOutputCapacity)];
    var reader = new AplibReader(compressed);
    var op = 0;

    // aPLib copies the first byte verbatim before the token loop starts.
    output[op++] = reader.ReadByte();
    var lwm = 0;
    var r0 = 0;

    while (op < maxOutputSize) {
      if (reader.ReadBit() == 0) {
        // Literal.
        Grow(ref output, op + 1, maxOutputSize);
        output[op++] = reader.ReadByte();
        lwm = 0;
        continue;
      }

      if (reader.ReadBit() == 0) {
        // "10" — normal match.
        var offs = (int)reader.ReadGamma();
        int len;
        if (!trackLastWasMatch) lwm = 0;
        if (lwm == 0 && offs == 2) {
          offs = r0;
          len = (int)reader.ReadGamma();
        } else {
          offs -= lwm == 0 ? 3 : 2;
          offs = (offs << 8) + reader.ReadByte();
          len = (int)reader.ReadGamma();
          if (offs >= 32000) len++;
          if (offs >= 1280) len++;
          if (offs < 128) len += 2;
          r0 = offs;
        }
        CopyMatch(ref output, ref op, offs, len, maxOutputSize);
        lwm = 1;
        continue;
      }

      if (reader.ReadBit() == 0) {
        // "110" — short match, or end-of-stream when offset is zero.
        var b = reader.ReadByte();
        if (b == 0) {
          endMarkerHit = true;
          break;
        }
        var len = 2 + (b & 1);
        var offs = b >> 1;
        CopyMatch(ref output, ref op, offs, len, maxOutputSize);
        r0 = offs;
        lwm = 1;
        continue;
      }

      // "111" — 4-bit offset single byte, or literal zero.
      var shortOffs = 0;
      for (var i = 0; i < 4; i++)
        shortOffs = (shortOffs << 1) + (int)reader.ReadBit();
      Grow(ref output, op + 1, maxOutputSize);
      if (shortOffs == 0)
        output[op++] = 0;
      else {
        if (shortOffs > op) throw new InvalidDataException("aPLib: single-byte back-reference before start of output.");
        output[op] = output[op - shortOffs];
        op++;
      }
      lwm = 0;
    }

    inputConsumed = reader.Position;
    return op == output.Length ? output : output[..op];
  }

  /// <summary>Initial output capacity for a decode whose final size is not known up front.</summary>
  private const int InitialOutputCapacity = 4096;

  /// <summary>
  /// Ensures <paramref name="output"/> can hold <paramref name="needed"/> bytes,
  /// doubling its capacity but never growing past <paramref name="max"/>.
  /// </summary>
  private static void Grow(ref byte[] output, int needed, int max) {
    if (needed <= output.Length)
      return;
    var capacity = output.Length;
    while (capacity < needed)
      capacity = capacity >= max / 2 ? max : capacity * 2;
    Array.Resize(ref output, capacity);
  }

  private static void CopyMatch(ref byte[] output, ref int op, int offs, int len, int max) {
    if (offs <= 0 || offs > op) throw new InvalidDataException("aPLib: match offset points before start of output.");
    Grow(ref output, (int)Math.Min((long)op + len, max), max);
    var src = op - offs;
    for (var i = 0; i < len && op < max; i++)
      output[op++] = output[src + i];
  }

  /// <summary>
  /// Compresses <paramref name="data"/> as a bare aPLib stream with no size prefix, in the
  /// requested <paramref name="dialect"/>. The encoder never emits the reuse-previous-offset
  /// token, so the two dialects differ only in the γ offset bias it writes after a match.
  /// </summary>
  internal static byte[] CompressBare(ReadOnlySpan<byte> data, AplibDialect dialect = AplibDialect.Standard) {
    var enc = new AplibWriter();
    if (data.Length == 0) return enc.ToArray();

    // First byte verbatim, matching the depacker's pre-loop copy.
    enc.PutByte(data[0]);

    const int hashBits = 16;
    var head = new int[1 << hashBits];
    var prev = new int[data.Length];
    Array.Fill(head, -1);
    Insert(data, 0, head, prev);

    var lwm = 0;
    var r0 = 0;
    var pos = 1;
    while (pos < data.Length) {
      FindMatch(data, pos, head, prev, out var bestOff, out var bestLen);

      if (bestLen >= MinNormalMatch && TryEncodableLength(bestOff, bestLen, out var encodedLen)) {
        enc.PutBit(1);
        enc.PutBit(0);
        var gammaOff = (uint)((bestOff >> 8) + (lwm == 0 ? 3 : 2));
        enc.PutGamma(gammaOff);
        enc.PutByte((byte)(bestOff & 0xFF));
        enc.PutGamma((uint)encodedLen);
        r0 = bestOff;
        lwm = dialect == AplibDialect.Standard ? 1 : 0;

        var end = pos + bestLen;
        for (var j = pos; j < end && j < data.Length; j++)
          Insert(data, j, head, prev);
        pos = end;
      } else {
        enc.PutBit(0);
        enc.PutByte(data[pos]);
        lwm = 0;
        Insert(data, pos, head, prev);
        pos++;
      }
    }

    // End-of-stream: "110" short match with a zero offset byte.
    enc.PutBit(1);
    enc.PutBit(1);
    enc.PutBit(0);
    enc.PutByte(0);
    _ = r0;
    return enc.ToArray();
  }

  private static void Insert(ReadOnlySpan<byte> data, int pos, int[] head, int[] prev) {
    if (pos + 2 >= data.Length) return;
    var h = Hash(data, pos);
    prev[pos] = head[h];
    head[h] = pos;
  }

  private static void FindMatch(ReadOnlySpan<byte> data, int pos, int[] head, int[] prev, out int bestOff, out int bestLen) {
    bestOff = 0;
    bestLen = 0;
    if (pos + 2 >= data.Length) return;

    var idx = head[Hash(data, pos)];
    var chain = 0;
    var maxLen = Math.Min(data.Length - pos, MaxMatch);
    while (idx >= 0 && chain < MaxChain) {
      var off = pos - idx;
      if (data[idx] == data[pos] && data[idx + bestLen] == data[pos + bestLen]) {
        var len = 0;
        while (len < maxLen && data[idx + len] == data[pos + len]) len++;
        if (len > bestLen) {
          bestLen = len;
          bestOff = off;
          if (len >= maxLen) break;
        }
      }
      idx = prev[idx];
      chain++;
    }
  }

  /// <summary>
  /// aPLib's normal-match length carries decode-time bumps depending on the
  /// offset magnitude; the encoded γ length must be the actual length minus those
  /// bumps and stay ≥ 2 (the γ minimum). Returns false when a match is too short
  /// to encode at the given offset (the caller then emits literals).
  /// </summary>
  private static bool TryEncodableLength(int offset, int length, out int encodedLen) {
    var adjust = (offset >= 32000 ? 1 : 0) + (offset >= 1280 ? 1 : 0) + (offset < 128 ? 2 : 0);
    encodedLen = length - adjust;
    return encodedLen >= 2;
  }

  private static int Hash(ReadOnlySpan<byte> d, int pos)
    => ((d[pos] << 8) ^ (d[pos + 1] << 4) ^ d[pos + 2]) & 0xFFFF;

  // ── Reader (bit-exact aP_depack bit/byte source) ───────────────────────────

  private ref struct AplibReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private uint _tag;
    private int _bitsLeft;

    public AplibReader(ReadOnlySpan<byte> data) {
      this._data = data;
      this._pos = 0;
      this._tag = 0;
      this._bitsLeft = 0;
    }

    public readonly int Position => this._pos;

    public byte ReadByte() {
      if (this._pos >= this._data.Length)
        throw new InvalidDataException("aPLib: unexpected end of stream.");
      return this._data[this._pos++];
    }

    public uint ReadBit() {
      if (this._bitsLeft == 0) {
        this._tag = this.ReadByte();
        this._bitsLeft = 8;
      }
      var bit = (this._tag >> 7) & 1;
      this._tag = (this._tag << 1) & 0xFF;
      this._bitsLeft--;
      return bit;
    }

    public uint ReadGamma() {
      uint result = 1;
      do {
        result = (result << 1) + this.ReadBit();
      } while (this.ReadBit() == 1);
      return result;
    }
  }

  // ── Writer (lazy tag byte, MSB-first, interleaved data bytes) ───────────────

  private sealed class AplibWriter {
    private readonly List<byte> _out = [];
    private int _tagPos = -1;
    private int _bitsInTag;

    public void PutBit(int bit) {
      if (this._bitsInTag == 0) {
        this._tagPos = this._out.Count;
        this._out.Add(0);
      }
      if (bit != 0)
        this._out[this._tagPos] |= (byte)(1 << (7 - this._bitsInTag));
      this._bitsInTag = (this._bitsInTag + 1) & 7;
    }

    public void PutByte(byte value) => this._out.Add(value);

    public void PutGamma(uint value) {
      if (value < 2) throw new ArgumentOutOfRangeException(nameof(value), "aPLib γ requires ≥ 2.");
      var msb = 31 - BitOperations.LeadingZeroCount(value);
      for (var i = msb - 1; i >= 0; i--) {
        this.PutBit((int)((value >> i) & 1));
        this.PutBit(i > 0 ? 1 : 0);
      }
    }

    public byte[] ToArray() => [.. this._out];
  }
}
