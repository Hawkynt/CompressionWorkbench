using System.Text;

namespace Compression.Tests.Dzip;

[TestFixture]
public class DzipTests {

  [Test, Category("HappyPath")]
  public void Magic_IsCorrect() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dzip.DzipWriter(ms, leaveOpen: true))
      w.AddEntry("a.txt", "x"u8.ToArray());

    var buf = ms.ToArray();
    Assert.That(buf.Length, Is.GreaterThanOrEqualTo(4));
    Assert.That(Encoding.ASCII.GetString(buf, 0, 4), Is.EqualTo("DZIP"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "hello"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dzip.DzipWriter(ms, leaveOpen: true))
      w.AddEntry("test.vmt", data);

    ms.Position = 0;
    var r = new FileFormat.Dzip.DzipReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.vmt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo(data.Length));
    Assert.That(r.Entries[0].CompressionFlag, Is.EqualTo((byte)0));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var d1 = "first"u8.ToArray();
    var d2 = new byte[256];
    Array.Fill(d2, (byte)0xAB);
    var d3 = "third entry contents"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dzip.DzipWriter(ms, leaveOpen: true)) {
      w.AddEntry("materials/test.vmt", d1);
      w.AddEntry("models/box.mdl", d2);
      w.AddEntry("scripts/dialog/intro.txt", d3);
    }

    ms.Position = 0;
    var r = new FileFormat.Dzip.DzipReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("materials/test.vmt"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("models/box.mdl"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("scripts/dialog/intro.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(d1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(d2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(d3));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[16];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Dzip.DzipReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadVersion() {
    var buf = new byte[16];
    "DZIP"u8.ToArray().CopyTo(buf, 0);
    BitConverter.GetBytes(1u).CopyTo(buf, 4);
    BitConverter.GetBytes(0u).CopyTo(buf, 8);
    BitConverter.GetBytes(16u).CopyTo(buf, 12);

    using var ms = new MemoryStream(buf);
    var ex = Assert.Throws<NotSupportedException>(() => _ = new FileFormat.Dzip.DzipReader(ms));
    Assert.That(ex!.Message, Does.Contain("version"));
  }

  [Test, Category("HappyPath")]
  public void Lzss_DecodesKnownSequence() {
    var input = new byte[] { 0xFF, (byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'E', (byte)'F', (byte)'G', (byte)'H' };
    var output = FileFormat.Dzip.DzipLzss.Decompress(input, expectedSize: 8);
    Assert.That(output, Is.EqualTo("ABCDEFGH"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Lzss_DecodesBackReference() {
    // First 4 bits = 1 (literals: A,B,C,D), then bit 4 = 0 (back-reference: length=5, distance=4).
    // Control byte: bits 0..3 = 1, bit 4 = 0, bits 5..7 don't matter (we stop after expectedSize=9 bytes).
    // 0b00001111 = 0x0F.
    // Back-ref: length = (hi & 0x0F) + 3 = 5 → hi&0x0F = 2, distance = (hi>>4) | (lo<<4) + 1 = 4 → (hi>>4)|(lo<<4) = 3
    // We pick hi = 0x32 (high nibble 3, low nibble 2), lo = 0x00. (0x32 >> 4)|(0x00 << 4) = 3 → distance = 4.
    var input = new byte[] { 0x0F, (byte)'A', (byte)'B', (byte)'C', (byte)'D', 0x32, 0x00 };
    var output = FileFormat.Dzip.DzipLzss.Decompress(input, expectedSize: 9);
    Assert.That(output, Is.EqualTo("ABCDABCDA"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Lzss_RejectsTruncated() {
    // Control byte says 8 literals, but only 3 follow.
    var input = new byte[] { 0xFF, 0x01, 0x02, 0x03 };
    Assert.Throws<InvalidDataException>(() => _ = FileFormat.Dzip.DzipLzss.Decompress(input, expectedSize: 8));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesStoredEntry() {
    // Hand-compose a DZIP with a single stored entry "x" containing 5 bytes "hello".
    var name = "x"u8.ToArray();
    var data = "hello"u8.ToArray();
    var buf = new MemoryStream();

    // Header (16 bytes): magic + version=2 + count=1 + tocOffset (placeholder)
    buf.Write("DZIP"u8);
    buf.Write(BitConverter.GetBytes(2u));
    buf.Write(BitConverter.GetBytes(1u));
    var tocOffsetPos = buf.Position;
    buf.Write(BitConverter.GetBytes(0u));

    // Data
    var dataOffset = (uint)buf.Position;
    buf.Write(data);

    // TOC
    var tocOffset = (uint)buf.Position;
    buf.WriteByte((byte)name.Length);
    buf.Write(name);
    buf.Write(BitConverter.GetBytes(dataOffset));
    buf.Write(BitConverter.GetBytes((uint)data.Length));
    buf.Write(BitConverter.GetBytes((uint)data.Length));
    buf.WriteByte(0); // stored

    buf.Position = tocOffsetPos;
    buf.Write(BitConverter.GetBytes(tocOffset));

    buf.Position = 0;
    var r = new FileFormat.Dzip.DzipReader(buf);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("x"));
    Assert.That(r.Entries[0].CompressionFlag, Is.EqualTo((byte)0));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Writer_AlwaysStored() {
    var data = new byte[1024];
    Array.Fill(data, (byte)0x55);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dzip.DzipWriter(ms, leaveOpen: true))
      w.AddEntry("compressible.bin", data);

    ms.Position = 0;
    var r = new FileFormat.Dzip.DzipReader(ms);
    Assert.That(r.Entries[0].CompressionFlag, Is.EqualTo((byte)0));
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo(r.Entries[0].Size));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Dzip.DzipFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Dzip"));
    Assert.That(d.DisplayName, Is.EqualTo("Bloodlines DZIP"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".dzip"));
    Assert.That(d.Extensions, Contains.Item(".dzip"));
    Assert.That(d.Extensions, Does.Not.Contain(".vpk"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("DZIP"u8.ToArray()));
    Assert.That(d.Methods[0].Name, Is.EqualTo("dzip"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReportsOriginalSize() {
    var data = "abc"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Dzip.DzipWriter(ms, leaveOpen: true))
      w.AddEntry("file.bin", data);

    ms.Position = 0;
    var d = new FileFormat.Dzip.DzipFormatDescriptor();
    var list = d.List(ms, password: null);
    Assert.That(list, Has.Count.EqualTo(1));
    Assert.That(list[0].Name, Is.EqualTo("file.bin"));
    Assert.That(list[0].OriginalSize, Is.EqualTo(data.Length));
    Assert.That(list[0].CompressedSize, Is.EqualTo(data.Length));
    Assert.That(list[0].Method, Is.EqualTo("Stored"));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsLongPath() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Dzip.DzipWriter(ms, leaveOpen: true);
    var longPath = new string('a', 256);
    Assert.Throws<ArgumentException>(() => w.AddEntry(longPath, [0x01]));
  }
}
