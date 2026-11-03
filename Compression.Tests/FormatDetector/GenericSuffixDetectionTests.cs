using Compression.Lib;
using Compression.Registry;
using Det = Compression.Lib.FormatDetector;

namespace Compression.Tests.FormatDetectorTests;

/// <summary>
/// Behind a suffix that says nothing about the contents (".bin", ".dat", ".img", …) a clear
/// signature decides the format; a specific suffix still decides on its own. And a descriptor
/// whose id differs from its generated enum member only in case is still recognised.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser d401d17 and b56edf5.</remarks>
[TestFixture]
public sealed class GenericSuffixDetectionTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_generic_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private string Zip(string name) {
    var file = Path.Combine(this._dir, "a.txt");
    File.WriteAllText(file, "alpha");
    var zip = Path.Combine(this._dir, "made.zip");
    ArchiveOperations.Create(zip, [new ArchiveInput(file, "a.txt")], new CompressionOptions());
    var target = Path.Combine(this._dir, name);
    File.Move(zip, target);
    return target;
  }

  [TestCase("backup.bin")]
  [TestCase("backup.dat")]
  [TestCase("backup.data")]
  [TestCase("backup.dump")]
  [TestCase("backup.raw")]
  [Category("HappyPath")]
  public void GivenAZipBehindAGenericSuffix_WhenDetected_ThenItIsAZip(string name) {
    var path = this.Zip(name);

    Assert.Multiple(() => {
      Assert.That(Det.Detect(path), Is.EqualTo(Det.Format.Zip));
      Assert.That(ArchiveOperations.List(path, null).Select(e => e.Name), Is.EqualTo(new[] { "a.txt" }));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAZipNamedJar_WhenDetected_ThenTheSpecificSuffixDecides() {
    var path = this.Zip("x.jar");

    Assert.That(Det.Detect(path), Is.EqualTo(Det.Format.Jar));
  }

  [Test, Category("HappyPath")]
  public void GivenAnIsoBehindBin_WhenDetected_ThenTheDiskImageProbeStillDecides() {
    var iso = new FileSystem.Iso.IsoWriter();
    iso.AddFile("A.TXT", "a"u8.ToArray());
    var path = Path.Combine(this._dir, "disc.bin");
    File.WriteAllBytes(path, iso.Build());

    Assert.That(Det.Detect(path), Is.EqualTo(Det.Format.Iso));
  }

  [Test, Category("EdgeCase")]
  public void GivenNoiseBehindBin_WhenDetected_ThenTheSuffixClaimantStays() {
    var path = Path.Combine(this._dir, "noise.bin");
    var noise = new byte[4096];
    new Random(7).NextBytes(noise);
    noise[0] = 0x00; // no accidental signature at the very start
    File.WriteAllBytes(path, noise);

    Assert.That(Det.Detect(path), Is.EqualTo(Det.DetectByExtension(path)));
  }

  [Test, Category("EdgeCase")]
  public void GivenAnEmptyFileBehindImg_WhenDetected_ThenTheSuffixClaimantStays() {
    var path = Path.Combine(this._dir, "new.img");
    File.WriteAllBytes(path, []);

    Assert.That(Det.Detect(path), Is.EqualTo(Det.Format.Fat));
  }

  [Test, Category("HappyPath")]
  public void GivenEveryRegisteredDescriptor_WhenItsIdIsMapped_ThenNoTwoIdsDifferOnlyInCase() {
    FormatRegistration.EnsureInitialized();

    var clashes = FormatRegistry.All
      .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
      .Where(g => g.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() > 1)
      .Select(g => string.Join("/", g.Select(d => d.Id)));

    Assert.That(clashes, Is.Empty, "ids differing only in case would map to one format");
  }

  [Test, Category("HappyPath")]
  public void GivenEveryRegisteredDescriptor_WhenItsIdIsParsedIgnoringCase_ThenItNamesAFormat() {
    FormatRegistration.EnsureInitialized();

    var unmapped = FormatRegistry.All
      .Where(d => Enum.TryParse<Det.Format>(d.Id, ignoreCase: false, out _) == false
                  && Enum.TryParse<Det.Format>(d.Id, ignoreCase: true, out _))
      .Select(d => d.Id)
      .ToList();

    // Such an id used to be dropped from the id map entirely; it is resolved now.
    foreach (var id in unmapped)
      Assert.That(Det.GetDesc(Enum.Parse<Det.Format>(id, ignoreCase: true))?.Id, Is.EqualTo(id), id);
  }
}
