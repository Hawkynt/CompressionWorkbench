#pragma warning disable CS1591
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.MinixFs;

namespace Compression.Tests.MinixFs;

/// <summary>
/// A Minix v3 volume has to be able to hold a file of any size the format
/// addresses, not just one that fits in seven direct zones.
/// </summary>
/// <remarks>
/// <para>The writer used to refuse anything past 7 168 bytes outright, while the
/// reader beside it read all four addressing levels quite happily. So a volume
/// from a real minix system could be read here and never written — the plainest
/// kind of difference there is between our filesystem and theirs.</para>
///
/// <para>It cost more than the files themselves. Three formats with that ceiling
/// were passed over entirely by the checks that ask an outside tool for an
/// opinion, because they could not build the probe volume, and that is how a
/// broken inode bitmap survived in MinixFs unnoticed.</para>
///
/// <para>The sizes below are chosen for where they land: inside the direct
/// zones, inside the single-indirect block, and past it into the double-indirect
/// tree — which is also the point at which the block mover has to follow a
/// pointer to find a pointer.</para>
/// </remarks>
[TestFixture]
public class MinixFsLargeFileTests {

  private static byte[] Solid(int length, int seed) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * seed + 7);
    return data;
  }

  /// <summary>One file per addressing level, and one that is mostly hole.</summary>
  private static Dictionary<string, byte[]> Files() {
    var holey = new byte[300_000];
    for (var i = 0; i < 2_000; ++i) holey[i] = (byte)(i * 31 + 7);
    for (var i = 298_000; i < holey.Length; ++i) holey[i] = (byte)(i * 31 + 7);

    return new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["SMALL.BIN"] = Solid(3_000, 17),        // the seven direct zones
      ["MEDIUM.BIN"] = Solid(60_000, 19),      // into the single-indirect block
      ["LARGE.BIN"] = Solid(400_000, 23),      // past it, into the double-indirect tree
      ["HOLEY.BIN"] = holey,                   // solid at both ends, hole between
    };
  }

  private static byte[] Build(Dictionary<string, byte[]> files, bool sparse = false) {
    using var ms = new MemoryStream();
    using (var writer = new MinixFsWriter(ms, leaveOpen: true) { MakeSparse = sparse }) {
      foreach (var (name, data) in files) writer.AddFile(name, data);
      writer.Finish();
    }
    return ms.ToArray();
  }

  private static void AssertReadsBack(byte[] image, Dictionary<string, byte[]> files, string what) {
    using var ms = new MemoryStream(image);
    using var reader = new MinixFsReader(ms, leaveOpen: true);
    var seen = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      seen[entry.Name] = reader.Extract(entry);
    }

    foreach (var (name, want) in files) {
      Assert.That(seen.ContainsKey(name), Is.True, $"'{name}' is missing {what}");
      Assert.That(seen[name], Is.EqualTo(want).AsCollection,
        $"'{name}' came back with different bytes {what}");
    }
  }

  [Test, Category("Regression")]
  public void AFilePastTheDirectZones_IsStoredAndReadBack() {
    var files = Files();
    AssertReadsBack(Build(files), files, "at every addressing level");
  }

  [Test, Category("Regression")]
  public void AFilePastTheDirectZones_CanAlsoBeSparse() {
    var files = Files();
    var solid = Build(files);
    var holey = Build(files, sparse: true);

    AssertReadsBack(holey, files, "with holes as well as indirect zones");
    Assert.That(holey.Length, Is.LessThan(solid.Length),
      "the file that is mostly hole should cost less once the holes are left out");
  }

  [Test, Category("Interop")]
  public void AVolumeOfLargeFiles_IsOneItsOwnToolsAccept() {
    var files = Files();
    var path = Path.Combine(Path.GetTempPath(), "cwb_mfl_" + Guid.NewGuid().ToString("N")[..8] + ".img");
    File.WriteAllBytes(path, Build(files));
    try {
      var checker = ThirdPartyFsCheck.Fsck("MinixFs", path);
      if (checker.Ran)
        Assert.That(checker.Ok, Is.True,
          $"fsck.minix rejected a volume holding files past the direct zones: {checker.Detail}");

      var read = ThirdPartyFsCheck.ReadBack("MinixFs", path, [.. files.Values]);
      if (read.Ran)
        Assert.That(read.Ok, Is.True,
          $"{read.Tool} read the volume and did not get the files back: {read.Detail}");

      if (!checker.Ran && !read.Ran)
        Assert.Ignore($"no third-party Minix reader here: {read.Detail}");
    } finally {
      try { File.Delete(path); } catch { /* the scratch image is already gone */ }
    }
  }

  [Test, Category("Regression")]
  public void MovingAZoneNamedByAnIndirectBlock_RepointsIt() {
    // The mover only ever looked at the ten pointers in the inode. That was
    // complete while no file could be large enough to need an indirect block,
    // and wrong the moment one could: a zone named from inside a pointer block
    // moved, and the pointer went on naming where it had been.
    var files = Files();
    var image = Build(files);
    var path = Path.Combine(Path.GetTempPath(), "cwb_mfm_" + Guid.NewGuid().ToString("N")[..8] + ".img");
    File.WriteAllBytes(path, image);

    try {
      var descriptor = new MinixFsFormatDescriptor();
      using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
        descriptor.Defragment(stream, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

      AssertReadsBack(File.ReadAllBytes(path), files, "after a defragmentation");

      var checker = ThirdPartyFsCheck.Fsck("MinixFs", path);
      if (checker.Ran)
        Assert.That(checker.Ok, Is.True,
          $"fsck.minix rejected the volume after a defragmentation: {checker.Detail}");
    } finally {
      try { File.Delete(path); } catch { /* the scratch image is already gone */ }
    }
  }
}
