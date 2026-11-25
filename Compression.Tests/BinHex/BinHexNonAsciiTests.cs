using System.Text;
using FileFormat.BinHex;

namespace Compression.Tests.BinHex;

/// <summary>A character outside the 7-bit BinHex alphabet in the encoded data is rejected, not a crash.</summary>
[TestFixture]
public class BinHexNonAsciiTests {

  private static string Encoded() {
    using var ms = new MemoryStream();
    BinHexWriter.Write(ms, "test.txt", "Hello, BinHex! Hello, BinHex!"u8.ToArray());
    return Encoding.ASCII.GetString(ms.ToArray());
  }

  /// <summary>Replaces the third character after the opening colon.</summary>
  private static MemoryStream WithCharacter(char c, Encoding encoding) {
    var text = Encoded();
    var at = text.IndexOf(':') + 3;
    text = text[..at] + c + text[(at + 1)..];
    return new MemoryStream(encoding.GetBytes(text));
  }

  [Category("Boundary")]
  [TestCase('\u007F')]
  [TestCase('\u0080')]
  [TestCase('é')]
  [TestCase('ÿ')]
  [TestCase('€')]
  public void Given_CharacterOutsideTheAlphabet_When_Decoded_Then_InvalidData(char c)
    => Assert.Throws<InvalidDataException>(() => BinHexReader.Decode(WithCharacter(c, Encoding.UTF8)));

  [Category("Boundary")]
  [Test]
  public void Given_InvalidUtf8Byte_When_Decoded_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => BinHexReader.Decode(WithCharacter('é', Encoding.Latin1)));

  [Category("Boundary")]
  [TestCase(1)]
  [TestCase(2)]
  public void Given_NonAsciiInTheTrailingGroup_When_Decoded_Then_InvalidData(int fromEnd) {
    var text = Encoded();
    var end = text.LastIndexOf(':');
    var at = end - fromEnd;
    text = text[..at] + 'é' + text[(at + 1)..];
    Assert.Throws<InvalidDataException>(() => BinHexReader.Decode(new MemoryStream(Encoding.UTF8.GetBytes(text))));
  }
}
