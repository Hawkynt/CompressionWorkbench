using Compression.Tests.CodecProof;
using FileFormat.YEnc;

namespace Compression.Tests.YEnc;

/// <summary>
/// Third-party proof for yEnc against the Rust crate <c>yenc</c> 0.2.2 (aswaving,
/// https://github.com/aswaving/yenc, MIT OR Apache-2.0), run as a black box at 128 columns. Its
/// files below decode here with size and CRC-32 checked, and for two of these inputs our encoder
/// writes the same bytes; its decoder restored every file we wrote (text, mixed, PE, zeros, 0 B to
/// 70 KB).
/// </summary>
/// <remarks>
/// The crate doubles a dot that starts a line, which is NNTP transport stuffing rather than part of
/// yEnc 1.3. Read as a file, such a line is two encoded bytes, so the decoded data differs and the
/// CRC-32 refuses it; <see cref="GivenADotStuffedLine_WhenDecodedAsAFile_ThenTheCrcRefusesIt"/>
/// pins that.
/// </remarks>
[TestFixture]
public sealed class YEncCrateOracleTests {

  private const string YencCrateText1K =
    "PXliZWdpbiBsaW5lPTEyOCBzaXplPTEwMjQgbmFtZT1kYXRhDQqPnY2Lmo9KmZyOj5xKnpKPSpyLmJGPSo6ZkUqZnI6PnEqOmZFKnaOXjJmWSp" +
    "ufk42VSlo0m5+TjZVKlJ+Xmp1KmZyOj5xKlouko0phY1tjNJmgj5xKj52Ni5qPSpeZjo+WSo+djYuaj0qUn5eanUpbX2JdYjSQmaJKjZmYno+i" +
    "ng0KSpmgj5xKm5+TjZVKmZyOj5xKl5mOj5ZKjpmRSpmcjo+cSpaLpKNKm5+TjZVKnaOXjJmWSpCZokqOmZFKjJyZoZhKXF1hX2E0jZmYno+ink" +
    "qXmY6PlkqQmaJKj52Ni5qPSpSfl5qdSo6ZkUqQmaJKXVtgYWA0mZyOj5xKm5+TjZUNCkqWi6SjSpufk42VSpmgj5xKjJyZoZhKXWNfY180louk" +
    "o0qXmY6PlkqXmY6PlkpeYV9bXjSUn5eanUqZoI+cSpmcjo+cSo2ZmJ6Pop5KnpKPSo2ZmJ6Pop5KjJyZoZhKjJyZoZhKmZyOj5xKlJ+Xmp1KkJ" +
    "miSoycmaGYSpmcjo+cDQpKX19eXV00m5+TjZVKnpKPSpeZjo+WSmBdXV9cNJCZokqZnI6PnEqNmZiej6KeSpSfl5qdSpufk42VSpmcjo+cSpyL" +
    "mJGPSmFbXGFbNJSfl5qdSo6ZkUqQmaJKjpmRSpufk42VSp2jl4yZlkqZoI+cSo2ZmJ6Pop5KYWNbY1o0nA0Ki5iRj0qQmaJKnaOXjJmWSp2jl4" +
    "yZlkqNmZiej6KeSpeZjo+WSp2jl4yZlkpiYVtaYzSdo5eMmZZKlJ+Xmp1Klouko0qNmZiej6KeSpCZokqXmY6PlkqZoI+cSp2jl4yZlkqbn5ON" +
    "lUqZnI6PnEpjX1pcYjSOmZFKkJmiSpyLmJENCo9Klouko0qWi6SjSpeZjo+WSo2ZmJ6Pop5KXGNeXjSUn5eanUqci5iRj0qbn5ONlUqWi6SjSp" +
    "6Sj0qPnY2Lmo9KjJyZoZhKjpmRSltaYmBdNJ6Sj0qWi6SjSo6ZkUqdo5eMmZZKj52Ni5qPSo+djYuaj0qQmaJKmaCPnEqOmZFKDQqeko9KW2Jh" +
    "Ylw0j52Ni5qPSoycmaGYSpufk42VSpCZokqZnI6PnEqUn5eanUqXmY6PlkqQmaJKlJ+Xmp1KnIuYkY9KXGBhWls0nIuYkY9KnIuYkY9KjpmRSp" +
    "mgj5xKnaOXjJmWSoycmaGYSpyLmJGPSpmcjo+cSo+djYuaj0qZoA0Kj5xKmaCPnEqMnJmhmEqZoI+cSpmcjo+cSl1eYFxaNIycmaGYSpeZjo+W" +
    "Spufk42VSpSfl5qdSpmgj5xKlouko0qci5iRj0qQmaJKj52Ni5qPSpmgj5xKlouko0qXmY6PlkpeXF9dYzSbn5ONlUqbn5ONlUqdo5eMmZZKjZ" +
    "mYno8NCg0KPXllbmQgc2l6ZT0xMDI0IGNyYzMyPTc1YmRkODI5DQo=";

