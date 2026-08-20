using Compression.Registry;

namespace Compression.Tests.Gfs1;

/// <summary>
/// Wiping free space must leave the volume's own structures alone.
/// </summary>
/// <remarks>
/// <para>The block map claimed one block for the inode table and one for the
/// root directory. The table is sized when the volume is written — one block per
/// sixteen inodes — so past sixteen files the rest of it looked like free space
/// and a wipe zeroed it. Every file went missing at once while the file data was
/// never touched: four kilobytes of metadata, and the volume could no longer be
/// read.</para>
///
/// <para>Counting the inodes present does not locate the table either, because
/// it does not shrink when files are removed. The map now claims everything
/// between the superblock and the first block any file occupies, which is a fact
/// about the volume rather than about its history.</para>
/// </remarks>
[TestFixture]
public class Gfs1WipeCoverageTests {

  [Test, Category("Regression")]
  public void WipingKeepsEveryFileWhenTheInodeTableSpansSeveralBlocks() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("Gfs1")!;

    // More than the sixteen inodes one table block holds, so the table spans
    // several and the old map left most of it unclaimed.
    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var inputs = new List<ArchiveInputInfo>();
    for (var i = 0; i < 30; ++i) {
      var data = new byte[5000 + i * 13];
      for (var j = 0; j < data.Length; ++j) data[j] = (byte)(j * 31 + i * 7);
      expected[$"F{i:D4}.BIN"] = data;
      inputs.Add(ArchiveInputInfo.InMemory($"F{i:D4}.BIN", data));
    }

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, inputs, new FormatCreateOptions());

    // Remove some. The table keeps its size, so a map that infers it from the
    // inodes still present now points a block short of the root directory.
    var doomed = expected.Keys.Take(8).ToArray();
    image.Position = 0;
    ((IArchiveModifiable)ops).Remove(image, doomed);
    foreach (var d in doomed) expected.Remove(d);

    image.Position = 0;
    ((IWipeEmpty)ops).WipeUnusedSpace(image);

    var outDir = Path.Combine(Path.GetTempPath(), "gfs1wipe_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      image.Position = 0;
      ((IArchiveFormatOperations)ops).Extract(image, outDir, null, null);
      foreach (var (name, want) in expected) {
        var path = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        Assert.That(path, Is.Not.Null, $"{name} is gone after wiping free space");
        Assert.That(File.ReadAllBytes(path!), Is.EqualTo(want), $"{name} did not survive the wipe");
      }
    } finally {
      try { Directory.Delete(outDir, true); } catch { }
    }
  }
}
