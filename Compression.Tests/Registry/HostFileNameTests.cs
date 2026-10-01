using Compression.Registry;

namespace Compression.Tests.Registry;

/// <summary>
/// Entry names that are legal in the archive or file system they came from but not on the host
/// extract under a recognisable name instead of failing — HFS+'s <c>forward:slash</c> and its
/// metadata names with U+0000 or a carriage return are the cases that stopped a whole-volume
/// extraction on Windows.
/// </summary>
[TestFixture]
public sealed class HostFileNameTests {

  [TestCase("plain.txt", "plain.txt", TestName = "GivenAnOrdinaryName_WhenMadeSafeForWindows_ThenItIsUnchanged")]
  [TestCase("forward:slash", "forward_slash", TestName = "GivenAColon_WhenMadeSafeForWindows_ThenItIsAnUnderscore")]
  [TestCase("a<b>c\"d|e?f*g", "a_b_c_d_e_f_g", TestName = "GivenEveryReservedPunctuationMark_WhenMadeSafeForWindows_ThenEachIsAnUnderscore")]
  [TestCase(".HFS+ Private Directory Data\r", ".HFS+ Private Directory Data_", TestName = "GivenACarriageReturn_WhenMadeSafeForWindows_ThenItIsAnUnderscore")]
  [TestCase("\0\0\0\0HFS+ Private Data", "____HFS+ Private Data", TestName = "GivenNulCharacters_WhenMadeSafeForWindows_ThenEachIsAnUnderscore")]
  [TestCase("\u001f\u0001", "__", TestName = "GivenControlCharactersAtTheBoundary_WhenMadeSafeForWindows_ThenEachIsAnUnderscore")]
  [TestCase(" ", "_", TestName = "GivenASingleBlank_WhenMadeSafeForWindows_ThenItIsAnUnderscore")]
  [TestCase("name. .", "name___", TestName = "GivenTrailingDotsAndBlanks_WhenMadeSafeForWindows_ThenEachIsAnUnderscore")]
  [TestCase(".hidden", ".hidden", TestName = "GivenALeadingDot_WhenMadeSafeForWindows_ThenItIsUnchanged")]
  [TestCase("CON", "_CON", TestName = "GivenADeviceName_WhenMadeSafeForWindows_ThenItIsPrefixed")]
  [TestCase("com1.txt", "_com1.txt", TestName = "GivenADeviceNameWithAnExtension_WhenMadeSafeForWindows_ThenItIsPrefixed")]
  [TestCase("CONSOLE", "CONSOLE", TestName = "GivenANameStartingLikeADevice_WhenMadeSafeForWindows_ThenItIsUnchanged")]
  [TestCase("COM10", "COM10", TestName = "GivenComTen_WhenMadeSafeForWindows_ThenItIsUnchanged")]
  [TestCase(".", ".", TestName = "GivenTheCurrentDirectoryName_WhenMadeSafeForWindows_ThenItIsUnchanged")]
  [TestCase("", "", TestName = "GivenAnEmptyName_WhenMadeSafeForWindows_ThenItStaysEmpty")]
  [Category("BoundaryCase")]
  public void Windows(string name, string expected) => Assert.That(FormatHelpers.HostFileName(name, windows: true), Is.EqualTo(expected));

  [TestCase("forward:slash", "forward:slash", TestName = "GivenAColon_WhenMadeSafeForPosix_ThenItIsKept")]
  [TestCase("a\rb", "a\rb", TestName = "GivenACarriageReturn_WhenMadeSafeForPosix_ThenItIsKept")]
  [TestCase("\0x", "_x", TestName = "GivenANul_WhenMadeSafeForPosix_ThenItIsAnUnderscore")]
  [TestCase("CON", "CON", TestName = "GivenAWindowsDeviceName_WhenMadeSafeForPosix_ThenItIsKept")]
  [TestCase("trailing.", "trailing.", TestName = "GivenATrailingDot_WhenMadeSafeForPosix_ThenItIsKept")]
  [Category("BoundaryCase")]
  public void Posix(string name, string expected) => Assert.That(FormatHelpers.HostFileName(name, windows: false), Is.EqualTo(expected));

  [Test, Category("HappyPath")]
  public void GivenAnEntryWithAHostIllegalName_WhenWritten_ThenItLandsInItsFolderUnderTheSafeName() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_hostname_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      FormatHelpers.WriteFile(dir, "vol/sub:dir/forward:slash", [1, 2]);
      var expected = OperatingSystem.IsWindows()
        ? Path.Combine(dir, "vol", "sub_dir", "forward_slash")
        : Path.Combine(dir, "vol", "sub:dir", "forward:slash");
      Assert.That(File.ReadAllBytes(expected), Is.EqualTo(new byte[] { 1, 2 }));
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
  }
}
