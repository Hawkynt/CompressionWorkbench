using System.Security.Cryptography;
using Compression.Lib;
using FileFormat.InstallShield;

namespace Compression.Tests.InstallShield;

[TestFixture]
public class InstallShieldCabTests {

  private static string VectorDir => Path.Combine(TestContext.CurrentContext.TestDirectory, "InstallShield", "ReferenceVectors");

  [Test]
  public void GivenTraversalCabinet_WhenExtracted_ThenNothingLandsOutsideTheTarget() {
    var root = Path.Combine(Path.GetTempPath(), "cwb-is-" + Guid.NewGuid().ToString("N"));
    var target = Path.Combine(root, "a", "b", "out");
    try {
      Directory.CreateDirectory(target);
      using var fs = File.OpenRead(Path.Combine(VectorDir, "data1.hdr"));
      try {
        new InstallShieldCabFormatDescriptor().Extract(fs, target, null, null);
      } catch (InvalidDataException) {
        // Refusing the damaged data is fine; escaping the target is not.
      }
      var outside = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => !Path.GetFullPath(p).StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));
      Assert.That(outside, Is.Empty);
    } finally {
      if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
    }
  }

  [Test]
  public void GivenTraversalCabinet_WhenListed_ThenNoNameClimbsOut() {
    using var fs = File.OpenRead(Path.Combine(VectorDir, "data1.hdr"));
    var entries = new InstallShieldCabFormatDescriptor().List(fs, null);
    Assert.That(entries.Select(e => e.Name), Has.None.Contains(".."));
  }

  [Test]
  public void GivenInstallShieldHeader_WhenDetected_ThenInstallShieldCabIsChosen() {
    Assert.Multiple(() => {
      Assert.That(FormatDetector.Detect(Path.Combine(VectorDir, "data1.hdr")), Is.EqualTo(FormatDetector.Format.InstallShieldCab));
      Assert.That(FormatDetector.Detect(Path.Combine(VectorDir, "data1.cab")), Is.EqualTo(FormatDetector.Format.InstallShieldCab));
    });
  }

  [Test]
  public void GivenMicrosoftCabinetBytes_WhenOpened_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => _ = new InstallShieldCabReader(new MemoryStream("MSCF\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray()), 1, _ => null));

  [Test]
  public void GivenTruncatedHeader_WhenListed_ThenAPartialMetadataEntryIsReturned() {
    var bytes = File.ReadAllBytes(Path.Combine(VectorDir, "data1.hdr"))[..40];
    var entries = new InstallShieldCabFormatDescriptor().List(new MemoryStream(bytes), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  /// <summary>
  /// unshield's real test sets (see ReferenceVectors/README.md; CWB_INSTALLSHIELD_FIXTURES):
  /// names and MD5s must match unshield's expected list.
  /// </summary>
  [Test]
  public void GivenUnshieldTestSets_WhenExtracted_ThenEveryFileMatchesUnshieldsMd5List() {
    var dir = Environment.GetEnvironmentVariable("CWB_INSTALLSHIELD_FIXTURES");
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
      Assert.Ignore("CWB_INSTALLSHIELD_FIXTURES not set.");
    foreach (var set in Directory.GetDirectories(dir!)) {
      var md5File = Directory.GetFiles(set, "*.md5").Single();
      var expected = File.ReadAllLines(md5File).Where(l => l.Length > 34)
        .ToDictionary(l => l[34..].TrimStart('.', '/'), l => l[..32]);
      var cab = Directory.GetFiles(set, "*.hdr", SearchOption.AllDirectories).FirstOrDefault()
        ?? Directory.GetFiles(set, "data1.cab", SearchOption.AllDirectories).First();
      using var fs = File.OpenRead(cab);
      var reader = InstallShieldCabFormatDescriptor.Open(fs);
      var got = new Dictionary<string, string>();
      var failures = new List<string>();
      foreach (var e in reader.Entries) {
        try {
          got[e.FileName] = Convert.ToHexStringLower(MD5.HashData(reader.Extract(e)));
        } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) {
          failures.Add($"{e.FileName} (#{e.Index}, {e.Size} bytes): {ex.Message}");
        }
      }
      Assert.That(failures, Is.Empty, $"{Path.GetFileName(set)} (IS {reader.MajorVersion}): {failures.Count} of {reader.Entries.Count} failed");
      Assert.That(got, Is.EquivalentTo(expected), Path.GetFileName(set));
    }
  }
}
