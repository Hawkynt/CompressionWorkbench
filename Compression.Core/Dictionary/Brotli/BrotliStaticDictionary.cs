using System.Reflection;

namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Brotli static dictionary and word transform functions (RFC 7932 Appendix A/B).
/// The static dictionary provides 122,784 bytes of word data across 21 length classes (4-24),
/// with 121 transforms that can modify each word (identity, uppercase, prefix, etc.).
/// </summary>
internal static class BrotliStaticDictionary {
  /// <summary>Minimum word length in the static dictionary.</summary>
  public const int MinWordLength = 4;

  /// <summary>Maximum word length in the static dictionary.</summary>
  public const int MaxWordLength = 24;

  /// <summary>Number of transforms.</summary>
  public const int NumTransforms = 121;

  /// <summary>
  /// Number of dictionary words per length (log2). Used for computing
  /// dictionary word index from distance code.
  /// </summary>
  private static readonly int[] NumBitsPerLength = [
    10, 10, 11, 11, 10, 10, 10, 10,  // lengths 4-11
    10, 10, 10, 10, 10,  9,  9,  8,  // lengths 12-19
     7,  7,  8,  7,  7              // lengths 20-24
  ];

  /// <summary>
  /// Byte offset into the dictionary data for each length class (4-24).
  /// Precomputed: offset[i] = sum of (2^NumBits[j] * (j+4)) for j=0..i-1.
  /// </summary>
  private static readonly int[] LengthOffsets = ComputeLengthOffsets();

  /// <summary>The raw 122,784-byte static dictionary from RFC 7932 Appendix A.</summary>
  private static readonly byte[] DictionaryData = LoadDictionary();

