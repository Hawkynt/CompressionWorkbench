using System.Buffers.Binary;
using System.Security.Cryptography;
using FileFormat.OnePkg;

namespace Compression.Tests.OnePkg;

/// <summary>
/// The OneNote packages Microsoft shipped inside Office 2007/2010 installers (the sample notebooks and the
/// OneNote guide). They are Microsoft's files and are not committed; point <c>CWB_ONEPKG_DIR</c> at a folder
/// of them (docs/ONEPKG-ON-DISK.md lists the 38 used, with their SHA-256). Each is read as 7-Zip reads it,
/// and each carries the Authenticode signature reserve that makes byte-exact reproduction impossible.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
public class OnePkgMicrosoftPackagesTests {

  private static List<string> Packages() {
    var dir = Environment.GetEnvironmentVariable("CWB_ONEPKG_DIR");
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Assert.Ignore("Set CWB_ONEPKG_DIR to a folder of Microsoft-made .onepkg files.");
    var files = Directory.GetFiles(dir).Where(f => File.ReadAllBytes(f).AsSpan().StartsWith("MSCF"u8)).Order(StringComparer.Ordinal).ToList();
    if (files.Count == 0) Assert.Ignore("No cabinet files in CWB_ONEPKG_DIR.");
    return files;
  }

  private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

  [Test]
  public void Given_MicrosoftPackages_When_ExtractedByUsAndSevenZip_Then_SameSections() {
    if (!FsInteropToolbox.SevenZipAvailable) Assert.Ignore("7-Zip not available.");
    var work = Path.Combine(Path.GetTempPath(), $"cwb_onepkgms_{Guid.NewGuid():N}");
    var failures = new List<string>();
    try {
      foreach (var (pkg, i) in Packages().Select((p, i) => (p, i))) {
        var ours = Path.Combine(work, $"{i}o");
        var theirs = Path.Combine(work, $"{i}s");
        new OnePkgFormatDescriptor().Extract(new MemoryStream(File.ReadAllBytes(pkg)), ours, null, null);
        var (stdout, _, exit) = FsInteropToolbox.Run7z($"x -y -o\"{theirs}\" \"{pkg}\"");
        Assert.That(exit, Is.Zero, stdout);
        // Member names are UTF-8 without the cabinet's UTF flag, so tools may spell them differently;
        // the sections themselves must be identical.
        static List<string> Contents(string d) => [.. Directory.GetFiles(d, "*", SearchOption.AllDirectories).Select(f => Sha(File.ReadAllBytes(f))).Order()];
        var mine = Contents(ours);
        var reference = Contents(theirs);
        if (!mine.SequenceEqual(reference))
          failures.Add($"{Path.GetFileName(pkg)}: {mine.Intersect(reference).Count()} of {reference.Count} sections identical");
        Assert.That(Directory.GetFiles(ours).Count(f => f.EndsWith(".onetoc2", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1), Path.GetFileName(pkg));
      }
      Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    } finally {
      if (Directory.Exists(work)) {
        // The .onetoc2 members carry the read-only attribute.
        foreach (var f in Directory.GetFiles(work, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(work, true);
      }
    }
  }

  [Test]
  public void Given_MicrosoftPackages_When_HeaderInspected_Then_SignedLzxCabinets() {
    foreach (var pkg in Packages()) {
      var b = File.ReadAllBytes(pkg);
      var name = Path.GetFileName(pkg);
      Assert.That((b[24], b[25]), Is.EqualTo(((byte)3, (byte)1)), $"{name}: cabinet format 1.3");
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(30)), Is.EqualTo(4), $"{name}: only the reserve flag");
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(36)), Is.EqualTo(20), $"{name}: 20-byte header reserve");
      var cabinetSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40)), Is.EqualTo(0x00100000u), $"{name}: signature reserve marker");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(44)), Is.EqualTo(cabinetSize), $"{name}: signature follows the cabinet");
      Assert.That(cabinetSize + BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(48)), Is.EqualTo((uint)b.Length), $"{name}: signature ends the file");
      Assert.That((b[cabinetSize], b[cabinetSize + 1]), Is.EqualTo(((byte)0x30, (byte)0x82)), $"{name}: PKCS #7 SignedData");
      int folders = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26));
      for (var f = 0; f < folders; ++f)
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(36 + 4 + 20 + f * 8 + 6)), Is.EqualTo(0x0F03), $"{name}: folder {f} is LZX with a 2^15 window");
    }
  }
}