  private const string YencCrateMixed600 =
    "PXliZWdpbiBsaW5lPTEyOCBzaXplPTYwMCBuYW1lPWRhdGENCkNoZIemh6Zh+ulgf2T6GN79OVg5YYm0CBIx+gjpCALrPUpkCHMUuMTjAqbQxJ" +
    "WdvD1K+qSoqHaVc2THnVNPbkM5j2fQKEcCOfgnpwEg66aSoQ/a+VggvsJYs9JgQ/kMFoyrPUo5Vuf1ZYQSPUrqsVU+XfnEL8klFzbews6wAvAP" +
    "DQoUUxuTRsno/T1KIkU2osHwh/TL53uaID1KNgjpVHOmNj1Kf3ktTKICoq2PBiUUvEDlWt/+6ccLR+u417xGwwKckbDCIFfwt2qJAmi6oodDYo" +
    "dlQ+weHDumGFsWVfUUIBgwJDrO7T1K/cNO/afGrckq8SaAn4fQdugQWXjO+R0Z3w0KMlHJAjewtAsqMt6PE7DkA+m4c98ovdz9FvcRIJa1fwjW" +
    "MOpvjkOwQli0SGcwAoRaIyFAPUrQaxvg+hlzpgJNPX3T8uqH2srsrMsYIOEg4oWkPUolzALfXn3w0NmY5jdWywI9QL1cEC/QvTIYnOkINh69IO" +
    "/C4Tb6Qryam7pZwjMNCvindJPebzWjXk1sc7P9LT1KJkU9QFioURH/HlbpMOSa2PfCZBkiLrHQ4WuFfJuKqfA5SIL0Y4KVj8dBejxbhzbLksUV" +
    "NCXLGFvD7j1Nc4+qxSLH5q0Y3TrloL9b90GN0XmYIIAMzL9ScdFSdYjLK0r9pBWAYgQj0C1Ad3fd/FjtDQpwRcW21VhiWZWIj64cGFs7YmiHlT" +
    "M7H7NBYPfXaHWmGjkCAia8wfMSwclOwFPM6zJkqLIMpcRkUcV7aH6dRuF06NhXdukwBr8OME8eWR+zYgko/1Kh0VfiAYVTV5Pzu9r8NoWzF5Sz" +
    "DQo9eWVuZCBzaXplPTYwMCBjcmMzMj01NDBhMzBlZQ0K";

  private const string YencCrateAbracadabra =
    "PXliZWdpbiBsaW5lPTEyOCBzaXplPTIzIG5hbWU9ZGF0YQ0Ki4yci42LjouMnItKi4yci42LjouMnIsNCj15ZW5kIHNpemU9MjMgY3JjMzI9MD" +
    "UxMDBlNGUNCg==";

  private const string YencCrateZeros2000 =
    "PXliZWdpbiBsaW5lPTEyOCBzaXplPTIwMDAgbmFtZT1kYXRhDQoqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "Kg0KKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioNCioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "KioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqDQoqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "KioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKg0KKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "KioqKioqKioqKioqKioqKioqKioqKioqKioNCioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqDQoqKioqKioq" +
    "KioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKg0KKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "KioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioNCioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "KioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqDQoqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "KioqKioqKioqKioqKioqKg0KKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioNCioqKioqKioqKioqKioqKioq" +
    "KioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqDQoqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "KioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKg0KKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "KioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioNCioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
    "KioqKioqDQoqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKi" +
    "oqKioqKg0KPXllbmQgc2l6ZT0yMDAwIGNyYzMyPTAyYzlmNDQ0DQo=";

