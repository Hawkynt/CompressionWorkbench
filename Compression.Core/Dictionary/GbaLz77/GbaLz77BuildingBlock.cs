using Compression.Registry;

namespace Compression.Core.Dictionary.GbaLz77;

/// <summary>
/// Nintendo GBA/NDS BIOS LZ77 — the "type 0x10" LZSS variant decoded by the Game Boy
/// Advance BIOS call SWI 0x11 (<c>LZ77UnCompReadNormalWrite8bit</c>) and reused unchanged
/// by the Nintendo DS BIOS. It is the standard container for compressed assets inside
/// commercial GBA and NDS ROMs.
/// </summary>
/// <remarks>
/// <para>
/// This is unrelated to <see cref="Compression.Core.Dictionary.DsLz77.BB_DsLz77"/>, whose
/// "DS" stands for Microsoft DoubleSpace/DriveSpace.
/// </para>
/// <para>
/// Container layout — the stream carries its own length, so no additional size header is added:
/// </para>
/// <list type="bullet">
///   <item>Byte 0: type identifier, always <c>0x10</c>.</item>
///   <item>Bytes 1..3: decompressed size, 24-bit little-endian (hence a 16 MiB − 1 ceiling).</item>
///   <item>Bytes 4..: the compressed block stream.</item>
/// </list>
/// <para>
/// The block stream is a sequence of groups. Each group opens with one flag byte whose bits
/// are consumed MSB-first, one bit per unit, up to eight units per flag byte:
/// </para>
/// <list type="bullet">
///   <item>Flag bit <c>0</c> ⇒ copy one raw literal byte from the stream.</item>
///   <item>
///     Flag bit <c>1</c> ⇒ copy a back-reference encoded in the next two bytes
///     <c>b0</c>, <c>b1</c>: <c>length = 3 + (b0 &gt;&gt; 4)</c> and
///     <c>distance = 1 + (((b0 &amp; 0x0F) &lt;&lt; 8) | b1)</c>. Lengths therefore span 3..18
///     and distances 1..4096. Copies are byte-by-byte and may overlap, so a distance of 1
///     degenerates into a run-length repeat.
///   </item>
/// </list>
/// <para>
/// Decoding stops as soon as the declared decompressed size has been produced; any unused
/// bits in the final flag byte are ignored.
/// </para>
/// <para>
/// The encoder is a plain greedy parser: at each position it takes the longest match in the
/// 4096-byte window and, among equally long matches, the one furthest back. Matches shorter
/// than three bytes are emitted as literals because a back-reference costs two bytes.
/// </para>
/// <para>
/// References: GBATEK, "LZ Decompression Functions"
/// (<see href="https://problemkaputt.de/gbatek-lz-decompression-functions.htm"/>) and
/// GBATEK, "BIOS Decompression Functions"
/// (<see href="https://problemkaputt.de/gbatek-bios-decompression-functions.htm"/>).
/// </para>
/// </remarks>
public sealed class GbaLz77BuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_GbaLz77";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Nintendo GBA/NDS LZ77 (type 0x10)";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "GBA/NDS BIOS LZSS (SWI 0x11) — flag-byte literals plus 4-bit length / 12-bit distance back-references";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <summary>Header type identifier for the LZ77 variant decoded by SWI 0x11.</summary>
  private const byte TypeId = 0x10;

  /// <summary>Shortest encodable back-reference; anything shorter is cheaper as literals.</summary>
  private const int MinMatch = 3;

  /// <summary>Longest encodable back-reference — the 4-bit length field biased by <see cref="MinMatch"/>.</summary>
  private const int MaxMatch = MinMatch + 0x0F;

  /// <summary>Largest encodable distance — the 12-bit displacement field biased by one.</summary>
  private const int MaxDistance = 1 + 0x0FFF;

  /// <summary>Largest value representable in the 24-bit size field.</summary>
  private const int MaxDecompressedSize = 0xFFFFFF;

  /// <summary>Width of the match-finder hash table, in bits.</summary>
  private const int HashBits = 16;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return [];
    if (data.Length > MaxDecompressedSize)
      throw new ArgumentOutOfRangeException(
        nameof(data),
        $"GBA LZ77: the 24-bit size header cannot describe more than {MaxDecompressedSize} bytes."
      );

    using var output = new MemoryStream();
    output.WriteByte(TypeId);
    output.WriteByte((byte)data.Length);
    output.WriteByte((byte)(data.Length >> 8));
    output.WriteByte((byte)(data.Length >> 16));

    var finder = new MatchFinder(data);
    Span<byte> units = stackalloc byte[16]; // eight units, each at most two bytes

    var position = 0;
    while (position < data.Length) {
      var flags = 0;
      var unitCount = 0;

      for (var slot = 0; slot < 8 && position < data.Length; ++slot) {
        finder.Find(position, out var length, out var distance);

        if (length >= MinMatch) {
          flags |= 1 << (7 - slot);
          var lengthField = length - MinMatch;
          var distanceField = distance - 1;
          units[unitCount++] = (byte)((lengthField << 4) | ((distanceField >> 8) & 0x0F));
          units[unitCount++] = (byte)(distanceField & 0xFF);
          position += length;
        } else {
          units[unitCount++] = data[position];
          ++position;
        }
      }

      output.WriteByte((byte)flags);
      output.Write(units[..unitCount]);
    }

    return output.ToArray();
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return [];
    if (data.Length < 4)
      throw new InvalidDataException("GBA LZ77: input is shorter than the 4-byte header.");
    if (data[0] != TypeId)
      throw new InvalidDataException($"GBA LZ77: unexpected type byte 0x{data[0]:X2}, expected 0x{TypeId:X2}.");

    var size = data[1] | (data[2] << 8) | (data[3] << 16);
    if (size == 0) return [];

    var output = new byte[size];
    var read = 4;
    var written = 0;

    while (written < size) {
      if (read >= data.Length)
        throw new InvalidDataException("GBA LZ77: stream ended before the declared size was produced.");
      var flags = data[read++];

      for (var slot = 0; slot < 8 && written < size; ++slot) {
        var isMatch = ((flags >> (7 - slot)) & 1) != 0;

        if (isMatch) {
          if (read + 1 >= data.Length)
            throw new InvalidDataException("GBA LZ77: truncated back-reference.");
          var b0 = data[read++];
          var b1 = data[read++];
          var length = MinMatch + (b0 >> 4);
          var distance = 1 + (((b0 & 0x0F) << 8) | b1);
          if (distance > written)
            throw new InvalidDataException("GBA LZ77: back-reference points before the start of the output.");

          var source = written - distance;
          // Copy one byte at a time: overlapping copies are part of the format.
          var copy = Math.Min(length, size - written);
          for (var i = 0; i < copy; ++i)
            output[written++] = output[source + i];
        } else {
          if (read >= data.Length)
            throw new InvalidDataException("GBA LZ77: truncated literal.");
          output[written++] = data[read++];
        }
      }
    }

    return output;
  }

  /// <summary>
  /// Greedy longest-match search over the 4096-byte window, resolving ties in favour of the
  /// furthest-back candidate.
  /// </summary>
  /// <remarks>
  /// Candidate positions sharing a three-byte prefix are threaded into per-bucket singly
  /// linked lists in ascending order, so the search visits the window oldest-first and can
  /// stop the moment it reaches the longest encodable length — the first candidate reaching
  /// that length is by construction also the furthest back. Each bucket keeps a cursor that
  /// only ever moves forward as the window slides, which keeps the skipping cost amortised.
  /// </remarks>
  private ref struct MatchFinder {

    private readonly ReadOnlySpan<byte> _data;
    private readonly int[] _cursor;
    private readonly int[] _next;

    public MatchFinder(ReadOnlySpan<byte> data) {
      this._data = data;
      this._cursor = new int[1 << HashBits];
      this._next = new int[data.Length];
      this._cursor.AsSpan().Fill(-1);

      // Walk backwards so each bucket ends up holding its smallest index first.
      for (var i = data.Length - MinMatch; i >= 0; --i) {
        var bucket = Hash(data, i);
        this._next[i] = this._cursor[bucket];
        this._cursor[bucket] = i;
      }
    }

    public void Find(int position, out int length, out int distance) {
      length = 0;
      distance = 0;

      var maxLength = Math.Min(MaxMatch, this._data.Length - position);
      if (maxLength < MinMatch) return;

      var bucket = Hash(this._data, position);
      var windowStart = Math.Max(0, position - MaxDistance);

      var candidate = this._cursor[bucket];
      while (candidate >= 0 && candidate < windowStart) candidate = this._next[candidate];
      this._cursor[bucket] = candidate;

      for (; candidate >= 0 && candidate < position; candidate = this._next[candidate]) {
        var run = 0;
        while (run < maxLength && this._data[candidate + run] == this._data[position + run]) ++run;

        if (run <= length) continue;

        length = run;
        distance = position - candidate;
        if (length == maxLength) break;
      }
    }

    private static int Hash(ReadOnlySpan<byte> data, int position) {
      var key = (uint)((data[position] << 16) | (data[position + 1] << 8) | data[position + 2]);
      return (int)(key * 2654435761u >> (32 - HashBits));
    }
  }
}
