using Compression.Core.Streams;
using FileFormat.Sqx;

namespace Compression.Tests.Sqx;

[TestFixture]
public class SqxMultiVolumeTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Read_TwoVolumes() {
    var archive = CreateTestArchive();

    var splitPoint = archive.Length / 2;
    using var cs = new ConcatenatedStream([
      new MemoryStream(archive[..splitPoint]),
      new MemoryStream(archive[splitPoint..])
    ]);
    using var reader = new SqxReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(MakeTestData(100, 0x41)));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CreateSplit_Write_Read_RoundTrip() {
    var data1 = MakeTestData(100, 0x30);
    var data2 = MakeTestData(200, 0x50);

    var volumes = SqxWriter.CreateSplit(
      maxVolumeSize: 200,
      entries: [("file1.bin", data1), ("file2.bin", data2)]);

    Assert.That(volumes.Length, Is.GreaterThan(1));

    var streams = volumes.Select(v => (Stream)new MemoryStream(v)).ToArray();
    using var cs = new ConcatenatedStream(streams);
    using var reader = new SqxReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(data2));
  }

  private static byte[] CreateTestArchive() {
    var writer = new SqxWriter();
    writer.AddFile("a.txt", MakeTestData(100, 0x41));
    writer.AddFile("b.txt", MakeTestData(200, 0x42));
    return writer.ToArray();
  }

  private static byte[] MakeTestData(int size, byte seed) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i)
      data[i] = (byte)((seed + i) % 256);
    return data;
  }
}
