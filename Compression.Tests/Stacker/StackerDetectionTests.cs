using Compression.Registry;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

[TestFixture]
public class StackerDetectionTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new StackerFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Stacker"));
    Assert.That(d.DisplayName, Is.EqualTo("Stacker CVF"));
    Assert.That(d.Extensions, Does.Contain(".sta"));
    Assert.That(d.Extensions, Does.Contain(".stk"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d is IFormatOptionsSchema schema && schema.OptionsSchema.Any(o => o.Key == "Compatibility"), Is.False,
      "Stacker writes one layout only; there is no layout choice to offer.");
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeCreatedByTheDescriptor_WhenDetected_ThenItIsStacker() {
    var image = Create(new FormatCreateOptions());

    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(image.AsSpan(0, 4096)).ToString(), Is.EqualTo("Stacker"));
  }

  [Test, Category("HappyPath")]
  public void GivenDefaultOptions_WhenCreated_ThenTheVolumeIsTheRealStacvolLayout() {
    var image = Create(new FormatCreateOptions());

    using var reader = new GenuineStackerReader(new MemoryStream(image));
    Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "DATA.BIN" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var d = new StackerFormatDescriptor();
    using var ms = new MemoryStream(Create(new FormatCreateOptions()));
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Some.Matches<ArchiveEntryInfo>(e => e.Name == "DATA.BIN"));
  }

  [Test, Category("Sad")]
  public void GivenAnImageWithoutTheStackerBanner_WhenListed_ThenItIsRejected() {
    var img = new byte[2048];
    img[0] = 0xFF; img[1] = 0xFF; img[2] = 0xFF;

    Assert.That(() => new StackerFormatDescriptor().List(new MemoryStream(img), null), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Sad")]
  public void GivenAnUnknownCodec_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(new FormatCreateOptions { MethodName = "stacker-lzs" }), Throws.InstanceOf<ArgumentException>());

  // A real Stacker volume produced by the Stacker 3.10 CREATE tool under MS-DOS 6.22, staged at
  // ~/.cw-qemu/real_stacker_v3.dsk.
  [Test, Category("ExternalInterop")]
  public void Detect_RealStackerVolume() {
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var path = Path.Combine(home, ".cw-qemu", "real_stacker_v3.dsk");
    if (!File.Exists(path))
      Assert.Ignore("Real Stacker STACVOL not staged at ~/.cw-qemu/real_stacker_v3.dsk.");
    using var reader = new GenuineStackerReader(File.OpenRead(path));
    Assert.That(reader.Version, Is.LessThan(410), "a Stacker 3 volume");
  }

  private static byte[] Create(FormatCreateOptions options) {
    using var ms = new MemoryStream();
    ((IArchiveCreatable)new StackerFormatDescriptor()).Create(ms, [ArchiveInputInfo.InMemory("DATA.BIN", [1, 2, 3, 4, 5])], options);
    return ms.ToArray();
  }
}
