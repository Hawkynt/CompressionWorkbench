#pragma warning disable CS1591
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using FileSystem.ExFat;

namespace Compression.Tests.ExFat;

/// <summary>
/// Tests for the planner-driven in-place exFAT defragmenter. Verifies that the
/// <see cref="DefragPlanner"/> + <see cref="ExFatBlockMover"/> pipeline correctly
/// moves cluster extents, patches FAT chains, allocation bitmap bits, and
/// directory entry sets (with set-checksum) so multi-file corpora round-trip
/// byte-exact across all defragmentation modes.
/// </summary>
[TestFixture]
public class ExFatPlannerDefragTests {

  private static MemoryStream BuildFragmentedImage() {
    var ms = new MemoryStream();
    ms.Write(new ExFatWriter().Build());
    ms.SetLength(ms.Position);

    // Add 6 files, then remove 2 to create holes in the cluster heap.
    ExFatModifier.AddFile(ms, "A.TXT", Encoding.ASCII.GetBytes("Alpha content here!"));
    ExFatModifier.AddFile(ms, "B.TXT", Encoding.ASCII.GetBytes("Beta content file."));
    ExFatModifier.AddFile(ms, "C.TXT", Encoding.ASCII.GetBytes("Charlie data block!"));
    ExFatModifier.AddFile(ms, "D.TXT", new byte[6000]);
    ExFatModifier.AddFile(ms, "E.TXT", Encoding.ASCII.GetBytes("Echo short."));
    ExFatModifier.AddFile(ms, "F.TXT", new byte[12000]);

    ExFatModifier.RemoveFile(ms, "B.TXT");
    ExFatModifier.RemoveFile(ms, "D.TXT");
    return ms;
  }

  private static Dictionary<string, byte[]> ExtractAll(MemoryStream ms) {
    ms.Position = 0;
    var reader = new ExFatReader(ms);
    return reader.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Name, e => reader.Extract(e));
  }

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void PlannerDefrag_MultipleFiles_PreservesAllBytesExactly(DefragMode mode) {
    using var ms = BuildFragmentedImage();
    var before = ExtractAll(ms);

    new ExFatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = mode,
      Profile = LayoutProfile.Performance,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count),
      $"File count unchanged ({mode}): before={before.Count}, after={after.Count}");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} present after defrag ({mode})");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} byte-exact after defrag ({mode})");
    }
  }

  /// <summary>
  /// Regression for task #149. Reproduces the integration-test failure mode:
  /// an 8 MiB exFAT image inside a 16 MiB stream (mirrors a 16 MiB partition
  /// formatted with the default 8 MiB ExFatWriter image). Before the fix, the
  /// planner used <c>archive.Length</c> as its image bound and ConsolidateAtEnd
  /// targeted offsets past the cluster heap — UpdateAllocationAfterMove then
  /// wrote a FAT entry past <c>fatLength</c>, corrupting file data.
  /// <para>
  /// Fix: <c>ExFatFormatDescriptor.DefragmentWithPlanner</c> now caps the
  /// planner's image bound at <c>ExFatBlockMover.VolumeSize</c>
  /// (= clusterHeapOffset + clusterCount × clusterSize).
  /// </para>
  /// </summary>
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void PlannerDefrag_ImageInLargerStream_PreservesBytes(DefragMode mode) {
    // 8 MiB exFAT image (default) inside a 16 MiB stream.
    var image = new ExFatWriter().Build();
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(16L * 1024 * 1024);

    var samples = new List<(string Name, byte[] Data)> {
      ("XFA.TXT", Encoding.ASCII.GetBytes("alpha sample payload")),
      ("XFB.TXT", Encoding.ASCII.GetBytes("bravo sample payload, slightly larger")),
      ("XFC.TXT", new byte[2048]),
      ("XFD.TXT", BuildDeterministicData(0x4D, 4096)),
      ("XFE.TXT", Encoding.ASCII.GetBytes("echo")),
    };

    var tempDir = Path.Combine(Path.GetTempPath(), "exfat-bug149-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try {
      var inputs = new List<ArchiveInputInfo>();
      foreach (var (n, d) in samples) {
        var p = Path.Combine(tempDir, n);
        File.WriteAllBytes(p, d);
        inputs.Add(new ArchiveInputInfo(p, n, false));
      }

      var desc = new ExFatFormatDescriptor();
      ((IArchiveModifiable)desc).Add(ms, inputs);

      ms.Position = 0;
      ((IArchiveDefragmentable)desc).Defragment(ms, new DefragOptions {
        Mode = mode,
        Profile = LayoutProfile.Performance,
      });

      ms.Position = 0;
      var r = new ExFatReader(ms);
      var byName = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => r.Extract(e));
      foreach (var (n, d) in samples) {
        Assert.That(byName, Contains.Key(n), $"File {n} present after defrag ({mode})");
        Assert.That(byName[n], Is.EqualTo(d), $"File {n} byte-exact after defrag ({mode})");
      }
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  private static byte[] BuildDeterministicData(int seed, int length) {
    var buf = new byte[length];
    var v = (byte)seed;
    for (var i = 0; i < length; ++i) { buf[i] = v; v = (byte)(v * 17 + 1); }
    return buf;
  }
}
