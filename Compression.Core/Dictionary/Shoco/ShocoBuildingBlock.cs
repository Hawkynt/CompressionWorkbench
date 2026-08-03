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
/// This implementation follows that scheme (small alphabet + successor-rank chains
/// + an escape for anything outside the model) but trains its own frequency and
/// digraph tables from a small embedded sample text at startup, rather than
/// reusing Shoco's own published model — a clean-room-safe substitute for its
/// trained tables while preserving the technique itself.
/// Reference: https://github.com/Ed-von-Schleck/shoco (algorithm description);
/// https://ed-von-schleck.github.io/shoco/ ("Deriving a Compression Algorithm for
/// Short Strings").
/// </summary>
public sealed class ShocoBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Shoco";
  /// <inheritdoc/>
  public string DisplayName => "Shoco";
  /// <inheritdoc/>
  public string Description => "Short-string compression using a trained alphabet and successor-rank chains";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MaxChainLength = 4;

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
        // otherwise be mistaken for a chain-token control byte) is emitted verbatim.
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

      var chain = new List<int> { firstId };
      var j = i + 1;
      while (chain.Count < MaxChainLength && j < data.Length) {
        var next = data[j];
        if (next == 0 || next >= 0x80)
          break;
        var nextId = CharIdOf[next];
        if (nextId < 0)
          break;
        chain.Add(nextId);
        j++;
      }

      var length = chain.Count;
      ms.WriteByte((byte)(0x80 | ((length - 1) << 5) | firstId));

      var prevId = firstId;
      for (var k = 1; k < length; k++) {
        ms.WriteByte((byte)SuccessorRankOf[prevId][chain[k]]);
        prevId = chain[k];
      }

      i = j;
    }

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalLength == 0)
      return [];

    var result = new byte[originalLength];
    var outPos = 0;
    var pos = 4;

    while (outPos < originalLength) {
      var control = data[pos++];

      if (control == 0x00) {
        result[outPos++] = data[pos++];
        continue;
      }

      if (control < 0x80) {
        result[outPos++] = control;
        continue;
      }

      var lengthCode = (control >> 5) & 0x03;
      var firstId = control & 0x1F;
      var length = lengthCode + 1;

      result[outPos++] = Alphabet[firstId];
      var prevId = firstId;
      for (var k = 1; k < length; k++) {
        var rank = data[pos++];
        var nextId = SuccessorIdAt[prevId][rank];
        result[outPos++] = Alphabet[nextId];
        prevId = nextId;
      }
    }

    return result;
  }
}
