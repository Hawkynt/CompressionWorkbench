using Compression.Core.Dictionary.Shoco;
using Compression.Tests.CodecProof;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Third-party proof for <c>BB_Shoco</c>: the streams below were written by shoco's own
/// command-line tool (<c>shoco c</c>, https://github.com/Ed-von-Schleck/shoco, MIT, revision
/// <c>7c92fa9c4161ea97da90cc78e38cfb18476ba882</c>, default model). Our encoder writes the same
/// bytes and our decoder restores them; <c>shoco d</c> restored every stream we wrote, including
/// binary input with zero and high bytes.
/// </summary>
[TestFixture]
public sealed class ShocoReferenceTests {

  private const string ShocoText1K =
    "g69wZSDGuHIgkGUgzsFlILpnIMa4ciC6ZyBzeW2+bCBxdWljayAwCnF1aWNrIGp1bXBzIMa4ciBsYXp5IDc5MTkKb3aAIIOvcGUgtrhsIIOvcG" +
    "UganVtcHMgMTU4MzgKZm94INYLZXh0IG92gCBxdWljayDGuHIgtrhsILpnIMa4ciBsYXp5IHF1aWNrIHN5bb5sIGZveCC6ZyDflm4gMjM3NTcK" +
    "1gtleHQgtrhsIGZveCCDr3BlIGp1bXBzILpnIGZveCAzMTY3NgrGuHIgcXVpY2sgbGF6eSBxdWljayBvdoAg35ZuIDM5NTk1CmxhenkgtrhsIL" +
    "a4bCA0NzUxNApqdW1wcyBvdoAgxrhyINYLZXh0IJBlINYLZXh0IN+WbiDflm4gxrhyIGp1bXBzIGZveCDflm4gxrhyIDU1NDMzCnF1aWNrIJBl" +
    "ILa4bCA2MzM1Mgpmb3ggxrhyINYLZXh0IGp1bXBzIHF1aWNrIMa4ciDOwWUgNzEyNzEKanVtcHMgumcgZm94ILpnIHF1aWNrIHN5bb5sIG92gC" +
    "DWC2V4dCA3OTE5MArOwWUgZm94IHN5bb5sIHN5bb5sINYLZXh0ILa4bCBzeW2+bCA4NzEwOQpzeW2+bCBqdW1wcyBsYXp5INYLZXh0IGZveCC2" +
    "uGwgb3aAIHN5bb5sIHF1aWNrIMa4ciA5NTAyOAq6ZyBmb3ggzsFlIGxhenkgbGF6eSC2uGwg1gtleHQgMjk0NApqdW1wcyDOwWUgcXVpY2sgbG" +
    "F6eSCQZSCDr3BlIN+WbiC6ZyAxMDg2MwqQZSBsYXp5ILpnIHN5bb5sIIOvcGUgg69wZSBmb3ggb3aAILpnIJBlIDE4NzgyCoOvcGUg35ZuIHF1" +
    "aWNrIGZveCDGuHIganVtcHMgtrhsIGZveCBqdW1wcyDOwWUgMjY3MDEKzsFlIM7BZSC6ZyBvdoAgc3ltvmwg35ZuIM7BZSDGuHIgg69wZSBvdo" +
    "Agb3aAIN+WbiBvdoAgxrhyIDM0NjIwCt+WbiC2uGwgcXVpY2sganVtcHMgb3aAIGxhenkgzsFlIGZveCCDr3BlIG92gCBsYXp5ILa4bCA0MjUz" +
    "OQpxdWljayBxdWljayBzeW2+bCDWC2U=";

  private const string ShocoAbracadabra = "YWKf1utinyBhYp/W62Kf";

  private static IEnumerable<TestCaseData> Vectors() {
    yield return new TestCaseData(CodecProofData.Text(1024), ShocoText1K).SetName("GivenTheSampleText_ThenItIsWhatShocoWrites");
    yield return new TestCaseData("abracadabra abracadabra"u8.ToArray(), ShocoAbracadabra).SetName("GivenAShortPhrase_ThenItIsWhatShocoWrites");
  }

  [TestCaseSource(nameof(Vectors)), Category("HappyPath")]
  public void EncoderMatchesShoco(byte[] input, string expected)
    => Assert.That(new ShocoBuildingBlock().Compress(input), Is.EqualTo(Convert.FromBase64String(expected)));

  [TestCaseSource(nameof(Vectors)), Category("HappyPath")]
  public void DecoderRestoresShoco(byte[] expected, string stream)
    => Assert.That(new ShocoBuildingBlock().Decompress(Convert.FromBase64String(stream)), Is.EqualTo(expected));

  [Test, Category("EdgeCase")]
  public void GivenZeroAndHighBytes_WhenCompressed_ThenEachFollowsTheSentinel()
    // shoco_decompress reads 00 00 as a zero byte and 00 E9 as 0xE9.
    => Assert.That(new ShocoBuildingBlock().Compress(new byte[] { 0x61, 0x00, 0x62, 0xE9 }), Is.EqualTo(new byte[] { 0x61, 0x00, 0x00, 0x62, 0x00, 0xE9 }));

  [Test, Category("Exception")]
  public void GivenASentinelAtTheEnd_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => new ShocoBuildingBlock().Decompress([0x61, 0x00]));

  [Test, Category("Exception")]
  public void GivenAPackCutShort_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => new ShocoBuildingBlock().Decompress([0xE0, 0x00]));

  [Test, Category("Exception")]
  public void GivenAHeaderOfNoPack_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => new ShocoBuildingBlock().Decompress([0xF0, 0x00, 0x00, 0x00]));
}
