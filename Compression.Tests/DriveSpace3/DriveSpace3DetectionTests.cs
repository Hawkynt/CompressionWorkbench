using Compression.Registry;
using FileSystem.DoubleSpace;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

[TestFixture]
public class DriveSpace3DetectionTests {

  private static byte[] Create(IArchiveCreatable descriptor, FormatCreateOptions? options = null) {
    using var ms = new MemoryStream();
    descriptor.Create(ms, [ArchiveInputInfo.InMemory("TEST.TXT", "hello"u8.ToArray())], options ?? new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new DriveSpace3FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("DriveSpace3"));
    Assert.That(d.DisplayName, Is.EqualTo("DriveSpace 3 CVF"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".cvf"));
    Assert.That(d.Extensions, Is.Empty); // shared with DoubleSpace, routed by signature
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(3));
    Assert.That(d.Methods.Select(m => m.Name), Is.SupersetOf(new[] { "stored", "jm", "sq", "auto", "auto++" }).And.Not.Contains("ds"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(((IFormatOptionsSchema)d).OptionsSchema.Any(o => o.Key == "Compatibility"), Is.False,
      "one layout only: no layout choice");
  }

  [Test, Category("HappyPath")]
  public void GivenDefaultOptions_WhenCreated_ThenTheVolumeIsTheRealDriveSpace3Container() {
    var image = Create(new DriveSpace3FormatDescriptor());

    Assert.Multiple(() => {
      Assert.That(System.Text.Encoding.ASCII.GetString(image, 3, 8), Is.EqualTo("MSDBL6.0"));
      Assert.That(image[0x0D], Is.EqualTo(64), "32 KiB clusters");
      Assert.That(image[0x33], Is.EqualTo(3), "version 3");
    });
    using var reader = new GenuineDvr3Reader(new MemoryStream(image));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "TEST.TXT")), Is.EqualTo("hello"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeOfEachCvfDescriptor_WhenDetected_ThenTheSignatureRoutesIt() {
    Assert.Multiple(() => {
      Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Create(new DriveSpace3FormatDescriptor()).AsSpan(0, 4096)).ToString(),
        Is.EqualTo("DriveSpace3"));
      Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Create(new DoubleSpaceFormatDescriptor()).AsSpan(0, 4096)).ToString(),
        Is.EqualTo("DoubleSpace"));
      // DriveSpace writes the very same version-2 container, so the DoubleSpace descriptor claims it.
      Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Create(new DriveSpaceFormatDescriptor()).AsSpan(0, 4096)).ToString(),
        Is.EqualTo("DoubleSpace"));
    });
  }

  [Test, Category("Sad")]
  public void GivenADoubleSpaceVolume_WhenListedAsDriveSpace3_ThenItIsRejected() {
    var image = Create(new DoubleSpaceFormatDescriptor());

    Assert.That(() => new DriveSpace3FormatDescriptor().List(new MemoryStream(image), null), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Sad")]
  public void GivenAnUnknownCodec_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(new DriveSpace3FormatDescriptor(), new FormatCreateOptions { MethodName = "ds-lz77" }),
      Throws.InstanceOf<ArgumentException>());
}
