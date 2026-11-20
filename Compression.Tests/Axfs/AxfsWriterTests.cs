using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Axfs;

namespace Compression.Tests.Axfs;

/// <summary>
/// The AXFS writer: round trips through our reader, the layout rules, and (where the kernel
/// oracle is prepared in WSL) the AXFS kernel driver mounting what we wrote.
/// </summary>
[TestFixture]
public sealed class AxfsWriterTests {

  private static byte[] Random(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static byte[] Text(int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)("AXFS pages compress " [i % 20] + (i / 4096 % 3));
    return data;
  }

  private static readonly byte[] Big = Text(70000);          // file sizes and offsets past 16 bits
  private static readonly byte[] Noise = Random(9000, 1);    // incompressible: byte-aligned nodes
  private static readonly byte[] Page = Random(4096, 2);     // exactly one page
  private static readonly byte[] Hello = "hello from our AXFS writer\n"u8.ToArray();

  private static AxfsWriteNode Tree() => new("", Children: [
    new("etc", Children: [
      new("motd", Hello),
      new("init.d", Children: [new("rcS", "#!/bin/sh\necho start\n"u8.ToArray()) { Permissions = 0x1ED }]),
    ]),
    new("bin", Children: [
      new("noise.bin", Noise) { Permissions = 0x180, Uid = 1000, Gid = 100 },
      new("page.bin", Page),
      new("motd-link", LinkTarget: "../etc/motd"),
    ]),
    new("big.txt", Big),
    new("empty", []),
    new("empty-dir", Children: []),
    new("Zebra", Hello),                                   // sorts before lower-case names
    new("ünïcode", Hello),
    new("deep", Children: [new("a", Children: [new("b", Children: [new("leaf", Hello)])])]) { Permissions = 0x1C9 },
  ]);

  private static byte[] Image() {
    using var ms = new MemoryStream();
    AxfsWriter.Write(ms, Tree());
    return ms.ToArray();
  }

  private static readonly Dictionary<string, byte[]> Files = new() {
    ["etc/motd"] = Hello, ["etc/init.d/rcS"] = "#!/bin/sh\necho start\n"u8.ToArray(), ["bin/noise.bin"] = Noise,
    ["bin/page.bin"] = Page, ["big.txt"] = Big, ["empty"] = [], ["Zebra"] = Hello, ["ünïcode"] = Hello, ["deep/a/b/leaf"] = Hello,
  };

  [Test]
  public void GivenATree_WhenWrittenAndRead_ThenEveryEntryComesBack() {
    var r = new AxfsReader(new MemoryStream(Image()));
    foreach (var (path, data) in Files)
      Assert.That(r.Extract(r.Entries.Single(e => e.Path == path)), Is.EqualTo(data), path);
    var link = r.Entries.Single(e => e.Path == "bin/motd-link");
    Assert.That(link.IsSymlink && link.Mode == 0xA1FF, Is.True);
    Assert.That(System.Text.Encoding.UTF8.GetString(r.Extract(link)), Is.EqualTo("../etc/motd"));
    var noise = r.Entries.Single(e => e.Path == "bin/noise.bin");
    Assert.That((noise.Mode, noise.Uid, noise.Gid), Is.EqualTo((0x8180u, 1000u, 100u)));
    Assert.That(r.Entries.Single(e => e.Path == "deep").Mode, Is.EqualTo(0x41C9u));
    Assert.That(r.Entries.Single(e => e.Path == "empty-dir").IsDirectory, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(Files.Count + 1 + 7));
  }

  [Test]
  public void GivenATree_WhenWritten_ThenBothNodeKindsAreUsedAndNoXip() {
    var r = new AxfsReader(new MemoryStream(Image()));
    var (xip, byteAligned, compressed) = r.CountNodes();
    Assert.That(xip, Is.Zero);
    Assert.That(byteAligned, Is.GreaterThanOrEqualTo(4), "noise.bin's three pages and page.bin do not shrink");
    Assert.That(compressed, Is.GreaterThanOrEqualTo(18), "big.txt's 18 pages compress");
  }

