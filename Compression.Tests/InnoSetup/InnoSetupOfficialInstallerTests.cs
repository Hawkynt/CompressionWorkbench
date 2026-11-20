using System.Security.Cryptography;
using FileFormat.InnoSetup;

namespace Compression.Tests.InnoSetup;

/// <summary>
/// Inno Setup's own distribution installers (innosetup-7.1.1-x64.exe / -x86.exe from
/// https://github.com/jrsoftware/issrc/releases/tag/is-7_1_1), too large to embed: point
/// CWB_INNO_OFFICIAL_DIR at a directory holding them. Every file must extract and hash, with the
/// BCL's SHA-256, to the SHA-256 the installer stores for it. CWB_REQUIRE_INNO_OFFICIAL=1 turns a
/// missing directory into a failure.
/// </summary>
[TestFixture]
public class InnoSetupOfficialInstallerTests {

  private static IReadOnlyList<string> Installers() {
    var dir = Environment.GetEnvironmentVariable("CWB_INNO_OFFICIAL_DIR");
    var found = string.IsNullOrEmpty(dir) || !Directory.Exists(dir) ? [] : Directory.GetFiles(dir, "innosetup-*.exe");
    if (found.Length == 0) {
      const string why = "No official Inno Setup installer (set CWB_INNO_OFFICIAL_DIR to a directory with innosetup-*.exe).";
      if (Environment.GetEnvironmentVariable("CWB_REQUIRE_INNO_OFFICIAL") == "1")
        Assert.Fail(why + " CWB_REQUIRE_INNO_OFFICIAL=1 says it is there.");
      Assert.Ignore(why);
    }
    return found;
  }

  [Test]
  public void GivenTheOfficialInstaller_WhenEveryFileIsExtracted_ThenEachMatchesTheSha256ItStores() {
    foreach (var path in Installers()) {
      using var stream = File.OpenRead(path);
      var reader = new InnoSetupReader(stream);
      var name = Path.GetFileName(path);
      var version = Path.GetFileNameWithoutExtension(path).Split('-')[1];

      Assert.That(reader.AppVersion, Does.StartWith(version), name); // the x86 build says "7.1.1 (32-bit)"
      Assert.That(reader.Entries.Select(e => e.FileName), Does.Contain("app/ISCC.exe"), name);

      var checkedFiles = 0;
      reader.ExtractMany(reader.Entries, (entry, bytes) => {
        var (kind, stored) = reader.StoredChecksum(entry);
        Assert.That(kind, Is.EqualTo(InnoChecksumKind.Sha256), entry.FileName);
        Assert.That(bytes, Has.Length.EqualTo(entry.Size), entry.FileName);
        Assert.That(SHA256.HashData(bytes), Is.EqualTo(stored), $"{name}: {entry.FileName}");
        ++checkedFiles;
      });
      Assert.That(checkedFiles, Is.EqualTo(reader.Entries.Count), name);
    }
  }
}
