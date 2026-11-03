using Compression.Registry;
using FileFormat.Ecm;

namespace Compression.Tests.Ecm;

[TestFixture]
public class EcmTests {
  private static byte[] Encode(byte[] data) {
    using var output = new MemoryStream();
    EcmCodec.Encode(new MemoryStream(data), output);
    return output.ToArray();
  }

  private static byte[] Decode(byte[] data) {
    using var output = new MemoryStream();
    EcmCodec.Decode(new MemoryStream(data), output);
    return output.ToArray();
  }

  [Test]
  public void GivenEmptyInput_WhenEncoded_ThenTheStreamIsMagicEndMarkerAndZeroEdc() {
    Assert.That(Encode([]), Is.EqualTo(new byte[] { 0x45, 0x43, 0x4D, 0x00, 0xFC, 0xFF, 0xFF, 0xFF, 0x3F, 0, 0, 0, 0 }));
    Assert.That(Decode(Encode([])), Is.Empty);
  }

  [TestCase(1)]
  [TestCase(31)]
  [TestCase(32)]
  [TestCase(2351)]
  [TestCase(2352)]
  [TestCase(5000)]
  public void GivenArbitraryBytes_WhenRoundTripped_ThenIdentical(int length) {
    var data = new byte[length];
    new Random(length).NextBytes(data);
    Assert.That(Decode(Encode(data)), Is.EqualTo(data));
  }

  [Test]
  public void GivenAMixedCdImage_WhenRoundTripped_ThenIdenticalAndSectorsAreModelled() {
    var image = EcmFixtures.MixedImage();
    var ecm = Encode(image);
    Assert.That(Decode(ecm), Is.EqualTo(image));
    // 6 Mode-1 sectors save 301 bytes each, 4 Form-1 save 284, 3 Form-2 save 8, the zero sector ~2.3 KB.
    Assert.That(ecm.Length, Is.LessThan(image.Length - 6 * 290 - 4 * 270));
  }

  [Test]
  public void GivenSectorsOnly_WhenEncoded_ThenEachTypeUsesItsRecord() {
    var random = new Random(7);
    var mode1 = EcmFixtures.Mode1(0, random);
    var ecm = Encode(mode1);
    // header byte: type 1, count-1 = 0 -> 0x01; then 3 address bytes + 2048 data.
    Assert.That(ecm[4], Is.EqualTo(0x01));
    Assert.That(ecm.Length, Is.EqualTo(4 + 1 + 3 + 2048 + 5 + 4));

    var form1 = EcmFixtures.Mode2Form1(0, random);
    ecm = Encode(form1);
    // 16 literal header bytes (type 0, count-1 = 15 -> 0x3C), then type 2 record (0x02).
    Assert.That(ecm[4], Is.EqualTo(0x3C));
    Assert.That(ecm[4 + 1 + 16], Is.EqualTo(0x02));
    Assert.That(ecm.Length, Is.EqualTo(4 + 1 + 16 + 1 + 4 + 2048 + 5 + 4));
  }

  [Test]
  public void GivenALargeImage_WhenRoundTripped_ThenRecordsSplitAndStayIdentical() {
    var random = new Random(99);
    using var ms = new MemoryStream();
    for (var i = 0; i < 2200; ++i) ms.Write(EcmFixtures.Mode1(i, random)); // > 4 MiB payload
    var image = ms.ToArray();
    Assert.That(Decode(Encode(image)), Is.EqualTo(image));
  }

  [Test]
  public void GivenNoMagic_WhenDecoded_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => Decode("ECX\0"u8.ToArray()));

  [Test]
  public void GivenTooShortInput_WhenDecoded_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => Decode("EC"u8.ToArray()));

  [Test]
  public void GivenATruncatedStream_WhenDecoded_ThenInvalidData() {
    var ecm = Encode(EcmFixtures.MixedImage());
    Assert.Throws<InvalidDataException>(() => Decode(ecm[..(ecm.Length / 2)]));
    Assert.Throws<InvalidDataException>(() => Decode(ecm[..^2]));
  }

  [Test]
  public void GivenACorruptedPayloadByte_WhenDecoded_ThenTheChecksumRefusesIt() {
    var ecm = Encode(EcmFixtures.MixedImage());
    ecm[200] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => Decode(ecm));
  }

  [Test]
  public void GivenACountWiderThan32Bits_WhenDecoded_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => Decode([0x45, 0x43, 0x4D, 0x00, 0xFC, 0xFF, 0xFF, 0xFF, 0x7F]));

  [Test]
  public void GivenTheDescriptor_WhenUsedThroughTheRegistry_ThenItRoundTrips() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var descriptor = FormatRegistry.GetById("Ecm");
    Assert.That(descriptor, Is.InstanceOf<EcmFormatDescriptor>());
    var ops = (IStreamFormatOperations)descriptor!;
    var image = EcmFixtures.MixedImage(5);
    using var packed = new MemoryStream();
    ops.Compress(new MemoryStream(image), packed);
    using var unpacked = new MemoryStream();
    ops.Decompress(new MemoryStream(packed.ToArray()), unpacked);
    Assert.That(unpacked.ToArray(), Is.EqualTo(image));
  }
}
