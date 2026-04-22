#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Pst;

namespace Compression.Tests.Pst;

[TestFixture]
public class PstTests {

  // Synthetic PST header: 512 bytes with magic "!BDN", a chosen wVer, and root offsets.
  private static byte[] MakeMinimalPst(ushort wVer, ulong rootBbt, ulong rootNbt) {
    var blob = new byte[512];
    blob[0] = 0x21; blob[1] = 0x42; blob[2] = 0x44; blob[3] = 0x4E; // "!BDN"
    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), 0xDEADBEEFu);  // dwCRCPartial
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(8), 0x4D53);       // wMagicClient "SM"
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(10), wVer);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(12), 19);          // wVerClient

    if (wVer >= 0x15) {
      // Unicode: root starts at offset 180, ibRoot BBT at +16 (u64), NBT at +24 (u64).
      const int rootOff = 180;
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(rootOff + 16), rootBbt);
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(rootOff + 24), rootNbt);
    } else {
      // ANSI: root starts at offset 172, ibRoot BBT at +16 (u32), NBT at +20 (u32).
      const int rootOff = 172;
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(rootOff + 16), (uint)rootBbt);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(rootOff + 20), (uint)rootNbt);
    }
    return blob;
  }

  [Category("HappyPath")]
  [Test]
  public void List_UnicodePst_ReturnsCanonicalEntries() {
    var data = MakeMinimalPst(0x17, rootBbt: 0x1000UL, rootNbt: 0x2000UL);
    using var ms = new MemoryStream(data);
    var entries = new PstFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.pst"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "header.bin"), Is.True);

    var headerEntry = entries.Single(e => e.Name == "header.bin");
    Assert.That(headerEntry.OriginalSize, Is.EqualTo(512));
  }

  [Category("HappyPath")]
  [Test]
  public void Extract_UnicodePst_WritesFilesAndMetadata() {
    var data = MakeMinimalPst(0x17, rootBbt: 0x1000UL, rootNbt: 0x2000UL);
    var tmp = Path.Combine(Path.GetTempPath(), "pst_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PstFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.pst")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "header.bin")), Is.True);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=unicode"));
      Assert.That(ini, Does.Contain("version=23"));
      Assert.That(ini, Does.Contain("root_bbt_offset=4096"));
      Assert.That(ini, Does.Contain("root_nbt_offset=8192"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void List_AnsiPst_RecognizedAsAnsi() {
    var data = MakeMinimalPst(0x0E, rootBbt: 0x100UL, rootNbt: 0x200UL);
    var tmp = Path.Combine(Path.GetTempPath(), "pst_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PstFormatDescriptor().Extract(ms, tmp, null, null);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=ansi"));
      Assert.That(ini, Does.Contain("version=14"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_MagicAndExtensions() {
    var d = new PstFormatDescriptor();
    Assert.That(d.Extensions, Does.Contain(".pst"));
    Assert.That(d.Extensions, Does.Contain(".ost"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x21, 0x42, 0x44, 0x4E }));
  }
}
