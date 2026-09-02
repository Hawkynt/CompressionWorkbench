using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Shoco;

/// <summary>
/// Exposes a Shoco-style short-string compressor as a benchmarkable building block.
/// Shoco (Christian Schramm / "Ed-von-Schleck", 2014) compresses short ASCII strings
/// by keeping a small alphabet of the most common characters and, for runs of
/// consecutive alphabet characters, encoding each character after the first as the
/// rank of its predecessor's most likely successors rather than the character
/// itself — most natural-language digraphs need only a few bits to identify.
/// </summary>
/// <remarks>
/// <para>
/// This is a clean-room implementation of Shoco's real bit-packing algorithm —
/// the multi-tier "pack" scheme in the reference's <c>shoco.c</c>/
/// <c>shoco_model.h</c> — written from that source's <c>packs[]</c> table and
/// <c>shoco_compress</c>/<c>shoco_decompress</c> logic, not a port of it:
/// </para>
/// <list type="bullet">
///   <item><description>A chain of consecutive "known" characters (the
///     leading character plus, for each character after it, the rank of that
///     character among its predecessor's most likely successors) is packed
///     into 1, 2 or 4 bytes using the largest of three fixed-shape tiers that
///     fits: a 1-byte/2-character tier (4-bit leader, 2-bit successor
///     rank), a 2-byte/4-character tier (4-bit leader, three 3-bit successor
///     ranks) or a 4-byte/8-character tier (5-bit leader, three 4-bit ranks,
///     three 3-bit ranks, a 2-bit rank).</description></item>
///   <item><description>Each packed group is prefixed, MSB-first within its
///     first byte, by a unary header — <c>1</c>, <c>2</c> or <c>3</c> one-bits
///     followed by a zero-bit for the 2/4/8-character tier respectively —
///     mirroring the reference's <c>decode_header</c> (count the leading
///     one-bits of the first byte to identify the tier; a leading zero-bit
///     means "plain literal byte").</description></item>
///   <item><description>A byte with the top bit set that is not itself
///     alphabet-packable (or is 0x00) is escaped as <c>0x00</c> followed by
///     the raw byte, matching the reference's "sentinel + verbatim byte" rule
///     for non-alphabet input — generalized here to also cover a literal
///     0x00 byte itself, since the reference API only ever compresses
///     NUL-terminated C strings and so never needs to represent an embedded
///     NUL, while this building block must round-trip arbitrary binary
///     data.</description></item>
/// </list>
/// <para>
/// The alphabet and successor-rank tables are trained on a small embedded
/// sample text at startup rather than reusing Shoco's own published
/// <c>shoco_model.h</c>: that header is itself the output of Shoco's model
/// generator run over a specific training corpus (a Project Gutenberg
/// selection) — trained-model data owned by the Shoco project, not part of
/// the algorithm's specification, so it is not bulk-copied here. Only the
/// three pack tiers' fixed bit-field shapes (from <c>shoco_model.h</c>'s
/// <c>packs[]</c> table, which the reference always ships with this exact
/// shape unless the model generator's optional bit-width optimizer is
/// invoked) are treated as part of the algorithm and reproduced exactly;
/// which characters land in the alphabet and how their successors rank is
/// this file's own data, trained from <see cref="TrainingCorpus"/>. One
/// further simplification versus the reference model: every character pair
/// here has a dense successor rank (0..alphabet size - 1), whereas a trained
/// <c>shoco_model.h</c> can leave a pair's rank unassigned ("never seen in
/// training") which forces a shorter chain or a literal byte; a dense
/// ranking can only find more compact chains, never fewer, so it cannot
/// break round-tripping.
/// </para>
/// <para>
/// Output is therefore Shoco-format-shaped (same tag/tier/bit-packing rules)
/// but keyed to this file's own trained model, not Shoco's; it is not claimed
/// to be bit-compatible with <c>shoco_compress</c>/<c>shoco_decompress</c>
/// output compiled against the reference <c>shoco_model.h</c>.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>Shoco — https://github.com/Ed-von-Schleck/shoco</description></item>
///   <item><description>Shoco source (pack tiers, <c>decode_header</c>, compress/decompress) — https://github.com/Ed-von-Schleck/shoco/blob/master/shoco.c</description></item>
///   <item><description>Shoco default model (<c>packs[]</c> table shape) — https://github.com/Ed-von-Schleck/shoco/blob/master/shoco_model.h</description></item>
///   <item><description>"Deriving a Compression Algorithm for Short Strings" — https://ed-von-schleck.github.io/shoco/</description></item>
/// </list>
/// </remarks>
public sealed class ShocoBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Shoco";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Shoco";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Shoco's real multi-tier bit-packed successor-chain scheme (1-/2-/4-byte packs, unary tier header), keyed to a locally trained alphabet/successor model";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  // A small self-authored sample used only to train the alphabet/successor model
  // below (a pangram plus ordinary prose, to get reasonable digraph statistics
  // and guarantee every letter of the alphabet plus common punctuation appears).
  private const string TrainingCorpus =
    "the quick brown fox jumps over the lazy dog, and then it runs back home " +
    "through the forest near the river; while thinking about all the things " +
    "that happened during the long and eventful day. the day had just come to " +
    "an end as the sun began to set slowly behind the distant mountains, " +
    "casting long shadows across the quiet valley where the animals were " +
    "settling down for the night, and the stars started to appear one by one " +
    "in the darkening sky above the peaceful countryside - it was truly a " +
    "wonderful sight to behold.";

  // The three fixed pack tiers, from smallest to largest: for each, the number
  // of leading one-bits in the unary header, and the bit width of the leader
  // character field followed by each successor-rank field. This exact shape
  // (2/4/8 characters packed into 1/2/4 bytes, with these specific per-position
  // bit widths) mirrors the reference's default `packs[]` table.
  private static readonly (int HeaderOnes, int[] FieldBits)[] Packs = [
    (1, [4, 2]),
    (2, [4, 3, 3, 3]),
    (3, [5, 4, 4, 4, 3, 3, 3, 2]),
  ];

  private static readonly int MaxChainLength = Packs[^1].FieldBits.Length;

  private static readonly byte[] Alphabet;
  private static readonly int[] CharIdOf = new int[256];
  private static readonly int[][] SuccessorIdAt;
  private static readonly int[][] SuccessorRankOf;

  static ShocoBuildingBlock() {
    Array.Fill(CharIdOf, -1);

    var lower = TrainingCorpus.ToLowerInvariant();
    var unigram = new int[256];
    foreach (var ch in lower)
      if (ch < 256)
        unigram[ch]++;

    Alphabet = [.. Enumerable.Range(0, 256)
      .Where(b => unigram[b] > 0)
      .OrderByDescending(b => unigram[b])
      .ThenBy(b => b)
      .Take(32)
      .Select(b => (byte)b)];

    for (var id = 0; id < Alphabet.Length; id++)
      CharIdOf[Alphabet[id]] = id;

    var n = Alphabet.Length;
    var bigram = new int[n, n];
    for (var i = 0; i + 1 < lower.Length; i++) {
      var a = lower[i];
      var b = lower[i + 1];
      if (a >= 256 || b >= 256)
        continue;
      var aId = CharIdOf[a];
      var bId = CharIdOf[b];
      if (aId >= 0 && bId >= 0)
        bigram[aId, bId]++;
    }

    SuccessorIdAt = new int[n][];
    SuccessorRankOf = new int[n][];
    for (var c = 0; c < n; c++) {
      var order = Enumerable.Range(0, n)
        .OrderByDescending(next => bigram[c, next])
        .ThenByDescending(next => unigram[Alphabet[next]])
        .ThenBy(next => next)
        .ToArray();

      SuccessorIdAt[c] = order;
      SuccessorRankOf[c] = new int[n];
      for (var rank = 0; rank < n; rank++)
        SuccessorRankOf[c][order[rank]] = rank;
    }
  }

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var i = 0;
    while (i < data.Length) {
      var b = data[i];

      if (b == 0 || b >= 0x80) {
        // Escape: byte value 0 and anything with the high bit set (which would
        // otherwise be mistaken for a pack header) is emitted verbatim, behind
        // a 0x00 sentinel.
        ms.WriteByte(0x00);
        ms.WriteByte(b);
        i++;
        continue;
      }

      var firstId = CharIdOf[b];
      if (firstId < 0) {
        // Not in the trained alphabet, but safely representable as-is (bit 7 clear).
        ms.WriteByte(b);
        i++;
        continue;
      }

      // Greedily extend a chain of leader + successor ranks, exactly as the
      // reference's shoco_compress does, up to the largest pack's capacity.
      var chain = new int[MaxChainLength];
      chain[0] = firstId;
      var count = 1;
      var prevId = firstId;
      var j = i + 1;
      while (count < MaxChainLength && j < data.Length) {
        var next = data[j];
        if (next == 0 || next >= 0x80)
          break;
        var nextId = CharIdOf[next];
        if (nextId < 0)
          break;
        chain[count++] = SuccessorRankOf[prevId][nextId];
        prevId = nextId;
        j++;
      }

      var packIndex = FindBestPack(chain, count);
      if (packIndex < 0) {
        // No pack fits (including the case of a lone, unextended leader
        // character): fall back to a plain literal byte, as the reference does.
        ms.WriteByte(b);
        i++;
        continue;
      }

      EmitPack(ms, packIndex, chain);
      i += Packs[packIndex].FieldBits.Length;
    }

    return ms.ToArray();
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalLength == 0)
      return [];

    var result = new byte[originalLength];
    var outPos = 0;
    var pos = 4;

    while (outPos < originalLength) {
      var first = data[pos];

      if (first == 0x00) {
        result[outPos++] = data[pos + 1];
        pos += 2;
        continue;
      }

      if (first < 0x80) {
        result[outPos++] = first;
        pos++;
        continue;
      }

      var packIndex = DecodeHeaderTier(first);
      var pack = Packs[packIndex];
      var bytesPacked = TotalBits(pack) / 8;

      ulong acc = 0;
      for (var k = 0; k < bytesPacked; k++)
        acc = (acc << 8) | data[pos + k];
      pos += bytesPacked;

      var remainingBits = TotalBits(pack) - (pack.HeaderOnes + 1);
      var fieldBits = pack.FieldBits;
      var prevId = -1;
      for (var k = 0; k < fieldBits.Length; k++) {
        remainingBits -= fieldBits[k];
        var value = (int)((acc >> remainingBits) & ((1UL << fieldBits[k]) - 1));

        int id;
        if (k == 0)
          id = value;
        else
          id = SuccessorIdAt[prevId][value];

        result[outPos++] = Alphabet[id];
        prevId = id;
      }
    }

    return result;
  }

  // Picks the largest pack tier whose field count fits within the available
  // chain length and whose every field value fits that tier's bit widths,
  // matching the reference's find_best_encoding (search from largest to
  // smallest, first fit wins).
  private static int FindBestPack(int[] chain, int chainLength) {
    for (var p = Packs.Length - 1; p >= 0; p--) {
      var fieldBits = Packs[p].FieldBits;
      if (chainLength < fieldBits.Length)
        continue;

      var fits = true;
      for (var k = 0; k < fieldBits.Length; k++) {
        if (chain[k] < (1 << fieldBits[k]))
          continue;
        fits = false;
        break;
      }

      if (fits)
        return p;
    }

    return -1;
  }

  private static void EmitPack(Stream output, int packIndex, int[] chain) {
    var (headerOnes, fieldBits) = Packs[packIndex];
    var totalBits = TotalBits(Packs[packIndex]);

    ulong acc = ((1UL << headerOnes) - 1) << 1; // e.g. 2 ones -> 0b110

    for (var k = 0; k < fieldBits.Length; k++)
      acc = (acc << fieldBits[k]) | (uint)chain[k];

    for (var byteIndex = (totalBits / 8) - 1; byteIndex >= 0; byteIndex--)
      output.WriteByte((byte)(acc >> (8 * byteIndex)));
  }

  // Mirrors the reference's decode_header: counts the leading one-bits of the
  // first byte of a pack (a leading zero-bit, handled by the caller before
  // this is invoked, means "plain literal").
  private static int DecodeHeaderTier(byte first) {
    var ones = 0;
    var b = (uint)first << 24;
    while ((b & 0x80000000u) != 0) {
      ones++;
      b <<= 1;
    }

    var packIndex = ones - 1;
    if (packIndex < 0 || packIndex >= Packs.Length)
      throw new InvalidDataException($"Shoco: unrecognized pack header (0x{first:X2}).");
    return packIndex;
  }

  private static int TotalBits((int HeaderOnes, int[] FieldBits) pack) =>
    pack.HeaderOnes + 1 + pack.FieldBits.Sum();
}
