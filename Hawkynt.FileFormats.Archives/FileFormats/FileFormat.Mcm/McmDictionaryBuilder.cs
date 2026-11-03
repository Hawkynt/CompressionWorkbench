using System.Text;

namespace FileFormat.Mcm;

/// <summary>
/// Counts the words of an archive's text runs and turns them into the code-word dictionary the
/// text block's filter carries (<c>docs/MCM-ON-DISK.md</c>, "Dict filter").
/// </summary>
internal sealed class McmDictionaryBuilder {
  private const int MinWordLength = 3;
  private const int MaxWordLength = 32;
  private const int MinOccurrences = 5;
  private const int OneByteTarget = 40;
  private const int TwoByteReserve = 32;
  private const int Codes = McmDictionary.CodeCount;

  private sealed class Word(string text) {
    public readonly string Text = text;
    public long Normal;
    public long FirstCapital;
    public long AllCapital;

    public long Count => this.Normal + this.FirstCapital + this.AllCapital;
    public long Capitals => this.FirstCapital + this.AllCapital;

    public long Savings(int codeLength) {
      long length = this.Text.Length, count = this.Count;
      return length * count - (codeLength * count + this.Capitals + length + 1);
    }

    public long SavingsVersusLonger(int codeLength)
      => codeLength < 3 ? this.Savings(codeLength) - this.Savings(codeLength + 1) : this.Savings(codeLength);
  }

  private readonly byte[] _word = new byte[MaxWordLength];
  private int _wordLength;
  private readonly Dictionary<string, Word> _words = new(StringComparer.Ordinal);
  private readonly List<Word> _order = [];

  private static bool IsWordByte(byte value) => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= 128;

  private static bool IsUpper(byte value) => value is >= (byte)'A' and <= (byte)'Z';

  /// <summary>Feeds one byte of a text run.</summary>
  public void Add(byte value) {
    if (IsWordByte(value)) {
      if (this._wordLength < MaxWordLength)
        this._word[this._wordLength++] = value;
      return;
    }
    if (this._wordLength >= MinWordLength) {
      var word = this._word.AsSpan(0, this._wordLength);
      var upper = 0;
      foreach (var b in word)
        if (IsUpper(b))
          ++upper;
      var all = upper == word.Length;
      var first = !all && IsUpper(word[0]) && upper == 1;
      if (all)
        for (var i = 0; i < word.Length; ++i)
          word[i] = (byte)(word[i] + 32);
      else if (first)
        word[0] = (byte)(word[0] + 32);
      if (all || first || upper == 0) {
        var key = Encoding.Latin1.GetString(word);
        if (!this._words.TryGetValue(key, out var entry)) {
          entry = new Word(key);
          this._words.Add(key, entry);
          this._order.Add(entry);
        }
        if (all)
          ++entry.AllCapital;
        else if (first)
          ++entry.FirstCapital;
        else
          ++entry.Normal;
      }
    }
    this._wordLength = 0;
  }

  /// <summary>Chooses the one-, two- and three-byte code words exactly as mcm does.</summary>
  public McmDictionary Build() {
    var pairs = this._order.Where(w => w.Count >= MinOccurrences).ToArray();
    McmIntroSort.SortReversed(pairs, (a, b) => a.SavingsVersusLonger(1) < b.SavingsVersusLonger(1));
    var one = Math.Min(OneByteTarget, pairs.Length);
    while (one + 1 < pairs.Length) {
      var remain = Codes - one;
      var two = (long)(remain - 1) * (remain - 1);
      if (remain == 0 || two < pairs.Length - one)
        break;
      ++one;
    }
    var oneByte = pairs[..one];
    McmIntroSort.Sort(oneByte, Lexicographic);
    var rest = pairs[one..];
    McmIntroSort.SortReversed(rest, (a, b) => a.SavingsVersusLonger(2) < b.SavingsVersusLonger(2));
    var three = 0;
    for (; three + one < Codes - TwoByteReserve; ++three) {
      var count3 = (long)three * Codes * Codes;
      var count2 = (long)(Codes - three - one) * Codes;
      if (count2 + count3 >= rest.Length)
        break;
    }
    var twoCount = Codes - one - three;
    var twoTaken = (int)Math.Min((long)twoCount * Codes, rest.Length);
    var twoByte = rest[..twoTaken];
    rest = rest[twoTaken..];
    McmIntroSort.SortReversed(rest, (a, b) => a.SavingsVersusLonger(3) < b.SavingsVersusLonger(3));
    var keep = rest.Length;
    while (keep > 0 && rest[keep - 1].Savings(3) <= 0)
      --keep;
    var threeTaken = (int)Math.Min((long)three * Codes * Codes, keep);
    var threeByte = rest[..threeTaken];
    McmIntroSort.Sort(twoByte, Lexicographic);
    McmIntroSort.Sort(threeByte, Lexicographic);
    return new McmDictionary {
      OneByteCount = one,
      TwoByteCount = twoCount,
      ThreeByteCount = three,
      Words = [.. oneByte.Concat(twoByte).Concat(threeByte).Select(w => Encoding.Latin1.GetBytes(w.Text))],
    };
  }

  private static bool Lexicographic(Word a, Word b) => string.CompareOrdinal(a.Text, b.Text) < 0;
}
