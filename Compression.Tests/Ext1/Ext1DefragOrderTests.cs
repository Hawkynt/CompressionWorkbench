using Compression.Registry;

namespace Compression.Tests.Ext1;

/// <summary>
/// Defragmenting must leave every file's blocks in the order they were in.
/// </summary>
/// <remarks>
/// <para>The relink patched a file's pointers one contiguous run at a time, and
/// each patch rewrote every pointer falling inside that run's <em>old</em>
/// range. When a later run's old range was where an earlier run had just been
/// put, the earlier run's pointers were rewritten a second time — and the file's
/// blocks came back in the wrong order.</para>
///
/// <para>What that looks like is the reason it went unnoticed: the file is
/// present, its length is right, and it holds its own bytes. Only their order is
/// wrong. One file of 3,169 bytes read back with its second block holding what
/// belonged in its third, differing from the first byte of block one onwards and
/// nowhere else.</para>
///
/// <para>Only the modes that pack towards the start were affected. Packing
/// towards the end puts runs where nothing still to be read is sitting, so the
/// aliasing never arises — which made it look like a bug in one defragmentation
/// mode rather than in the relink underneath all of them.</para>
/// </remarks>
[TestFixture]
public class Ext1DefragOrderTests {

  [Test, Category("Regression")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void DefragmentKeepsEachFilesBlocksInOrder(DefragMode mode) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("Ext1")!;

    const int totalBytes = 50 * 1024;
    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var inputs = new List<ArchiveInputInfo>();

    // The byte at index j carries j in its low bits and j >> 11 above them, so a
    // block that ends up in the wrong place differs from the one that belongs
    // there and says which it is.
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

    // Files have to be moved for their pointers to be relinked at all, and a
    // freshly written volume has nothing out of place to move.
    var doomed = expected.Keys.Where(k => k.StartsWith('F')).Where((_, n) => n % 3 == 1).ToArray();
    image.Position = 0;
    ((IArchiveModifiable)ops).Remove(image, doomed);
    foreach (var d in doomed) expected.Remove(d);

    var added = new List<ArchiveInputInfo>();
    for (var i = 0; i < 6; ++i) Add(added, $"ADD{i:D2}.BIN", 3 * 1024 + i * 97, 900 + i);
    image.Position = 0;
    ((IArchiveModifiable)ops).Add(image, added);

    image.Position = 0;
    ((IArchiveDefragmentable)ops).Defragment(image, new DefragOptions { Mode = mode });

    var outDir = Path.Combine(Path.GetTempPath(), "ext1order_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      image.Position = 0;
      ((IArchiveFormatOperations)ops).Extract(image, outDir, null, null);

      foreach (var (name, want) in expected) {
        var path = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        Assert.That(path, Is.Not.Null, $"{mode}: {name} is missing after defragmenting");

        var got = File.ReadAllBytes(path!);
        Assert.That(got.Length, Is.EqualTo(want.Length), $"{mode}: {name} changed length");
        if (got.AsSpan().SequenceEqual(want)) continue;

        var at = 0;
        while (at < want.Length && got[at] == want[at]) ++at;
        Assert.Fail($"{mode}: {name} differs from offset 0x{at:X} — its blocks are out of order");
      }
    } finally {
      try { Directory.Delete(outDir, true); } catch { }
    }
  }
}
