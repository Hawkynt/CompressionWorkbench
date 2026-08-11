namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Encoder-side search over the RFC 7932 Section 8 static dictionary: finds the
/// transformed dictionary word that best covers the input at a given position.
/// </summary>
/// <remarks>
/// <para>
/// Transforms are grouped by their prefix and by their elementary word operation, so a
/// single hash probe per prefix locates every candidate base word:
/// </para>
/// <list type="bullet">
///   <item>Identity and OmitLast1-9 all leave the head of the base word intact, so one
///   index entry keyed on the first four bytes of the base word serves all ten - the
///   longest common prefix decides which of them fit.</item>
///   <item>FermentFirst and FermentAll change bytes in place without changing the length,
///   so each gets its own index entry keyed on the first four bytes of the already
///   fermented word.</item>
///   <item>OmitFirst1-9 shift the word head out of view and would need a separate index
///   per omission count; those eight transforms are not searched. They all carry an empty
///   prefix and suffix, so nothing else is lost with them.</item>
/// </list>
/// <para>
/// Every decision here is made with integer arithmetic only, so that the JavaScript port
/// in the Cipher project produces byte-identical output.
/// </para>
/// </remarks>
internal static class BrotliDictionaryMatcher {
  /// <summary>Number of bits in the dictionary hash table index.</summary>
  private const int HashBits = 16;

  /// <summary>Shortest transformed word worth a copy command of its own.</summary>
  public const int MinOutputLength = 4;

  /// <summary>One transform of a prefix group: its id and the suffix it appends.</summary>
  private readonly record struct SuffixCandidate(int TransformId, byte[] Suffix);

  /// <summary>
  /// The transforms of one prefix group that share an elementary word operation, split by
  /// the first byte of their suffix so a match only tests the ones that can possibly follow.
  /// </summary>
  private sealed class OperationSlot {
    public readonly List<SuffixCandidate> Empty = [];
    public readonly List<SuffixCandidate>?[] ByFirstByte = new List<SuffixCandidate>?[256];
  }

  /// <summary>All transforms sharing one prefix, indexed by elementary transform id.</summary>
  private sealed class PrefixGroup(byte[] prefix) {
    public readonly byte[] Prefix = prefix;
    public readonly OperationSlot?[] ByTid = new OperationSlot?[BrotliStaticDictionary.TransformIdCount];
  }

  /// <summary>
  /// Chained hash table over the first four bytes of every word form, so one probe finds
  /// every base word that could start at the current input position.
  /// </summary>
  private sealed class WordIndex {
    public required int[] Buckets { get; init; }
    public required int[] Next { get; init; }
    public required byte[] WordLength { get; init; }
    public required ushort[] WordIndexInClass { get; init; }
    public required byte[] Form { get; init; }
  }

  private static readonly PrefixGroup[] Groups = BuildGroups();

  private static readonly WordIndex Index = BuildIndex();

  private static PrefixGroup[] BuildGroups() {
    var groups = new List<PrefixGroup>();

    for (var id = 0; id < BrotliStaticDictionary.Transforms.Length; ++id) {
      var (prefix, tid, suffix) = BrotliStaticDictionary.Transforms[id];
      if (tid is >= BrotliStaticDictionary.TransformOmitFirstLow
              and <= BrotliStaticDictionary.TransformOmitFirstHigh)
        continue;

      PrefixGroup? group = null;
      foreach (var candidate in groups)
        if (candidate.Prefix.AsSpan().SequenceEqual(prefix)) {
          group = candidate;
          break;
        }

      if (group == null) {
        group = new PrefixGroup(prefix);
        groups.Add(group);
      }

      var slot = group.ByTid[tid] ??= new OperationSlot();
      var entry = new SuffixCandidate(id, suffix);
      if (suffix.Length == 0) {
        slot.Empty.Add(entry);
        continue;
      }

      (slot.ByFirstByte[suffix[0]] ??= []).Add(entry);
    }

    return [.. groups];
  }

