#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Moving a file's bytes has to move the volume's account of them too.
/// </summary>
/// <remarks>
/// <para>In bcachefs the position of a run is one word in one extent key, so a
/// defragmentation is the copy plus that word — and a volume rearranged that way
/// reads back perfectly while being internally inconsistent. The same fact is
/// written down three times: the extent says where the bytes are, the alloc tree
/// says what the bucket holding them contains, and the freespace tree says that
/// bucket is not empty. A pass that rewrote only the first left extents pointing
/// into buckets the alloc tree had never heard of, which
/// <c>bcachefs fsck</c> reports once per run and then repairs on the next mount.</para>
///
/// <para>The kernel tools are the authority, and the lifecycle tests use them
/// where they are installed. These check the same invariant off the image itself,
/// so the machines without bcachefs still run the check.</para>
/// </remarks>
[TestFixture]
public class BcacheFsAllocationAfterDefragTests {

  private static readonly DefragMode[] Modes = [
    DefragMode.ConsolidateAtStart,
    DefragMode.ConsolidateAtEnd,
    DefragMode.FillHolesLazy,
  ];

  [TestCaseSource(nameof(Modes)), Category("Regression")]
  public void EveryPlacement_LeavesTheAllocTreeAgreeingWithTheExtents(DefragMode mode) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("BcacheFs")!;

    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var inputs = new List<ArchiveInputInfo>();
    for (var i = 0; i < 12; ++i) {
      var data = new byte[40_000 + i * 3_100];
      for (var j = 0; j < data.Length; ++j) data[j] = (byte)(j * 31 + i * 7 + (j >> 11));
      expected[$"F{i:D2}.BIN"] = data;
      inputs.Add(ArchiveInputInfo.InMemory($"F{i:D2}.BIN", data));
    }

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, inputs, new FormatCreateOptions());

    var mover = new BcacheFsBlockMover();
    image.Position = 0;
    Assert.That(mover.DescribeAllocationDiscrepancies(image), Is.Empty,
      "a freshly written volume should already agree with itself");

    image.Position = 0;
    ((IArchiveDefragmentable)ops).Defragment(image, new DefragOptions { Mode = mode });

    // The bytes first: an accounting fix that lost a file would be no fix at all.
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_bcfs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      image.Position = 0;
      ((IArchiveFormatOperations)ops).Extract(image, outDir, null, null);
      foreach (var (name, want) in expected) {
        var path = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.Ordinal));
        Assert.That(path, Is.Not.Null, $"{mode}: '{name}' went missing");
        Assert.That(File.ReadAllBytes(path!), Is.EqualTo(want).AsCollection,
          $"{mode}: '{name}' did not survive the move");
      }
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }

    image.Position = 0;
    var problems = new BcacheFsBlockMover().DescribeAllocationDiscrepancies(image);
    Assert.That(problems, Is.Empty,
      $"{mode} left the volume disagreeing with itself:{Environment.NewLine}"
      + string.Join(Environment.NewLine, problems.Take(8)));
  }
}
