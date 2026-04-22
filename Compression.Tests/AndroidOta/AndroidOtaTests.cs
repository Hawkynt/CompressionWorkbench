using System.Buffers.Binary;

namespace Compression.Tests.AndroidOta;

[TestFixture]
public class AndroidOtaTests {

  // A minimal synthetic CrAU payload: 24-byte header + fake manifest + fake signature + fake data.
  private static byte[] BuildSyntheticPayload(byte[] manifest, byte[] signature, byte[] data, ulong version = 2UL) {
    using var ms = new MemoryStream();
    ms.Write("CrAU"u8);
    Span<byte> buf = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(buf, version);
    ms.Write(buf);
    BinaryPrimitives.WriteUInt64BigEndian(buf, (ulong)manifest.Length);
    ms.Write(buf);
    Span<byte> buf4 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)signature.Length);
    ms.Write(buf4);
    ms.Write(manifest);
    ms.Write(signature);
    ms.Write(data);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.AndroidOta.AndroidOtaFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("AndroidOta"));
    // .bin is too generic (BinCue owns it); AndroidOta detects via CrAU magic only.
    Assert.That(d.Extensions, Is.Empty);
    Assert.That(d.DefaultExtension, Is.EqualTo(".bin"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("CrAU"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsCanonicalEntries() {
    var manifest = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    var signature = new byte[] { 0xAA, 0xBB, 0xCC };
    var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
    var payload = BuildSyntheticPayload(manifest, signature, data);

    var desc = new FileFormat.AndroidOta.AndroidOtaFormatDescriptor();
    using var ms = new MemoryStream(payload);
    var entries = desc.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(5));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.bin"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
    Assert.That(entries[2].Name, Is.EqualTo("manifest.pb"));
    Assert.That(entries[2].OriginalSize, Is.EqualTo(manifest.Length));
    Assert.That(entries[3].Name, Is.EqualTo("metadata_signature.bin"));
    Assert.That(entries[3].OriginalSize, Is.EqualTo(signature.Length));
    Assert.That(entries[4].Name, Is.EqualTo("data.bin"));
    Assert.That(entries[4].OriginalSize, Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesAllCanonicalFiles() {
    var manifest = new byte[] { 1, 2, 3, 4 };
    var signature = new byte[] { 0x55, 0x66 };
    var data = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
    var payload = BuildSyntheticPayload(manifest, signature, data, version: 42);

    var desc = new FileFormat.AndroidOta.AndroidOtaFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), "ota_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(payload);
      desc.Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.bin")), Is.True);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.bin")), Is.EqualTo(payload));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "manifest.pb")), Is.EqualTo(manifest));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "metadata_signature.bin")), Is.EqualTo(signature));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "data.bin")), Is.EqualTo(data));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("version=42"));
      Assert.That(meta, Does.Contain("manifest_size=" + manifest.Length));
      Assert.That(meta, Does.Contain("metadata_signature_size=" + signature.Length));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void ReadLayout_RejectsMissingMagic() {
    var bogus = new byte[64];
    bogus[0] = (byte)'X';
    var desc = new FileFormat.AndroidOta.AndroidOtaFormatDescriptor();
    using var ms = new MemoryStream(bogus);
    Assert.That(() => desc.List(ms, null), Throws.InstanceOf<InvalidDataException>());
  }
}
