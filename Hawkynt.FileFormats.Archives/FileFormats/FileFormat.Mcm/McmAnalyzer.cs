namespace FileFormat.Mcm;

/// <summary>
/// mcm's content detector: cuts a file into text and binary runs (<c>docs/MCM-ON-DISK.md</c>,
/// "Analyzer"). Wave detection is deliberately left out — mcm's Wav16 decoder cannot restore what
/// its encoder writes, so stereo PCM is kept in binary runs that mcm can read back.
/// </summary>
internal static class McmAnalyzer {
  private const int Window = 256 * 1024;
  private const int MinTextLength = 64;

  private static readonly bool[] Forbidden = BuildForbidden();

  private static bool[] BuildForbidden() {
    var table = new bool[256];
    foreach (var b in (ReadOnlySpan<byte>)[0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 14, 15, 16, 17, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31])
      table[b] = true;
    return table;
  }

  private static int Space(int value) => value is >= 9 and <= 13 or 32 ? 1 : 0;

  private static bool IsDigit(int value) => value is >= '0' and <= '9';

  private static bool IsWordOrArt(int value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= 128 or '|' or '_' or '-';

  /// <summary>Runs of one file, merged as mcm merges them; text bytes go to <paramref name="words"/>.</summary>
  public static List<(McmBlockProfile Profile, long Length)> Analyze(ReadOnlySpan<byte> data, McmDictionaryBuilder words) {
    var runs = new List<(McmBlockProfile Profile, long Length)>();
    uint lastWord = 0;
    var position = 0;
    while (position < data.Length) {
      var window = data.Slice(position, Math.Min(Window, data.Length - position));
      var (profile, length) = Detect(window, ref lastWord);
      if (profile == McmBlockProfile.Text)
        foreach (var b in window[..length])
          words.Add(b);
      position += length;
      if (runs.Count > 0 && runs[^1].Profile == profile)
        runs[^1] = (profile, runs[^1].Length + length);
      else
        runs.Add((profile, length));
    }
    return runs;
  }

  private static (McmBlockProfile Profile, int Length) Detect(ReadOnlySpan<byte> window, ref uint lastWord) {
    var binary = 0;
    while (binary < window.Length) {
      var utf8Pending = 0;
      int text = 0, spaces = 0, wordLength = 0, numberLength = 0, score = 0;
      while (binary + text < window.Length) {
        int c = window[binary + text];
        lastWord = (lastWord << 8) | (uint)c;
        if (!Utf8Step(c, ref utf8Pending) || Forbidden[c])
          break;
        ++text;
        var last = (int)((lastWord >> 8) & 0xFF);
        score += Space(c);
        if (last != c) {
          if (IsWordOrArt(c)) {
            ++wordLength;
            score += Space(last) * 10;
          } else if (wordLength != 0) {
            if (wordLength is >= 3 and < 32)
              score += wordLength * 3;
            score += Space(c) * 10;
            wordLength = 0;
          }
          numberLength = IsDigit(c) ? numberLength + 1 : 0;
          if (numberLength is >= 1 and <= 12)
            ++score;
          spaces += Space(c);
        } else if (Space(c) == 0 && !IsDigit(c))
          --score;
      }
      if (text > MinTextLength && spaces * 100 > text && score > text) {
        if (binary == 0)
          return (McmBlockProfile.Text, text);
        break;
      }
      binary += text;
      if (binary >= window.Length)
        break;
      ++binary;
    }
    return (McmBlockProfile.Binary, binary);
  }

  /// <summary>One step of mcm's checking UTF-8 decoder; false on an error.</summary>
  private static bool Utf8Step(int c, ref int pending) {
    if (pending > 0) {
      --pending;
      return (c >> 6) == 2;
    }
    if ((c & 0x80) == 0)
      return true;
    pending = (c & 0xFE) == 0xFC ? 5 : (c & 0xFC) == 0xF8 ? 4 : (c & 0xF8) == 0xF0 ? 3 : (c & 0xF0) == 0xE0 ? 2 : (c & 0xE0) == 0xC0 ? 1 : -1;
    if (pending < 0) {
      pending = 0;
      return false;
    }
    return true;
  }
}
