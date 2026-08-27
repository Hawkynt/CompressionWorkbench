#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

[TestFixture]
public class BcacheFsFullDefragmenterRoutingTests {

  [Test, Category("Regression")]
  public void DescriptorDefragment_UsesPhysicalMapAndMetadataRelocator() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("BcacheFs")!;

    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var inputs = new List<ArchiveInputInfo>();
    for (var i = 0; i < 10; ++i) {
      var data = new byte[48_000 + i * 7_919];
      for (var j = 0; j < data.Length; ++j)
        data[j] = (byte)(i * 43 + j * 17 + (j >> 9));
      var name = $"tree/part-{i:D2}.bin";
      expected[name] = data;
      inputs.Add(ArchiveInputInfo.InMemory(name, data));
    }

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, inputs, new FormatCreateOptions());

    var phases = new List<string>();
    var statuses = new List<string>();
    image.Position = 0;
    ((IArchiveDefragmentable)ops).Defragment(image, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      MetadataZonePlacement = MetadataZone.Back,
      InterleaveStride = 3,
      OnProgress = p => {
        phases.Add(p.Phase);
        if (p.Status != null) statuses.Add(p.Status);
      },
    });

    Assert.That(phases, Does.Contain("metadata"),
      "descriptor routing regressed to the generic extent-only defragmenter");
    Assert.That(statuses.Any(s => s.Contains("metadata placement", StringComparison.OrdinalIgnoreCase)), Is.True,
      "the bcachefs metadata relocation phase was not entered");
    Assert.That(phases.LastOrDefault(), Is.EqualTo("complete"));

    image.Position = 0;
    var problems = new BcacheFsBlockMover().DescribeAllocationDiscrepancies(image);
    Assert.That(problems, Is.Empty,
      "full defrag left physical extent/allocation metadata inconsistent: "
      + string.Join("; ", problems.Take(8)));

    var outDir = Path.Combine(Path.GetTempPath(), "cwb_bcfs_full_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      image.Position = 0;
      ((IArchiveFormatOperations)ops).Extract(image, outDir, null, null);
      foreach (var (name, want) in expected) {
        var path = Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(path), Is.True, $"'{name}' disappeared during metadata-aware defrag");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(want).AsCollection,
          $"'{name}' changed during metadata-aware defrag");
      }
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }
}
