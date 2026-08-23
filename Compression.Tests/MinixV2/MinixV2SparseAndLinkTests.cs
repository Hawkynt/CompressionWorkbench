#pragma warning disable CS1591
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.MinixV2;

namespace Compression.Tests.MinixV2;

/// <summary>
/// A Minix volume can leave its zeros unallocated and store one copy of files
/// that are identical — and still be a Minix volume.
/// </summary>
/// <remarks>
/// <para>Both were already asked for and answered only by ext, which said so and
/// meant it while every other filesystem answered <see cref="LayoutReclaim.None" />
/// whether or not it could. Minix can: a zone pointer of zero names no zone and a
/// reader hands back zeros for it, and the inode has counted the names pointing at
/// it since 1987.</para>
///
/// <para>What makes this worth checking outside our own reader is that both
/// changes are invisible to it. A hole and a zone full of zeros read back exactly
/// the same; so do one inode with two names and two inodes with one each. Only
/// <c>fsck.minix</c> and the kernel's driver can say whether what was written is
/// a volume their own system would have produced, which is why they are asked
/// here rather than trusted to agree.</para>
/// </remarks>
[TestFixture]
public class MinixV2SparseAndLinkTests {

  /// <summary>A file that is mostly hole, one that is all hole, and one that is solid.</summary>
  private static Dictionary<string, byte[]> Holey() {
    byte[] Make(int length, params (int At, int Run)[] solid) {
      var data = new byte[length];
      foreach (var (at, run) in solid)
        for (var i = at; i < Math.Min(length, at + run); ++i)
          data[i] = (byte)(i * 31 + 7);
      return data;
    }

    return new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      // Past the seven direct zones and well into the single-indirect block, so
      // the holes fall on both sides of the change in how a zone is addressed.
      ["MIDHOLE.BIN"] = Make(120_000, (0, 2_000), (118_000, 2_000)),
      ["HEADHOLE.BIN"] = Make(60_000, (56_000, 4_000)),
      ["TAILHOLE.BIN"] = Make(60_000, (0, 4_000)),
      // Every zone a hole, including the whole of the single-indirect block's
      // range: nothing at all should be allocated for it.
      ["ALLHOLE.BIN"] = Make(80_000),
      ["SOLID.BIN"] = Make(9_000, (0, 9_000)),
    };
  }

  /// <summary>Several names for the same bytes, and one file that differs.</summary>
  private static Dictionary<string, byte[]> Repeated() {
    var body = new byte[40_000];
    for (var i = 0; i < body.Length; ++i) body[i] = (byte)(i * 17 + 3);

    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < 5; ++i) files[$"COPY{i}.BIN"] = (byte[])body.Clone();

    var other = new byte[40_000];
    for (var i = 0; i < other.Length; ++i) other[i] = (byte)(i * 29 + 11);
    files["OTHER.BIN"] = other;
    return files;
  }

  private static byte[] Build(Dictionary<string, byte[]> files, bool sparse = false, bool dedup = false) {
    using var ms = new MemoryStream();
    using (var writer = new MinixV2Writer(ms, leaveOpen: true) {
      MakeSparse = sparse,
      DeduplicateWithLinks = dedup,
    }) {
      foreach (var (name, data) in files) writer.AddFile(name, data);
      writer.Finish();
    }
    return ms.ToArray();
  }

  private static void AssertReadsBack(byte[] image, Dictionary<string, byte[]> files, string what) {
    using var ms = new MemoryStream(image);
    using var reader = new MinixV2Reader(ms);
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

  /// <summary>
  /// Hands the volume to <c>fsck.minix</c> and to the kernel's own driver, and
  /// says which of them actually ran.
  /// </summary>
  private static void AssertOutsideAgrees(byte[] image, Dictionary<string, byte[]> files, string what) {
    var path = Path.Combine(Path.GetTempPath(), "cwb_mx_" + Guid.NewGuid().ToString("N")[..8] + ".img");
    File.WriteAllBytes(path, image);
    try {
      var checker = ThirdPartyFsCheck.Fsck("MinixV2", path);
      if (checker.Ran)
        Assert.That(checker.Ok, Is.True, $"fsck.minix rejected the volume {what}: {checker.Detail}");

      var mounted = ThirdPartyFsCheck.ReadBack("MinixV2", path, [.. files.Values]);
      if (mounted.Ran)
        Assert.That(mounted.Ok, Is.True,
          $"{mounted.Tool} read the volume {what} and did not get the files back: {mounted.Detail}");

      if (!checker.Ran && !mounted.Ran)
        Assert.Ignore($"no third-party Minix reader here: {checker.Detail}");
    } finally {
      try { File.Delete(path); } catch { /* the scratch image is already gone */ }
    }
  }

  [Test, Category("Regression")]
  public void AskingForHoles_ShrinksTheVolumeAndKeepsEveryByte() {
    var files = Holey();

    var solid = Build(files);
    var holey = Build(files, sparse: true);

    AssertReadsBack(solid, files, "without holes");
    AssertReadsBack(holey, files, "with holes");

    // Roughly 316 of the 329 KB written is zeros, so what comes off should be
    // most of the volume rather than a rounding difference.
    Assert.That(holey.Length, Is.LessThan(solid.Length / 2),
      $"asking for holes saved {solid.Length - holey.Length} of {solid.Length} bytes, which is "
      + "not the bulk of a file set that is mostly zeros");
  }

  [Test, Category("Interop")]
  public void AVolumeWithHoles_IsStillAMinixVolume() {
    var files = Holey();
    AssertOutsideAgrees(Build(files, sparse: true), files, "written with holes");
  }

  [Test, Category("Regression")]
  public void AFileThatIsAllHole_TakesNoIndirectBlockEither() {
    // A file whose every zone is a hole should cost nothing at all. Allocating an
    // indirect block full of zero pointers for it would read back identically and
    // still be a volume no Minix system would have written, because the kernel
    // never asks for a block it is not filling.
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["ALLHOLE.BIN"] = new byte[80_000],
    };

    var image = Build(files, sparse: true);
    AssertReadsBack(image, files, "when the whole file is hole");

    using var ms = new MemoryStream(image);
    using var reader = new MinixV2Reader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory);
    Assert.That(reader.EnumerateDataExtents(entry).Any(), Is.False,
      "a file that is nothing but hole should own no zones at all");
  }

  [Test, Category("Regression")]
  public void EveryZoneAfterAHole_IsStillReportedAsOwned() {
    // Where a file's bytes are is what a defragmentation moves and what a
    // shrink accounts for. The enumeration used to stop at the first zone
    // pointer of zero, which is a hole and not the end of anything, so
    // everything past it was owned by a file that did not admit to owning it —
    // free to be handed out twice.
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      // Solid in the first two direct zones and again at the very end, with the
      // rest of the direct zones and most of the indirect block hole.
      ["MIDHOLE.BIN"] = Holey()["MIDHOLE.BIN"],
    };

    var image = Build(files, sparse: true);
    using var ms = new MemoryStream(image);
    using var reader = new MinixV2Reader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory);

    var owned = reader.EnumerateDataExtents(entry).Sum(e => e.Length) / 1024;
    // Two zones at the front, three at the back: 2 000 bytes and 2 000 bytes,
    // each straddling the zone it starts in.
    Assert.That(owned, Is.EqualTo(5),
      "the zones behind the hole were left out of what the file owns");
  }

  [Test, Category("Regression")]
  public void WithoutAsking_EveryZeroIsStillAllocated() {
    // The switch is a switch. A hole is a promise about what a reader will be
    // given, and not everything that reads Minix is this project.
    var files = Holey();
    var plain = Build(files);
    var holey = Build(files, sparse: true);

    Assert.That(plain.Length, Is.GreaterThan(holey.Length));
    AssertReadsBack(plain, files, "by default");
  }

  [Test, Category("Regression")]
  public void AskingForLinks_StoresOneCopyAndKeepsEveryName() {
    var files = Repeated();

    var copies = Build(files);
    var linked = Build(files, dedup: true);

    AssertReadsBack(copies, files, "without links");
    AssertReadsBack(linked, files, "with links");

    // Five of the six files are the same forty kilobytes, so four copies of it
    // should stop being paid for.
    Assert.That(linked.Length, Is.LessThan(copies.Length / 2),
      $"linking saved {copies.Length - linked.Length} of {copies.Length} bytes, which is not "
      + "the four copies that stopped being stored");
  }

  [Test, Category("Interop")]
  public void AVolumeWithHardLinks_IsStillAMinixVolume() {
    // fsck.minix counts the directory entries naming each inode and compares them
    // against the inode's own link count, so a wrong count is exactly what it
    // exists to find.
    var files = Repeated();
    AssertOutsideAgrees(Build(files, dedup: true), files, "written with hard links");
  }

  [Test, Category("Regression")]
  public void TheRebuildHonoursBothSwitches() {
    var files = Holey();
    foreach (var (name, data) in Repeated()) files[name] = data;

    var descriptor = new MinixV2FormatDescriptor();
    Assert.That(descriptor.ReclaimSupport.HasFlag(LayoutReclaim.Sparse), Is.True,
      "Minix can express a hole, and should say so");
    Assert.That(descriptor.ReclaimSupport.HasFlag(LayoutReclaim.HardLinks), Is.True,
      "Minix counts the names an inode answers to, and should say so");

    var source = Build(files);
    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    descriptor.RebuildStreaming(input, output,
      new LayoutRebuildOptions { MakeSparse = true, DeduplicateWithLinks = true });

    var rebuilt = output.ToArray();
    AssertReadsBack(rebuilt, files, "after the rebuild");
    Assert.That(rebuilt.Length, Is.LessThan(source.Length),
      "a rebuild asked for holes and links should give back a smaller volume");
  }
}
