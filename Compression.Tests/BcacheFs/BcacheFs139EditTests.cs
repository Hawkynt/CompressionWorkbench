using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Volumes at metadata version 1.39, as bcachefs-tools 1.39.6 writes them: edited in place with
/// the derived trees, the accounting tree and the superblock rebuilt by 1.39's rules. A 6.8 kernel
/// stops at 1.4, so the proof is that version's own fsck (named in <c>CWB_BCACHEFS_EXTRA</c>)
/// and the reader; the checked-in reference volumes are edited and read back everywhere.
/// </summary>
[TestFixture]
public sealed class BcacheFs139EditTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_bcachefs_139_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static MemoryStream Image(string name) {
    using var resource = typeof(BcacheFs139EditTests).Assembly.GetManifestResourceStream("BcacheFsVectors." + name)!;
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private static byte[] Payload(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static Dictionary<string, string> Read(IReadOnlyList<Stream> members) {
    using var reader = new BcacheFsReader(members);
    Assert.That(reader.Valid, Is.True, reader.Status);
    return reader.Entries.Where(static e => e.LinkTarget == null)
      .ToDictionary(e => e.Name, e => Hash(reader.Read(e)), StringComparer.Ordinal);
  }

  /// <summary>Adds files (one spanning many buckets), replaces one, removes one the tool wrote.</summary>
  private static Dictionary<string, string> Edit(IReadOnlyList<Stream> members, string? removeTheirs) {
    var expected = Read(members);
    var descriptor = new BcacheFsFormatDescriptor();
    var added = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["ours/small.txt"] = Encoding.ASCII.GetBytes("a small file\n"),
      ["ours/big.bin"] = Payload(900_000, 1),
      ["ours/text.txt"] = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 12_000).Select(i => $"line {i}: the quick brown fox\n"))),
      ["ours/deep/a/b/c.bin"] = Payload(70_000, 2),
      ["ours/replaced.bin"] = Payload(40_000, 4),
    };
    descriptor.AddToVolume(members, [.. added.Select(static f => ArchiveInputInfo.InMemory(f.Key, f.Value))]);
    var replacement = Payload(120_000, 5);
    descriptor.AddToVolume(members, [ArchiveInputInfo.InMemory("ours/replaced.bin", replacement)]);
    added["ours/replaced.bin"] = replacement;
    if (removeTheirs != null) {
      descriptor.RemoveFromVolume(members, [removeTheirs]);
      expected.Remove(removeTheirs);
    }
    foreach (var (name, data) in added) expected[name] = Hash(data);
    return expected;
  }

  [Test, Category("HappyPath")]
  public void GivenThe139VolumeTheToolsPopulated_WhenEdited_ThenEveryFileReadsBack() {
    using var image = Image("bcachefs-tools-1.39-format-source.img.gz");
    var expected = Edit([image], "hello.txt");
    Assert.That(Read([image]), Is.EquivalentTo(expected));
  }

  /// <summary>
  /// Volumes asking for compression and other data checksums: new data is stored compressed
  /// with the volume's codec (raw and marked incompressible where that does not help) and
  /// checksummed as asked, and every file, the tool's and ours, reads back.
  /// </summary>
  [TestCase("bcachefs-tools-1.39-lz4-crc32c.img.gz", "tiny.txt", TestName = "GivenThe139Lz4Volume_WhenEdited_ThenEveryFileReadsBack")]
  [TestCase("bcachefs-tools-1.39-gzip-crc64.img.gz", "bin/noise.bin", TestName = "GivenThe139GzipCrc64Volume_WhenEdited_ThenEveryFileReadsBack")]
  [TestCase("bcachefs-tools-1.39-zstd-xxhash.img.gz", "text/odd-size.txt", TestName = "GivenThe139ZstdXxhashVolume_WhenEdited_ThenEveryFileReadsBack")]
  [Category("HappyPath")]
  public void CompressedReferenceVolume(string name, string removeTheirs) {
    using var image = Image(name);
    var expected = Edit([image], removeTheirs);
    Assert.That(Read([image]), Is.EquivalentTo(expected));
  }

  /// <summary>
  /// fsck checks a compressed run's checksum, not that it decodes. Every run we compress into a
  /// gzip or zstd volume is decoded here by a reference decoder — Python's zlib for the raw
  /// DEFLATE stream, the zstd tool for the frame behind its length — and must give back the
  /// file's bytes.
  /// </summary>
  [TestCase("bcachefs-tools-1.39-gzip-crc64.img.gz", TestName = "GivenOurGzipRuns_WhenAReferenceDecoderReadsThem_ThenTheyDecodeToTheFile")]
  [TestCase("bcachefs-tools-1.39-zstd-xxhash.img.gz", TestName = "GivenOurZstdRuns_WhenAReferenceDecoderReadsThem_ThenTheyDecodeToTheFile")]
  [Category("ExternalFsInterop")]
  public void CompressedRunsDecodeWithAReferenceDecoder(string name) {
    if (!FsInteropToolbox.WslHasTool("python3") || !FsInteropToolbox.WslHasTool("zstd"))
      Assert.Ignore("python3 and zstd are needed in the Linux environment as reference decoders.");
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 20_000).Select(i => $"row {i}: compressible text\n")));
    using var image = Image(name);
    new BcacheFsFormatDescriptor().AddToVolume([image], [ArchiveInputInfo.InMemory("ours/text.txt", text)]);

    image.Position = 0;
    using var reader = new BcacheFsReader([image]);
    var entry = reader.Entries.Single(e => e.Name == "ours/text.txt");
    var decoded = 0;
    foreach (var extent in entry.Extents) {
      if (extent.Stored is not { Crc: { Compressed: true } crc } stored) continue;
      var region = new byte[crc.CompressedSize * 512];
      image.Position = stored.RegionSector * 512;
      image.ReadExactly(region);
      var regionPath = Path.Combine(this._tmpDir, $"run{decoded}.bin");
      var wsl = FsInteropToolbox.WinToWsl(regionPath);
      string command;
      if (crc.CompressionType == BcacheFsCompressionType.Zstd) {
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(region);
        File.WriteAllBytes(regionPath, region.AsSpan(4, (int)length).ToArray());
        command = $"zstd -d -q -c {wsl} | base64 -w0";
      } else {
        File.WriteAllBytes(regionPath, region);
        command = $"python3 -c 'import sys,zlib,base64; d=zlib.decompressobj(-15); print(base64.b64encode(d.decompress(open(sys.argv[1],\"rb\").read())).decode())' {wsl}";
      }
      var run = FsInteropToolbox.RunWsl(command);
      Assert.That(run.ExitCode, Is.Zero, run.StdErr);
      var bytes = Convert.FromBase64String(run.StdOut.Trim());
      var expected = new byte[(long)crc.UncompressedSize * 512];
      var from = (int)extent.FileOffset;
      text.AsSpan(from, Math.Min(text.Length - from, expected.Length)).CopyTo(expected);
      Assert.That(bytes, Is.EqualTo(expected), $"run at file offset {from} decodes to the file's bytes");
      ++decoded;
    }
    Assert.That(decoded, Is.GreaterThan(0), "the text compressed into at least one run");
  }

  // ── Proven by 1.39's own fsck ────────────────────────────────────────────

  private static string? _newer;
  private static bool _probed;
  private static string _why = "";

  /// <summary>A bcachefs named in CWB_BCACHEFS_EXTRA that reads 1.39 volumes, or skips the test.</summary>
  private static string Newer() {
    if (!_probed) {
      _probed = true;
      BcacheFsOracleGate.RequireBcachefsTools();
      var extra = (Environment.GetEnvironmentVariable("CWB_BCACHEFS_EXTRA") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
      _newer = extra.FirstOrDefault(c => {
        var probe = FsInteropToolbox.RunWsl($"d=$(mktemp -d) && truncate -s 32M $d/p.img && {c} format -q --force $d/p.img >/dev/null 2>&1 "
          + $"&& {c} show-super $d/p.img 2>/dev/null | grep -m1 '^Version:'; rm -rf $d");
        _why += $"{c}: exit {probe.ExitCode} {probe.StdOut} {probe.StdErr}; ";
        return probe.StdOut.Contains("(1.39)", StringComparison.Ordinal);
      });
    }
    if (_newer == null) Assert.Ignore($"No bcachefs-tools 1.39 named in CWB_BCACHEFS_EXTRA. {_why}");
    return _newer!;
  }

  private static void AssertFsckClean(string checker, params string[] paths) {
    var wsl = string.Join(" ", paths.Select(FsInteropToolbox.WinToWsl));
    var result = FsInteropToolbox.RunWsl($"{checker} fsck -n {wsl}");
    var output = result.StdOut + "\n" + result.StdErr;
    TestContext.Out.WriteLine($"$ {checker} fsck -n (exit {result.ExitCode})\n{output}");
    Assert.That(result.ExitCode, Is.Zero, $"1.39 fsck rejected the volume:\n{output}");
    Assert.That(output, Does.Contain("check_nlinks... done"), $"1.39 fsck did not walk the whole volume:\n{output}");
    Assert.That(output, Does.Not.Contain("fixing").And.Not.Contain("mismatch").And.Not.Contain("incorrect"),
      $"1.39 fsck found something to repair:\n{output}");
  }

  private string[] Format(string checker, string options, bool populate, params string[] sizes) {
    var paths = sizes.Select((_, i) => Path.Combine(this._tmpDir, $"member{i}.img")).ToArray();
    var wsl = paths.Select(FsInteropToolbox.WinToWsl).ToArray();
    var source = FsInteropToolbox.WinToWsl(Path.Combine(this._tmpDir, "src"));
    var populateCommand = populate
      ? $"mkdir -p {source}/sub/deeper {source}/empty && printf 'hello\\n' > {source}/hello.txt "
        + $"&& head -c 300000 /dev/urandom > {source}/sub/deeper/big.bin && head -c 4097 /dev/urandom > {source}/sub/odd.bin "
        + $"&& ln -s hello.txt {source}/link && "
      : "";
    var truncate = string.Join(" && ", wsl.Select((p, i) => $"truncate -s {sizes[i]} {p}"));
    var run = FsInteropToolbox.RunWsl($"{populateCommand}{truncate} && {checker} format -q --force {options}"
      + (populate ? $" --source={source}" : "") + $" {string.Join(" ", wsl)}");
    Assert.That(run.ExitCode, Is.Zero, $"format {options}: {run.StdOut}{run.StdErr}");
    return paths;
  }

  [TestCase("", true, "64M", TestName = "GivenAPopulated139Volume_WhenEdited_Then139FsckIsClean")]
  [TestCase("--bucket_size=128k", false, "64M", TestName = "GivenAnEmpty139VolumeWith128KBuckets_WhenEdited_Then139FsckIsClean")]
  [TestCase("--replicas=2", false, "64M", "64M", TestName = "GivenAMirrored139VolumeOnTwoMembers_WhenEdited_Then139FsckIsClean")]
  [TestCase("--compression=lz4", true, "64M", TestName = "GivenALz4Compressed139Volume_WhenEdited_Then139FsckIsClean")]
  [TestCase("--compression=gzip --data_checksum=crc64", true, "64M", TestName = "GivenAGzipCrc64139Volume_WhenEdited_Then139FsckIsClean")]
  [TestCase("--compression=zstd --data_checksum=xxhash", true, "64M", TestName = "GivenAZstdXxhash139Volume_WhenEdited_Then139FsckIsClean")]
  [TestCase("--background_compression=zstd", false, "64M", TestName = "GivenABackgroundZstd139Volume_WhenEdited_Then139FsckIsClean")]
  [TestCase("--data_checksum=none", true, "64M", TestName = "GivenAnUnchecksummed139Volume_WhenEdited_Then139FsckIsClean")]
  [Category("ExternalFsInterop")]
  public void FormattedEdited(string options, bool populate, params string[] sizes) {
    var checker = Newer();
    var paths = this.Format(checker, options, populate, sizes);
    AssertFsckClean(checker, paths);
    var members = paths.Select(p => (Stream)new FileStream(p, FileMode.Open, FileAccess.ReadWrite)).ToArray();
    try {
      var expected = Edit(members, populate ? "hello.txt" : null);
      Assert.That(Read(members), Is.EquivalentTo(expected));
    } finally {
      foreach (var member in members) member.Dispose();
    }
    AssertFsckClean(checker, paths);
  }

  /// <summary>
  /// <c>bcachefs format --source</c> with two copies leaves reconcile work queued (the copies it
  /// still owes); an edit here does not rebuild that tree, so it refuses and writes nothing.
  /// </summary>
  [Test, Category("ExternalFsInterop")]
  public void GivenPendingReconcileWork_WhenEdited_ThenItIsRefusedAndUnchanged() {
    var checker = Newer();
    var paths = this.Format(checker, "--replicas=2", true, "64M", "64M");
    var before = paths.Select(File.ReadAllBytes).ToArray();
    var members = paths.Select(p => (Stream)new FileStream(p, FileMode.Open, FileAccess.ReadWrite)).ToArray();
    try {
      Assert.That(() => new BcacheFsFormatDescriptor().AddToVolume(members, [ArchiveInputInfo.InMemory("x.txt", [1])]),
        Throws.InstanceOf<NotSupportedException>().With.Message.Contains("Reconcile"));
    } finally {
      foreach (var member in members) member.Dispose();
    }
    for (var i = 0; i < paths.Length; ++i)
      Assert.That(File.ReadAllBytes(paths[i]).SequenceEqual(before[i]), Is.True, $"member {i} unchanged");
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenThe139ReferenceVolume_WhenEdited_Then139FsckIsClean() {
    var checker = Newer();
    var path = Path.Combine(this._tmpDir, "reference.img");
    using (var image = Image("bcachefs-tools-1.39-format-source.img.gz")) {
      Edit([image], "hello.txt");
      File.WriteAllBytes(path, image.ToArray());
    }
    AssertFsckClean(checker, path);
  }
}
