using Compression.Core.Streams;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

[TestFixture]
public class RarMultiVolumeTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Read_TwoVolumes_Store() {
    byte[] archive = CreateTestArchive(RarConstants.MethodStore);

    int splitPoint = archive.Length / 2;
    byte[] vol1 = archive[..splitPoint];
    byte[] vol2 = archive[splitPoint..];

    using var cs = new ConcatenatedStream([new MemoryStream(vol1), new MemoryStream(vol2)]);
    using var reader = new RarReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(MakeTestData(100, 0x41)));
    Assert.That(reader.Extract(1), Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Read_TwoVolumes_Compressed() {
    byte[] archive = CreateTestArchive(RarConstants.MethodNormal);

    int splitPoint = archive.Length / 2;
    byte[] vol1 = archive[..splitPoint];
    byte[] vol2 = archive[splitPoint..];

    using var cs = new ConcatenatedStream([new MemoryStream(vol1), new MemoryStream(vol2)]);
    using var reader = new RarReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(MakeTestData(100, 0x41)));
    Assert.That(reader.Extract(1), Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Read_ThreeVolumes() {
    byte[] archive = CreateTestArchive(RarConstants.MethodStore);

    int split1 = archive.Length / 3;
    int split2 = 2 * archive.Length / 3;

    using var cs = new ConcatenatedStream([
      new MemoryStream(archive[..split1]),
      new MemoryStream(archive[split1..split2]),
      new MemoryStream(archive[split2..])
    ]);
    using var reader = new RarReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(MakeTestData(100, 0x41)));
    Assert.That(reader.Extract(1), Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CreateSplit_Write_Read_RoundTrip() {
    byte[] data1 = MakeTestData(100, 0x30);
    byte[] data2 = MakeTestData(200, 0x50);

    byte[][] volumes = RarWriter.CreateSplit(
      maxVolumeSize: 200,
      entries: [("file1.bin", data1), ("file2.bin", data2)],
      method: RarConstants.MethodStore);

    Assert.That(volumes.Length, Is.GreaterThan(1));
    foreach (var vol in volumes)
      Assert.That(vol.Length, Is.LessThanOrEqualTo(200));

    var streams = volumes.Select(v => (Stream)new MemoryStream(v)).ToArray();
    using var cs = new ConcatenatedStream(streams);
    using var reader = new RarReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
  }

  private static byte[] CreateTestArchive(int method) {
    using var ms = new MemoryStream();
    using (var writer = new RarWriter(ms, leaveOpen: true, method: method)) {
      writer.AddFile("a.txt", MakeTestData(100, 0x41));
      writer.AddFile("b.txt", MakeTestData(200, 0x42));
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
