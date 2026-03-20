using Compression.Core.Streams;
using FileFormat.Tar;

namespace Compression.Tests.Tar;

[TestFixture]
public class TarMultiVolumeTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Read_TwoVolumes() {
    byte[] archive = CreateTestArchive();

    int splitPoint = archive.Length / 2;
    using var cs = new ConcatenatedStream([
      new MemoryStream(archive[..splitPoint]),
      new MemoryStream(archive[splitPoint..])
    ]);
    using var reader = new TarReader(cs, leaveOpen: true);

    var entries = ReadAllEntries(reader);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Data, Is.EqualTo(MakeTestData(100, 0x41)));
    Assert.That(entries[1].Data, Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CreateSplit_Write_Read_RoundTrip() {
    byte[] data1 = MakeTestData(100, 0x30);
    byte[] data2 = MakeTestData(200, 0x50);

    byte[][] volumes = TarWriter.CreateSplit(
      maxVolumeSize: 800,
      entries: [("file1.bin", data1), ("file2.bin", data2)]);

    Assert.That(volumes.Length, Is.GreaterThan(1));

    var streams = volumes.Select(v => (Stream)new MemoryStream(v)).ToArray();
    using var cs = new ConcatenatedStream(streams);
    using var reader = new TarReader(cs, leaveOpen: true);

    var entries = ReadAllEntries(reader);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Data, Is.EqualTo(data1));
    Assert.That(entries[1].Data, Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void GnuMultiVolume_ContinuationEntry_Parsed() {
    // Create a tar with a GNU multi-volume continuation entry
    using var ms = new MemoryStream();
    using (var writer = new TarWriter(ms, leaveOpen: true)) {
      // Write a continuation entry (type 'M')
      var contEntry = new TarEntry {
        Name = "bigfile.bin",
        TypeFlag = TarConstants.TypeGnuMultiVolume,
        Offset = 1024,
        RealSize = 4096,
      };
      byte[] chunkData = MakeTestData(512, 0x55);
      writer.AddEntry(contEntry, chunkData.AsSpan());
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new TarReader(ms, leaveOpen: true);
    var entry = reader.GetNextEntry();

    Assert.That(entry, Is.Not.Null);
    Assert.That(entry!.Name, Is.EqualTo("bigfile.bin"));
    // Continuation entries are exposed as regular files
    Assert.That(entry.IsFile, Is.True);

    using var entryStream = reader.GetEntryStream();
    byte[] data = new byte[512];
    entryStream.ReadExactly(data);
    Assert.That(data, Is.EqualTo(MakeTestData(512, 0x55)));
  }

  private static byte[] CreateTestArchive() {
    using var ms = new MemoryStream();
    using (var writer = new TarWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new TarEntry { Name = "a.txt" }, MakeTestData(100, 0x41).AsSpan());
      writer.AddEntry(new TarEntry { Name = "b.txt" }, MakeTestData(200, 0x42).AsSpan());
      writer.Finish();
    }
    return ms.ToArray();
  }

  private static List<(TarEntry Entry, byte[] Data)> ReadAllEntries(TarReader reader) {
    var result = new List<(TarEntry, byte[])>();
    while (reader.GetNextEntry() is { } entry) {
      using var stream = reader.GetEntryStream();
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      result.Add((entry, ms.ToArray()));
    }
    return result;
  }

  private static byte[] MakeTestData(int size, byte seed) {
    var data = new byte[size];
    for (int i = 0; i < size; ++i)
      data[i] = (byte)((seed + i) % 256);
    return data;
  }
}
