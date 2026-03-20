using FileFormat.Tar;

namespace Compression.Tests.Tar;

[TestFixture]
public class TarHeaderTests {
  [Category("HappyPath")]
  [Test]
  public void Header_ChecksumIsCorrect() {
    using var ms = new MemoryStream();
    var entry = new TarEntry {
      Name = "test.txt",
      Size = 100,
      Mode = 420, // 0644
    };

    TarHeader.WriteHeader(ms, entry);
    byte[] header = ms.ToArray();

    Assert.That(header.Length, Is.EqualTo(TarConstants.BlockSize));

    // Parse the stored checksum
    var storedChecksum = TarHeader.ParseOctalLong(header.AsSpan(148, 8));

    // Compute the checksum independently
    int computed = TarHeader.ComputeChecksum(header);

    Assert.That(storedChecksum, Is.EqualTo(computed));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Header_OctalEncoding() {
    // Test encoding various values
    byte[] buffer = new byte[8];

    TarHeader.WriteOctal(buffer.AsSpan(), 0, 8);
    Assert.That(TarHeader.ParseOctalLong(buffer), Is.EqualTo(0));

    TarHeader.WriteOctal(buffer.AsSpan(), 420, 8); // 0644
    Assert.That(TarHeader.ParseOctalLong(buffer), Is.EqualTo(420));

    TarHeader.WriteOctal(buffer.AsSpan(), 493, 8); // 0755
    Assert.That(TarHeader.ParseOctalLong(buffer), Is.EqualTo(493));

    byte[] largeBuffer = new byte[12];
    TarHeader.WriteOctal(largeBuffer.AsSpan(), 1048576, 12); // 1 MB
    Assert.That(TarHeader.ParseOctalLong(largeBuffer), Is.EqualTo(1048576));
  }

  [Category("EdgeCase")]
  [Test]
  public void Reader_DetectsEndOfArchive() {
    // An empty archive consists of two 512-byte zero blocks
    byte[] emptyArchive = new byte[TarConstants.BlockSize * 2];

    using var tr = new TarReader(new MemoryStream(emptyArchive));
    var entry = tr.GetNextEntry();
    Assert.That(entry, Is.Null);
  }

  [Category("Boundary")]
  [Test]
  public void Writer_PadsTo512ByteBoundary() {
    // Write a file with a non-512-aligned size
    byte[] data = new byte[700]; // 700 bytes, needs padding to 1024 (2 blocks)
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i & 0xFF);

    using var ms = new MemoryStream();
    using (var tw = new TarWriter(ms, leaveOpen: true)) {
      var entry = new TarEntry { Name = "padded.bin" };
      tw.AddEntry(entry, data);
    }

    byte[] archive = ms.ToArray();

    // Total should be: 1 header block (512) + 2 data blocks (1024) + 2 end blocks (1024) = 2560
    Assert.That(archive.Length % TarConstants.BlockSize, Is.EqualTo(0));

    // Verify: header (512) + ceil(700/512)*512 = 512 + 1024 = 1536 bytes for the entry
    // Plus two zero blocks for end-of-archive = 1536 + 1024 = 2560
    Assert.That(archive.Length, Is.EqualTo(2560));
  }
}
