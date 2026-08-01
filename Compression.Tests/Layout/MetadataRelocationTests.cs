#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// Drives the volume's own structures around, not only its files.
/// </summary>
/// <remarks>
/// <para>A layout request like "metadata at the front" is a request to move the
/// MFT, the bitmaps and the inode tables — the things a defragmenter that treats
/// every metadata extent as immovable can only plan around. Whether a structure
/// can move is a property of the format: something has to record where it is, or
/// there is nothing to repoint. NTFS keeps the $MFT's position in its boot
/// sector and every other system file's in that file's own record; ext keeps
/// each group's bitmaps and inode table in the group descriptor.</para>
///
/// <para>Each case asserts both halves: the structure ends up on the side it was
/// asked to go to, and every file still reads back byte for byte. Moving the
/// MFT and losing the files would satisfy neither.</para>
/// </remarks>
[TestFixture]
public class MetadataRelocationTests {

  private static readonly (string Format, string Region)[] Cases = [
    ("Ntfs", "$MFT"),
    ("Ext", "ext inode table (group 0)"),
  ];

  private static IEnumerable<TestCaseData> Formats()
    => Cases.Select(c => new TestCaseData(c.Format, c.Region).SetName($"Relocates {c.Region} on {c.Format}"));

  [TestCaseSource(nameof(Formats))]
  public void MetadataZoneBack_MovesTheStructure_AndKeepsEveryFile(string formatId, string region) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var format = Enum.Parse<FormatDetector.Format>(formatId);
    var work = Path.Combine(Path.GetTempPath(), "cwb_meta_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);

    try {
      var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      var inputs = new List<ArchiveInput>();
      for (var i = 0; i < 6; ++i) {
        var payload = new byte[300 * 1024 + i * 7 * 1024];
        for (var b = 0; b < payload.Length; ++b) payload[b] = (byte)(b * 13 + i * 29);
        var path = Path.Combine(work, $"F{i}.BIN");
        File.WriteAllBytes(path, payload);
        inputs.Add(new ArchiveInput(path, $"F{i}.BIN"));
        expected[$"F{i}.BIN"] = Digest(payload);
      }

      var image = Path.Combine(work, "volume.img");
      ArchiveOperations.Create(image, inputs, new CompressionOptions(), format, null);

      var before = OffsetOf(ops, image, region);
      Assert.That(before, Is.GreaterThanOrEqualTo(0),
        $"{formatId}: the extent map does not report a region named '{region}'.");

      using (var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite))
        ((IArchiveDefragmentable)ops).Defragment(stream, new DefragOptions {
          Mode = DefragMode.ConsolidateAtStart,
          MetadataZonePlacement = MetadataZone.Back,
        });

      var after = OffsetOf(ops, image, region);
      Assert.That(after, Is.GreaterThan(before),
        $"{formatId}: '{region}' was asked to move towards the end of the volume and did not.");

      foreach (var (name, digest) in ReadBack(image))
        Assert.That(expected.TryGetValue(name, out var want) && want == digest, Is.True,
          $"{formatId}: '{name}' did not survive the relocation.");
      Assert.That(ReadBack(image).Count, Is.EqualTo(expected.Count),
        $"{formatId}: the volume no longer holds every file.");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  /// <summary>Where the extent map says a named region currently sits, or -1.</summary>
  private static long OffsetOf(IArchiveFormatOperations ops, string image, string region) {
    using var stream = File.OpenRead(image);
    foreach (var extent in ((IFilesystemExtentMap)ops).EnumerateExtents(stream))
      if (string.Equals(extent.FileName, region, StringComparison.OrdinalIgnoreCase))
        return extent.Offset;
    return -1;
  }

  /// <summary>Every extracted file's digest, keyed by leaf name.</summary>
  private static Dictionary<string, string> ReadBack(string image) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_metaout_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      ArchiveOperations.Extract(image, outDir, null, null);
      foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        result[Path.GetFileName(file)] = Digest(File.ReadAllBytes(file));
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
    return result;
  }

  private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
