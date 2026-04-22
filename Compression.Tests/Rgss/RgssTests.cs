using System.Text;
using Compression.Registry;
using FileFormat.Rgss;

namespace Compression.Tests.Rgss;

[TestFixture]
public class RgssTests {

  private static void WriteU32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    b[0] = (byte)v; b[1] = (byte)(v >> 8); b[2] = (byte)(v >> 16); b[3] = (byte)(v >> 24);
    s.Write(b);
  }

  /// <summary>
  /// Build a minimal RGSS3A: magic + rawMaster + one entry + data at specified offset.
  /// Decryption: masterKey = rawMaster * 9 + 3. All index fields are XOR'd with masterKey.
  /// Entry data is XOR'd with the per-file key (cycled 4 bytes).
  /// </summary>
  private static byte[] BuildMinimalRgss3(out string expectedName, out byte[] expectedData, out uint masterKey, out uint fileKey) {
    expectedName = "Graphics/tiny.txt";
    expectedData = "Secret payload"u8.ToArray();
    uint rawMaster = 0x11223344;
    masterKey = rawMaster * 9u + 3u;
    fileKey = 0xA5A5A5A5;

    var ms = new MemoryStream();
    // Magic
    ms.Write("RGSSAD\0\x3"u8);
    // Raw master key
    WriteU32(ms, rawMaster);

    // Reserve space for index entry first, then write the data later — we need to know the offset.
    // Let's write index entry with placeholder offset, then write data, then backfill.
    long entryStart = ms.Position;

    // Index entry: offset, size, fileKey, nameLen, name — all XOR'd with masterKey
    // We'll know the data offset only after the index is complete.
    var nameBytes = Encoding.UTF8.GetBytes(expectedName);

    // First emit entry with placeholder offset 0
    WriteU32(ms, 0 ^ masterKey); // placeholder offset
    WriteU32(ms, (uint)expectedData.Length ^ masterKey);
    WriteU32(ms, fileKey ^ masterKey);
    WriteU32(ms, (uint)nameBytes.Length ^ masterKey);
    var encName = (byte[])nameBytes.Clone();
    for (int i = 0; i < encName.Length; i++) {
      uint kb = (masterKey >> ((i % 4) * 8)) & 0xFF;
      encName[i] ^= (byte)kb;
    }
    ms.Write(encName);

    // No more entries — but the parser reads until EOF or zero-offset sentinel; since we want a single
    // entry, append a zero-offset terminator: offset ^ masterKey ... wait, we need offset==0 to terminate.
    // In practice, real RGSS3A files don't have terminators — they end at EOF (the reader loop exits on
    // TryReadU32 returning false).  Instead we will just ensure data is at the NEXT aligned position.

    long dataOffset = ms.Position;
    // Encrypt data
    var enc = (byte[])expectedData.Clone();
    for (int i = 0; i < enc.Length; i++) {
      enc[i] ^= (byte)((fileKey >> ((i % 4) * 8)) & 0xFF);
    }
    ms.Write(enc);

    // Backfill offset
    var buf = ms.ToArray();
    var bo = BitConverter.GetBytes((uint)dataOffset ^ masterKey);
    for (int i = 0; i < 4; i++) buf[entryStart + i] = bo[i];

    return buf;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new RgssFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Rgss"));
    Assert.That(d.Extensions, Contains.Item(".rgss3a"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(3));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesRgss3a() {
    var bytes = BuildMinimalRgss3(out var name, out var data, out var masterKey, out _);
    using var ms = new MemoryStream(bytes);
    var r = new RgssReader(ms);
    Assert.That(r.Version, Is.EqualTo(3));
    Assert.That(r.MasterKeyV3, Is.EqualTo(masterKey));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo(name));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsMetadataAndEntry() {
    var bytes = BuildMinimalRgss3(out var name, out _, out _, out _);
    using var ms = new MemoryStream(bytes);
    var d = new RgssFormatDescriptor();
    var list = d.List(ms, null);
    Assert.That(list.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(list.Any(e => e.Name == name), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFilesToDisk() {
    var bytes = BuildMinimalRgss3(out var name, out var expected, out _, out _);
    var dir = Path.Combine(Path.GetTempPath(), "rgss_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      var d = new RgssFormatDescriptor();
      d.Extract(ms, dir, null, null);

      Assert.That(File.Exists(Path.Combine(dir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(dir, name)), Is.True);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, name)), Is.EqualTo(expected));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void BadMagic_Throws() {
    var buf = new byte[32];
    Array.Fill(buf, (byte)0xAA);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new RgssReader(ms));
  }
}
