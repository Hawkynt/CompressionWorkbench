using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.OnePkg;

namespace Compression.Tests.OnePkg;

/// <summary>
/// OneNote packages. The fixture is a cabinet built by Windows' <c>makecab</c> from the two Apache
/// Tika OneNote sections already used by the OneNote tests; 7-Zip 26.04 lists it as an MSZIP CAB.
/// See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class OnePkgTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OnePkgVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded ONEPKG vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly (string Name, long Size, string Sha256)[] Members = [
    ("testOneNote.one", 30288, "b614dc94b890b53db7cb2d3053382cb398c59385533c256e2509850cc3247270"),
    ("test-tika-4303-Chinese-notes.one", 51816, "39f6eeaadcabc8aeb480d3db2adf22b338bfc90030ea343209d5e7407e0e5d0e"),
  ];

  [Test, Category("ThirdPartyVector")]
  public void Given_MakecabPackage_When_Listed_Then_BothSectionsWithTheirSizes() {
    var list = new OnePkgFormatDescriptor().List(new MemoryStream(Vector("makecab-package.onepkg")), null);
    Assert.That(list.Select(e => (e.Name, e.OriginalSize)), Is.EqualTo(Members.Select(m => (m.Name, m.Size))));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_MakecabPackage_When_Extracted_Then_SectionsAreTheOriginalOneNoteFiles() {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_onepkg_{Guid.NewGuid():N}");
    try {
      new OnePkgFormatDescriptor().Extract(new MemoryStream(Vector("makecab-package.onepkg")), dir, null, null);
      foreach (var (name, _, sha) in Members)
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name)))), Is.EqualTo(sha), name);
      // The extracted section is a OneNote file our OneNote reader accepts.
      var section = File.ReadAllBytes(Path.Combine(dir, "testOneNote.one"));
      Assert.That(new FileFormat.OneNote.OneNoteFormatDescriptor().List(new MemoryStream(section), null), Is.Not.Empty);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Given_OnePkgPath_When_Detected_Then_OnePkgNotCab() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_onepkg_{Guid.NewGuid():N}.onepkg");
    File.WriteAllBytes(path, Vector("makecab-package.onepkg"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("OnePkg"));
    } finally { File.Delete(path); }
  }

  [Test, Category("HappyPath")]
  public void Given_Descriptor_When_Inspected_Then_ReadOnly() {
    var d = new OnePkgFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongMagic_When_Listed_Then_Throws() {
    var bytes = Vector("makecab-package.onepkg");
    bytes[0] = (byte)'X';
    Assert.Catch<Exception>(() => new OnePkgFormatDescriptor().List(new MemoryStream(bytes), null));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedPackage_When_Extracted_Then_Throws() {
    var bytes = Vector("makecab-package.onepkg")[..4000];
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_onepkg_{Guid.NewGuid():N}");
    try {
      Assert.Catch<Exception>(() => new OnePkgFormatDescriptor().Extract(new MemoryStream(bytes), dir, null, null));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
