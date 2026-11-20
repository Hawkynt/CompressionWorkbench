using System.Text;
using FileFormat.StuffItX;

namespace Compression.Tests.StuffItX;

/// <summary>
/// The English preprocessor's filter rules on hand-made streams (the dictionary itself is pinned by
/// its CRC-32 when it loads); real archives are covered by StuffItXLocalSampleTests.
/// </summary>
[TestFixture]
public class StuffItXEnglishTests {
  // escape 1, word 2, capitalised word 3, upper-case word 4
  private static string Decode(params object[] parts) {
    var bytes = new List<byte> { 1, 2, 3, 4 };
    foreach (var p in parts) {
      if (p is string s) bytes.AddRange(Encoding.Latin1.GetBytes(s));
      else bytes.Add(Convert.ToByte(p));
    }
    return Encoding.Latin1.GetString(StuffItXEnglish.Decode([.. bytes], 1 << 20));
  }

  [Test, Category("HappyPath")]
  public void GivenTheDictionary_WhenLoaded_ThenItPassesItsCrcAndHasWords() {
    Assert.That(StuffItXEnglish.Word(0), Is.Not.Empty);
    Assert.That(StuffItXEnglish.Word(100365), Is.Not.Empty);
  }

  [Test, Category("HappyPath")]
  public void GivenLiteralText_WhenDecoded_ThenEverySentenceStartFlipsCase()
    => Assert.That(Decode("Hello. World! x"), Is.EqualTo("hello. world! X"));

  [Test, Category("HappyPath")]
  public void GivenAnEscapedByte_WhenDecoded_ThenItPassesAndEndsTheSentenceStart()
    => Assert.That(Decode(1, 2, "Ab"), Is.EqualTo("\u0002Ab"));

  [Test, Category("HappyPath")]
  public void GivenWordCodes_WhenDecoded_ThenTheDictionaryWordsComeBackInTheirCase() {
    var w = StuffItXEnglish.Word(1); // word number 1 is spelled 'a'
    // an upper-case word lowers every byte by 32, a capitalised one only the first
    var upper = new string(w.Select(c => (char)(byte)(c - 32)).ToArray());
    var capital = (char)(byte)(w[0] - 32) + w[1..];
    // the escape before 'x' ends the sentence start, so no case flip; the byte after the word is kept
    Assert.That(Decode(1, "x", 2, "a "), Is.EqualTo("x" + w + " "));
    Assert.That(Decode(1, "x", 4, "a "), Is.EqualTo("x" + upper + " "));
    Assert.That(Decode(1, "x", 3, "a "), Is.EqualTo("x" + capital + " "));
    // at the very start the first letter flips: the plain word code comes back capitalised
    Assert.That(Decode(2, "a."), Is.EqualTo(capital + "."));
  }

  [Test, Category("Exception")]
  public void GivenAWordNumberPastTheDictionary_WhenDecoded_ThenRefused()
    => Assert.That(() => Decode(2, "ZZZ "), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exception")]
  public void GivenAStreamShorterThanItsCodes_WhenDecoded_ThenRefused()
    => Assert.That(() => StuffItXEnglish.Decode([1, 2], 10), Throws.InstanceOf<InvalidDataException>());
}
