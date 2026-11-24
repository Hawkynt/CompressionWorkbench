using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// Volumes of every block size <c>mkfs.reiser4 -b</c> makes besides the 4 KiB default: 512, 1024,
/// 2048 and 8192 bytes. The fixed blocks sit at <c>65536 / blockSize</c> onwards in the same order
/// as at 4 KiB, and nodes, extents and bitmaps are counted in blocks of that size. Our reader and
/// writer check each other without tools; <c>mkfs.reiser4</c>, <c>fsck.reiser4</c> and
/// <c>debugfs.reiser4</c> judge both directions wherever reiser4progs is installed. (A Reiser4 kernel
/// mounts only volumes whose block size equals its page size, so on x86 it refuses all of these.)
/// </summary>
[TestFixture]
public sealed class Reiser4BlockSizeTests {

  private static readonly Reiser4FormatDescriptor Descriptor = new();

  public static readonly int[] OtherSizes = [512, 1024, 2048, 8192];

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_r4bs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  /// <summary>Plain text debugfs.reiser4 -k can print: no '%', which it would read as a format.</summary>
  private static byte[] Text(int lines, int salt) {
    var text = new StringBuilder();
    for (var i = 0; i < lines; ++i) text.Append("line ").Append(i).Append(" of ").Append(salt).Append(": words for the block size checks\n");
    return Encoding.ASCII.GetBytes(text.ToString());
  }

  private static byte[] Random(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  /// <summary>Files around the block boundaries of <paramref name="blockSize" />, long names and nested directories.</summary>
  private static Dictionary<string, byte[]> Files(int blockSize) {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var seed = 0;
    foreach (var size in new[] { 0, 1, blockSize - 1, blockSize, blockSize + 1, 3 * blockSize + 17, 70_000 })
      files[$"sizes/f{size}"] = Random(size, ++seed);
    files["text/readme.txt"] = Text(200, 1);
    files["text/" + new string('n', 24) + ".txt"] = Text(40, 2);
    files["deep/a/b/c/" + new string('x', 255)] = Text(3, 3);
    return files;
  }

  private static Reiser4Writer Writer(int blockSize, Reiser4FileBody body, Dictionary<string, byte[]> files) {
    var writer = new Reiser4Writer { VolumeBlockSize = blockSize, FileBody = body, BlockCount = 32 * 1024 * 1024 / (ulong)blockSize };
    foreach (var (name, data) in files) writer.AddFile(name, data);
    writer.AddDirectory("empty/inner");
    writer.AddSymlink("links/short", "../text/readme.txt");
    writer.AddHardLink("links/hard", "sizes/f1");
    writer.AddSpecialFile("links/fifo", 0x11A4);
    writer.AddSpecialFile("links/char", 0x21A4, 1UL << 20 | 3);
    return writer;
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] image, int blockSize) {
    using var reader = new Reiser4Reader(new MemoryStream(image));
    Assert.That(reader.NativeTreeValid, Is.True, reader.NativeProblem);
    Assert.That(reader.BlockSize, Is.EqualTo(blockSize));
    return reader.Entries.Where(static e => e.Kind == Reiser4Reader.EntryKind.File)
      .ToDictionary(static e => e.Name, reader.Extract, StringComparer.Ordinal);
  }

  private static void AssertFiles(byte[] image, int blockSize, Dictionary<string, byte[]> files) {
    var seen = ReadAll(image, blockSize);
    foreach (var (name, want) in files) {
      Assert.That(seen.ContainsKey(name), Is.True, name + " is missing");
      Assert.That(seen[name], Is.EqualTo(want), name);
    }
  }

  private static IEnumerable<TestCaseData> SizesAndBodies() {
    foreach (var size in OtherSizes)
      foreach (var body in new[] { Reiser4FileBody.Extents, Reiser4FileBody.Tails, Reiser4FileBody.Compressed })
        yield return new TestCaseData(size, body).SetName($"{size}_{body}");
  }

  // ── Without tools ──────────────────────────────────────────────────────

