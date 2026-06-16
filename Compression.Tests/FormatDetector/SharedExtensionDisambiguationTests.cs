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
}