  /// <summary>Hash of four bytes, the same multiply-shift the window match finder uses.</summary>
  private static int HashFourBytes(byte b0, byte b1, byte b2, byte b3) {
    var word = (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
    return (int)(word * 2654435761u >> (32 - HashBits));
  }

  private static WordIndex BuildIndex() {
    var total = 0;
    for (var length = BrotliStaticDictionary.MinWordLength;
         length <= BrotliStaticDictionary.MaxWordLength;
         ++length)
      total += BrotliStaticDictionary.GetNumWords(length);
    total *= BrotliStaticDictionary.FormCount;

    var buckets = new int[1 << HashBits];
    Array.Fill(buckets, -1);
    var index = new WordIndex {
      Buckets = buckets,
      Next = new int[total],
      WordLength = new byte[total],
      WordIndexInClass = new ushort[total],
      Form = new byte[total]
    };

    var count = 0;
    for (var form = 0; form < BrotliStaticDictionary.FormCount; ++form) {
      var source = BrotliStaticDictionary.Forms[form];
      for (var length = BrotliStaticDictionary.MinWordLength;
           length <= BrotliStaticDictionary.MaxWordLength;
           ++length) {
        var words = BrotliStaticDictionary.GetNumWords(length);
        for (var word = 0; word < words; ++word) {
          var offset = BrotliStaticDictionary.GetWordOffset(length, word);
          var bucket = HashFourBytes(source[offset], source[offset + 1],
            source[offset + 2], source[offset + 3]);
          index.WordLength[count] = (byte)length;
          index.WordIndexInClass[count] = (ushort)word;
          index.Form[count] = (byte)form;
          index.Next[count] = buckets[bucket];
          buckets[bucket] = count;
          ++count;
        }
      }
    }

    return index;
  }

  /// <summary>A static dictionary reference found at one input position.</summary>
  /// <param name="CopyLength">The base word length, which the copy length code carries.</param>
  /// <param name="OutputLength">The number of input bytes the transformed word covers.</param>
  /// <param name="Distance">The distance value that addresses this word and transform.</param>
  /// <param name="Score">The parse ranking of this reference.</param>
  public readonly record struct DictionaryMatch(int CopyLength, int OutputLength, int Distance, int Score);

  /// <summary>
  /// Finds the best static dictionary reference at <paramref name="position"/>. Ties are
  /// broken towards the smaller distance so the result never depends on traversal order.
  /// </summary>
  /// <param name="data">The input being compressed.</param>
  /// <param name="position">The position the reference has to start at.</param>
  /// <param name="maxAllowedDistance">
  /// The value the decoder will compute, that is the minimum of the window size and the
  /// number of bytes produced so far.
  /// </param>
  /// <param name="match">Receives the best reference found.</param>
  /// <returns><see langword="true"/> when a reference that pays for itself was found.</returns>
  public static bool TryFindMatch(byte[] data, int position, int maxAllowedDistance,
    out DictionaryMatch match) {
    match = default;
    var found = false;

    foreach (var group in Groups) {
      var prefix = group.Prefix;
      var wordStart = position + prefix.Length;
      if (wordStart + BrotliStaticDictionary.MinWordLength > data.Length)
        continue;

      var prefixMatches = true;
      for (var i = 0; i < prefix.Length; ++i)
        if (data[position + i] != prefix[i]) {
          prefixMatches = false;
          break;
        }

      if (!prefixMatches)
        continue;

      var bucket = HashFourBytes(data[wordStart], data[wordStart + 1],
        data[wordStart + 2], data[wordStart + 3]);

      for (var e = Index.Buckets[bucket]; e >= 0; e = Index.Next[e]) {
        var wordLength = Index.WordLength[e];
        var wordIndex = Index.WordIndexInClass[e];
        var form = Index.Form[e];
        var source = BrotliStaticDictionary.Forms[form];
        var offset = BrotliStaticDictionary.GetWordOffset(wordLength, wordIndex);

        var limit = Math.Min(wordLength, data.Length - wordStart);
        var common = 0;
        while (common < limit && source[offset + common] == data[wordStart + common])
          ++common;

        if (common < BrotliStaticDictionary.MinWordLength)
          continue;

        if (form != BrotliStaticDictionary.FormBase) {
          if (common != wordLength)
            continue;

          var fermentTid = form == BrotliStaticDictionary.FormFermentFirst
            ? BrotliStaticDictionary.TransformFermentFirst
            : BrotliStaticDictionary.TransformFermentAll;
          ConsiderTransforms(data, maxAllowedDistance, group, fermentTid, wordLength, wordIndex,
            prefix.Length + wordLength, wordStart + wordLength, ref match, ref found);
          continue;
        }

        // Identity keeps the whole word, OmitLast_k drops its last k bytes; both
        // only need the head of the word to match.
        for (var omit = 0; omit <= 9; ++omit) {
          var middle = wordLength - omit;
          if (middle < 1)
            break;
          if (middle > common)
            continue;

          var tid = omit == 0
            ? BrotliStaticDictionary.TransformIdentity
            : BrotliStaticDictionary.TransformOmitLastBase + omit;
          ConsiderTransforms(data, maxAllowedDistance, group, tid, wordLength, wordIndex,
            prefix.Length + middle, wordStart + middle, ref match, ref found);
        }
      }
    }

    return found;
  }

  private static void ConsiderTransforms(byte[] data, int maxAllowedDistance, PrefixGroup group,
    int tid, int wordLength, int wordIndex, int headLength, int suffixStart,
    ref DictionaryMatch match, ref bool found) {
    var slot = group.ByTid[tid];
    if (slot == null)
      return;

    ConsiderList(data, maxAllowedDistance, slot.Empty, wordLength, wordIndex, headLength,
      suffixStart, ref match, ref found);

    if (suffixStart >= data.Length)
      return;

    var list = slot.ByFirstByte[data[suffixStart]];
    if (list == null)
      return;

    ConsiderList(data, maxAllowedDistance, list, wordLength, wordIndex, headLength,
      suffixStart, ref match, ref found);
  }

  private static void ConsiderList(byte[] data, int maxAllowedDistance, List<SuffixCandidate> list,
    int wordLength, int wordIndex, int headLength, int suffixStart,
    ref DictionaryMatch match, ref bool found) {
    foreach (var candidate in list) {
      var suffix = candidate.Suffix;
      if (suffixStart + suffix.Length > data.Length)
        continue;

      var matches = true;
      for (var k = 0; k < suffix.Length; ++k)
        if (data[suffixStart + k] != suffix[k]) {
          matches = false;
          break;
        }

      if (!matches)
        continue;

      var outputLength = headLength + suffix.Length;
      if (outputLength < MinOutputLength)
        continue;

      var distance = maxAllowedDistance + 1 +
        candidate.TransformId * BrotliStaticDictionary.GetNumWords(wordLength) + wordIndex;
      var cost = BrotliCompressor.DictionaryMatchCost(wordLength, distance);
      if (outputLength * BrotliCompressor.DictionaryLiteralBits <= cost)
        continue;

      var score = outputLength * BrotliCompressor.MatchRankLiteralBits - cost;
      if (found && (score < match.Score || (score == match.Score && distance >= match.Distance)))
        continue;

      match = new DictionaryMatch(wordLength, outputLength, distance, score);
      found = true;
    }
  }
}
