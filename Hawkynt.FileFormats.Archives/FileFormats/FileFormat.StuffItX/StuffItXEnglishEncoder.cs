namespace FileFormat.StuffItX;

/// <summary>
/// Applies StuffIt X's English preprocessor (preprocess id 0), the inverse of
/// <see cref="StuffItXEnglish.Decode"/>: letter runs that are dictionary words (as written, with a
/// capital first letter, or all in capitals) become a code byte and the word number, and every other
/// byte passes through, run through the same sentence-start case model so the decoder flips it back.
/// </summary>
internal static class StuffItXEnglishEncoder {
  // the decoder copies a word and the byte after it into a 64-byte buffer
  private const int MaxWordLength = 62;

  private static readonly Lazy<Dictionary<string, int>> Index = new(Build);

  private static Dictionary<string, int> Build() {
    var map = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 0; i < StuffItXEnglish.WordCount; ++i) {
      var word = StuffItXEnglish.Word(i);
      if (word.Length is 0 or > MaxWordLength || !IsLetters(word)) continue;
      map.TryAdd(word, i); // the lowest number has the shortest code
    }
    return map;
  }

  private static bool IsLetters(string s) {
    foreach (var c in s)
      if (!IsLetter(c)) return false;
    return true;
  }

  private static bool IsLetter(int c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
  private static bool IsLower(int c) => c is >= 'a' and <= 'z';
  private static int Flip(int c) => c ^ 0x20;

  /// <summary>The filtered form of <paramref name="data"/>.</summary>
  public static byte[] Encode(byte[] data) {
    var index = Index.Value;
    Span<bool> used = stackalloc bool[256];
    foreach (var b in data) used[b] = true;
    // four distinct non-letter code bytes, unused ones first so that little needs escaping
    var codes = new List<int>(4);
    foreach (var pass in (ReadOnlySpan<bool>)[false, true])
      for (var c = 255; c >= 0 && codes.Count < 4; --c)
        if (!IsLetter(c) && used[c] == pass && !codes.Contains(c)) codes.Add(c);
    int escape = codes[0], wordCode = codes[1], first = codes[2], upper = codes[3];

    var output = new MemoryStream(data.Length + 4);
    output.WriteByte((byte)escape);
    output.WriteByte((byte)wordCode);
    output.WriteByte((byte)first);
    output.WriteByte((byte)upper);

    var caseFlag = true;
    Span<byte> digits = stackalloc byte[8];
    var p = 0;
    while (p < data.Length) {
      if (IsLetter(data[p])) {
        var end = p;
        while (end < data.Length && IsLetter(data[end])) ++end;
        if (TryWord(index, data.AsSpan(p, end - p), caseFlag, wordCode, first, upper, out var code, out var number)) {
          var count = 0;
          for (var n = number; n > 0;) {
            var d = n % 52;
            if (d == 0) d = 52;
            digits[count++] = (byte)(d <= 26 ? 'a' + d - 1 : 'A' + d - 27);
            n = (n - d) / 52;
          }
          if (1 + count < end - p) {
            output.WriteByte((byte)code);
            for (var k = count - 1; k >= 0; --k) output.WriteByte(digits[k]);
            if (end < data.Length) {
              int next = data[end];
              if (next == escape) output.WriteByte((byte)escape);
              output.WriteByte((byte)next);
              caseFlag = next is '.' or '?' or '!';
              ++end;
            }
            p = end;
            continue;
          }
        }
        for (; p < end; ++p) Literal(output, data[p], ref caseFlag, escape, wordCode, first, upper);
        continue;
      }
      Literal(output, data[p++], ref caseFlag, escape, wordCode, first, upper);
    }
    return output.ToArray();
  }

  private static void Literal(MemoryStream output, int c, ref bool caseFlag, int escape, int wordCode, int first, int upper) {
    if (c == escape || c == wordCode || c == first || c == upper) {
      output.WriteByte((byte)escape);
      output.WriteByte((byte)c);
      caseFlag = false;
      return;
    }
    if (caseFlag && IsLetter(c)) {
      output.WriteByte((byte)Flip(c));
      caseFlag = false;
      return;
    }
    output.WriteByte((byte)c);
    if (c is '.' or '?' or '!') caseFlag = true;
    else if (c is not (' ' or '\n' or '\r' or '\t')) caseFlag = false;
  }

  /// <summary>
  /// Finds the code and word number whose expansion, after the decoder's case flip at a sentence
  /// start, is <paramref name="run"/>.
  /// </summary>
  private static bool TryWord(Dictionary<string, int> index, ReadOnlySpan<byte> run, bool caseFlag, int wordCode, int first, int upper, out int code, out int number) {
    code = number = 0;
    if (run.Length > MaxWordLength) return false;
    Span<char> pre = stackalloc char[run.Length];
    for (var i = 0; i < run.Length; ++i) pre[i] = (char)run[i];
    if (caseFlag) pre[0] = (char)Flip(pre[0]);

    var lookup = index.GetAlternateLookup<ReadOnlySpan<char>>();
    if (lookup.TryGetValue(pre, out number)) {
      code = wordCode;
      return true;
    }
    Span<char> lower = stackalloc char[run.Length];
    for (var i = 0; i < run.Length; ++i) lower[i] = (char)(pre[i] | 0x20);
    // capitalised: only the first letter is upper case, and the dictionary word starts lower case
    if (!IsLower(pre[0]) && lower[1..].SequenceEqual(pre[1..]) && lookup.TryGetValue(lower, out number)) {
      code = first;
      return true;
    }
    // all capitals of an all-lower-case dictionary word
    var allUpper = true;
    foreach (var c in pre)
      if (IsLower(c)) { allUpper = false; break; }
    if (allUpper && lookup.TryGetValue(lower, out number)) {
      code = upper;
      return true;
    }
    return false;
  }
}
