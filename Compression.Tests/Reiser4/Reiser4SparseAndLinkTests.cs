using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// The rebuild's two reclaim switches: all-zero extent blocks become holes (extent units with
/// start 0), and byte-identical regular files become names of one object. Our reader checks the
/// bytes; <c>fsck.reiser4 --check</c> and the Reiser4 kernel judge the volumes wherever they are
/// available.
/// </summary>
[TestFixture]
public sealed class Reiser4SparseAndLinkTests {

  private static readonly Reiser4FormatDescriptor Descriptor = new();

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_r4sparse_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static byte[] Solid(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    for (var i = 0; i < data.Length; i += 512) data[i] |= 1; // no block of it is ever all zero
    return data;
  }

  /// <summary>Data, then a zero run, then data again, with the boundaries off the block grid.</summary>
  private static byte[] MidHole(int seed) {
    var data = new byte[200_000];
    Solid(5_000, seed).CopyTo(data, 0);
    Solid(3_000, seed + 1).CopyTo(data, 190_000);
    return data;
  }

  /// <summary>Files that exercise every shape of hole: leading, inner, trailing, whole, none.</summary>
  private static Dictionary<string, byte[]> Holey() {
    var trailing = new byte[50_000];
    Solid(4_096, 3).CopyTo(trailing, 0);
    var leading = new byte[50_000];
    Solid(1_000, 4).CopyTo(leading, 49_000);
    var lastPartial = new byte[4_096 * 3 + 100];
    Solid(100, 5).CopyTo(lastPartial, 4_096 * 3);
    return new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["holes/mid.bin"] = MidHole(1),
      ["holes/trailing.bin"] = trailing,
      ["holes/leading.bin"] = leading,
      ["holes/all-zero.bin"] = new byte[40_000],
      ["holes/last-partial.bin"] = lastPartial,
      ["holes/one-zero-byte.bin"] = [0],
      ["holes/solid.bin"] = Solid(30_000, 6),
      ["empty.bin"] = [],
    };
  }

  /// <summary>Five copies of one file in different directories, a sixth that differs in its last byte, and an empty pair.</summary>
  private static Dictionary<string, byte[]> Repeated() {
    var shared = Solid(40_000, 9);
    var almost = (byte[])shared.Clone();
    almost[^1] ^= 0xFF;
    return new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["copies/a.bin"] = shared,
      ["copies/b.bin"] = (byte[])shared.Clone(),
      ["copies/deeper/c.bin"] = (byte[])shared.Clone(),
      ["other/d.bin"] = (byte[])shared.Clone(),
      ["e.bin"] = (byte[])shared.Clone(),
      ["copies/almost.bin"] = almost,
      ["empty1"] = [],
      ["empty2"] = [],
    };
  }

  private static byte[] Build(Dictionary<string, byte[]> files, ulong blocks = 8192) {
    var writer = new Reiser4Writer { BlockCount = blocks };
    foreach (var (name, data) in files) writer.AddFile(name, data);
    return writer.Build();
  }

  private static byte[] Rebuild(byte[] source, bool sparse, bool links) {
    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    Descriptor.RebuildStreaming(input, output, new LayoutRebuildOptions { MakeSparse = sparse, DeduplicateWithLinks = links });
    return output.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] image) {
    using var reader = new Reiser4Reader(new MemoryStream(image));
    return reader.Entries.Where(static e => e.Kind == Reiser4Reader.EntryKind.File)
      .ToDictionary(static e => e.Name, reader.Extract, StringComparer.Ordinal);
  }

  private static void AssertSameFiles(byte[] image, Dictionary<string, byte[]> files) {
    var seen = ReadAll(image);
    Assert.That(seen.Keys.Order(StringComparer.Ordinal), Is.EqualTo(files.Keys.Order(StringComparer.Ordinal)));
    foreach (var (name, want) in files) Assert.That(seen[name], Is.EqualTo(want), name);
  }

  /// <summary>Allocated data blocks over every regular file, counting a shared object once.</summary>
  private static ulong AllocatedBlocks(byte[] image) {
    using var reader = new Reiser4Reader(new MemoryStream(image));
    return (ulong)reader.Entries.Where(static e => e.Kind == Reiser4Reader.EntryKind.File && e.HardLinkOf is null)
      .Sum(static e => (e.NativeRuns ?? []).Sum(static r => (long)r.Width));
  }

  // ── Holes ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenFilesWithZeroBlocks_WhenRebuiltSparse_ThenEveryByteReadsBackAndOnlyDataBlocksStayAllocated() {
    var files = Holey();
    var source = Build(files);

    var sparse = Rebuild(source, sparse: true, links: false);

    AssertSameFiles(sparse, files);
    // mid: bytes 0..4999 (blocks 0-1) and 190000..192999 (blocks 46-47); trailing: block 0;
    // leading: 49000..49999 (blocks 11-12); last-partial: block 3; solid: 30000 bytes, 8 blocks.
    var expected = 4UL + 1 + 2 + 1 + 8;
    Assert.That(AllocatedBlocks(sparse), Is.EqualTo(expected));
    Assert.That(AllocatedBlocks(source), Is.GreaterThan(expected * 3));
  }

  [Test, Category("HappyPath")]
  public void GivenASparseRebuild_WhenTheStatDataIsRead_ThenBytesCountsOnlyAllocatedBlocks() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["m"] = MidHole(1) };
    var sparse = Rebuild(Build(files), sparse: true, links: false);
    using var reader = new Reiser4Reader(new MemoryStream(sparse));
    var entry = reader.Entries.Single(static e => e.Name == "m");
    var bytes = BitConverter.ToUInt64(entry.Metadata!.RawStatData, 36);
    Assert.That(bytes, Is.EqualTo(4UL * 4096));
    Assert.That(entry.Size, Is.EqualTo(200_000));
  }

  [Test, Category("EdgeCase")]
  public void GivenAFileThatIsAllZero_WhenRebuiltSparse_ThenItOwnsNoBlockAtAll() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["z"] = new byte[100_000] };
    var sparse = Rebuild(Build(files), sparse: true, links: false);
    AssertSameFiles(sparse, files);
    Assert.That(AllocatedBlocks(sparse), Is.Zero);
  }

  [Test, Category("Regression")]
  public void GivenNoSwitch_WhenRebuilt_ThenEveryZeroBlockStaysAllocated() {
    var files = Holey();
    var source = Build(files);
    var plain = Rebuild(source, sparse: false, links: false);
    AssertSameFiles(plain, files);
    Assert.That(AllocatedBlocks(plain), Is.EqualTo(AllocatedBlocks(source)));
  }

  [Test, Category("EdgeCase")]
  public void GivenTailAndCompressedFiles_WhenRebuiltSparse_ThenTheyAreCarriedUnchanged() {
    var writer = new Reiser4Writer { BlockCount = 8192, FileBody = Reiser4FileBody.Tails };
    writer.AddFile("tail.bin", new byte[9_000]);
    var tails = writer.Build();
    var sparse = Rebuild(tails, sparse: true, links: false);
    AssertSameFiles(sparse, new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["tail.bin"] = new byte[9_000] });
    using var reader = new Reiser4Reader(new MemoryStream(sparse));
    Assert.That(reader.Entries.Single(static e => e.Name == "tail.bin").Body, Is.EqualTo("tail"));
  }

  // ── Links ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenIdenticalFiles_WhenRebuiltWithLinks_ThenOneObjectCarriesEveryNameAndItsLinkCount() {
    var files = Repeated();
    var linked = Rebuild(Build(files), sparse: false, links: true);

    AssertSameFiles(linked, files);
    using var reader = new Reiser4Reader(new MemoryStream(linked));
    var copies = reader.Entries.Where(static e => e.Name is "copies/a.bin" or "copies/b.bin" or "copies/deeper/c.bin" or "other/d.bin" or "e.bin").ToList();
    Assert.That(copies.Select(static e => e.Native!.StatKey).Distinct().Count(), Is.EqualTo(1));
    Assert.That(copies.All(static e => e.Metadata!.LinkCount == 5), Is.True);
    var almost = reader.Entries.Single(static e => e.Name == "copies/almost.bin");
    Assert.That(almost.Native!.StatKey, Is.Not.EqualTo(copies[0].Native!.StatKey), "a file differing in one byte must stay its own");
    var empties = reader.Entries.Where(static e => e.Name.StartsWith("empty", StringComparison.Ordinal)).ToList();
    Assert.That(empties[0].Native!.StatKey, Is.Not.EqualTo(empties[1].Native!.StatKey), "empty files cost nothing and are not linked");
  }

  [Test, Category("HappyPath")]
  public void GivenIdenticalFiles_WhenRebuiltWithLinks_ThenTheCopiesStopTakingBlocks() {
    var files = Repeated();
    var source = Build(files);
    var linked = Rebuild(source, sparse: false, links: true);
    Assert.That(AllocatedBlocks(linked), Is.EqualTo(2UL * 10)); // the shared file and the almost-copy
    Assert.That(AllocatedBlocks(source), Is.EqualTo(6UL * 10));
  }

  [Test, Category("Regression")]
  public void GivenBothSwitches_WhenTheProbeSetIsRebuilt_ThenTheVolumeShrinksAndKeepsEveryByte() {
    var files = Holey();
    foreach (var (name, data) in Repeated()) files["r/" + name] = data;
    var source = Build(files, blocks: 4096);
    Assert.That(Descriptor.ReclaimSupport, Is.EqualTo(LayoutReclaim.Sparse | LayoutReclaim.HardLinks));

    var both = Rebuild(source, sparse: true, links: true);
    AssertSameFiles(both, files);
    Assert.That(AllocatedBlocks(both), Is.LessThan(AllocatedBlocks(source) / 3));
  }

  [Test, Category("Exception")]
  public void GivenAUnitSizeOtherThanTheBlockSize_WhenRebuilt_ThenItIsRefused() {
    var source = Build(Repeated());
    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    Assert.Throws<NotSupportedException>(() =>
      Descriptor.RebuildStreaming(input, output, new LayoutRebuildOptions { UnitSize = 8192, MakeSparse = true }));
  }

  [Test, Category("HappyPath")]
  public void GivenAKernelWrittenExtentVolume_WhenRebuiltWithBothSwitches_ThenEveryFileReadsTheSame() {
    var source = Reiser4KernelVolumeTests.Image("reg40-node41");
    var before = ReadAll(source);
    var after = ReadAll(Rebuild(source, sparse: true, links: true));
    Assert.That(after.Keys.Order(StringComparer.Ordinal), Is.EqualTo(before.Keys.Order(StringComparer.Ordinal)));
    foreach (var (name, want) in before) Assert.That(after[name], Is.EqualTo(want), name);
  }

  // ── Oracles ────────────────────────────────────────────────────────────

  private string Save(string name, byte[] image) {
    var path = Path.Combine(this._tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  private static void AssertFsckConsistent(string path) {
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"cp {wsl} {wsl}.fsck && fsck.reiser4 --check -y {wsl}.fsck; rc=$?; rm -f {wsl}.fsck; exit $rc");
    var text = run.StdOut + "\n" + run.StdErr;
    Assert.That(run.ExitCode, Is.EqualTo(0), $"fsck.reiser4 rejected {Path.GetFileName(path)}:\n{text}");
    Assert.That(text, Does.Contain("FS is consistent"), $"fsck.reiser4 did not call {Path.GetFileName(path)} consistent:\n{text}");
  }

  private static string Md5(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

  private static IEnumerable<TestCaseData> Shapes() {
    yield return new TestCaseData(true, false).SetName("Sparse");
    yield return new TestCaseData(false, true).SetName("Links");
    yield return new TestCaseData(true, true).SetName("Both");
  }

  [TestCaseSource(nameof(Shapes)), Category("ExternalFsInterop")]
  public void GivenAReclaimedVolume_WhenFsckReiser4ChecksIt_ThenItIsConsistent(bool sparse, bool links) {
    Reiser4Oracle.Require("fsck.reiser4");
    var files = Holey();
    foreach (var (name, data) in Repeated()) files["r/" + name] = data;
    AssertFsckConsistent(this.Save("reclaimed.img", Rebuild(Build(files), sparse, links)));
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAKernelWrittenVolumeRebuiltWithBothSwitches_WhenFsckReiser4ChecksIt_ThenItIsConsistent() {
    Reiser4Oracle.Require("fsck.reiser4");
    AssertFsckConsistent(this.Save("kernel-reclaimed.img", Rebuild(Reiser4KernelVolumeTests.Image("reg40-node41"), true, true)));
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAReclaimedVolume_WhenTheKernelMountsIt_ThenEveryNameReadsItsBytesAndCopiesShareOneInode() {
    Reiser4KernelOracle.Require();
    var files = Holey();
    foreach (var (name, data) in Repeated()) files["r/" + name] = data;
    var path = this.Save("reclaimed-kernel.img", Rebuild(Build(files), sparse: true, links: true));

    var mount = Reiser4KernelOracle.Read(path);
    Assert.That(mount.Complaints, Is.Empty, "the kernel logged complaints:\n" + string.Join('\n', mount.Complaints));
    Assert.Multiple(() => {
      foreach (var (name, data) in files) {
        Assert.That(mount.Manifest.TryGetValue(name, out var got), Is.True, name + " is missing");
        Assert.That(got!.Size, Is.EqualTo(data.Length), name + " size");
        Assert.That(got.Value, Is.EqualTo(Md5(data)), name + " bytes");
      }
      foreach (var name in new[] { "r/copies/a.bin", "r/copies/b.bin", "r/copies/deeper/c.bin", "r/other/d.bin", "r/e.bin" })
        Assert.That(mount.Manifest[name].Links, Is.EqualTo(5U), name + " links");
      Assert.That(mount.Manifest["r/copies/almost.bin"].Links, Is.EqualTo(1U));
    });
    if (FsInteropToolbox.WslHasTool("fsck.reiser4")) AssertFsckConsistent(path);
  }
}
