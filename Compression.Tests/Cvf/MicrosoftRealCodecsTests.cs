using System.Buffers.Binary;
using Compression.Core.FileSystems.Cvf;
using Compression.Registry;
using FileSystem.DoubleSpace;
using FileSystem.DriveSpace3;

namespace Compression.Tests.Cvf;

/// <summary>
/// Each Microsoft CVF product is created only with the codecs its real driver writes — DoubleSpace DS,
/// DriveSpace JM, DriveSpace 3 JM and SQ — and a version-2 volume refuses an SQ cluster, which only
/// DriveSpace 3 volumes carry. Edits keep the codec a volume already uses.
/// </summary>
[TestFixture]
public sealed class MicrosoftRealCodecsTests {

  private static byte[] Create(IArchiveCreatable descriptor, string? method = null) {
    using var ms = new MemoryStream();
    descriptor.Create(ms, [ArchiveInputInfo.InMemory("TEXT.TXT", CvfSamples.Get("text-8k"))], new FormatCreateOptions { MethodName = method });
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void GivenDoubleSpace_WhenCreated_ThenItsClustersAreDs()
    => Assert.That(GenuineCvfVolume.CodecInUse(Create(new DoubleSpaceFormatDescriptor())), Is.EqualTo(CvfLzMethod.Ds));

  [Test, Category("HappyPath")]
  public void GivenDriveSpace_WhenCreated_ThenItsClustersAreJm()
    => Assert.That(GenuineCvfVolume.CodecInUse(Create(new DriveSpaceFormatDescriptor())), Is.EqualTo(CvfLzMethod.Jm));

  [Test, Category("Sad")]
  public void GivenDoubleSpace_WhenAskedForAnotherProductsCodec_ThenItIsRefused([Values("jm", "sq", "auto", "sd3")] string method)
    => Assert.That(() => Create(new DoubleSpaceFormatDescriptor(), method), Throws.InstanceOf<ArgumentException>());

  [Test, Category("Sad")]
  public void GivenDriveSpace_WhenAskedForAnotherProductsCodec_ThenItIsRefused([Values("ds", "sq", "auto", "sd4")] string method)
    => Assert.That(() => Create(new DriveSpaceFormatDescriptor(), method), Throws.InstanceOf<ArgumentException>());

  [Test, Category("Sad")]
  public void GivenDriveSpace3_WhenAskedForDs_ThenItIsRefused([Values("ds", "ds++")] string method)
    => Assert.That(() => Create(new DriveSpace3FormatDescriptor(), method), Throws.InstanceOf<ArgumentException>());

  [Test, Category("Sad")]
  public void GivenTheVersion2Writer_WhenAskedForSqOrAuto_ThenItIsRefused([Values(CvfLzMethod.Sq, CvfLzMethod.Auto, CvfLzMethod.Sd3)] CvfLzMethod method) {
    var w = new GenuineCvfWriter { CompressionMethod = method };
    w.AddFile("A.TXT", CvfSamples.Get("text-8k"));

    Assert.That(() => w.Build(), Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("Sad")]
  public void GivenTheDriveSpace3Writer_WhenAskedForDs_ThenItIsRefused() {
    var w = new GenuineDvr3Writer { CompressionMethod = CvfLzMethod.Ds };
    w.AddFile("A.TXT", CvfSamples.Get("text-8k"));

    Assert.That(() => w.Build(), Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("HappyPath")]
  public void GivenDriveSpace3Auto_WhenCreated_ThenNoClusterIsDs() {
    var image = Create(new DriveSpace3FormatDescriptor(), "auto");

    Assert.That(image.AsSpan().IndexOf("DS\0\0"u8), Is.LessThan(0));
  }

  [Test, Category("HappyPath")]
  public void GivenADriveSpaceVolume_WhenEditedThroughTheDoubleSpaceDescriptor_ThenItKeepsItsJmClusters() {
    using var image = new MemoryStream();
    image.Write(Create(new DriveSpaceFormatDescriptor()));

    new DoubleSpaceFormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("MORE.TXT", CvfSamples.Get("dir-8k"))]);

    Assert.That(GenuineCvfVolume.CodecInUse(image.ToArray()), Is.EqualTo(CvfLzMethod.Jm));
  }

  [Test, Category("Sad")]
  public void GivenAnSqClusterInAVersion2Volume_WhenRead_ThenItIsRefused() {
    var data = CvfSamples.Get("text-8k");
    var image = Create(new DriveSpaceFormatDescriptor());
    // Cluster 2's MDFAT entry: (word@0x24 + 1) sectors in, index base word@0x2D.
    var mdfat = (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x24)) + 1) * 512 + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x2D)) * 4;
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(mdfat + 2 * 4));
    var payloadAt = (int)((entry & 0x1FFFFFu) + 1) * 512;
    var sectors = (int)((entry >> 22) & 0xF) + 1;
    var sq = CvfLzCodec.Encode(data, CvfLzMethod.Sq, 2)!;
    Assume.That(sq.Length, Is.LessThanOrEqualTo(sectors * 512), "the SQ payload must fit the JM run");
    Array.Clear(image, payloadAt, sectors * 512);
    sq.CopyTo(image, payloadAt);

    Assert.That(() => new DoubleSpaceFormatDescriptor().Extract(new MemoryStream(image), Path.Combine(Path.GetTempPath(), $"cwb-sq-{Guid.NewGuid():N}"), null, null),
      Throws.InstanceOf<NotSupportedException>());
  }
}