  private const string YencCrateDigits =
    "PXliZWdpbiBsaW5lPTEyOCBzaXplPTEzMzggbmFtZT1kYXRhDQpaW1xdXl9gYWJjWltcXV5fYGFiWltcXV5fYGFiY1pbXF1eX2BhYlpbXF1eX2" +
    "BhYmNaW1xdXl9gYWJaW1xdXl9gYWJjWltcXV5fYGFiWltcXV5fYGFiY1pbXF1eX2BhYlpbXF1eX2BhYmNaW1xdXl9gYWJaW1xdXl9gYWJjWltc" +
    "XQ0KXl9gYWJaW1xdXl9gYWJjWltcXV5fYGFiWltcXV5fYGFiY1pbXF1eX2BhYlpbXF1eX2BhYmNaW1xdXl9gYWJaW1xdXl9gYWJjWltcXV5fYG" +
    "FiWltcXV5fYGFiY1pbXF1eX2BhYlpbXF1eX2BhYmNaW1xdXl9gYWJaW1xdXl9gYWINCmNaW1xdXl9gYWJaW1xdXl9gYWJjWltcXV5fYGFiWltc" +
    "XV5fYGFiY1pbXF1eX2BhYlpbXF1eX2BhYmNaW1xdXl9gYWJaW1xdXl9gYWJjWltcXV5fYGFiWltcXV5fYGFiY1pbXF1eX2BhYlpbXF1eX2BhYm" +
    "NaW1xdXl9gYWIqKywtDQouLi8wMTIzNDU2Nzg5Ojs8PX0+P0BBQkNERUZHSElKS0xNTk9QUVJTVFVWV1hZWltcXV5fYGFiY2RlZmdoaWprbG1u" +
    "b3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWGh4iJiouMjY6PkJGSk5SVlpeYmZqbnJ2en6ChoqOkpaanqKmqqw0KrK2ur7CxsrO0tba3uLm6u7y9vr" +
    "/AwcLDxMXGx8jJysvMzc7P0NHS09TV1tfY2drb3N3e3+Dh4uPk5ebn6Onq6+zt7u/w8fLz9PX29/j5+vv8/f7/PUABAgMEBQYHCAk9SgsMPU0O" +
    "DxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygNCikqKywtLi8wMTIzNDU2Nzg5Ojs8PX0+P0BBQkNERUZHSElKS0xNTk9QUVJTVFVWV1hZWltcXV" +
    "5fYGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWGh4iJiouMjY6PkJGSk5SVlpeYmZqbnJ2en6ChoqOkpaanDQqoqaqrrK2u" +
    "r7CxsrO0tba3uLm6u7y9vr/AwcLDxMXGx8jJysvMzc7P0NHS09TV1tfY2drb3N3e3+Dh4uPk5ebn6Onq6+zt7u/w8fLz9PX29/j5+vv8/f7/PU" +
    "ABAgMEBQYHCAk9SgsMPU0ODxAREhMUFRYXGBkaGxwdHh8gISIjJA0KJSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8PX0+P0BBQkNERUZHSElKS0xN" +
    "Tk9QUVJTVFVWV1hZWltcXV5fYGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWGh4iJiouMjY6PkJGSk5SVlpeYmZqbnJ2en6" +
    "ChoqMNCqSlpqeoqaqrrK2ur7CxsrO0tba3uLm6u7y9vr/AwcLDxMXGx8jJysvMzc7P0NHS09TV1tfY2drb3N3e3+Dh4uPk5ebn6Onq6+zt7u/w" +
    "8fLz9PX29/j5+vv8/f7/PUABAgMEBQYHCAk9SgsMPU0ODxAREhMUFRYXGBkaGxwdHh8gDQohIiMkJSYnKClbXF1eX2BhYmNaW1xdXl9gYWJjW1" +
    "xdXl9gYWJjWltcXV5fYGFiY1tcXV5fYGFiY1pbXF1eX2BhYmNbXF1eX2BhYmNaW1xdXl9gYWJjW1xdXl9gYWJjWltcXV5fYGFiY1tcXV5fYGFi" +
    "Y1pbXF1eX2BhYmNbXF1eXw0KYGFiY1pbXF1eX2BhYmNbXF1eX2BhYmNaW1xdXl9gYWJjW1xdXl9gYWJjWltcXV5fYGFiY1tcXV5fYGFiY1pbXF" +
    "1eX2BhYmMNCj15ZW5kIHNpemU9MTMzOCBjcmMzMj03NzIwYjBkNw0K";

