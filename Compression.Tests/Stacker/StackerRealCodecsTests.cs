using Compression.Core.FileSystems.Cvf;
using Compression.Registry;
using Compression.Tests.Cvf;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Stacker volumes carry only Stacker's own codecs — SD-3 (Stacker 3 and 4) and SD-4 (Stacker 4 only).
/// DoubleSpace's DS codec is not part of the Stacker format: it is neither written nor read, and SD-4 is
/// never written into a Stacker 3 volume.
/// </summary>
[TestFixture]
public sealed class StackerRealCodecsTests {
  private const int AmapEntryOfCluster2 = (2 + 3) * 512 + 2 * 3;   // AMAP area 0 (FAT start 2), cluster 2

  private static byte[] Create(FormatCreateOptions options) {
    using var ms = new MemoryStream();
    new StackerFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("F.BIN", CvfSamples.Get("text-8k"))], options);
    return ms.ToArray();
  }

  [Test, Category("Spec")]
  public void GivenTheDescriptor_WhenItsCodecsAreListed_ThenOnlyStackerCodecsAppear() {
    var d = new StackerFormatDescriptor();
    var method = ((IFormatOptionsSchema)d).OptionsSchema.Single(o => o.Key == "Method");

    Assert.Multiple(() => {
      Assert.That(d.Methods.Select(m => m.Name), Has.None.StartsWith("ds"));
      Assert.That(method.AllowedValues, Is.EquivalentTo(new[] { "Stored", "SD3", "SD4", "Auto" }));
    });
  }

  [Test, Category("Sad")]
  public void GivenTheDsCodec_WhenCreated_ThenItIsRefused([Values("ds", "ds++", "jm", "sq")] string method)
    => Assert.That(() => Create(new FormatCreateOptions { MethodName = method }), Throws.InstanceOf<ArgumentException>());

  [Test, Category("Sad")]
  public void GivenSd4OnAStacker3Volume_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(new FormatCreateOptions { MethodName = "sd4" }), Throws.InstanceOf<ArgumentException>());

  [Test, Category("Sad")]
  public void GivenTheWriter_WhenAskedForANonStackerCodec_ThenItIsRefused(
      [Values(CvfLzMethod.Ds, CvfLzMethod.Jm, CvfLzMethod.Sq)] CvfLzMethod method) {
    var w = new GenuineStackerWriter { CompressionMethod = method, Version = 4 };
    w.AddFile("F.BIN", CvfSamples.Get("text-8k"));

    Assert.That(() => w.Build(), Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("Sad")]
  public void GivenTheWriter_WhenAskedForSd4OnStacker3_ThenItIsRefused() {
    var w = new GenuineStackerWriter { CompressionMethod = CvfLzMethod.Sd4, Version = 3 };
    w.AddFile("F.BIN", CvfSamples.Get("text-8k"));

    Assert.That(() => w.Build(), Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("HappyPath")]
  public void GivenAutoOnStacker3_WhenCreated_ThenNoClusterIsSd4() {
    var image = Create(new FormatCreateOptions());
    var run = image[AmapEntryOfCluster2] | image[AmapEntryOfCluster2 + 1] << 8;

    Assert.That(image[run * 512] | image[run * 512 + 1] << 8, Is.Not.EqualTo(0x0081), "an SD-4 cluster in a Stacker 3 volume");
  }

  [Test, Category("Sad")]
  public void GivenADsClusterInAStackerVolume_WhenRead_ThenItIsRefused() {
    var data = CvfSamples.Get("text-8k");
    var image = Create(new FormatCreateOptions { MethodName = "stored" });
    var run = image[AmapEntryOfCluster2] | image[AmapEntryOfCluster2 + 1] << 8;
    var payload = CvfLzCodec.Encode(data, CvfLzMethod.Ds, 2)!;
    var sectors = (payload.Length + 511) / 512;
    Array.Clear(image, run * 512, 16 * 512);
    payload.CopyTo(image, run * 512);
    image[AmapEntryOfCluster2 + 2] = (byte)(sectors - 1);   // compressed run

    using var reader = new GenuineStackerReader(new MemoryStream(image));
    Assert.That(() => reader.Extract(reader.Entries.Single(e => e.Name == "F.BIN")), Throws.InstanceOf<NotSupportedException>());
  }
}
