using Compression.Core.Streams;
using FileFormat.Wim;

namespace Compression.Tests.Wim;

[TestFixture]
public class WimMultiVolumeTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SplitArchive_Read_TwoVolumes() {
    byte[] wim = CreateTestWim();

    int splitPoint = wim.Length / 2;
    using var cs = new ConcatenatedStream([
      new MemoryStream(wim[..splitPoint]),
      new MemoryStream(wim[splitPoint..])
    ]);
    using var reader = new WimReader(cs);

    Assert.That(reader.Resources, Has.Count.EqualTo(2));
    Assert.That(reader.ReadResource(0), Is.EqualTo(MakeTestData(100, 0x41)));
    Assert.That(reader.ReadResource(1), Is.EqualTo(MakeTestData(200, 0x42)));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CreateSplit_Write_Read_RoundTrip() {
    byte[] data1 = MakeTestData(100, 0x30);
    byte[] data2 = MakeTestData(200, 0x50);

    byte[][] volumes = WimWriter.CreateSplit(
      maxVolumeSize: 200,
      resources: [data1, data2]);

    Assert.That(volumes.Length, Is.GreaterThan(1));

    var streams = volumes.Select(v => (Stream)new MemoryStream(v)).ToArray();
    using var cs = new ConcatenatedStream(streams);
    using var reader = new WimReader(cs);

    Assert.That(reader.Resources, Has.Count.EqualTo(2));
    Assert.That(reader.ReadResource(0), Is.EqualTo(data1));
    Assert.That(reader.ReadResource(1), Is.EqualTo(data2));
  }

  private static byte[] CreateTestWim() {
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms);
    writer.Write([MakeTestData(100, 0x41), MakeTestData(200, 0x42)]);
    return ms.ToArray();
  }

  private static byte[] MakeTestData(int size, byte seed) {
    var data = new byte[size];
    for (int i = 0; i < size; ++i)
      data[i] = (byte)((seed + i) % 256);
    return data;
  }
}
