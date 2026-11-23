using System.Text;
using FileFormat.Cab;

namespace Compression.Tests.Cab;

/// <summary>
/// Member names of cabinets: UTF-8 when the UTF attribute says so, otherwise in a code page the cabinet
/// does not record. Microsoft's own OneNote packages store UTF-8 names (Cyrillic, accented Latin)
/// without the attribute; decoding those as ASCII turned every non-ASCII byte into '?', so different
/// sections of one package got the same name and overwrote each other on extraction.
/// </summary>
[TestFixture]
public class CabNameDecodingTests {

  [Test, Category("HappyPath")]
  public void Given_AsciiNameWithoutAttribute_When_Decoded_Then_Unchanged()
    => Assert.That(CabReader.DecodeName("General.one"u8.ToArray(), false), Is.EqualTo("General.one"));

  [Test, Category("HappyPath")]
  public void Given_Utf8NameWithAttribute_When_Decoded_Then_Utf8()
    => Assert.That(CabReader.DecodeName(Encoding.UTF8.GetBytes("Заметки.one"), true), Is.EqualTo("Заметки.one"));

  [Test, Category("Regression")]
  public void Given_Utf8NamesWithoutAttribute_When_Decoded_Then_DistinctUtf8Names() {
    var a = CabReader.DecodeName(Encoding.UTF8.GetBytes("Начало.one"), false);
    var b = CabReader.DecodeName(Encoding.UTF8.GetBytes("Задачи.one"), false);
    Assert.That((a, b), Is.EqualTo(("Начало.one", "Задачи.one")));
  }

  [Test, Category("EdgeCase")]
  public void Given_CodePageBytesThatAreNotUtf8_When_Decoded_Then_LatinOneByteForByte() {
    byte[] cp1252 = [(byte)'C', 0xE9, (byte)'.', (byte)'t', (byte)'x', (byte)'t']; // "Cé.txt" in Windows-1252
    Assert.That(CabReader.DecodeName(cp1252, false), Is.EqualTo("Cé.txt"));
    Assert.That(CabReader.DecodeName([0xC0, 0xC1], false), Is.Not.EqualTo(CabReader.DecodeName([0xC0, 0xC2], false)));
  }

  [Test, Category("Boundary")]
  public void Given_EmptyName_When_Decoded_Then_Empty()
    => Assert.That(CabReader.DecodeName([], false), Is.Empty);
}
