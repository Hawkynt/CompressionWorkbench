using System.Reflection;
using System.Text;
using Compression.Core.Streams;
using FileFormat.Gzip;
using FileFormat.Hlp;

namespace Compression.Tests.Hlp;

/// <summary>
/// Windows Help. The fixture is PuTTY 0.60's PUTTY.HLP (MIT; compiled by HCRTF), stored gzip-compressed.
/// Oracle: helpdeco (<c>-d</c> lists the internal directory with file offsets, <c>helpdeco file "|X"</c>
/// reports each internal file's size). See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class HlpTests {

  private static byte[] Putty() {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HlpVectors.putty-0.60.hlp.gz")
      ?? throw new FileNotFoundException("Embedded HLP vector is missing from the test assembly.");
    using var gz = new GzipStream(stream, CompressionStreamMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>helpdeco's view of PUTTY.HLP: name, FILEHEADER offset (<c>-d</c>) and FileSize.</summary>
  private static readonly (string Name, int Offset, int Size)[] Helpdeco = [
    ("|CONTEXT", 0x00000010, 6182), ("|CTXOMAP", 0x0000183F, 2), ("|FONT", 0x0000184A, 203),
    ("|KWBTREE", 0x0000191E, 24614), ("|KWDATA", 0x0000794D, 5792), ("|KWMAP", 0x00008FF6, 68),
    ("|SYSTEM", 0x00009043, 340), ("|TOPIC", 0x000091A0, 564452), ("|TTLBTREE", 0x00092E8D, 26662),
  ];

  [Test, Category("ThirdPartyVector")]
  public void Given_PuttyHelp_When_DirectoryRead_Then_MatchesHelpdeco() {
    var r = new HlpReader(Putty());
    Assert.That(r.Files.Select(f => (f.Name, f.HeaderOffset, f.UsedSpace)), Is.EqualTo(Helpdeco));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_PuttyHelp_When_SystemExtracted_Then_HoldsTheTitleHelpdecoShows() {
    var r = new HlpReader(Putty());
    var system = r.Extract(r.Files.Single(f => f.Name == "|SYSTEM"));
    Assert.That(system, Has.Length.EqualTo(340));
    Assert.That(BitConverter.ToUInt16(system, 0), Is.EqualTo(0x036C), "|SYSTEM magic");
    Assert.That(Encoding.Latin1.GetString(system), Does.Contain("PuTTY User Manual"));
  }

  [Test, Category("HappyPath")]
  public void Given_PuttyHelp_When_Listed_Then_PipeShownAsHash() {
    var list = new HlpFormatDescriptor().List(new MemoryStream(Putty()), null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(Helpdeco.Select(h => "#" + h.Name[1..])));
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_hlp_{Guid.NewGuid():N}");
    try {
      new HlpFormatDescriptor().Extract(new MemoryStream(Putty()), dir, null, ["#FONT"]);
      Assert.That(new FileInfo(Path.Combine(dir, "#FONT")).Length, Is.EqualTo(203));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Given_HlpPath_When_Detected_Then_Hlp() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_hlp_{Guid.NewGuid():N}.hlp");
    File.WriteAllBytes(path, Putty());
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("Hlp"));
    } finally { File.Delete(path); }
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongMagic_When_Read_Then_InvalidData() {
    var bytes = Putty();
    bytes[0] = 0;
    Assert.Throws<InvalidDataException>(() => _ = new HlpReader(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_DirectoryOffsetPastTheEnd_When_Read_Then_InvalidData() {
    var bytes = Putty();
    BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, 4);
    Assert.Throws<InvalidDataException>(() => _ = new HlpReader(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedFile_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new HlpReader(Putty()[..4096]));

  [Test, Category("EdgeCase")]
  public void Given_InternalFileClaimingMoreThanTheFile_When_Read_Then_InvalidData() {
    var bytes = Putty();
    BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, 0x91A0 + 4); // |TOPIC's UsedSpace
    Assert.Throws<InvalidDataException>(() => _ = new HlpReader(bytes));
  }

  // ── helpdeco live (built from source in WSL when available) ────────

  [Test, Category("EndToEnd")]
  public void Given_PuttyHelp_When_ListedByHelpdeco_Then_SameDirectory() {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL not available.");
    var helpdeco = Environment.GetEnvironmentVariable("CWB_HELPDECO");
    if (string.IsNullOrEmpty(helpdeco)) Assert.Ignore("Set CWB_HELPDECO to a helpdeco binary in WSL (built from github.com/pmachapman/helpdeco).");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_hlp_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      File.WriteAllBytes(Path.Combine(dir, "PUTTY.HLP"), Putty());
      var tool = helpdeco.StartsWith('/') ? helpdeco : FsInteropToolbox.WinToWsl(helpdeco); // a Windows path is mapped into WSL
      var run = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(dir)} && {tool} PUTTY.HLP -d 2>&1");
      var listing = run.StdOut + run.StdErr;
      foreach (var (name, offset, _) in Helpdeco)
        Assert.That(listing, Does.Match($@"{System.Text.RegularExpressions.Regex.Escape(name)}\s+0x{offset:X8}"));
    } finally { Directory.Delete(dir, true); }
  }
}
