using System.Security.Cryptography;
using FileFormat.InnoSetup;

namespace Compression.Tests.InnoSetup;

/// <summary>
/// The Inno Setup writer across every setup data layout from 6.0 to 7.1.1, one installed release
/// per layout below CWB_INNO_STUBS_ROOT (directories IS605, IS612, …, IS711 holding that release's
/// SetupLdr.e32, Setup.e32, Default.isl and wizard bitmaps). For each release:
/// <list type="bullet">
///   <item><description>both setup-0 blocks we write are byte for byte the ones that release's ISCC wrote
///   for ReferenceVectors/iscc-headers/ref.iss and the same two files (SHA-256 below, captured by
///   make-iscc-headers.sh);</description></item>
///   <item><description>our reader reads the installer back;</description></item>
///   <item><description>under wine (CWB_INNO_WINEPREFIX) the installer, run with /VERYSILENT /DIR=, installs
///   exactly the payload.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class InnoSetupWriterVersionTests {
  /// <summary>Release directory, setup data id, SHA-256 of ISCC's block 1 (headers) and block 2 (file locations).</summary>
  private static readonly (string Release, string Layout, string Block1, string Block2)[] Releases = [
    ("IS605", "6.0.0", "149539f25dafa0d1c91bc8558e9f826a63afe5c1e4dd5a6a3385d2298bfd77fa", "bd595709532ae459986d9ed4aba1701850f35fa829796ae15c23342e8f51cb45"),
    ("IS612", "6.1.0", "1ddc4a41c0d5c7c2d4448e3dc044f8b8b6b47b609b5423300cfb8e96c12c4a0f", "bd595709532ae459986d9ed4aba1701850f35fa829796ae15c23342e8f51cb45"),
    ("IS622", "6.1.0", "1ddc4a41c0d5c7c2d4448e3dc044f8b8b6b47b609b5423300cfb8e96c12c4a0f", "bd595709532ae459986d9ed4aba1701850f35fa829796ae15c23342e8f51cb45"),
    ("IS633", "6.3.0", "086c6661c9e0e104e20ceca5b1f0b2f4ce95627ed54c5a9ad3167a4573945158", "9ebcfcbd78d27bd6375384cf5d42518320e760de238f08b99d2bb55d5e243dd5"),
    ("IS641", "6.4.0.1", "3e3d5d05718cb439ce017dee2804709d87ced739560776faccf0b3d4d58b0789", "58a8f1533233b118e33e316ae5854b23beacbab08a65a2a42013de78c72f3e80"),
    ("IS642", "6.4.2", "e2a7aa8102ae552103fa98f6e9f52802d3b0a45cb512d027b03995d09063c15f", "58a8f1533233b118e33e316ae5854b23beacbab08a65a2a42013de78c72f3e80"),
    ("IS643", "6.4.3", "e2a7aa8102ae552103fa98f6e9f52802d3b0a45cb512d027b03995d09063c15f", "5b46653996dd35c9d53ba5fcff673edf0e1265dfcc2cbf2a3a05bb0c9cc62b33"),
    ("IS651", "6.5.0", "38f7ec4c659da7b768e776a0a77c3133dcf57b8951477b5590d7477949c44569", "5b46653996dd35c9d53ba5fcff673edf0e1265dfcc2cbf2a3a05bb0c9cc62b33"),
    ("IS654", "6.5.2", "a03a899a9e837a15c3c9fd9657d87180f2f1c9b8875c4e60579439814fe7979d", "24ea3b5eefa53af6dcf845f3dd8d6960b238d1151a5a51728cf9809678d32384"),
    ("IS660", "6.6.0", "b569be1b43b13baf12eff8ea953bf5d8891eee9a6792a38f0885fcdd99fe65f1", "24ea3b5eefa53af6dcf845f3dd8d6960b238d1151a5a51728cf9809678d32384"),
    ("IS661", "6.6.1", "e67318ef72ccb89db9de06e87871317c8c4ba669c7c1d5fa77b978fbb0743836", "24ea3b5eefa53af6dcf845f3dd8d6960b238d1151a5a51728cf9809678d32384"),
    ("IS674", "6.7.0", "db5235709accd7fa0022820e51ea56ad281963727b7e4442d22ea54de7fa0c64", "24ea3b5eefa53af6dcf845f3dd8d6960b238d1151a5a51728cf9809678d32384"),
    ("IS710", "7.0.0.3", "00dcb0db39e8e835cafa0914a9dbd68dcf6d647180f90eb86e9f4c4def9a4e31", "24ea3b5eefa53af6dcf845f3dd8d6960b238d1151a5a51728cf9809678d32384"),
    ("IS711", "7.1.1", "e1574961d623819fc632bf8bcaf5469dc763c1b3ff7bb230171f0913f73a2983", "24ea3b5eefa53af6dcf845f3dd8d6960b238d1151a5a51728cf9809678d32384"),
  ];

  private static IEnumerable<TestCaseData> AllReleases() => Releases.Select(r => new TestCaseData(r.Release).SetArgDisplayNames(r.Release));

  private static InnoSetupStubs Stubs(string release) {
    var root = Environment.GetEnvironmentVariable("CWB_INNO_STUBS_ROOT");
    var stubs = string.IsNullOrEmpty(root) ? null : InnoSetupStubs.Load(Path.Combine(root, release));
    if (stubs == null || !File.Exists(Path.Combine(root!, release, "SetupLdr.e32")))
      Assert.Ignore($"No Inno Setup release {release} below CWB_INNO_STUBS_ROOT.");
    return stubs!;
  }

  /// <summary>The two files of ref.iss, with the time stamp make-iscc-headers.sh gives them.</summary>
  private static List<InnoSetupInstallerFile> ReferenceFiles() {
    var when = new DateTime(2020, 1, 2, 3, 4, 4, DateTimeKind.Utc);
    return [
      new("hello.txt", "hello inno\r\n"u8.ToArray(), when),
      new("sub/data.bin", Enumerable.Repeat((byte)'x', 3000).ToArray(), when),
    ];
  }

  [TestCaseSource(nameof(AllReleases)), Category("ExternalInterop")]
  public void GivenTheReferenceScript_WhenWeWriteItsHeaders_ThenTheyAreWhatThatReleasesIsccWrote(string release) {
    var stubs = Stubs(release);
    var expected = Releases.Single(r => r.Release == release);
    Assert.That(System.Text.Encoding.ASCII.GetString(InnoSetupWriter.Identify(stubs).SetupId).TrimEnd('\0'), Does.StartWith($"Inno Setup Setup Data ({expected.Layout})"));
    var (block1, block2) = InnoSetupWriter.Headers(stubs, ReferenceFiles(),
      new InnoSetupInstallerOptions { AppName = "CWB Test", AppVersion = "1.0", Compression = InnoSetupWriterCompression.Stored });
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(block1)), Is.EqualTo(expected.Block1), "setup-0 headers block");
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(block2)), Is.EqualTo(expected.Block2), "setup-0 file locations block");
  }

  [TestCaseSource(nameof(AllReleases)), Category("RoundTrip")]
  public void GivenAnInstallerForThatRelease_WhenOurReaderReadsIt_ThenEveryFileComesBack(string release) {
    var stubs = Stubs(release);
    foreach (var compression in new[] { InnoSetupWriterCompression.Stored, InnoSetupWriterCompression.Lzma2 }) {
      var installer = Build(stubs, compression);
      var reader = new InnoSetupReader(new MemoryStream(installer), null);
      var got = reader.Entries.ToDictionary(e => e.FileName, reader.Extract);
      var expected = InnoSetupWriterTests.ExpectedFiles().ToDictionary(kv => "app/" + kv.Key, kv => kv.Value);
      Assert.That(got.Keys, Is.EquivalentTo(expected.Keys), $"{compression}: names");
      foreach (var (name, data) in expected)
        Assert.That(got[name], Is.EqualTo(data), $"{compression}: {name}");
    }
  }

  [TestCaseSource(nameof(AllReleases)), Category("EndToEnd")]
  public void GivenAnInstallerForThatRelease_WhenRunVerySilentlyUnderWine_ThenItInstallsExactlyTheFiles(string release) {
    var stubs = Stubs(release);
    // From 6.3 Setup expands the default directory before it reads /DIR=, and wine has no per-user
    // program files folder for {autopf} in a non-administrative install (ISCC's own installers fail
    // the same way there), so these run with a default directory wine can expand.
    foreach (var compression in new[] { InnoSetupWriterCompression.Stored, InnoSetupWriterCompression.Lzma2 })
      InnoSetupWriterTests.RunAndCheck(Build(stubs, compression, "{localappdata}\\CWB Test"), $"{release} {compression}");
  }

  private static byte[] Build(InnoSetupStubs stubs, InnoSetupWriterCompression compression, string? defaultDir = null) {
    using var ms = new MemoryStream();
    InnoSetupWriter.Write(ms, stubs, InnoSetupWriterTests.Payload(),
      new InnoSetupInstallerOptions { AppName = "CWB Test", Compression = compression, DefaultDirName = defaultDir });
    return ms.ToArray();
  }
}
