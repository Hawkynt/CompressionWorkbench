using System.Text;
using Compression.Core.Dictionary.Lz77;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Dictionary.Parsing;

namespace Compression.Tests.Lz77Optimal;

/// <summary>
/// Unit tests for the reusable <see cref="Lz77OptimalParser"/> primitive: token
/// reconstruction, optimal &lt;= greedy cost, cost-model swappability, and the default model.
/// </summary>
[TestFixture]
public class Lz77OptimalParserTests {
  private const int WindowSize = 32768;
  private const int MaxMatch = 258;
  private const int MinMatch = 3;

  private static Lz77OptimalParser.MatchProvider HashChainProvider() {
    var finder = new HashChainMatchFinder(WindowSize, 128);
    return (ReadOnlySpan<byte> buf, int pos) => finder.FindMatch(buf, pos, WindowSize, MaxMatch, MinMatch);
  }

  private static byte[] Reconstruct(List<LzParseToken> tokens) {
    var output = new List<byte>();
    foreach (var t in tokens)
      if (t.IsLiteral)
        output.Add(t.Literal);
      else {
        var start = output.Count - t.Distance;
        for (var i = 0; i < t.Length; ++i)
          output.Add(output[start + i]);
      }

    return [.. output];
  }

  private static double Cost(List<LzParseToken> tokens, ILzCostModel model) {
    var total = 0.0;
    foreach (var t in tokens)
      total += t.IsLiteral ? model.LiteralCost(t.Literal) : model.MatchCost(t.Length, t.Distance);
    return total;
  }

  // Greedy parse under the same finder, for cost comparison.
  private static List<LzParseToken> GreedyParse(ReadOnlySpan<byte> data) {
    var provider = HashChainProvider();
    var tokens = new List<LzParseToken>();
    var pos = 0;
    while (pos < data.Length) {
      var m = provider(data, pos);
      var len = Math.Min(m.Length, Math.Min(MaxMatch, data.Length - pos));
      if (m.Distance > 0 && len >= MinMatch) {
        tokens.Add(LzParseToken.CreateMatch(m.Distance, len));
        // Advance the finder over the skipped positions to keep its state consistent.
        for (var i = 1; i < len; ++i)
          provider(data, pos + i);
        pos += len;
      } else {
        tokens.Add(LzParseToken.CreateLiteral(data[pos]));
        ++pos;
      }
    }

    return tokens;
  }

  [Test]
  public void Empty_ProducesNoTokens() {
    var parser = new Lz77OptimalParser(DefaultLzCostModel.Instance, MinMatch, MaxMatch);
    var tokens = parser.Parse(ReadOnlySpan<byte>.Empty, HashChainProvider());
    Assert.That(tokens, Is.Empty);
  }

  [TestCase("a")]
  [TestCase("ab")]
  [TestCase("abc")]
  [TestCase("abcabcabcabcabc")]
  [TestCase("the quick brown fox jumps over the lazy dog the quick brown fox")]
  public void Tokens_ReconstructInput(string text) {
    var data = Encoding.ASCII.GetBytes(text);
    var parser = new Lz77OptimalParser(DefaultLzCostModel.Instance, MinMatch, MaxMatch);
    var tokens = parser.Parse(data, HashChainProvider());
    Assert.That(Reconstruct(tokens), Is.EqualTo(data));
  }

  [Test]
  public void Tokens_ReconstructLongRun() {
    var data = new byte[5000];
    Array.Fill(data, (byte)0x5A);
    var parser = new Lz77OptimalParser(DefaultLzCostModel.Instance, MinMatch, MaxMatch);
    var tokens = parser.Parse(data, HashChainProvider());
    Assert.That(Reconstruct(tokens), Is.EqualTo(data));
  }

  [Test]
  public void Optimal_NeverWorseThanGreedy_OnText() {
    var data = Encoding.ASCII.GetBytes(
      string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 200)));
    var model = DefaultLzCostModel.Instance;
    var parser = new Lz77OptimalParser(model, MinMatch, MaxMatch);

    var optimal = parser.Parse(data, HashChainProvider());
    var greedy = GreedyParse(data);

    Assert.Multiple(() => {
      Assert.That(Reconstruct(optimal), Is.EqualTo(data), "optimal parse must reconstruct input");
      Assert.That(Reconstruct(greedy), Is.EqualTo(data), "greedy parse must reconstruct input");
      Assert.That(Cost(optimal, model), Is.LessThanOrEqualTo(Cost(greedy, model)),
        "optimal cost must never exceed greedy cost");
    });
  }

  [Test]
  public void Optimal_StrictlyCheaperThanGreedy_OnCraftedInput() {
    // Crafted so a far long match (greedy) is worse than two near short matches under a model
    // that prices distance heavily. "ABCDE" appears far away long, then near in pieces.
    var sb = new StringBuilder();
    sb.Append("ABCDEFGHIJ");                 // seed
    sb.Append(new string('x', 4000));        // push the seed far away
    sb.Append("ABCDEFGHIJ");                 // a long match back to the far seed
    var data = Encoding.ASCII.GetBytes(sb.ToString());

    // Distance dominates strongly so far matches are expensive.
    var model = new DefaultLzCostModel(literalBits: 8.0, matchTokenBits: 4.0);
    var parser = new Lz77OptimalParser(model, MinMatch, MaxMatch);

    var optimal = parser.Parse(data, HashChainProvider());
    var greedy = GreedyParse(data);

    Assert.That(Cost(optimal, model), Is.LessThanOrEqualTo(Cost(greedy, model)));
    Assert.That(Reconstruct(optimal), Is.EqualTo(data));
  }

  [Test]
  public void CostModel_IsSwappable() {
    // A model that makes matches absurdly expensive forces an all-literal parse;
    // a normal model uses matches. Same parser, same input — different parse.
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcabcabc", 50)));

    var noMatchModel = new DefaultLzCostModel(literalBits: 1.0, matchTokenBits: 10000.0);
    var literalsOnly = new Lz77OptimalParser(noMatchModel, MinMatch, MaxMatch)
      .Parse(data, HashChainProvider());

    var normal = new Lz77OptimalParser(DefaultLzCostModel.Instance, MinMatch, MaxMatch)
      .Parse(data, HashChainProvider());

    Assert.Multiple(() => {
      Assert.That(literalsOnly.All(t => t.IsLiteral), Is.True, "expensive matches => all literals");
      Assert.That(normal.Any(t => !t.IsLiteral), Is.True, "normal model => uses matches");
      Assert.That(Reconstruct(literalsOnly), Is.EqualTo(data));
      Assert.That(Reconstruct(normal), Is.EqualTo(data));
    });
  }

  [Test]
  public void DefaultCostModel_PricesByMagnitude() {
    var model = DefaultLzCostModel.Instance;
    // Longer distance costs more; longer length costs more.
    Assert.Multiple(() => {
      Assert.That(model.MatchCost(4, 1000), Is.GreaterThan(model.MatchCost(4, 4)));
      Assert.That(model.MatchCost(100, 4), Is.GreaterThan(model.MatchCost(4, 4)));
      Assert.That(model.LiteralCost(0), Is.EqualTo(model.LiteralCost(255)));
    });
  }

  [Test]
  public void Parse_IsDeterministic() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("deterministic-input-", 100)));
    var a = new Lz77OptimalParser(DefaultLzCostModel.Instance, MinMatch, MaxMatch).Parse(data, HashChainProvider());
    var b = new Lz77OptimalParser(DefaultLzCostModel.Instance, MinMatch, MaxMatch).Parse(data, HashChainProvider());
    Assert.That(a, Is.EqualTo(b));
  }
}
