using System.Text;
using Compression.Registry;
using FileFormat.Srec;

namespace Compression.Tests.Srec;

[TestFixture]
public class SrecTests {

  private static readonly byte[] Ramp = [
    0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
    0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
    0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
  ];

  private static string NewTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "srec_" + Path.GetRandomFileName());
    Directory.CreateDirectory(dir);
    return dir;
  }

  // ── Writer → Reader round-trip per address width ─────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  [TestCase(2, '1', '9')]
  [TestCase(3, '2', '8')]
  [TestCase(4, '3', '7')]
  public void RoundTrip_AddressWidth(int width, char dataType, char termType) {
    var text = SrecWriter.Write(Ramp, baseAddress: 0, addressWidth: width);
    Assert.That(text, Does.StartWith("S0"));           // header record
    Assert.That(text, Does.Contain("S" + dataType));   // data records
    Assert.That(text, Does.Contain("S" + termType));   // termination

    var image = SrecReader.Read(text);
    Assert.That(image.ToFlatBinary(), Is.EqualTo(Ramp).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void AutoWidth_SelectsSmallestType() {
    Assert.Multiple(() => {
      Assert.That(SrecWriter.Write([1, 2, 3], baseAddress: 0x0000), Does.Contain("S1"));
      Assert.That(SrecWriter.Write([1, 2, 3], baseAddress: 0x01_0000), Does.Contain("S2"));
      Assert.That(SrecWriter.Write([1, 2, 3], baseAddress: 0x01_00_0000), Does.Contain("S3"));
    });
  }

  [Test, Category("EdgeCase")]
  public void Write_AddressTooWideForPinnedType_Throws() {
    Assert.That(() => SrecWriter.Write([1, 2, 3], baseAddress: 0x01_0000, addressWidth: 2),
      Throws.InstanceOf<ArgumentException>());
  }

  // ── Multi-block ──────────────────────────────────────────────────────────

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void MultiBlock_SeparateSegmentsPreserved() {
    var blockA = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
    var blockB = new byte[] { 0x11, 0x22, 0x33 };
    // Two independent images at a 0x1000 gap; the reader keeps them as two
    // segments and fills the gap on flatten.
    var text = SrecWriter.Write(blockA, baseAddress: 0x0000) +
               SrecWriter.Write(blockB, baseAddress: 0x1000);

    var image = SrecReader.Read(text);
    Assert.That(image.Segments, Has.Count.EqualTo(2));
    Assert.That(image.Segments[0].Address, Is.EqualTo(0x0000u));
    Assert.That(image.Segments[0].Data, Is.EqualTo(blockA).AsCollection);
    Assert.That(image.Segments[1].Address, Is.EqualTo(0x1000u));
    Assert.That(image.Segments[1].Data, Is.EqualTo(blockB).AsCollection);

    var flat = image.ToFlatBinary(fill: 0xFF);
    Assert.That(flat, Has.Length.EqualTo(0x1000 + blockB.Length));
    Assert.That(flat[0], Is.EqualTo(0xAA));
    Assert.That(flat[4], Is.EqualTo(0xFF)); // gap fill
    Assert.That(flat[0x1000], Is.EqualTo(0x11));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void ContiguousRecords_CoalesceIntoOneSegment() {
    // 40 bytes spans three 16-byte data records at base 0 — must merge.
    var data = new byte[40];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 3);
    var image = SrecReader.Read(SrecWriter.Write(data));
    Assert.That(image.Segments, Has.Count.EqualTo(1));
    Assert.That(image.ToFlatBinary(), Is.EqualTo(data).AsCollection);
  }

  // ── Checksum & structural validation ─────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Checksum_IsOnesComplementOfByteSum() {
    // S1 record, addr 0x0000, one data byte 0x00 → count=04, sum=04 → cksum=FB.
    var image = SrecReader.Read("S1040000" + "00" + "FB\nS9030000FC\n");
    Assert.That(image.ToFlatBinary(fill: 0), Is.EqualTo(new byte[] { 0x00 }).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Checksum_Mismatch_Throws() {
    Assert.That(() => SrecReader.Read("S1040000" + "00" + "FF\nS9030000FC\n"),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void MissingTermination_Throws() {
    Assert.That(() => SrecReader.Read("S0030000FC\nS1040000" + "00" + "FB\n"),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void EmptyData_ProducesEmptyBinary() {
    var image = SrecReader.Read(SrecWriter.Write([]));
    Assert.That(image.ToFlatBinary(), Is.Empty);
    Assert.That(image.DataRecordCount, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void StartAddress_FromTermination_IsCaptured() {
    var text = SrecWriter.Write(Ramp, baseAddress: 0, addressWidth: 2, startAddress: 0x1234);
    var image = SrecReader.Read(text);
    Assert.That(image.StartAddress, Is.EqualTo(0x1234u));
  }

  // ── Descriptor Create → List → Extract ───────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_CreateListExtract_FirmwareByteIdentical() {
    var rng = new Random(99);
    var firmware = new byte[500];
    rng.NextBytes(firmware);

    var desc = new SrecFormatDescriptor();
    using var archive = new MemoryStream();
    desc.Create(archive, [ArchiveInputInfo.InMemory("firmware.bin", firmware)], new FormatCreateOptions());

    archive.Position = 0;
    var names = desc.List(archive, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("firmware.bin"));

    var dir = NewTempDir();
    try {
      archive.Position = 0;
      desc.Extract(archive, dir, null, null);
      var extracted = File.ReadAllBytes(Path.Combine(dir, "firmware.bin"));
      Assert.That(extracted, Is.EqualTo(firmware).AsCollection);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_MagicSignature_IsS0() {
    var desc = new SrecFormatDescriptor();
    Assert.That(Encoding.ASCII.GetString(desc.MagicSignatures[0].Bytes), Is.EqualTo("S0"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsCreateCapability() {
    Assert.That(new SrecFormatDescriptor().Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }
}
