using System.Text;
using Compression.Core.Dictionary.Ibm842;
using Compression.Tests.CodecProof;

namespace Compression.Tests.Ibm842;

/// <summary>
/// Third-party proof for IBM 842 (<c>BB_842</c>). The streams below were written by the Linux
/// kernel's software 842 compressor (<c>lib/842/842_compress.c</c>, Linux 6.x, GPL-2.0) compiled in
/// user space with stand-ins for the kernel headers and run as a black-box oracle
/// (<c>sw842_compress</c>). Our encoder reproduces them byte for byte and our decoder restores them;
/// <c>sw842_decompress</c> restored every stream we wrote, from 0 bytes to 4 MB.
/// </summary>
[TestFixture]
public sealed class Ibm842ReferenceTests {

  private const string KernelText1K =
    "AyubGwuDKQN4HJkZXIgdGhlAQOTC3M7KQMiW9nAwE8LDCBzeW0DE3thA4urSxkayAwCgWoYanVtcHMgA3uTIyuRA2MIHp5IDc5MTkKK3uweQMr" +
    "mxhYXBlIG1vIZCoAAgQCBqdW1wcyAxAanBmcBTM3vACBjb250ZXh0aGuzKRDUxscCEkIuRzAjI3s5AI7BEkIHEjq0sbWCaApmb3ggoJmJyb3cD" +
    "cQGRmbmpuFAY29udGV4dCC4XWVqa2Yhm4aNhBgiQMx2QGYTE2NzYKbw6ZSpioJA2ML08kCHgRBQJAxOTe7tzuE5NTk1CmyBnBNAlsNJM0Nxmpi" +
    "aBQ6PSH4aHQMAcPYD4DyH3IkVESra6foJULg4uU3V2K07GyKACA1NTQzMwpxwPymAgbW9kZWwgNnYRqZB25Zv6iVTVVdrctvuJPF0C/cCBRKEx" +
    "MjcxCmrYwOR1LP6erayK4FhDyVP4yY5MApynw8RcweT0FzeW1ib2z4uavvInk5SCogzg3MTCROEBIVccdEBOPkBlgZi25KFfaXuJCYpMqgyFcT" +
    "qEaQwMjgKF1Em5MLczoZSBRUh/CQSkjAiIyRZKZHJoac2DYcJgPeeLEBFdGiaA2je5dvO1XcQGJgcGxmkKdMdWSWjo7GcI0K4CwAkvgQHupdmU" +
    "2Tr1t1MTg3OEmQU7K+6wYlXCrQODQWsKgySEY6Pk8qilQgMjY3MDGX/P0E9hAAi4gfW/C4Yk4qnal2RJOUnN0gSvBPX1Re3EF/hwG8BAZmhsZG" +
    "AUxYLDLnMRbGujp3Ju4UFCuz7eZnNXbeu38tqyorSDQyNTMYuDTMxwmUJhCob24G95WAAAAAA=";

  private const string KernelMixed600 =
    "AMnx0uvi6+G4NC/NlU60O60Bph5cHm6/FbwOgH0N6/3tjBBwHW8k9UdNXINh8ppprc5LgBoPT8/JjWknQJ1zKSVEGQ9lAe038O7AfnfoH3X9sF" +
    "8aHflBYZ5d7SkwXRIKg2Gc/i7GKBBwB5Ze5Z2tdAODAhysUM8+aACz/32hlpMUgIbYxuXqKfFpAOT99p8HwNhgHiXxl3Kob1RA4e3AGb1+VJIH" +
    "wM4FVPAyJ4BsPEGy7n31SQBa7MLXUv53hAO4MdWyQ5M7AHJnhpj2LcaNAgL+wfSDwujIDhdOxnC9PIRA+dxj2FeX1ewO4G+hCkw+DTBMkmm+zk" +
    "HPgAMf8VnVdpky+BzF6dSZ/n32oAgnn9gNhorhAABFoy9MNdbIL+OSbX+k7LTB2ZvP7NkWq7wKwGwEVkGYYYAXRQ8eg2wtGAPn3FuCmQfG2Bod" +
    "6S+bBGJ1IMjAXbCgwoKhB3e1v7XC29cAPui2LU0U8amBXt14GllDsawJMy5gWmkwjuA5X+8Gekn7YoJi3DNAYknBxBIF8wE5z6lNILRFC3k0I0" +
    "JJBE6YHwfg3rFwH4n59X0LL8GBdOFdmzB13/AASHprdBW1JxAwP+MHjyxlHIFhrZZ0XUBIxAuhlC0TfWFfYKHuMZnE40llBATfxO3kH3WYBC7d" +
    "pUxzRdjBTp7d7K3FRSoChHpyhLXqEBAQab11qxxtfIKYDFk1Ns9IuBhow3NxlWXHAC9rXmWE8u4xAInB8utYSI+oIkXNs2tPkt8B4B+xsfklL5" +
    "IOiXnySWKaLBAEHT9EcT3NHQCebUT5Ucxy3AlX1cWpl+DbgJXkBiX0L/WJAcb/9qlDvTloLjXWyktacmRBYaQYtxPa1RPovPdPMAAAAAA=";