  private static IEnumerable<TestCaseData> CrateFiles() {
    yield return new TestCaseData(YencCrateText1K, CodecProofData.Text(1024)).SetName("GivenTheCratesText_WhenDecoded_ThenItIsTheSample");
    yield return new TestCaseData(YencCrateMixed600, CodecProofData.Mixed(600)).SetName("GivenTheCratesMixedData_WhenDecoded_ThenItIsTheSample");
    yield return new TestCaseData(YencCrateAbracadabra, "abracadabra abracadabra"u8.ToArray()).SetName("GivenTheCratesShortPhrase_WhenDecoded_ThenItIsThePhrase");
    yield return new TestCaseData(YencCrateZeros2000, new byte[2000]).SetName("GivenTheCratesZeros_WhenDecoded_ThenTheyAreRestored");
  }

  [TestCaseSource(nameof(CrateFiles)), Category("HappyPath")]
  public void DecodesTheCrate(string file, byte[] expected) {
    var (name, size, crc, data) = YEncDecoder.Decode(new MemoryStream(Convert.FromBase64String(file)));
    Assert.Multiple(() => {
      Assert.That(data, Is.EqualTo(expected));
      Assert.That(name, Is.EqualTo("data"));
      Assert.That(size, Is.EqualTo(expected.Length));
      Assert.That(crc, Is.EqualTo(Compression.Core.Checksums.Crc32.Compute(expected)));
    });
  }

  private static IEnumerable<TestCaseData> IdenticalFiles() {
    yield return new TestCaseData("abracadabra abracadabra"u8.ToArray(), YencCrateAbracadabra).SetName("GivenAShortPhrase_WhenEncoded_ThenItIsWhatTheCrateWrites");
    yield return new TestCaseData(new byte[2000], YencCrateZeros2000).SetName("GivenZeros_WhenEncoded_ThenItIsWhatTheCrateWrites");
  }

  [TestCaseSource(nameof(IdenticalFiles)), Category("HappyPath")]
  public void EncoderMatchesTheCrate(byte[] data, string file) {
    using var output = new MemoryStream();
    YEncEncoder.Encode(output, "data", data);
    Assert.That(output.ToArray(), Is.EqualTo(Convert.FromBase64String(file)));
  }

  [Test, Category("Exception")]
  public void GivenADotStuffedLine_WhenDecodedAsAFile_ThenTheCrcRefusesIt()
    => Assert.Throws<InvalidDataException>(() => YEncDecoder.Decode(new MemoryStream(Convert.FromBase64String(YencCrateDigits))));

  [Test, Category("Exception")]
  public void GivenACorruptByte_WhenDecoded_ThenTheCrcRefusesIt() {
    var file = Convert.FromBase64String(YencCrateText1K);
    file[60] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => YEncDecoder.Decode(new MemoryStream(file)));
  }

  [Test, Category("Exception")]
  public void GivenNoYend_WhenDecoded_ThenItIsRefusedAsTruncated() {
    var file = Convert.FromBase64String(YencCrateText1K);
    Assert.Throws<InvalidDataException>(() => YEncDecoder.Decode(new MemoryStream(file, 0, file.Length - 40)));
  }

  [Test, Category("Exception")]
  public void GivenNoYbegin_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => YEncDecoder.Decode(new MemoryStream("just some text"u8.ToArray())));

  [Test, Category("Exception")]
  public void GivenAnEscapeAtTheEndOfALine_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => YEncDecoder.Decode(new MemoryStream("=ybegin line=128 size=1 name=a\r\nk=\r\n=yend size=1\r\n"u8.ToArray())));
}
