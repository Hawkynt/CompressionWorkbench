using System.Reflection;

namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Brotli static dictionary and word transform functions (RFC 7932 Section 8, Appendix A/B).
/// The static dictionary holds 122,784 bytes of word data across 21 length classes (4-24),
/// with 121 transforms that can modify each word (identity, case flips, prefix and suffix
/// insertion, head and tail truncation).
/// </summary>
/// <remarks>
/// A copy command whose distance exceeds the maximum in-window backward distance addresses
/// this word list instead of the sliding window. RFC 7932 Section 8 decodes it as
/// <c>word_id = distance - max_allowed_distance - 1</c>, <c>index = word_id mod
/// NWORDS[copy_length]</c> and <c>transform_id = word_id div NWORDS[copy_length]</c>; the
/// copy length selects the length class of the base word, while the number of bytes actually
/// produced is the length of the transformed word.
/// </remarks>
internal static class BrotliStaticDictionary {
  /// <summary>Minimum word length in the static dictionary.</summary>
  public const int MinWordLength = 4;

  /// <summary>Maximum word length in the static dictionary.</summary>
  public const int MaxWordLength = 24;

  /// <summary>Number of transforms defined by RFC 7932 Appendix B.</summary>
  public const int NumTransforms = 121;

  /// <summary>Elementary transform: leave the word unchanged.</summary>
  public const int TransformIdentity = 0;

  /// <summary>Elementary transform: flip the case of the first character.</summary>
  public const int TransformFermentFirst = 1;

  /// <summary>Elementary transform: flip the case of every character.</summary>
  public const int TransformFermentAll = 2;

  /// <summary>Lowest elementary transform id that drops leading bytes (OmitFirst1).</summary>
  public const int TransformOmitFirstLow = 3;

  /// <summary>Highest elementary transform id that drops leading bytes (OmitFirst9).</summary>
  public const int TransformOmitFirstHigh = 11;

  /// <summary>OmitLast_k has elementary transform id <c>TransformOmitLastBase + k</c>.</summary>
  public const int TransformOmitLastBase = 11;

  /// <summary>Number of distinct elementary transform ids (0-20).</summary>
  public const int TransformIdCount = 21;

  /// <summary>Index of the transform that is a bare FermentFirst with no prefix or suffix.</summary>
  private const int FermentFirstOnly = 9;

  /// <summary>Index of the transform that is a bare FermentAll with no prefix or suffix.</summary>
  private const int FermentAllOnly = 44;

  /// <summary>
  /// RFC 7932 Section 8 NDBITS, indexed by word length. <c>NWORDS[length]</c> is
  /// <c>1 &lt;&lt; NDBITS[length]</c>; lengths below 4 hold no words.
  /// </summary>
  private static readonly int[] NumBitsPerLength = [
    0, 0, 0, 0, 10, 10, 11, 11, 10, 10,
    10, 10, 10, 9, 9, 8, 7, 7, 8, 7,
    7, 6, 6, 5, 5
  ];

  /// <summary>
  /// Byte offset into the dictionary data at which each length class starts:
  /// <c>DOFFSET[length + 1] = DOFFSET[length] + length * NWORDS[length]</c>.
  /// </summary>
  private static readonly int[] LengthOffsets = ComputeLengthOffsets();

  /// <summary>
  /// The word list in three forms: untouched, every word with its first character
  /// case-flipped, and every word with all characters case-flipped. Neither ferment
  /// changes a word's length, so all three share the layout of the original. Built on
  /// first use, because it needs the transform table declared further down.
  /// </summary>
  private static readonly Lazy<byte[][]> WordForms = new(BuildWordForms);

  /// <summary>Number of word forms held by <see cref="Forms"/>.</summary>
  public const int FormCount = 3;

  /// <summary>Form index of the untouched word list.</summary>
  public const int FormBase = 0;

  /// <summary>Form index of the word list with every first character case-flipped.</summary>
  public const int FormFermentFirst = 1;

  /// <summary>Form index of the word list with every character case-flipped.</summary>
  public const int FormFermentAll = 2;

  /// <summary>The three forms of the word list, indexed by <c>Form*</c>.</summary>
  public static byte[][] Forms => WordForms.Value;

  private static int[] ComputeLengthOffsets() {
    var offsets = new int[MaxWordLength + 2];
    for (var length = 0; length <= MaxWordLength; ++length)
      offsets[length + 1] = offsets[length] + length * GetNumWords(length);
    return offsets;
  }

  private static byte[] LoadDictionary() {
    using var stream = Assembly.GetExecutingAssembly()
      .GetManifestResourceStream("brotli-dictionary.bin")
      ?? throw new InvalidOperationException("Brotli dictionary resource not found");
    var data = new byte[stream.Length];
    stream.ReadExactly(data);
    return data;
  }

