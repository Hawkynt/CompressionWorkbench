using Compression.Core.Deflate;

namespace Compression.Tests.Deflate;

[TestFixture]
public class OptimalParserTests {
  [Category("EdgeCase")]
  [Test]
  public void Parse_EmptyInput_ReturnsEmpty() {
    var hashChain = new ZopfliHashChain();
    int[] litLenLengths = DeflateConstants.GetStaticLiteralLengths();
    int[] distLengths = DeflateConstants.GetStaticDistanceLengths();

    var symbols = OptimalParser.Parse([], hashChain, litLenLengths, distLengths);
    Assert.That(symbols, Is.Empty);
  }

  [Category("EdgeCase")]
  [Test]
  public void Parse_ShortData_AllLiterals() {
    byte[] data = "AB"u8.ToArray();
    var hashChain = new ZopfliHashChain();
    int[] litLenLengths = DeflateConstants.GetStaticLiteralLengths();
    int[] distLengths = DeflateConstants.GetStaticDistanceLengths();

    var symbols = OptimalParser.Parse(data, hashChain, litLenLengths, distLengths);

    Assert.That(symbols.Length, Is.EqualTo(2));
    Assert.That(symbols.All(s => s.IsLiteral), Is.True);
  }

  [Category("HappyPath")]
  [Test]
  public void Parse_RepetitiveData_ContainsMatches() {
    byte[] data = "ABCABCABCABCABCABCABCABC"u8.ToArray();
    var hashChain = new ZopfliHashChain();
    int[] litLenLengths = DeflateConstants.GetStaticLiteralLengths();
    int[] distLengths = DeflateConstants.GetStaticDistanceLengths();

    var symbols = OptimalParser.Parse(data, hashChain, litLenLengths, distLengths);

    // Should have at least one match
    Assert.That(symbols.Any(s => !s.IsLiteral), Is.True);
    // Should have fewer symbols than data length (due to matches)
    Assert.That(symbols.Length, Is.LessThan(data.Length));
  }

  [Category("HappyPath")]
  [Test]
  public void Parse_SymbolsReconstructInput() {
    byte[] data = "ABCABCABCDEF"u8.ToArray();
    var hashChain = new ZopfliHashChain();
    int[] litLenLengths = DeflateConstants.GetStaticLiteralLengths();
    int[] distLengths = DeflateConstants.GetStaticDistanceLengths();

    var symbols = OptimalParser.Parse(data, hashChain, litLenLengths, distLengths);

    // Reconstruct data from symbols
    byte[] reconstructed = ReconstructFromSymbols(symbols, data);
    Assert.That(reconstructed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void Parse_SingleByte_OneLiteral() {
    byte[] data = [0x42];
    var hashChain = new ZopfliHashChain();
    int[] litLenLengths = DeflateConstants.GetStaticLiteralLengths();
    int[] distLengths = DeflateConstants.GetStaticDistanceLengths();

    var symbols = OptimalParser.Parse(data, hashChain, litLenLengths, distLengths);

    Assert.That(symbols.Length, Is.EqualTo(1));
    Assert.That(symbols[0].IsLiteral, Is.True);
    Assert.That(symbols[0].LitLen, Is.EqualTo(0x42));
  }

  [Category("EdgeCase")]
  [Test]
  public void Parse_AllZeros_UsesMatches() {
    byte[] data = new byte[256];
    var hashChain = new ZopfliHashChain();
    int[] litLenLengths = DeflateConstants.GetStaticLiteralLengths();
    int[] distLengths = DeflateConstants.GetStaticDistanceLengths();

    var symbols = OptimalParser.Parse(data, hashChain, litLenLengths, distLengths);

    // Highly compressible — should use matches
    Assert.That(symbols.Length, Is.LessThan(data.Length));

    // Verify reconstruction
    byte[] reconstructed = ReconstructFromSymbols(symbols, data);
    Assert.That(reconstructed, Is.EqualTo(data));
  }

  private static byte[] ReconstructFromSymbols(LzSymbol[] symbols, byte[] originalData) {
    var output = new List<byte>();
    foreach (var sym in symbols) {
      if (sym.IsLiteral)
        output.Add((byte)sym.LitLen);
      else {
        var start = output.Count - sym.Distance;
        for (int i = 0; i < sym.LitLen; ++i)
          output.Add(output[start + i]);
      }
    }
    return [.. output];
  }
}
