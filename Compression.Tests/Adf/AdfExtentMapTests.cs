using Compression.Registry;

namespace Compression.Tests.Adf;

/// <summary>
/// The block map must give each block to the one file that owns it.
/// </summary>
/// <remarks>
/// An OFS data block carries a pointer to the next block of its file, and the
/// map followed that chain from the file header. A stale pointer walks straight
/// out of one file and into its neighbour, and the map then listed the same
/// blocks under both names — two files appearing to share space on a volume
/// where both read back correctly, which is the signature of a map that is
/// wrong rather than a volume that is.
///
/// <para>It matters beyond the picture it draws: the defragmentation planner is
/// handed this map, and a planner told that two files occupy one block plans
/// with a layout that does not exist.</para>
/// </remarks>
[TestFixture]
public class AdfExtentMapTests {

  [Test, Category("Regression")]
  public void NoBlockIsClaimedByTwoFiles() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("Adf")!;

    // The set that showed it: a file taking half the volume, a spread of
    // middling ones, and a few far smaller than a block.
    const int totalBytes = 50 * 1024;
    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var inputs = new List<ArchiveInputInfo>();

    void Add(List<ArchiveInputInfo> into, string name, int length, int seed) {
      var data = new byte[length];
      for (var j = 0; j < length; ++j) data[j] = (byte)(j * 31 + seed * 7 + (j >> 11));
      expected[name] = data;
      into.Add(ArchiveInputInfo.InMemory(name, data));
    }

    Add(inputs, "BIG00001.BIN", totalBytes / 2, 1);
    var perFile = (totalBytes - totalBytes / 2) / 10;
    for (var i = 0; i < 10; ++i) {
      var length = Math.Max(1, perFile + (i % 7) * 1024 - 3 * 1024);
      if (i % 11 == 0) length = 17 + i;
      Add(inputs, $"F{i:D4}.BIN", length, i + 2);
    }

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, inputs, new FormatCreateOptions());

    // Remove and then add. A freshly created volume has no stale pointers for a
    // chain to wander into; the ones a removal leaves behind are exactly what
    // the walk followed out of its own file.
    var doomed = expected.Keys.Where(k => k.StartsWith('F')).Where((_, n) => n % 3 == 1).ToArray();
    image.Position = 0;
    ((IArchiveModifiable)ops).Remove(image, doomed);
    foreach (var d in doomed) expected.Remove(d);

    var added = new List<ArchiveInputInfo>();
    for (var i = 0; i < 6; ++i) Add(added, $"ADD{i:D2}.BIN", 3 * 1024 + i * 97, 900 + i);
    image.Position = 0;
    ((IArchiveModifiable)ops).Add(image, added);

    image.Position = 0;
    var used = ((IFilesystemExtentMap)ops).EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used)
      .OrderBy(e => e.Offset)
      .ToList();

    Assert.That(used, Is.Not.Empty, "the map should describe the volume's files");

    for (var i = 1; i < used.Count; ++i) {
      var previous = used[i - 1];
      var current = used[i];
      if (current.Offset >= previous.Offset + previous.Length) continue;
      if (previous.FileName != null && previous.FileName == current.FileName) continue;
      Assert.Fail($"{previous.FileName} [{previous.Offset}..{previous.Offset + previous.Length}) "
        + $"and {current.FileName} [{current.Offset}..{current.Offset + current.Length}) "
        + "are both said to hold the same blocks");
    }
  }
}