  private static byte[][] BuildWordForms() {
    var basis = LoadDictionary();
    var fermentFirst = new byte[basis.Length];
    var fermentAll = new byte[basis.Length];

    Span<byte> scratch = stackalloc byte[MaxWordLength * 2 + 16];
    for (var length = MinWordLength; length <= MaxWordLength; ++length) {
      var count = GetNumWords(length);
      for (var index = 0; index < count; ++index) {
        var offset = LengthOffsets[length] + index * length;

        basis.AsSpan(offset, length).CopyTo(scratch);
        ApplyTransform(FermentFirstOnly, scratch, length);
        scratch[..length].CopyTo(fermentFirst.AsSpan(offset));

        basis.AsSpan(offset, length).CopyTo(scratch);
        ApplyTransform(FermentAllOnly, scratch, length);
        scratch[..length].CopyTo(fermentAll.AsSpan(offset));
      }
    }

    return [basis, fermentFirst, fermentAll];
  }

  /// <summary>Gets the number of dictionary index bits for a given word length.</summary>
  /// <param name="length">The word length.</param>
  /// <returns>NDBITS for that length, or zero when the length holds no words.</returns>
  public static int GetNumBits(int length) =>
    length is < MinWordLength or > MaxWordLength ? 0 : NumBitsPerLength[length];

  /// <summary>Gets the number of words in one length class.</summary>
  /// <param name="length">The word length.</param>
  /// <returns>NWORDS for that length, or zero when the length holds no words.</returns>
  public static int GetNumWords(int length) =>
    length is < MinWordLength or > MaxWordLength ? 0 : 1 << NumBitsPerLength[length];

  /// <summary>Gets the byte offset of one word inside every word form.</summary>
  /// <param name="length">The word length.</param>
  /// <param name="index">The index of the word within its length class.</param>
  /// <returns>The offset into the arrays returned by <see cref="Forms"/>.</returns>
  public static int GetWordOffset(int length, int index) => LengthOffsets[length] + index * length;

  /// <summary>
  /// Looks up a word from the static dictionary and applies a transform to it.
  /// </summary>
  /// <param name="length">The word length (4-24).</param>
  /// <param name="wordIndex">The index of the word within the length class.</param>
  /// <param name="transformIndex">The transform to apply (0-120).</param>
  /// <param name="output">Buffer receiving the transformed word.</param>
  /// <returns>Number of bytes written to the output, or zero if the reference is invalid.</returns>
  public static int GetWord(int length, int wordIndex, int transformIndex, Span<byte> output) {
    if (length is < MinWordLength or > MaxWordLength)
      return 0;
    if (transformIndex is < 0 or >= NumTransforms)
      return 0;
    if (wordIndex < 0 || wordIndex >= GetNumWords(length))
      return 0;

    var offset = GetWordOffset(length, wordIndex);
    var source = Forms[FormBase];
    if (offset + length > source.Length)
      return 0;

    var (prefix, _, suffix) = Transforms[transformIndex];
    if (prefix.Length + length + suffix.Length > output.Length)
      return 0;

    source.AsSpan(offset, length).CopyTo(output);
    return ApplyTransform(transformIndex, output, length);
  }

  /// <summary>
  /// Applies transform <paramref name="transformIndex"/> in place to the
  /// <paramref name="length"/> word bytes at the start of <paramref name="word"/>,
  /// per RFC 7932 Section 8: <c>transform(word) = prefix + T(word) + suffix</c>.
  /// </summary>
  /// <returns>The length of the transformed word.</returns>
  private static int ApplyTransform(int transformIndex, Span<byte> word, int length) {
    var (prefix, tid, suffix) = Transforms[transformIndex];

    var middle = length;
    switch (tid) {
      case TransformFermentFirst:
        FermentFirst(word, middle);
        break;
      case TransformFermentAll:
        FermentAll(word, middle);
        break;
      case >= TransformOmitFirstLow and <= TransformOmitFirstHigh: {
        var omit = Math.Min(tid - (TransformOmitFirstLow - 1), middle);
        word[omit..middle].CopyTo(word);
        middle -= omit;
        break;
      }
      case > TransformOmitLastBase and < TransformOmitLastBase + 10:
        middle = Math.Max(0, middle - (tid - TransformOmitLastBase));
        break;
    }

    if (prefix.Length > 0) {
      word[..middle].CopyTo(word[prefix.Length..]);
      prefix.CopyTo(word);
    }

    var total = prefix.Length + middle;
    if (suffix.Length > 0) {
      suffix.CopyTo(word[total..]);
      total += suffix.Length;
    }

    return total;
  }

