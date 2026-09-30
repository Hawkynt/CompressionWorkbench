using Compression.Core.Streams;
using FileFormat.Wim;

namespace Compression.Tests.Wim;

[TestFixture]
public class WimMultiVolumeTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Read_TwoVolumes() {
    var wim = CreateTestWim();

    var splitPoint = wim.Length / 2;
    using var cs = new ConcatenatedStream([
      new MemoryStream(wim[..splitPoint]),
      new MemoryStream(wim[splitPoint..])
    ]);
    using var reader = new WimReader(cs);

    // Two payloads and the image metadata that names them: a WIM that held only
    // the payloads would be one no reader could list.
    Assert.That(reader.Resources.Count(r => !r.IsMetadata), Is.EqualTo(2));
    Assert.That(reader.ReadResource(0), Is.EqualTo(MakeTestData(100, 0x41)));
    Assert.That(reader.ReadResource(1), Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CreateSplit_Write_Read_RoundTrip() {
    var data1 = MakeTestData(100, 0x30);
    var data2 = MakeTestData(200, 0x50);

    var volumes = WimWriter.CreateSplit(
      maxVolumeSize: 200,
      resources: [data1, data2]);

    Assert.That(volumes.Length, Is.GreaterThan(1));

    // Each part lists only its own resources, so the combined table runs part by part; the data
    // resources keep their relative order.
    var streams = volumes.Select(v => new MemoryStream(v)).ToArray();
    using var reader = new WimReader(streams[0], streams.Skip(1).Cast<Stream>().ToArray());

    var data = reader.Resources.Select((r, i) => (r, i)).Where(x => !x.r.IsMetadata).Select(x => x.i).ToArray();
    Assert.That(data, Has.Length.EqualTo(2));
    Assert.That(reader.ReadResource(data[0]), Is.EqualTo(data1));
    Assert.That(reader.ReadResource(data[1]), Is.EqualTo(data2));
  }

  private static byte[] CreateTestWim() {
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms);
    writer.Write([MakeTestData(100, 0x41), MakeTestData(200, 0x42)]);
    return ms.ToArray();
  }

  private static byte[] MakeTestData(int size, byte seed) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i)
      data[i] = (byte)((seed + i) % 256);
    return data;
  }
}
