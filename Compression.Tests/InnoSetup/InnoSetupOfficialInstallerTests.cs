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

  // isetup16-1.2.16.exe (https://files.jrsoftware.org/is/1.2/, SHA-256 9f4fbab8…1dc5), the 16-bit
  // Inno Setup 1.2.16 distribution, itself a 1.2.10 (16-bit) installer. Expected: what innoextract
  // 1.10-dev (6e9e34e) extracts from it, path|size|SHA-256. Point CWB_INNO_LEGACY_DIR at its directory.
  private static readonly string[] Isetup16Files = [
    "app/COMPIL16.EXE|280984|c42ff94c38376e1f3a0a3d8913ae2540921345f58ff3c534dc4c87e57905b02f",
    "app/DEFAULT.ISL|12020|8933ad32a9ae6cdc4e69e0c9848d4011a4f92fde22eeb15c9b37f0e216890c89",
    "app/ISETUP.CNT|930|1c7cf398683cb08899b2cbe73afa11c4ea90903af9fa1051ffe9ee2b208e6219",
    "app/ISETUP.HLP|112760|e244f96a989110c9944034f3288c1a5b43d3d9140e9bf72cf510ee6e323f2f54",
    "app/SAMPLE16/CTL3DV2.DLL|27632|128f259a06b050beb488f80cee093d3deb4a2b4a4e03460ce3075d3cd0b580e6",
    "app/SAMPLE16/MYPROG.EXE|3840|24f2d55d790792c57a007a904523a5699dc78d307f8604c0d8934fc53c78b07c",
    "app/SAMPLE16/MYPROG.HLP|6006|6458c79246dc505adedf7498abd5a9e4198eef7b9fbd2b5cd8b3667fe6a4696b",
    "app/SAMPLE16/README.TXT|48|8cb5a1067b66e78c21d16a457f7732eb68345af897a91ab61d9247b314f66b75",
    "app/SAMPLE16/SAMPLE1.ISS|634|93ff84e4b55aa54c74259aecf068f2654d639258f0d38047ba4cdc0c510a47c1",
    "app/SETUP.E16|309040|e2bba0d1f3043693a9413ee588d141e5e144727ae19919a0cca3f446386965cc",
    "app/SETUPLDR.E16|43498|d085507901f93d394695cced52533bc88447538202e3840a911b838025de1133",
    "app/UNINST.E16|37846|7867c2526fc59a8d2d770b028170fb62799164e94fac0d876f9ed74c3e705af7",
    "app/WIZIMAGE.BMP|7798|e2a7e322fbf1c04c9778ac5bca971c824404cd68d6a9523f6f9065a98aacc3f6",
  ];

  [Test]
  public void GivenThe16BitInnoSetupDistribution_WhenEveryFileIsExtracted_ThenItMatchesInnoextract() {
    var dir = Environment.GetEnvironmentVariable("CWB_INNO_LEGACY_DIR");
    var path = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "isetup16-1.2.16.exe");
    if (path == null || !File.Exists(path))
      Assert.Ignore("Set CWB_INNO_LEGACY_DIR to a directory holding isetup16-1.2.16.exe.");
    var bytes = File.ReadAllBytes(path);
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo("9f4fbab8039f40cd3ea6dcbe34de0d4d40ed4037bc1c9737027b20181e461dc5"));

    var reader = new InnoSetupReader(new MemoryStream(bytes));
    Assert.That(reader.Version.Is16Bit, Is.True);
    var extracted = new List<string>();
    reader.ExtractMany(reader.Entries, (entry, data) =>
      extracted.Add($"{entry.FileName}|{data.Length}|{Convert.ToHexStringLower(SHA256.HashData(data))}"));
    Assert.That(extracted.Order(StringComparer.Ordinal), Is.EqualTo(Isetup16Files));
  }
}