  /// <summary>
  /// RFC 7932 Section 8 single-step case flip, covering ASCII plus two- and three-byte
  /// UTF-8 sequences. Returns the number of bytes the step consumed.
  /// </summary>
  private static int FermentStep(Span<byte> word, int length, int position) {
    var value = word[position];
    if (value < 192) {
      if (value is >= 97 and <= 122)
        word[position] = (byte)(value ^ 32);
      return 1;
    }

    if (value < 224) {
      if (position + 1 < length)
        word[position + 1] ^= 32;
      return 2;
    }

    if (position + 2 < length)
      word[position + 2] ^= 5;
    return 3;
  }

  private static void FermentFirst(Span<byte> word, int length) {
    if (length > 0)
      FermentStep(word, length, 0);
  }

  private static void FermentAll(Span<byte> word, int length) {
    var position = 0;
    while (position < length)
      position += FermentStep(word, length, position);
  }

  /// <summary>RFC 7932 Appendix B transform 102 prefixes the word with U+00A0 in UTF-8.</summary>
  private static readonly byte[] NonBreakingSpace = [0xC2, 0xA0];

  /// <summary>Converts a latin-1 literal into the byte sequence RFC 7932 Appendix B spells out.</summary>
  private static byte[] Bytes(string text) {
    var result = new byte[text.Length];
    for (var i = 0; i < text.Length; ++i)
      result[i] = (byte)text[i];
    return result;
  }

