#pragma warning disable CS1591
using Compression.Lib;
using Det = Compression.Lib.FormatDetector;

namespace Compression.Tests.FormatDetectorTests;

/// <summary>
/// Several real formats share an ambiguous file extension (.dsk, .arc, .wad) but
/// carry distinct magic. The registry's first-claim-wins extension map would route
/// every such file to one descriptor, so a freshly-written image of the OTHER format
/// is mis-detected and its reader rejects the foreign magic. These tests pin the
/// magic-aware disambiguation that keeps each writer's output self-detecting.
/// </summary>
[TestFixture]
[Category("HappyPath")]
public class SharedExtensionDisambiguationTests {

  private static string WriteTemp(string ext, byte[] magicPrefix, int totalLen) {
    var path = Path.Combine(Path.GetTempPath(), "ext_" + Guid.NewGuid().ToString("N")[..10] + ext);
    var buf = new byte[Math.Max(totalLen, magicPrefix.Length)];
    Array.Copy(magicPrefix, buf, magicPrefix.Length);
    File.WriteAllBytes(path, buf);
    return path;
  }

  [Test]
  public void DskWithCpcMagic_DetectsAsCpcDsk() {
    var p = WriteTemp(".dsk", "MV - CPCEMU Disk-File\r\nDisk-Info\r\n"u8.ToArray(), 256);
    try {
      Assert.That(Det.Detect(p).ToString(), Is.EqualTo("CpcDsk"));
    } finally { File.Delete(p); }
  }

  [Test]
  public void DskWithExtendedMagic_DetectsAsCpcDsk() {
    var p = WriteTemp(".dsk", "EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8.ToArray(), 256);
    try {
      Assert.That(Det.Detect(p).ToString(), Is.EqualTo("CpcDsk"));
    } finally { File.Delete(p); }
  }

  [Test]
  public void ArcWithFreeArcMagic_DetectsAsFreeArc() {
    var p = WriteTemp(".arc", [(byte)'A', (byte)'r', (byte)'C', 0x01], 64);
    try {
      Assert.That(Det.Detect(p).ToString(), Is.EqualTo("FreeArc"));
    } finally { File.Delete(p); }
  }

  [Test]
  public void ArcWithLegacyMagic_DetectsAsArc() {
    var p = WriteTemp(".arc", [0x1A, 0x08], 64);
    try {
      Assert.That(Det.Detect(p).ToString(), Is.EqualTo("Arc"));
    } finally { File.Delete(p); }
  }

  [Test]
  public void WadWithWad3Magic_DetectsAsWad2() {
    var p = WriteTemp(".wad", "WAD3"u8.ToArray(), 64);
    try {
      Assert.That(Det.Detect(p).ToString(), Is.EqualTo("Wad2"));
    } finally { File.Delete(p); }
  }

  [Test]
  public void WadWithWad2Magic_DetectsAsWad2() {
    var p = WriteTemp(".wad", "WAD2"u8.ToArray(), 64);
    try {
      Assert.That(Det.Detect(p).ToString(), Is.EqualTo("Wad2"));
    } finally { File.Delete(p); }
  }

  [Test]
  public void WadWithDoomMagic_DetectsAsWad() {
    var p = WriteTemp(".wad", "IWAD"u8.ToArray(), 64);
    try {
      Assert.That(Det.Detect(p).ToString(), Is.EqualTo("Wad"));
    } finally { File.Delete(p); }
  }

  // The create side has no bytes to disambiguate by, so a shared extension is settled by
  // capability instead: the first claimant that can create wins. Without this, .bundle went to
  // Mach-O and .vib to Veeam -- both read-only -- and two formats that advertise CanCreate were
  // unreachable through their own default extension.

  [TestCase(".bundle", "UnityBundle")]
  [TestCase(".vib", "Vib")]
  public void SharedExtension_ForCreate_PrefersTheClaimantThatCanCreate(string ext, string expected) {
    var path = Path.Combine(Path.GetTempPath(), "never-written" + ext);
    Assert.That(File.Exists(path), Is.False, "the create-side lookup must not need the file to exist");
    Assert.That(Det.DetectByExtensionForCreate(path).ToString(), Is.EqualTo(expected));
  }

  [TestCase(".bundle")]
  [TestCase(".vib")]
  public void SharedExtension_ForCreate_ResolvesToSomethingCreatable(string ext) {
    var format = Det.DetectByExtensionForCreate("x" + ext);
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    Assert.That(ops, Is.InstanceOf<Compression.Registry.IArchiveCreatable>(),
      $"{ext} resolved to {format}, which cannot create");
  }

  // A shared extension the read side settles by a DELIBERATE rule -- ".img" is
  // routed to FAT so that a create has a writable target, ".tar.gz" to the tar
  // compound rather than to plain Gzip -- must keep that answer on create too.
  // The capability preference is the tie-breaker for extensions nobody ruled on,
  // not a replacement for the rules.

  [TestCase(".img", "Fat")]
  [TestCase(".tar.gz", "TarGz")]
  [TestCase(".tgz", "TarGz")]
  public void RuledExtension_ForCreate_KeepsTheRuledAnswer(string ext, string expected) {
    Assert.That(Det.DetectByExtensionForCreate("volume" + ext).ToString(), Is.EqualTo(expected));
  }

  /// <summary>
  /// Every extension whose read-side answer can already create is answered the
  /// same way on create. Only an extension that resolves to a format which
  /// CANNOT create may differ, and then only towards one that can.
  /// </summary>
  [Test]
  public void ForCreate_OnlyDivergesFromTheReadSideWhereTheReadSideCannotWrite() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var divergent = new List<string>();
    foreach (var descriptor in Compression.Registry.FormatRegistry.All)
      foreach (var ext in descriptor.Extensions.Concat(descriptor.CompoundExtensions).Distinct()) {
        if (string.IsNullOrEmpty(ext) || ext == ".exe" || ext == ".ar" || ext == ".a" || ext == ".deb") continue;
        var path = "probe" + ext;
        var read = Det.DetectByExtension(path);
        if (read == Det.Format.Unknown) continue;
        var readDesc = Compression.Registry.FormatRegistry.GetById(read.ToString());
        if (readDesc == null || !readDesc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate)) continue;
        var create = Det.DetectByExtensionForCreate(path);
        if (create != read) divergent.Add($"{ext}: read={read} create={create}");
      }
    Assert.That(divergent, Is.Empty,
      "The create-side lookup overrode an extension whose read-side answer can already create: "
      + string.Join("; ", divergent));
  }

  [Test]
  public void UnsharedExtension_ForCreate_MatchesTheReadSideLookup() {
    Assert.That(Det.DetectByExtensionForCreate("x.zip"), Is.EqualTo(Det.DetectByExtension("x.zip")));
  }
}
