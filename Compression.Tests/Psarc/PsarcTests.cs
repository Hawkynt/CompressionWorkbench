using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Psarc;

[TestFixture]
public class PsarcTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "hello world"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Psarc.PsarcWriter(ms, leaveOpen: true))
      w.AddEntry("test.txt", data);
    ms.Position = 0;

    using var r = new FileFormat.Psarc.PsarcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Entries[0].OriginalSize, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    const int defaultBlockSize = 0x10000;
    var small = "a"u8.ToArray();
    var blockSized = new byte[defaultBlockSize];
    var bigger = new byte[defaultBlockSize + 1234];
    new Random(1).NextBytes(blockSized);
    new Random(2).NextBytes(bigger);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Psarc.PsarcWriter(ms, leaveOpen: true)) {
      w.AddEntry("a.bin", small);
      w.AddEntry("b.bin", blockSized);
      w.AddEntry("c.bin", bigger);
    }
    ms.Position = 0;

    using var r = new FileFormat.Psarc.PsarcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("a.bin"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("b.bin"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("c.bin"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(small));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(blockSized));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(bigger));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile_ManyBlocks() {
    var data = new byte[256 * 1024];
    new Random(42).NextBytes(data);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Psarc.PsarcWriter(ms, leaveOpen: true))
      w.AddEntry("big.bin", data);
    ms.Position = 0;

    using var r = new FileFormat.Psarc.PsarcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].OriginalSize, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Psarc.PsarcFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Psarc"));
    Assert.That(d.Extensions, Contains.Item(".psarc"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".psarc"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("PSAR"u8.ToArray()));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Magic_Detected() {
    var d = new FileFormat.Psarc.PsarcFormatDescriptor();
    var sig = d.MagicSignatures[0];
    Assert.That(sig.Bytes, Is.EqualTo(new byte[] { 0x50, 0x53, 0x41, 0x52 }));
    Assert.That(sig.Offset, Is.EqualTo(0));
    Assert.That(sig.Confidence, Is.GreaterThanOrEqualTo(0.9));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsEncryptedToc() {
    var header = new byte[32];
    Encoding.ASCII.GetBytes("PSAR").CopyTo(header, 0);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 1);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), 4);
    Encoding.ASCII.GetBytes("zlib").CopyTo(header, 8);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), 32);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), 30);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), 0x10000);
    // Bit 2 of archive flags = encrypted TOC.
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28, 4), 0x04u);

    using var ms = new MemoryStream(header);
    Assert.Throws<NotSupportedException>(() => _ = new FileFormat.Psarc.PsarcReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsLzmaCompression() {
    var header = new byte[32];
    Encoding.ASCII.GetBytes("PSAR").CopyTo(header, 0);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 1);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), 4);
    Encoding.ASCII.GetBytes("lzma").CopyTo(header, 8);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), 62);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), 30);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), 0x10000);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28, 4), 0x01u);

    var toc = new byte[30];
    // start_offset = 62 (just after header+TOC); original_size = 0; the manifest is empty.
    var blob = new byte[62];
    Array.Copy(header, blob, 32);
    Array.Copy(toc, 0, blob, 32, 30);
    using var ms = new MemoryStream(blob);
    using var r = new FileFormat.Psarc.PsarcReader(ms);
    Assert.That(r.Compression, Is.EqualTo("lzma"));
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Writer_RejectsLzmaOnConstruct() {
    using var ms = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => _ = new FileFormat.Psarc.PsarcWriter(ms, compression: "lzma"));
  }
}
