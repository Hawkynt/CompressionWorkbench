#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// A file with a hole in it must read back at its full length.
/// </summary>
/// <remarks>
/// <para>The classic Unix block map says where each of a file's blocks lives, and
/// a zero entry means the file has nothing in that block. Every implementation of
/// these formats hands back zeros for such a block and carries on; the file's
/// length is what the inode says, not where its pointers happen to run out.</para>
///
/// <para>Eight readers here treated a zero pointer as the end of the file. That
/// is invisible on volumes this project writes, because its writers allocate
/// every block — and it is exactly what happens on a volume written by anything
/// else. mke2fs and mkfs.minix both leave files sparse, and any such file was
/// read back cut off at its first hole, silently and at the wrong length.</para>
///
/// <para>Rather than depend on those tools being installed, the hole is made the
/// way they would leave one: write a volume, then clear one block pointer in an
/// inode. That is all a hole is on disk.</para>
/// </remarks>
[TestFixture]
public class HolesAreNotEndOfFileTests {

  /// <summary>Formats whose files are addressed by a classic block map.</summary>
  private static readonly string[] BlockMapFormats = [
    "Coherent", "Ext", "Ext1", "MinixFs", "MinixV1", "MinixV2", "SysV", "Xenix",
  ];

  [TestCaseSource(nameof(BlockMapFormats)), Category("Regression")]
  public void AFileWithAClearedBlockPointer_StillReadsBackWhole(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId);
    if (ops is not IArchiveCreatable) {
      Assert.Ignore($"{formatId}: cannot create a volume here.");
      return;
    }

    var format = Enum.Parse<FormatDetector.Format>(formatId);
    var work = Path.Combine(Path.GetTempPath(), "cwb_hole_eof_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);

    try {
      // One file long enough to own several blocks under any of these formats.
      var payload = new byte[64 * 1024];
      for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i * 37 + 5);
      var source = Path.Combine(work, "PUNCHED.BIN");
      File.WriteAllBytes(source, payload);

      var image = Path.Combine(work, "volume.img");
      try {
        ArchiveOperations.Create(image, [new ArchiveInput(source, "PUNCHED.BIN")],
          new CompressionOptions(), format, null);
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: cannot hold the probe file ({ex.GetType().Name}).");
        return;
      }

      var before = Read(ops, image, "PUNCHED.BIN");
      if (before == null || before.Length != payload.Length) {
        Assert.Ignore($"{formatId}: the probe file did not read back before the hole was made.");
        return;
      }

      // Punch the hole: find the run of bytes the file's second block occupies
      // and clear whichever pointer names it. The pointer is found by searching
      // the image for the block number, which is what an inode holds.
      if (!TryPunch(image, formatId)) {
        Assert.Ignore($"{formatId}: could not find a block pointer to clear.");
        return;
      }

      var after = Read(ops, image, "PUNCHED.BIN");
      Assert.That(after, Is.Not.Null, $"{formatId}: the file vanished when a block was freed");
      Assert.That(after!.Length, Is.EqualTo(payload.Length),
        $"{formatId}: a hole cut the file short — it came back {after.Length} of {payload.Length} "
        + "bytes, which is what happens to any volume a reference tool left sparse");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Clears one of the file's block pointers, leaving a hole where its second
  /// block was.
  /// </summary>
  /// <remarks>
  /// The extent map says where the file's bytes are; the pointer that names the
  /// second run is found by looking for that block number near the first, which
  /// is how an inode lays its map out. Approximate on purpose: any pointer this
  /// clears is one the reader has to treat as a hole.
  /// </remarks>
  private static bool TryPunch(string image, string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId);
    if (ops is not IFilesystemExtentMap map) return false;

    List<DefragBlockInfo> used;
    using (var probe = File.OpenRead(image))
      used = map.EnumerateExtents(probe).Where(e => e.Kind == DefragBlockKind.Used).ToList();
    if (used.Count == 0) return false;

    var bytes = File.ReadAllBytes(image);
    var first = used.MinBy(e => e.Offset)!;

    // Try each plausible block size: the second block's number is the first's
    // plus one, and the inode holds them next to each other.
    foreach (var blockSize in new[] { 512, 1024, 2048, 4096 }) {
      var firstBlock = (uint)(first.Offset / blockSize);
      if (firstBlock == 0) continue;

      for (var width = 2; width <= 4; ++width) {
        for (var at = 0; at + width * 2 <= bytes.Length; ++at) {
          if (ReadLe(bytes, at, width) != firstBlock) continue;
          if (ReadLe(bytes, at + width, width) != firstBlock + 1) continue;

          for (var b = 0; b < width; ++b) bytes[at + width + b] = 0;
          File.WriteAllBytes(image, bytes);
          return true;
        }
      }
    }

    return false;
  }

  private static ulong ReadLe(byte[] data, int at, int width) {
    ulong value = 0;
    for (var i = 0; i < width; ++i) value |= (ulong)data[at + i] << (8 * i);
    return value;
  }

  private static byte[]? Read(IArchiveFormatOperations ops, string image, string name) {
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_hole_out_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using (var stream = File.OpenRead(image))
        ops.Extract(stream, outDir, null, null);

      var file = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
        .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
      return file == null ? null : File.ReadAllBytes(file);
    } catch {
      return null;
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }
}