  [Test]
  public void GivenAnImage_WhenItsFramingIsChecked_ThenItFollowsTheMkfsLayout() {
    var image = Image();
    Assert.That(image.Length % 4096, Is.Zero, "whole pages");
    var size = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(72));
    Assert.That(BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(88)), Is.EqualTo(size), "mmap_size = size");
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan((int)size - 4)), Is.EqualTo(AxfsReader.Magic), "the magic ends the volume");
    for (var r = 0; r < 18; ++r)
      Assert.That(BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(96 + 8 * r)), Is.EqualTo(264 + 40 * r), "descriptor offsets");
    var xip = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(304));
    Assert.That(xip % 4096, Is.Zero, "the XIP region starts on a page");
    Assert.That(image[256], Is.EqualTo(12));
    Assert.That(image.AsSpan(240, 4).ToArray(), Is.EqualTo(new byte[] { 2, 2, 0, 0 }));
    // the file_size table needs three bytes for 70000; node_type one
    Assert.That(image[264 + 40 * 10 + 32], Is.EqualTo(3), "inode_file_size depth");
    Assert.That(image[264 + 40 * 4 + 32], Is.EqualTo(1), "node_type depth");
  }

  [Test]
  public void GivenSiblings_WhenWritten_ThenTheyAreSortedByteWise() {
    var r = new AxfsReader(new MemoryStream(Image()));
    var top = r.Entries.Where(e => !e.Path.Contains('/')).OrderBy(e => e.Index).Select(e => e.Path).ToList();
    Assert.That(top, Is.EqualTo(new[] { "Zebra", "big.txt", "bin", "deep", "empty", "empty-dir", "etc", "ünïcode" }));
  }

  [Test]
  public void GivenTheSameTree_WhenWrittenTwice_ThenTheImagesAreIdentical() =>
    Assert.That(Image(), Is.EqualTo(Image()));

  [TestCase("")]
  [TestCase("a/b")]
  [TestCase("..")]
  [TestCase("nul\0")]
  public void GivenAnInvalidName_WhenWritten_ThenItIsRejected(string name) =>
    Assert.Throws<ArgumentException>(() => AxfsWriter.Write(Stream.Null, new("", Children: [new(name, [1])])));

  [Test]
  public void GivenADuplicateName_WhenWritten_ThenItIsRejected() =>
    Assert.Throws<ArgumentException>(() => AxfsWriter.Write(Stream.Null, new("", Children: [new("a", [1]), new("a", Children: [])])));

  [Test]
  public void GivenANodeThatIsBothFileAndLink_WhenWritten_ThenItIsRejected() =>
    Assert.Throws<ArgumentException>(() => AxfsWriter.Write(Stream.Null, new("", Children: [new("a", [1], LinkTarget: "b")])));

  [Test]
  public void GivenAFileAsRoot_WhenWritten_ThenItIsRejected() =>
    Assert.Throws<ArgumentException>(() => AxfsWriter.Write(Stream.Null, new("", [1])));

  [Test]
  public void GivenAnEmptyRoot_WhenWritten_ThenOurReaderReadsItEmpty() {
    using var ms = new MemoryStream();
    AxfsWriter.Write(ms, new("", Children: []));
    Assert.That(new AxfsReader(new MemoryStream(ms.ToArray())).Entries, Is.Empty);
  }

  [Test]
  public void GivenTheDescriptor_WhenCreating_ThenModesAndOwnersComeFromTheMetadata() {
    var d = new AxfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [
      ArchiveInputInfo.InMemory("a/b/c.txt", "c\n"u8.ToArray(), new ArchiveEntryMetadata(UnixMode: 0x81C0, UnixUserId: 7, UnixGroupId: 8)),
      ArchiveInputInfo.InMemory("top.txt", "top\n"u8.ToArray()),
    ], new FormatCreateOptions());
    var r = new AxfsReader(new MemoryStream(ms.ToArray()));
    Assert.That(r.Entries.Select(e => e.Path), Is.EquivalentTo(new[] { "a", "a/b", "a/b/c.txt", "top.txt" }));
    var c = r.Entries.Single(e => e.Path == "a/b/c.txt");
    Assert.That((c.Mode, c.Uid, c.Gid), Is.EqualTo((0x81C0u, 7u, 8u)));
    Assert.That(r.Extract(c), Is.EqualTo("c\n"u8.ToArray()));
    Assert.That(r.Entries.Single(e => e.Path == "top.txt").Mode, Is.EqualTo(0x81A4u));
  }

  // ---- the AXFS kernel driver as the oracle ----

  private static readonly Lazy<bool> OracleReady = new(() => {
    var script = Script();
    var mode = Environment.GetEnvironmentVariable("CWB_AXFS_KERNEL_DOWNLOAD") == "1" ? "--prepare" : "--check";
    return FsInteropToolbox.RunWsl($"bash {script} {mode}").ExitCode == 0;
  });

  private static string Script() => FsInteropToolbox.WinToWsl(Path.Combine(
    Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "Axfs", "KernelOracle", "axfs-run.sh"));

  /// <summary>Mounts <paramref name="image"/> with the AXFS driver; returns the manifest lines it printed.</summary>
  private static List<string> KernelManifest(byte[] image) {
    if (!FsInteropToolbox.WslAvailable && OperatingSystem.IsWindows()) Assert.Ignore("WSL is not available.");
    if (!OracleReady.Value) Assert.Ignore("The AXFS kernel oracle is not prepared (set CWB_AXFS_KERNEL_DOWNLOAD=1 to build it; needs qemu-system-x86_64, gcc, make).");
    var path = Path.Combine(Path.GetTempPath(), "cwb-axfs-" + Guid.NewGuid().ToString("N") + ".img");
    File.WriteAllBytes(path, image);
    try {
      var run = FsInteropToolbox.RunWsl($"bash {Script()} {FsInteropToolbox.WinToWsl(path)}");
      var log = run.StdOut + "\n" + run.StdErr;
      TestContext.Out.WriteLine(log);
      Assert.That(run.ExitCode, Is.Zero, log);
      Assert.That(log, Does.Not.Contain("MOUNTFAIL").And.Not.Contain("BUG").And.Not.Contain("Oops"), log);
      var start = log.IndexOf("===MANIFEST===\n", StringComparison.Ordinal);
      var end = log.IndexOf("===END-MANIFEST===", StringComparison.Ordinal);
      Assert.That(start >= 0 && end > start, Is.True, log);
      return [.. log[(start + 15)..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    } finally {
      try { File.Delete(path); } catch { /* best effort */ }
    }
  }

  private static string Md5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

  [Test, Category("ExternalInterop")]
  public void GivenOurImage_WhenTheAxfsDriverMountsIt_ThenItReadsEveryFileAsWritten() {
    var lines = KernelManifest(Image());
    var expected = new List<string> {
      "D|.|755|0|0", "D|./bin|755|0|0", "D|./deep|711|0|0", "D|./deep/a|755|0|0", "D|./deep/a/b|755|0|0",
      "D|./empty-dir|755|0|0", "D|./etc|755|0|0", "D|./etc/init.d|755|0|0",
      "L|./bin/motd-link|777|0|0|../etc/motd",
      $"F|./bin/noise.bin|600|1000|100|{Noise.Length}|{Md5(Noise)}",
    };
    foreach (var (path, data) in Files.Where(f => f.Key is not ("bin/noise.bin" or "ünïcode"))) {
      var mode = path == "etc/init.d/rcS" ? "755" : "644";
      expected.Add($"F|./{path}|{mode}|0|0|{data.Length}|{Md5(data)}");
    }
    // the console output's code page is not ours to choose: the non-ASCII name is matched by its data
    var ascii = lines.Where(static l => l.All(char.IsAscii)).ToList();
    Assert.That(ascii, Is.EquivalentTo(expected));
    Assert.That(lines.Except(ascii).Single(), Does.StartWith("F|./").And.EndWith($"|644|0|0|{Hello.Length}|{Md5(Hello)}"));
  }

  /// <summary>The oracle itself: it reads the images <c>mkfs.axfs</c> built as their manifests say.</summary>
  [TestCase("axfs-compressed")]
  [TestCase("axfs-xip")]
  [Category("ExternalInterop")]
  public void GivenMkfsAxfsImage_WhenTheAxfsDriverMountsIt_ThenItListsTheSourceTree(string name) {
    using var res = typeof(AxfsTests).Assembly.GetManifestResourceStream($"AxfsVectors.{name}.img.gz")!;
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    var lines = KernelManifest(ms.ToArray());
    Assert.That(lines, Has.Count.EqualTo(19), "the root and the 18 entries of the source tree");
    Assert.That(lines, Has.Some.StartsWith("F|./etc/numbers.txt|644|1000|1000|13893|"));
    Assert.That(lines, Has.Some.EqualTo("L|./bin/motd-link|777|1000|1000|../etc/motd"));
  }
}