  private static int[] ComputeLengthOffsets() {
    var offsets = new int[MaxWordLength - MinWordLength + 1];
    var pos = 0;
    for (var i = 0; i < offsets.Length; ++i) {
      offsets[i] = pos;
      pos += (1 << NumBitsPerLength[i]) * (i + MinWordLength);
    }
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

  /// <summary>
  /// Gets the number of dictionary index bits for a given word length.
  /// </summary>
  public static int GetNumBits(int length) =>
    length is < MinWordLength or > MaxWordLength ? 0 : NumBitsPerLength[length - MinWordLength];

  /// <summary>
  /// Gets the total number of words for a given length.
  /// </summary>
  public static int GetNumWords(int length) =>
    length is < MinWordLength or > MaxWordLength ? 0 : 1 << NumBitsPerLength[length - MinWordLength];

  /// <summary>
  /// Looks up a word from the static dictionary and applies a transform.
  /// </summary>
  /// <param name="length">The word length (4-24).</param>
  /// <param name="wordIndex">The index of the word within the length class.</param>
  /// <param name="transformIndex">The transform to apply (0-120).</param>
  /// <param name="output">Buffer to write the resulting bytes.</param>
  /// <returns>Number of bytes written to the output, or 0 if not found.</returns>
  public static int GetWord(int length, int wordIndex, int transformIndex, Span<byte> output) {
    if (length is < MinWordLength or > MaxWordLength)
      return 0;
    if (transformIndex >= NumTransforms)
      return 0;

    var numWords = 1 << NumBitsPerLength[length - MinWordLength];
    if (wordIndex >= numWords)
      return 0;

    // Look up the raw word from the dictionary data
    var offset = LengthOffsets[length - MinWordLength] + wordIndex * length;
    if (offset + length > DictionaryData.Length)
      return 0;

    var pos = Math.Min(length, output.Length);
    DictionaryData.AsSpan(offset, pos).CopyTo(output);

    // Apply transform
    pos = ApplyTransform(transformIndex, output, pos);

    return pos;
  }

  /// <summary>
  /// Applies a Brotli dictionary transform to a word in place.
  /// RFC 7932 Appendix B defines 121 transforms as (prefix, type, suffix) triples.
  /// </summary>
  private static int ApplyTransform(int transformIndex, Span<byte> word, int length) {
    // Decode the transform from the RFC 7932 Appendix B table
    var (prefix, type, suffix) = Transforms[transformIndex];

    // Start with prefix
    var pos = 0;
    if (prefix.Length > 0 && prefix.Length + length + suffix.Length <= word.Length) {
      // Need to shift word right to make room for prefix
      word[..length].CopyTo(word[prefix.Length..]);
      prefix.CopyTo(word);
      pos = prefix.Length + length;
    } else {
      pos = length;
    }

    // Apply word transform type
    switch (type) {
      case TransformType.Identity:
        break;
      case TransformType.UppercaseFirst:
        TransformUppercaseFirst(word[prefix.Length..], length);
        break;
      case TransformType.UppercaseAll:
        TransformUppercaseAll(word[prefix.Length..], length);
        break;
      case TransformType.OmitFirst1: pos = OmitFirst(word, prefix.Length, pos, 1); break;
      case TransformType.OmitFirst2: pos = OmitFirst(word, prefix.Length, pos, 2); break;
      case TransformType.OmitFirst3: pos = OmitFirst(word, prefix.Length, pos, 3); break;
      case TransformType.OmitFirst4: pos = OmitFirst(word, prefix.Length, pos, 4); break;
      case TransformType.OmitFirst5: pos = OmitFirst(word, prefix.Length, pos, 5); break;
      case TransformType.OmitFirst6: pos = OmitFirst(word, prefix.Length, pos, 6); break;
      case TransformType.OmitFirst7: pos = OmitFirst(word, prefix.Length, pos, 7); break;
      case TransformType.OmitFirst8: pos = OmitFirst(word, prefix.Length, pos, 8); break;
      case TransformType.OmitFirst9: pos = OmitFirst(word, prefix.Length, pos, 9); break;
      case TransformType.OmitLast1: pos = Math.Max(prefix.Length, pos - 1); break;
      case TransformType.OmitLast2: pos = Math.Max(prefix.Length, pos - 2); break;
      case TransformType.OmitLast3: pos = Math.Max(prefix.Length, pos - 3); break;
      case TransformType.OmitLast4: pos = Math.Max(prefix.Length, pos - 4); break;
      case TransformType.OmitLast5: pos = Math.Max(prefix.Length, pos - 5); break;
      case TransformType.OmitLast6: pos = Math.Max(prefix.Length, pos - 6); break;
      case TransformType.OmitLast7: pos = Math.Max(prefix.Length, pos - 7); break;
      case TransformType.OmitLast8: pos = Math.Max(prefix.Length, pos - 8); break;
      case TransformType.OmitLast9: pos = Math.Max(prefix.Length, pos - 9); break;
    }

    // Append suffix
    if (suffix.Length > 0 && pos + suffix.Length <= word.Length) {
      suffix.CopyTo(word[pos..]);
      pos += suffix.Length;
    }

    return pos;
  }

  private static int OmitFirst(Span<byte> word, int prefixLen, int totalLen, int count) {
    var wordStart = prefixLen;
    var wordEnd = totalLen;
    var wordLen = wordEnd - wordStart;
    if (count >= wordLen)
      return prefixLen;
    word[(wordStart + count)..wordEnd].CopyTo(word[wordStart..]);
    return totalLen - count;
  }

  private static void TransformUppercaseFirst(Span<byte> word, int length) {
    if (length <= 0) return;
    if (word[0] >= 0x61 && word[0] <= 0x7A) {
      word[0] -= 0x20;
    } else if (word[0] >= 0xC0 && length >= 2) {
      // UTF-8 2-byte sequence
      if (word[0] < 0xE0) {
        word[1] ^= 0x20;
      } else if (length >= 3) {
        // UTF-8 3-byte sequence
        word[2] ^= 0x20; // not precisely correct but matches reference
      }
    }
  }

  private static void TransformUppercaseAll(Span<byte> word, int length) {
    for (var i = 0; i < length;) {
      if (word[i] >= 0x61 && word[i] <= 0x7A) {
        word[i] -= 0x20;
        ++i;
      } else if (word[i] >= 0xC0 && i + 1 < length) {
        if (word[i] < 0xE0) {
          word[i + 1] ^= 0x20;
          i += 2;
        } else if (i + 2 < length) {
          word[i + 2] ^= 0x20;
          i += 3;
        } else {
          ++i;
        }
      } else {
        ++i;
      }
    }
  }

  private enum TransformType {
    Identity,
    UppercaseFirst,
    UppercaseAll,
    OmitFirst1, OmitFirst2, OmitFirst3, OmitFirst4, OmitFirst5,
    OmitFirst6, OmitFirst7, OmitFirst8, OmitFirst9,
    OmitLast1, OmitLast2, OmitLast3, OmitLast4, OmitLast5,
    OmitLast6, OmitLast7, OmitLast8, OmitLast9
  }

  /// <summary>
  /// RFC 7932 Appendix B: 121 transforms as (prefix, type, suffix) triples.
  /// </summary>
  private static readonly (byte[] Prefix, TransformType Type, byte[] Suffix)[] Transforms = [
    (""u8.ToArray(), TransformType.Identity, ""u8.ToArray()),           // 0
    (""u8.ToArray(), TransformType.Identity, " "u8.ToArray()),          // 1
    (" "u8.ToArray(), TransformType.Identity, " "u8.ToArray()),         // 2
    (""u8.ToArray(), TransformType.OmitFirst1, ""u8.ToArray()),         // 3
    (""u8.ToArray(), TransformType.UppercaseFirst, " "u8.ToArray()),    // 4
    (""u8.ToArray(), TransformType.Identity, " the "u8.ToArray()),      // 5
    (" "u8.ToArray(), TransformType.Identity, ""u8.ToArray()),          // 6
    ("s "u8.ToArray(), TransformType.Identity, " "u8.ToArray()),        // 7
    (""u8.ToArray(), TransformType.Identity, " of "u8.ToArray()),       // 8
    (""u8.ToArray(), TransformType.UppercaseFirst, ""u8.ToArray()),     // 9
    (""u8.ToArray(), TransformType.Identity, " and "u8.ToArray()),      // 10
    (""u8.ToArray(), TransformType.OmitFirst2, ""u8.ToArray()),         // 11
    (""u8.ToArray(), TransformType.OmitLast1, ""u8.ToArray()),          // 12
    (", "u8.ToArray(), TransformType.Identity, " "u8.ToArray()),        // 13
    (""u8.ToArray(), TransformType.Identity, ", "u8.ToArray()),         // 14
    (" "u8.ToArray(), TransformType.UppercaseFirst, " "u8.ToArray()),   // 15
    (""u8.ToArray(), TransformType.Identity, " in "u8.ToArray()),       // 16
    (""u8.ToArray(), TransformType.Identity, " to "u8.ToArray()),       // 17
    ("e "u8.ToArray(), TransformType.Identity, " "u8.ToArray()),        // 18
    (""u8.ToArray(), TransformType.Identity, "\""u8.ToArray()),         // 19
    (""u8.ToArray(), TransformType.Identity, "."u8.ToArray()),          // 20
    (""u8.ToArray(), TransformType.Identity, "\">"u8.ToArray()),        // 21
    (""u8.ToArray(), TransformType.Identity, "\n"u8.ToArray()),         // 22
    (""u8.ToArray(), TransformType.OmitLast3, ""u8.ToArray()),          // 23
    (""u8.ToArray(), TransformType.Identity, "]"u8.ToArray()),          // 24
    (""u8.ToArray(), TransformType.Identity, " for "u8.ToArray()),      // 25
    (""u8.ToArray(), TransformType.OmitFirst3, ""u8.ToArray()),         // 26
    (""u8.ToArray(), TransformType.OmitLast2, ""u8.ToArray()),          // 27
    (""u8.ToArray(), TransformType.Identity, " a "u8.ToArray()),        // 28
    (""u8.ToArray(), TransformType.Identity, " that "u8.ToArray()),     // 29
    (" "u8.ToArray(), TransformType.UppercaseFirst, ""u8.ToArray()),    // 30
    (""u8.ToArray(), TransformType.Identity, ". "u8.ToArray()),         // 31
    ("."u8.ToArray(), TransformType.Identity, ""u8.ToArray()),          // 32
    (" "u8.ToArray(), TransformType.Identity, ", "u8.ToArray()),        // 33
    (""u8.ToArray(), TransformType.OmitFirst4, ""u8.ToArray()),         // 34
    (""u8.ToArray(), TransformType.Identity, " with "u8.ToArray()),     // 35
    (""u8.ToArray(), TransformType.Identity, "'"u8.ToArray()),          // 36
    (""u8.ToArray(), TransformType.Identity, " from "u8.ToArray()),     // 37
    (""u8.ToArray(), TransformType.Identity, " by "u8.ToArray()),       // 38
    (""u8.ToArray(), TransformType.OmitFirst5, ""u8.ToArray()),         // 39
    (""u8.ToArray(), TransformType.OmitFirst6, ""u8.ToArray()),         // 40
    (" the "u8.ToArray(), TransformType.Identity, ""u8.ToArray()),      // 41
    (""u8.ToArray(), TransformType.OmitLast4, ""u8.ToArray()),          // 42
    (""u8.ToArray(), TransformType.Identity, ". The "u8.ToArray()),     // 43
    (""u8.ToArray(), TransformType.UppercaseAll, ""u8.ToArray()),       // 44
    (""u8.ToArray(), TransformType.Identity, " on "u8.ToArray()),       // 45
    (""u8.ToArray(), TransformType.Identity, " as "u8.ToArray()),       // 46
    (""u8.ToArray(), TransformType.Identity, " is "u8.ToArray()),       // 47
    (""u8.ToArray(), TransformType.OmitLast7, ""u8.ToArray()),          // 48
    (""u8.ToArray(), TransformType.OmitLast1, "ing "u8.ToArray()),      // 49
    (""u8.ToArray(), TransformType.Identity, "\n\t"u8.ToArray()),       // 50
    (""u8.ToArray(), TransformType.Identity, ":"u8.ToArray()),          // 51
    (" "u8.ToArray(), TransformType.Identity, ". "u8.ToArray()),        // 52
    (""u8.ToArray(), TransformType.Identity, "ed "u8.ToArray()),        // 53
    (""u8.ToArray(), TransformType.OmitFirst9, ""u8.ToArray()),         // 54
    (""u8.ToArray(), TransformType.OmitFirst7, ""u8.ToArray()),         // 55
    (""u8.ToArray(), TransformType.OmitLast6, ""u8.ToArray()),          // 56
    (""u8.ToArray(), TransformType.Identity, "("u8.ToArray()),          // 57
    (""u8.ToArray(), TransformType.UppercaseFirst, ", "u8.ToArray()),   // 58
    (""u8.ToArray(), TransformType.OmitLast8, ""u8.ToArray()),          // 59
    (""u8.ToArray(), TransformType.Identity, " at "u8.ToArray()),       // 60
    (""u8.ToArray(), TransformType.Identity, "ly "u8.ToArray()),        // 61
    (" the "u8.ToArray(), TransformType.Identity, " of "u8.ToArray()),  // 62
    (""u8.ToArray(), TransformType.OmitLast5, ""u8.ToArray()),          // 63
    (""u8.ToArray(), TransformType.OmitLast9, ""u8.ToArray()),          // 64
    (" "u8.ToArray(), TransformType.UppercaseFirst, ", "u8.ToArray()),  // 65
    (""u8.ToArray(), TransformType.UppercaseFirst, "\""u8.ToArray()),   // 66
    ("."u8.ToArray(), TransformType.Identity, "("u8.ToArray()),         // 67
    (""u8.ToArray(), TransformType.UppercaseAll, " "u8.ToArray()),      // 68
    (""u8.ToArray(), TransformType.UppercaseFirst, "\">"u8.ToArray()),  // 69
    (""u8.ToArray(), TransformType.Identity, "=\""u8.ToArray()),        // 70
    (" "u8.ToArray(), TransformType.Identity, "."u8.ToArray()),         // 71
    (".com/"u8.ToArray(), TransformType.Identity, ""u8.ToArray()),      // 72
    (" the "u8.ToArray(), TransformType.Identity, " of the "u8.ToArray()), // 73
    (""u8.ToArray(), TransformType.UppercaseFirst, "'"u8.ToArray()),    // 74
    (""u8.ToArray(), TransformType.Identity, ". This "u8.ToArray()),    // 75
    (""u8.ToArray(), TransformType.Identity, ","u8.ToArray()),          // 76
    ("."u8.ToArray(), TransformType.Identity, " "u8.ToArray()),         // 77
    (""u8.ToArray(), TransformType.UppercaseFirst, "("u8.ToArray()),    // 78
    (""u8.ToArray(), TransformType.UppercaseFirst, "."u8.ToArray()),    // 79
    (""u8.ToArray(), TransformType.Identity, " not "u8.ToArray()),      // 80
    (" "u8.ToArray(), TransformType.Identity, "=\""u8.ToArray()),       // 81
    (""u8.ToArray(), TransformType.Identity, "er "u8.ToArray()),        // 82
    (" "u8.ToArray(), TransformType.UppercaseAll, " "u8.ToArray()),     // 83
    (""u8.ToArray(), TransformType.Identity, "al "u8.ToArray()),        // 84
    (" "u8.ToArray(), TransformType.UppercaseAll, ""u8.ToArray()),      // 85
    (""u8.ToArray(), TransformType.Identity, "='"u8.ToArray()),         // 86
    (""u8.ToArray(), TransformType.UppercaseAll, "\""u8.ToArray()),     // 87
    (""u8.ToArray(), TransformType.UppercaseFirst, ". "u8.ToArray()),   // 88
    (" "u8.ToArray(), TransformType.Identity, "("u8.ToArray()),         // 89
    (""u8.ToArray(), TransformType.Identity, "ful "u8.ToArray()),       // 90
    (" "u8.ToArray(), TransformType.UppercaseFirst, ". "u8.ToArray()),  // 91
    (""u8.ToArray(), TransformType.Identity, "ive "u8.ToArray()),       // 92
    (""u8.ToArray(), TransformType.Identity, "less "u8.ToArray()),      // 93
    (""u8.ToArray(), TransformType.UppercaseAll, "'"u8.ToArray()),      // 94
    (""u8.ToArray(), TransformType.Identity, "est "u8.ToArray()),       // 95
    (" "u8.ToArray(), TransformType.UppercaseFirst, "."u8.ToArray()),   // 96
    (""u8.ToArray(), TransformType.UppercaseAll, "\">"u8.ToArray()),    // 97
    (" "u8.ToArray(), TransformType.Identity, "='"u8.ToArray()),        // 98
    (""u8.ToArray(), TransformType.UppercaseFirst, ","u8.ToArray()),    // 99
    (""u8.ToArray(), TransformType.Identity, "ize "u8.ToArray()),       // 100
    (""u8.ToArray(), TransformType.UppercaseAll, "."u8.ToArray()),      // 101
    ("\xc2\xa0"u8.ToArray(), TransformType.Identity, ""u8.ToArray()),   // 102
    (" "u8.ToArray(), TransformType.Identity, ","u8.ToArray()),         // 103
    (""u8.ToArray(), TransformType.UppercaseFirst, "=\""u8.ToArray()),  // 104
    (""u8.ToArray(), TransformType.UppercaseAll, "=\""u8.ToArray()),    // 105
    (""u8.ToArray(), TransformType.Identity, "ous "u8.ToArray()),       // 106
    (""u8.ToArray(), TransformType.UppercaseAll, ", "u8.ToArray()),     // 107
    (""u8.ToArray(), TransformType.UppercaseFirst, "='"u8.ToArray()),   // 108
    (" "u8.ToArray(), TransformType.UppercaseFirst, ","u8.ToArray()),   // 109
    (" "u8.ToArray(), TransformType.UppercaseAll, "=\""u8.ToArray()),   // 110
    (" "u8.ToArray(), TransformType.UppercaseAll, ", "u8.ToArray()),    // 111
    (""u8.ToArray(), TransformType.UppercaseAll, ","u8.ToArray()),      // 112
    (""u8.ToArray(), TransformType.UppercaseAll, "("u8.ToArray()),      // 113
    (""u8.ToArray(), TransformType.UppercaseAll, ". "u8.ToArray()),     // 114
    (" "u8.ToArray(), TransformType.UppercaseAll, "."u8.ToArray()),     // 115
    (""u8.ToArray(), TransformType.UppercaseAll, "='"u8.ToArray()),     // 116
    (" "u8.ToArray(), TransformType.UppercaseAll, ". "u8.ToArray()),    // 117
    (" "u8.ToArray(), TransformType.UppercaseFirst, "=\""u8.ToArray()), // 118
    (" "u8.ToArray(), TransformType.UppercaseAll, "='"u8.ToArray()),    // 119
    (" "u8.ToArray(), TransformType.UppercaseFirst, "='"u8.ToArray()),  // 120
  ];

  /// <summary>
  /// Computes the distance parameters for a static dictionary reference.
  /// </summary>
  /// <param name="distance">The raw distance value from the stream.</param>
  /// <param name="maxDistance">The current maximum backward distance.</param>
  /// <param name="wordLength">Output: the word length.</param>
  /// <param name="wordIndex">Output: the word index within the length class.</param>
  /// <param name="transformIndex">Output: the transform to apply.</param>
  /// <returns><see langword="true"/> if this is a valid static dictionary reference.</returns>
  public static bool TryGetStaticReference(int distance, int maxDistance,
    out int wordLength, out int wordIndex, out int transformIndex) {
    wordLength = 0;
    wordIndex = 0;
    transformIndex = 0;

    if (distance <= maxDistance)
      return false; // Regular backward reference, not a dictionary reference

    var offset = distance - maxDistance - 1;

    // The static dictionary is addressed by: word_id = offset
    // word_id encodes (length, word_index, transform_index)
    // Layout: for each length L from 4 to 24:
    //   numWords(L) * NumTransforms entries

    for (var len = MinWordLength; len <= MaxWordLength; ++len) {
      var numWords = 1 << NumBitsPerLength[len - MinWordLength];
      var blockSize = numWords * NumTransforms;
      if (offset < blockSize) {
        wordLength = len;
        wordIndex = offset % numWords;
        transformIndex = offset / numWords;
        return true;
      }
      offset -= blockSize;
    }

    return false;
  }
}