  // "abracadabra abracadabra": two templated groups, then 7 bytes of short data.
  private const string KernelAbracadabra = "AwsTkwsbCyMJGJyYSAAd7GwsjCxOTD7QUNPXAAAAAAA=";

  // 2000 zero bytes: one zeros code, then repeats of 64 and 60 groups.
  private const string KernelZeros2000 = "5v/f+/98eAAAAAAAAAAAAA==";

  private static byte[] Abracadabra => Encoding.ASCII.GetBytes("abracadabra abracadabra");

  private static IEnumerable<TestCaseData> Vectors() {
    yield return new TestCaseData(CodecProofData.Text(1024), KernelText1K).SetName("GivenTheSampleText_ThenItIsWhatSw842Writes");
    yield return new TestCaseData(CodecProofData.Mixed(600), KernelMixed600).SetName("GivenMixedData_ThenItIsWhatSw842Writes");
    yield return new TestCaseData(Abracadabra, KernelAbracadabra).SetName("GivenAShortPhraseWithATail_ThenItIsWhatSw842Writes");
    yield return new TestCaseData(new byte[2000], KernelZeros2000).SetName("GivenZeros_ThenItIsWhatSw842Writes");
  }

  [TestCaseSource(nameof(Vectors)), Category("HappyPath")]
  public void EncoderMatchesTheReference(byte[] input, string expected)
    => Assert.That(Ibm842Codec.Compress(input), Is.EqualTo(Convert.FromBase64String(expected)));

  [TestCaseSource(nameof(Vectors)), Category("HappyPath")]
  public void DecoderRestoresTheReference(byte[] expected, string stream)
    => Assert.That(Ibm842Codec.Decompress(Convert.FromBase64String(stream)), Is.EqualTo(expected));

  [Test, Category("HappyPath")]
  public void GivenTheBuildingBlock_WhenCompressing_ThenItWritesTheRawSw842Stream()
    => Assert.That(new Ibm842BuildingBlock().Compress(CodecProofData.Text(1024)), Is.EqualTo(Convert.FromBase64String(KernelText1K)));

  [Test, Category("EdgeCase")]
  public void GivenNoBytes_WhenCompressed_ThenItIsTheEndCodeAZeroCrcAndPadding()
    => Assert.That(Ibm842Codec.Compress([]), Is.EqualTo(new byte[] { 0xF0, 0, 0, 0, 0, 0, 0, 0 }));

  [TestCase(1, TestName = "GivenOneByte_WhenRoundTripped_ThenItIsShortData")]
  [TestCase(7, TestName = "GivenSevenBytes_WhenRoundTripped_ThenItIsShortData")]
  [TestCase(8, TestName = "GivenOneGroup_WhenRoundTripped_ThenItIsRestored")]
  [TestCase(9, TestName = "GivenOneGroupAndOneByte_WhenRoundTripped_ThenItIsRestored")]
  [TestCase(520, TestName = "GivenMoreThanTheTwoByteRing_WhenRoundTripped_ThenItIsRestored")]
  [TestCase(2056, TestName = "GivenMoreThanTheEightByteRing_WhenRoundTripped_ThenItIsRestored")]
  [TestCase(300_000, TestName = "GivenManyRingTurns_WhenRoundTripped_ThenItIsRestored")]
  [Category("Boundary")]
  public void RoundTrips(int size) {
    var data = CodecProofData.Text(size);
    var compressed = Ibm842Codec.Compress(data);
    Assert.Multiple(() => {
      Assert.That(compressed.Length % 8, Is.Zero);
      Assert.That(Ibm842Codec.Decompress(compressed), Is.EqualTo(data));
    });
  }

  [Test, Category("Exception")]
  public void GivenACorruptCrc_WhenDecoded_ThenItIsRefused() {
    var stream = Convert.FromBase64String(KernelAbracadabra);
    stream[^9] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => Ibm842Codec.Decompress(stream));
  }

  [Test, Category("Exception")]
  public void GivenATruncatedStream_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Ibm842Codec.Decompress(Convert.FromBase64String(KernelText1K).AsSpan(0, 100)));

  [Test, Category("Exception")]
  public void GivenTheUndefinedOperation_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Ibm842Codec.Decompress(new byte[] { 0xF8, 0, 0, 0, 0, 0, 0, 0 }));

  [Test, Category("Exception")]
  public void GivenAnIndexBeforeAnyOutput_WhenDecoded_ThenItIsRefused()
    // 0x19 (I8) then index 0: nothing has been written yet.
    => Assert.Throws<InvalidDataException>(() => Ibm842Codec.Decompress(new byte[] { 0xC8, 0, 0, 0, 0, 0, 0, 0 }));

  [Test, Category("Exception")]
  public void GivenARepeatBeforeAnyOutput_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Ibm842Codec.Decompress(new byte[] { 0xD8, 0, 0, 0, 0, 0, 0, 0 }));
}