  /// <summary>
  /// RFC 7932 Appendix B: the 121 transforms as (prefix, elementary transform id, suffix)
  /// triples. Elementary ids are 0 Identity, 1 FermentFirst, 2 FermentAll,
  /// 3-11 OmitFirst1-9, 12-20 OmitLast1-9.
  /// </summary>
  public static readonly (byte[] Prefix, int Tid, byte[] Suffix)[] Transforms = [
    (Bytes(""), 0, Bytes("")),          (Bytes(""), 0, Bytes(" ")),        (Bytes(" "), 0, Bytes(" ")),
    (Bytes(""), 3, Bytes("")),          (Bytes(""), 1, Bytes(" ")),        (Bytes(""), 0, Bytes(" the ")),
    (Bytes(" "), 0, Bytes("")),         (Bytes("s "), 0, Bytes(" ")),      (Bytes(""), 0, Bytes(" of ")),
    (Bytes(""), 1, Bytes("")),          (Bytes(""), 0, Bytes(" and ")),    (Bytes(""), 4, Bytes("")),
    (Bytes(""), 12, Bytes("")),         (Bytes(", "), 0, Bytes(" ")),      (Bytes(""), 0, Bytes(", ")),
    (Bytes(" "), 1, Bytes(" ")),        (Bytes(""), 0, Bytes(" in ")),     (Bytes(""), 0, Bytes(" to ")),
    (Bytes("e "), 0, Bytes(" ")),       (Bytes(""), 0, Bytes("\"")),       (Bytes(""), 0, Bytes(".")),
    (Bytes(""), 0, Bytes("\">")),       (Bytes(""), 0, Bytes("\n")),       (Bytes(""), 14, Bytes("")),
    (Bytes(""), 0, Bytes("]")),         (Bytes(""), 0, Bytes(" for ")),    (Bytes(""), 5, Bytes("")),
    (Bytes(""), 13, Bytes("")),         (Bytes(""), 0, Bytes(" a ")),      (Bytes(""), 0, Bytes(" that ")),
    (Bytes(" "), 1, Bytes("")),         (Bytes(""), 0, Bytes(". ")),       (Bytes("."), 0, Bytes("")),
    (Bytes(" "), 0, Bytes(", ")),       (Bytes(""), 6, Bytes("")),         (Bytes(""), 0, Bytes(" with ")),
    (Bytes(""), 0, Bytes("'")),         (Bytes(""), 0, Bytes(" from ")),   (Bytes(""), 0, Bytes(" by ")),
    (Bytes(""), 7, Bytes("")),          (Bytes(""), 8, Bytes("")),         (Bytes(" the "), 0, Bytes("")),
    (Bytes(""), 15, Bytes("")),         (Bytes(""), 0, Bytes(". The ")),   (Bytes(""), 2, Bytes("")),
    (Bytes(""), 0, Bytes(" on ")),      (Bytes(""), 0, Bytes(" as ")),     (Bytes(""), 0, Bytes(" is ")),
    (Bytes(""), 18, Bytes("")),         (Bytes(""), 12, Bytes("ing ")),    (Bytes(""), 0, Bytes("\n\t")),
    (Bytes(""), 0, Bytes(":")),         (Bytes(" "), 0, Bytes(". ")),      (Bytes(""), 0, Bytes("ed ")),
    (Bytes(""), 11, Bytes("")),         (Bytes(""), 9, Bytes("")),         (Bytes(""), 17, Bytes("")),
    (Bytes(""), 0, Bytes("(")),         (Bytes(""), 1, Bytes(", ")),       (Bytes(""), 19, Bytes("")),
    (Bytes(""), 0, Bytes(" at ")),      (Bytes(""), 0, Bytes("ly ")),      (Bytes(" the "), 0, Bytes(" of ")),
    (Bytes(""), 16, Bytes("")),         (Bytes(""), 20, Bytes("")),        (Bytes(" "), 1, Bytes(", ")),
    (Bytes(""), 1, Bytes("\"")),        (Bytes("."), 0, Bytes("(")),       (Bytes(""), 2, Bytes(" ")),
    (Bytes(""), 1, Bytes("\">")),       (Bytes(""), 0, Bytes("=\"")),      (Bytes(" "), 0, Bytes(".")),
    (Bytes(".com/"), 0, Bytes("")),     (Bytes(" the "), 0, Bytes(" of the ")), (Bytes(""), 1, Bytes("'")),
    (Bytes(""), 0, Bytes(". This ")),   (Bytes(""), 0, Bytes(",")),        (Bytes("."), 0, Bytes(" ")),
    (Bytes(""), 1, Bytes("(")),         (Bytes(""), 1, Bytes(".")),        (Bytes(""), 0, Bytes(" not ")),
    (Bytes(" "), 0, Bytes("=\"")),      (Bytes(""), 0, Bytes("er ")),      (Bytes(" "), 2, Bytes(" ")),
    (Bytes(""), 0, Bytes("al ")),       (Bytes(" "), 2, Bytes("")),        (Bytes(""), 0, Bytes("='")),
    (Bytes(""), 2, Bytes("\"")),        (Bytes(""), 1, Bytes(". ")),       (Bytes(" "), 0, Bytes("(")),
    (Bytes(""), 0, Bytes("ful ")),      (Bytes(" "), 1, Bytes(". ")),      (Bytes(""), 0, Bytes("ive ")),
    (Bytes(""), 0, Bytes("less ")),     (Bytes(""), 2, Bytes("'")),        (Bytes(""), 0, Bytes("est ")),
    (Bytes(" "), 1, Bytes(".")),        (Bytes(""), 2, Bytes("\">")),      (Bytes(" "), 0, Bytes("='")),
    (Bytes(""), 1, Bytes(",")),         (Bytes(""), 0, Bytes("ize ")),     (Bytes(""), 2, Bytes(".")),
    (NonBreakingSpace, 0, Bytes("")), (Bytes(" "), 0, Bytes(",")), (Bytes(""), 1, Bytes("=\"")),
    (Bytes(""), 2, Bytes("=\"")),       (Bytes(""), 0, Bytes("ous ")),     (Bytes(""), 2, Bytes(", ")),
    (Bytes(""), 1, Bytes("='")),        (Bytes(" "), 1, Bytes(",")),       (Bytes(" "), 2, Bytes("=\"")),
    (Bytes(" "), 2, Bytes(", ")),       (Bytes(""), 2, Bytes(",")),        (Bytes(""), 2, Bytes("(")),
    (Bytes(""), 2, Bytes(". ")),        (Bytes(" "), 2, Bytes(".")),       (Bytes(""), 2, Bytes("='")),
    (Bytes(" "), 2, Bytes(". ")),       (Bytes(" "), 1, Bytes("=\"")),     (Bytes(" "), 2, Bytes("='")),
    (Bytes(" "), 1, Bytes("='"))
  ];

  /// <summary>
  /// Splits a raw stream distance into the static dictionary reference it addresses.
  /// </summary>
  /// <param name="distance">The distance value decoded from the stream.</param>
  /// <param name="maxAllowedDistance">The minimum of the window size and the bytes produced so far.</param>
  /// <param name="copyLength">The copy length of the command, which selects the length class.</param>
  /// <param name="wordIndex">Receives the word index within the length class.</param>
  /// <param name="transformIndex">Receives the transform to apply.</param>
  /// <returns><see langword="true"/> if this is a valid static dictionary reference.</returns>
  public static bool TryGetStaticReference(int distance, int maxAllowedDistance, int copyLength,
    out int wordIndex, out int transformIndex) {
    wordIndex = 0;
    transformIndex = 0;

    if (distance <= maxAllowedDistance)
      return false; // Regular backward reference, not a dictionary reference

    var numBits = GetNumBits(copyLength);
    if (numBits == 0)
      return false;

    var wordId = distance - maxAllowedDistance - 1;
    if (wordId < 0)
      return false;

    wordIndex = wordId & ((1 << numBits) - 1);
    transformIndex = wordId >> numBits;
    return transformIndex < NumTransforms;
  }
}
