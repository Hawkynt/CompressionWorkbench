using Compression.Core.Streams;
using FileFormat.Arc;

namespace Compression.Tests.Arc;

[TestFixture]
public class ArcMultiVolumeTests {
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
    using var reader = new ArcReader(cs, leaveOpen: true);

    var entries = new List<(ArcEntry Entry, byte[] Data)>();
    while (reader.GetNextEntry() is { } entry)
      entries.Add((entry, reader.ReadEntryData()));

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Data, Is.EqualTo(MakeTestData(100, 0x41)));
    Assert.That(entries[1].Data, Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CreateSplit_Write_Read_RoundTrip() {
    var data1 = MakeTestData(100, 0x30);
    var data2 = MakeTestData(200, 0x50);

    var volumes = ArcWriter.CreateSplit(
      maxVolumeSize: 200,
      entries: [("file1.bin", data1), ("file2.bin", data2)]);

    Assert.That(volumes.Length, Is.GreaterThan(1));

    var streams = volumes.Select(v => (Stream)new MemoryStream(v)).ToArray();
    using var cs = new ConcatenatedStream(streams);
    using var reader = new ArcReader(cs, leaveOpen: true);

    var entries = new List<(ArcEntry Entry, byte[] Data)>();
    while (reader.GetNextEntry() is { } entry)
      entries.Add((entry, reader.ReadEntryData()));

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Data, Is.EqualTo(data1));
    Assert.That(entries[1].Data, Is.EqualTo(data2));
  }

  private static byte[] CreateTestArchive() {
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, leaveOpen: true)) {
      writer.AddEntry("a.txt", MakeTestData(100, 0x41));
      writer.AddEntry("b.txt", MakeTestData(200, 0x42));
      writer.Finish();
    }
    return ms.ToArray();
  }

  private static byte[] MakeTestData(int size, byte seed) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i)
      data[i] = (byte)((seed + i) % 256);
    return data;
  }
}
