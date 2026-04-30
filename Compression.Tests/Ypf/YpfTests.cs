using FileFormat.Ypf;

namespace Compression.Tests.Ypf;

[TestFixture]
public class YpfTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFileStored() {
    // Random-looking bytes don't compress; writer must fall back to stored.
    var data = new byte[] { 0x37, 0x91, 0xC4, 0x0A, 0xEE };

    using var ms = new MemoryStream();
    using (var w = new YpfWriter(ms, leaveOpen: true))
      w.AddEntry("incompressible.bin", data);
    ms.Position = 0;

    using var r = new YpfReader(ms);
    Assert.That(r.Version, Is.EqualTo(480u));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("incompressible.bin"));
    Assert.That(r.Entries[0].Compression, Is.EqualTo((byte)YpfConstants.CompressionStored));
    Assert.That(r.Entries[0].RawSize, Is.EqualTo((uint)data.Length));
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo((uint)data.Length));
    Assert.That(r.Entries[0].IsCorrupt, Is.False);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFileZlib() {
    // 100 KB of 0xCC compresses to a few hundred bytes.
    var data = new byte[100 * 1024];
    Array.Fill(data, (byte)0xCC);

    using var ms = new MemoryStream();
    using (var w = new YpfWriter(ms, leaveOpen: true))
      w.AddEntry("payload.dat", data);
    ms.Position = 0;

    using var r = new YpfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Compression, Is.EqualTo((byte)YpfConstants.CompressionZlib));
    Assert.That(r.Entries[0].RawSize, Is.EqualTo((uint)data.Length));
    Assert.That(r.Entries[0].CompressedSize, Is.LessThan((uint)data.Length));
    Assert.That(r.Entries[0].IsCorrupt, Is.False);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var a = new byte[1024];
    var b = new byte[2048];
    var c = new byte[] { 1, 2, 3, 4, 5 };
    Array.Fill(a, (byte)0xAA);
    Array.Fill(b, (byte)0xBB);

    using var ms = new MemoryStream();
    using (var w = new YpfWriter(ms, leaveOpen: true)) {
      w.AddEntry("a.bin", a);
      w.AddEntry("b.bin", b);
      w.AddEntry("c.bin", c);
    }
    ms.Position = 0;

    using var r = new YpfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("a.bin"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("b.bin"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("c.bin"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(a));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(b));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(c));
  }

  [Test, Category("HappyPath")]
  public void Hash_KnownVector() {
    // Regression-locks our rolling-hash impl: h = h * 0x1003F + lower(c) over "test.bin".
    // Computed deterministically as 0xACE3EB2B (2900618027). If this number drifts the
    // archive's NameHash field changes and round-trips against external tooling break.
    Assert.That(YpfHash.Hash("test.bin"), Is.EqualTo(0xACE3EB2Bu));
  }

  [Test, Category("HappyPath")]
  public void Crc32_KnownVector() {
    // Standard CRC-32 of ASCII "123456789" — universal CRC-32 test vector.
    var bytes = "123456789"u8.ToArray();
    Assert.That(YpfCrc32.Compute(bytes), Is.EqualTo(0xCBF43926u));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[YpfConstants.HeaderSize];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new YpfReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsUnsupportedVersion() {
    var buf = new byte[YpfConstants.HeaderSize];
    YpfConstants.Magic.CopyTo(buf, 0);
    BitConverter.GetBytes(0x100u).CopyTo(buf, 4);
    BitConverter.GetBytes(0u).CopyTo(buf, 8);
    BitConverter.GetBytes(0u).CopyTo(buf, 12);

    using var ms = new MemoryStream(buf);
    var ex = Assert.Throws<NotSupportedException>(() => _ = new YpfReader(ms));
    Assert.That(ex!.Message, Does.Contain("256"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_DetectsCrcMismatch_DoesNotThrow() {
    var data = new byte[] { 1, 2, 3, 4 };
    using var ms = new MemoryStream();
    using (var w = new YpfWriter(ms, leaveOpen: true))
      w.AddEntry("x.bin", data);

    // Locate the CRC field of the (only) entry record. Layout:
    //   header(32) + nameHash(4) + nameLen(1) + name("x.bin"=5) + type(1) + comp(1)
    //   + raw(4) + comp(4) + offset(4) + crc(4)
    // CRC starts at: 32 + 4 + 1 + 5 + 1 + 1 + 4 + 4 + 4 = 56.
    var bytes = ms.ToArray();
    bytes[56] ^= 0xFF;
    bytes[57] ^= 0xFF;

    using var corrupted = new MemoryStream(bytes);
    using var r = new YpfReader(corrupted);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].IsCorrupt, Is.True);
    // Reader still surfaces the bytes — corruption is signaled, not enforced.
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new YpfFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ypf"));
    Assert.That(d.DisplayName, Is.EqualTo("YukaScript YPF"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".ypf"));
    Assert.That(d.Extensions, Contains.Item(".ypf"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(YpfConstants.Magic));
    Assert.That(d.Methods[0].Name, Is.EqualTo("ypf-v480"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }
}
