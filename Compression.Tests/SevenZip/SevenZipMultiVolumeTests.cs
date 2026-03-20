using Compression.Core.Streams;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

[TestFixture]
public class SevenZipMultiVolumeTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Read_TwoVolumes() {
    byte[] archive = CreateTestArchive();

    int splitPoint = archive.Length / 2;
    byte[] vol1 = archive[..splitPoint];
    byte[] vol2 = archive[splitPoint..];

    using var cs = new ConcatenatedStream([new MemoryStream(vol1), new MemoryStream(vol2)]);
    using var reader = new SevenZipReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    byte[] data1 = reader.Extract(0);
    Assert.That(data1, Is.EqualTo(MakeTestData(100, 0x41)));
    byte[] data2 = reader.Extract(1);
    Assert.That(data2, Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Read_ThreeVolumes() {
    byte[] archive = CreateTestArchive();

    int split1 = archive.Length / 3;
    int split2 = 2 * archive.Length / 3;
    byte[] vol1 = archive[..split1];
    byte[] vol2 = archive[split1..split2];
    byte[] vol3 = archive[split2..];

    using var cs = new ConcatenatedStream([
      new MemoryStream(vol1), new MemoryStream(vol2), new MemoryStream(vol3)
    ]);
    using var reader = new SevenZipReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(MakeTestData(100, 0x41)));
    Assert.That(reader.Extract(1), Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Write_Read_RoundTrip() {
    byte[] data1 = MakeTestData(500, 0x30);
    byte[] data2 = MakeTestData(300, 0x50);

    // Write a normal archive
    using var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "file1.bin" }, data1);
      writer.AddEntry(new SevenZipEntry { Name = "file2.bin" }, data2);
      writer.Finish();
    }

    byte[] archive = ms.ToArray();

    // Split into 4 volumes
    int chunkSize = (archive.Length + 3) / 4;
    var volumes = new List<MemoryStream>();
    for (int i = 0; i < archive.Length; i += chunkSize) {
      int len = Math.Min(chunkSize, archive.Length - i);
      volumes.Add(new MemoryStream(archive[i..(i + len)]));
    }

    using var cs = new ConcatenatedStream(volumes.Cast<Stream>().ToArray());
    using var reader = new SevenZipReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CreateSplit_Write_Read_RoundTrip() {
    byte[] data1 = MakeTestData(500, 0x30);
    byte[] data2 = MakeTestData(300, 0x50);

    byte[][] volumes = SevenZipWriter.CreateSplit(
      maxVolumeSize: 200,
      entries: [("file1.bin", data1), ("file2.bin", data2)]);

    Assert.That(volumes.Length, Is.GreaterThan(1));
    foreach (var vol in volumes)
      Assert.That(vol.Length, Is.LessThanOrEqualTo(200));

    // Read back via ConcatenatedStream
    var streams = volumes.Select(v => (Stream)new MemoryStream(v)).ToArray();
    using var cs = new ConcatenatedStream(streams);
    using var reader = new SevenZipReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
  }

  private static byte[] CreateTestArchive() {
    using var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "a.txt" }, MakeTestData(100, 0x41));
      writer.AddEntry(new SevenZipEntry { Name = "b.txt" }, MakeTestData(200, 0x42));
      writer.Finish();
    }

    return ms.ToArray();
  }

  private static byte[] MakeTestData(int size, byte seed) {
    var data = new byte[size];
    for (int i = 0; i < size; ++i)
      data[i] = (byte)((seed + i) % 256);
    return data;
  }
}