  [TestCaseSource(nameof(SizesAndBodies)), Category("HappyPath")]
  public void GivenABlockSize_WhenTheWriterMakesAVolume_ThenTheReaderGetsEveryObjectBack(int blockSize, Reiser4FileBody body) {
    var files = Files(blockSize);
    var image = Writer(blockSize, body, files).Build();

    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(65536 + 18)), Is.EqualTo(blockSize), "the master's block size");
    Assert.That(image.Length % blockSize, Is.Zero);
    AssertFiles(image, blockSize, files);
    using var reader = new Reiser4Reader(new MemoryStream(image));
    var byName = reader.Entries.ToDictionary(static e => e.Name, StringComparer.Ordinal);
    Assert.Multiple(() => {
      Assert.That(byName["links/short"].LinkTarget, Is.EqualTo("../text/readme.txt"));
      Assert.That(byName["links/hard"].Native!.StatKey, Is.EqualTo(byName["sizes/f1"].Native!.StatKey));
      Assert.That(byName["links/hard"].Metadata!.LinkCount, Is.EqualTo(2U));
      Assert.That(byName["links/fifo"].Kind, Is.EqualTo(Reiser4Reader.EntryKind.Fifo));
      Assert.That(byName["links/char"].Device, Is.EqualTo(1UL << 20 | 3));
      Assert.That(byName["empty/inner"].IsDirectory, Is.True);
    });
  }

  [TestCaseSource(nameof(OtherSizes)), Category("HappyPath")]
  public void GivenANonDefaultVolume_WhenAddingRemovingAndDefragmenting_ThenItKeepsItsBlockSizeAndEveryFile(int blockSize) {
    var files = Files(blockSize);
    using var image = new MemoryStream(Writer(blockSize, Reiser4FileBody.Extents, files).Build());

    Descriptor.Add(image, [ArchiveInputInfo.InMemory("added/new.txt", Text(90, 7))]);
    Descriptor.Remove(image, ["sizes/f70000"]);
    Descriptor.Defragment(image);
    files.Remove("sizes/f70000");
    files["added/new.txt"] = Text(90, 7);

    AssertFiles(image.ToArray(), blockSize, files);
  }

  [TestCaseSource(nameof(OtherSizes)), Category("HappyPath")]
  public void GivenANonDefaultVolume_WhenScatteredAndDefragmentedInPlace_ThenNoNodeMovesAndEveryFileStays(int blockSize) {
    var writer = new Reiser4Writer { VolumeBlockSize = blockSize, BlockCount = 16 * 1024 * 1024 / (ulong)blockSize };
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < 60; ++i) writer.AddFile($"t/f{i:D3}", files[$"t/f{i:D3}"] = Random(1 + i * 777, i));
    using var image = new MemoryStream(writer.Build());
    var nodes = Reiser4TestLayouts.NodeBlocks(image);

    Reiser4TestLayouts.ScatterInPlace(image);
    AssertFiles(image.ToArray(), blockSize, files);
    Descriptor.Defragment(image);

    AssertFiles(image.ToArray(), blockSize, files);
    Assert.That(Reiser4TestLayouts.NodeBlocks(image), Is.EqualTo(nodes));
  }

  [Test, Category("HappyPath")]
  public void GivenTheBlockSizeOption_WhenCreating_ThenTheVolumeUsesIt() {
    using var output = new MemoryStream();

    var options = new FormatCreateOptions { FormatSpecific = new() { ["BlockSize"] = "1 KB" } };
    Descriptor.Create(output, [ArchiveInputInfo.InMemory("a.txt", Text(10, 1))], options);
    AssertFiles(output.ToArray(), 1024, new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["a.txt"] = Text(10, 1) });
  }

  [TestCase("16 KB"), TestCase("3000"), TestCase("256 B"), Category("Exception")]
  public void GivenABlockSizeMkfsDoesNotMake_WhenCreating_ThenItIsRefused(string value) {
    using var output = new MemoryStream();

    var options = new FormatCreateOptions { FormatSpecific = new() { ["BlockSize"] = value } };
    Assert.Throws<NotSupportedException>(() => Descriptor.Create(output, [ArchiveInputInfo.InMemory("a.txt", Text(1, 1))], options));
  }

  [Test, Category("Exception")]
  public void GivenA512ByteVolume_WhenASymlinkTargetCannotFitANode_ThenTheEditIsRefusedAndTheImageUntouched() {
    var source = Writer(512, Reiser4FileBody.Extents, Files(512)).Build();
    using var image = new MemoryStream(source.ToArray());
    Assert.Throws<NotSupportedException>(() => Descriptor.AddSymlink(image, "too-long", new string('t', 600)));
    Assert.That(image.ToArray(), Is.EqualTo(source));
    Descriptor.AddSymlink(image, "fits", new string('t', 300));
    using var reader = new Reiser4Reader(new MemoryStream(image.ToArray()));
    Assert.That(reader.Entries.Single(static e => e.Name == "fits").LinkTarget, Is.EqualTo(new string('t', 300)));
  }

  [TestCaseSource(nameof(OtherSizes)), Category("HappyPath")]
  public void GivenANonDefaultVolume_WhenRebuiltSparseAndLinked_ThenItKeepsItsBlockSize(int blockSize) {
    var files = Files(blockSize);
    files["copy"] = files["text/readme.txt"].ToArray();
    files["zeros"] = new byte[20_000];
    var source = Writer(blockSize, Reiser4FileBody.Extents, files).Build();
    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    Descriptor.RebuildStreaming(input, output, new LayoutRebuildOptions { MakeSparse = true, DeduplicateWithLinks = true });
    AssertFiles(output.ToArray(), blockSize, files);
  }

  // ── reiser4progs ───────────────────────────────────────────────────────

  private string Save(string name, byte[] image) {
    var path = Path.Combine(this._tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  private byte[] Mkfs(int blockSize, long bytes, string options = "") {
    var path = Path.Combine(this._tmpDir, $"mkfs-{blockSize}.img");
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"rm -f {wsl} && truncate -s {bytes} {wsl} && mkfs.reiser4 -fffy -b {blockSize} {options} {wsl}");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"mkfs.reiser4 -b {blockSize} failed:\n{run.StdOut}\n{run.StdErr}");
    return File.ReadAllBytes(path);
  }

  private static void AssertFsckConsistent(string path) {
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"cp {wsl} {wsl}.fsck && fsck.reiser4 --check -y {wsl}.fsck; rc=$?; rm -f {wsl}.fsck; exit $rc");
    var text = run.StdOut + "\n" + run.StdErr;
    Assert.That(run.ExitCode, Is.EqualTo(0), $"fsck.reiser4 rejected {Path.GetFileName(path)}:\n{text}");
    Assert.That(text, Does.Contain("FS is consistent"), $"fsck.reiser4 did not call {Path.GetFileName(path)} consistent:\n{text}");
  }

  private static string Cat(string path, string member) {
    var run = FsInteropToolbox.RunWsl($"debugfs.reiser4 -k '/{member}' {FsInteropToolbox.WinToWsl(path)} </dev/null 2>/dev/null");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"debugfs.reiser4 -k /{member} failed:\n{run.StdOut}\n{run.StdErr}");
    return run.StdOut;
  }

  private static string Items(string path, string member) {
    var run = FsInteropToolbox.RunWsl($"debugfs.reiser4 -i '/{member}' {FsInteropToolbox.WinToWsl(path)} </dev/null 2>&1");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"debugfs.reiser4 -i /{member} failed:\n{run.StdOut}\n{run.StdErr}");
    Assert.That(run.StdOut, Does.Not.Contain("Can't find"), $"debugfs.reiser4 cannot resolve /{member}:\n{run.StdOut}");
    return run.StdOut;
  }

  /// <summary>
  /// The tail-formatted and extent-formatted text files debugfs.reiser4 can print, and the link
  /// targets and counts it reports.
  /// </summary>
  private static void AssertDebugfsReads(string path) {
    Assert.Multiple(() => {
      Assert.That(Cat(path, "text/readme.txt"), Does.Contain(Encoding.ASCII.GetString(Text(200, 1))));
      Assert.That(Cat(path, "text/" + new string('n', 24) + ".txt"), Does.Contain(Encoding.ASCII.GetString(Text(40, 2))));
      Assert.That(Cat(path, "deep/a/b/c/" + new string('x', 255)), Does.Contain(Encoding.ASCII.GetString(Text(3, 3))));
      Assert.That(Items(path, "links/short"), Does.Contain("../text/readme.txt"));
      Assert.That(Items(path, "links/hard"), Does.Contain("nlink:\t\t2"));
    });
  }

  [TestCaseSource(nameof(SizesAndBodies)), Category("ExternalFsInterop")]
  public void GivenAWriterVolumeOfABlockSize_WhenReiser4progsChecksIt_ThenItIsConsistentAndReadsBack(int blockSize, Reiser4FileBody body) {
    Reiser4Oracle.Require();
    var path = this.Save($"w{blockSize}-{body}.img", Writer(blockSize, body, Files(blockSize)).Build());
    AssertFsckConsistent(path);
    // reiser4progs has no decompressor: compressed files are checked for structure only.
    if (body != Reiser4FileBody.Compressed) AssertDebugfsReads(path);
    else Assert.That(Items(path, "links/short"), Does.Contain("../text/readme.txt"));
  }

  [TestCaseSource(nameof(OtherSizes)), Category("ExternalFsInterop")]
  public void GivenAnMkfsVolumeOfABlockSize_WhenReadAndEdited_ThenOurReaderSeesTheRootAndFsckCallsTheEditConsistent(int blockSize) {
    Reiser4Oracle.Require();
    var made = this.Mkfs(blockSize, 32L * 1024 * 1024, "-o create=reg40");
    using (var reader = new Reiser4Reader(new MemoryStream(made))) {
      Assert.That(reader.NativeTreeValid, Is.True, reader.NativeProblem);
      Assert.That(reader.BlockSize, Is.EqualTo(blockSize));
      Assert.That(reader.Entries, Is.Empty, "a fresh volume holds only its root");
    }

    using var image = new MemoryStream();
    image.Write(made);
    var files = Files(blockSize);
    Descriptor.Add(image, [.. files.Select(static f => ArchiveInputInfo.InMemory(f.Key, f.Value))], Reiser4FileBody.Profile);
    Descriptor.AddSymlink(image, "links/short", "../text/readme.txt");
    Descriptor.AddHardLink(image, "links/hard", "sizes/f1");
    var path = this.Save($"edited-{blockSize}.img", image.ToArray());

    AssertFsckConsistent(path);
    AssertDebugfsReads(path);
    AssertFiles(image.ToArray(), blockSize, files);
  }

  [TestCaseSource(nameof(OtherSizes)), Category("ExternalFsInterop")]
  public void GivenAWriterVolumeScatteredAndDefragmented_WhenFsckChecksEachStep_ThenBothAreConsistent(int blockSize) {
    Reiser4Oracle.Require("fsck.reiser4");
    var writer = new Reiser4Writer { VolumeBlockSize = blockSize, BlockCount = 16 * 1024 * 1024 / (ulong)blockSize };
    for (var i = 0; i < 60; ++i) writer.AddFile($"t/f{i:D3}", Random(1 + i * 777, i));
    using var image = new MemoryStream(writer.Build());
    Reiser4TestLayouts.ScatterInPlace(image);
    AssertFsckConsistent(this.Save($"scattered-{blockSize}.img", image.ToArray()));
    Descriptor.Defragment(image);
    AssertFsckConsistent(this.Save($"defragmented-{blockSize}.img", image.ToArray()));
  }

  /// <summary>Block sizes with volume sizes that hold several bitmaps, so superblock copies are laid down too.</summary>
  private static IEnumerable<TestCaseData> EmptyVolumes() {
    foreach (var size in OtherSizes) yield return new TestCaseData(size, 64).SetName($"Empty_{size}_64M");
    yield return new TestCaseData(512, 16).SetName("Empty_512_16M");
    yield return new TestCaseData(4096, 192).SetName("Empty_4096_192M");
    yield return new TestCaseData(4096, 16).SetName("Empty_4096_16M");
  }

  [TestCaseSource(nameof(EmptyVolumes)), Category("ExternalFsInterop")]
  public void GivenMkfsAndTheWriterAtOneBlockSize_WhenBothMakeAnEmptyVolume_ThenTheImagesAreTheSameButForTheRootsTimes(int blockSize, int megabytes) {
    Reiser4Oracle.Require("mkfs.reiser4");
    var made = this.Mkfs(blockSize, megabytes * 1024L * 1024);
    var layout = new Reiser4Layout(blockSize);
    var master = made.AsSpan(65536, blockSize);
    var format = made.AsSpan(checked((int)(layout.Format * (ulong)blockSize)), blockSize);
    var ours = new Reiser4Writer {
      VolumeBlockSize = blockSize, BlockCount = (ulong)(made.Length / blockSize),
      Uuid = master.Slice(20, 16).ToArray(),
      MkfsId = BinaryPrimitives.ReadUInt32LittleEndian(format[48..]),
    }.Build();

    // The root's stat data carries the time it was made: atime, mtime and ctime at 24..35 of the
    // stat data, which is the first item of the first leaf and starts right after the node header.
    var times = (int)(layout.FirstLeaf * (ulong)blockSize) + Reiser4Tree.NodeHeaderBytes + 24;
    made.AsSpan(times, 12).Clear();
    ours.AsSpan(times, 12).Clear();
    Assert.That(ours.Length, Is.EqualTo(made.Length));
    var differing = Enumerable.Range(0, made.Length / blockSize)
      .Where(b => !ours.AsSpan(b * blockSize, blockSize).SequenceEqual(made.AsSpan(b * blockSize, blockSize))).ToList();
    Assert.That(differing, Is.Empty, "blocks that differ from what mkfs.reiser4 -b wrote");
    if (megabytes == 192) Assert.That(layout.BackupCopies((ulong)(made.Length / blockSize)), Is.EqualTo(new[] { 32737UL }), "the copy behind the second bitmap");
  }
}
