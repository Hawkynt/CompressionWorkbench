using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Pea;

namespace Compression.Tests.Pea;

/// <summary>
/// PEA: archives PeaZip 11.4 wrote with every compression level and most check algorithms
/// (Pea/ReferenceVectors), our writer, and PeaZip's own pea (headless) as the oracle for what we
/// write.
/// </summary>
[TestFixture]
public class PeaTests {

  private static readonly byte[] Fox = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog.\n", 300)));
  private static readonly byte[] Hello = "hello pea\n"u8.ToArray();
  private static readonly byte[] Nested = "nested\n"u8.ToArray();
  private static readonly DateTime SourceTime = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_pea_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [TestCase("store.pea")]    // PCOMPRESS0, CRC32 volume/object, no stream check
  [TestCase("deflate3.pea")] // PCOMPRESS1, Adler-32 volume, CRC-64 objects, SHA-256 stream
  [TestCase("deflate6.pea")] // PCOMPRESS2, CRC32, CRC32, SHA-256
  [TestCase("deflate9.pea")] // PCOMPRESS3, SHA-1 volume, MD5 objects, SHA-512 stream
  [TestCase("sha3.pea")]     // SHA3-256 volume, SHA3-512 objects, BLAKE2b stream
  [TestCase("misc.pea")]     // RIPEMD-160 volume, Whirlpool objects, BLAKE2s stream
  [Category("ThemVsUs")]
  public void GivenArchiveWrittenByPeaZip_WhenRead_ThenEveryObjectAndCheckVerifies(string vector) {
    var entries = PeaArchive.Read(new MemoryStream(LoadVector(vector)));

    Assert.That(entries.Select(e => (e.Path, e.IsDirectory)), Is.EquivalentTo(new[] {
      ("hello.txt", false), ("fox.txt", false), ("empty.txt", false), ("sub", true), ("sub/n.txt", false) }));
    Assert.That(entries.Single(e => e.Path == "hello.txt").Data, Is.EqualTo(Hello));
    Assert.That(entries.Single(e => e.Path == "fox.txt").Data, Is.EqualTo(Fox));
    Assert.That(entries.Single(e => e.Path == "empty.txt").Data, Is.Empty);
    Assert.That(entries.Single(e => e.Path == "sub/n.txt").Data, Is.EqualTo(Nested));
    Assert.That(entries.Single(e => e.Path == "fox.txt").LastModified, Is.EqualTo(SourceTime));
  }

  [Test, Category("ThemVsUs")]
  public void GivenMultiBlockArchiveWrittenByPeaZip_WhenRead_ThenAll3MiBDecode() {
    var big = PeaArchive.Read(new MemoryStream(LoadVector("big.pea"))).Single();
    var expected = Enumerable.Range(0, 3_000_000).Select(i => (byte)((i * 131 + (i >> 7)) % 256)).ToArray();
    Assert.That(big.Path, Is.EqualTo("big.bin"));
    Assert.That(big.Data, Is.EqualTo(expected));
  }

  [Test, Category("Exception")]
  public void GivenFlippedDataByte_WhenRead_ThenAChecksumRefusesIt() {
    var bytes = LoadVector("store.pea");
    var at = bytes.AsSpan().IndexOf("The quick"u8);
    bytes[at + 5] ^= 0x01;
    Assert.That(() => PeaArchive.Read(new MemoryStream(bytes)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenEncryptedStreamHeader_WhenRead_ThenRefusedAsUnsupported() {
    var bytes = LoadVector("deflate6.pea");
    bytes[18] = 0x31; // EAX
    Assert.That(() => PeaArchive.Read(new MemoryStream(bytes)), Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("Exception")]
  public void GivenNotPea_WhenRead_ThenRefused()
    => Assert.That(() => PeaArchive.Read(new MemoryStream(new byte[64])), Throws.InstanceOf<InvalidDataException>());

  // ── our writer ──────────────────────────────────────────────────────

  private static List<PeaArchive.Input> Inputs() => [
    new("hello.txt", Hello, SourceTime),
    new("dir/fox.txt", Fox, SourceTime),
    new("dir/deep/empty.bin", [], SourceTime),
    new("big.bin", Enumerable.Range(0, 2_500_000).Select(i => (byte)(i * 7 + (i >> 9))).ToArray(), SourceTime),
  ];

  [TestCase(0, PeaArchive.Check.Crc32, PeaArchive.Check.None, PeaArchive.Check.Crc32)]
  [TestCase(1, PeaArchive.Check.Crc64, PeaArchive.Check.Sha256, PeaArchive.Check.Adler32)]
  [TestCase(2, PeaArchive.Check.Crc32, PeaArchive.Check.Sha256, PeaArchive.Check.Crc32)]
  [TestCase(3, PeaArchive.Check.Md5, PeaArchive.Check.Sha512, PeaArchive.Check.Sha1)]
  [Category("RoundTrip")]
  public void GivenFiles_WhenWrittenAndRead_ThenEveryByteRoundTrips(int compression, PeaArchive.Check obj, PeaArchive.Check stream, PeaArchive.Check volume) {
    using var ms = new MemoryStream();
    PeaArchive.Write(ms, Inputs(), compression, obj, stream, volume);
    var entries = PeaArchive.Read(new MemoryStream(ms.ToArray()));
    foreach (var input in Inputs())
      Assert.That(entries.Single(e => e.Path == input.Path).Data, Is.EqualTo(input.Data), input.Path);
    Assert.That(entries.Where(e => e.IsDirectory).Select(e => e.Path), Is.EquivalentTo(new[] { "dir", "dir/deep" }));
  }

  [Test, Category("Exception")]
  public void GivenPassword_WhenCreated_ThenRefused()
    => Assert.That(() => new PeaFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1])], new FormatCreateOptions { Password = "x" }),
      Throws.InstanceOf<NotSupportedException>());

  [Test, Category("ExternalInterop")]
  public void GivenOurArchive_WhenPeaZipExtractsIt_ThenEveryFileMatches() {
    var pea = RequirePea();
    var path = Path.Combine(this._tmpDir, "ours.pea");
    using (var f = File.Create(path))
      PeaArchive.Write(f, Inputs());
    var outDir = Path.Combine(this._tmpDir, "out");
    Directory.CreateDirectory(outDir);

    var r = FsInteropToolbox.RunWsl($"timeout 120 xvfb-run -a '{pea}' UNPEA '{FsInteropToolbox.WinToWsl(path)}' '{FsInteropToolbox.WinToWsl(outDir)}/' RESETDATE RESETATTR EXTRACT2DIR HIDDEN '' ''");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"pea UNPEA failed:\n{r.StdOut}\n{r.StdErr}");
    var root = Path.Combine(outDir, "ours.pea");
    foreach (var input in Inputs())
      Assert.That(File.ReadAllBytes(Path.Combine(root, input.Path.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(input.Data), input.Path);
  }

  private static string RequirePea() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    var pea = Environment.GetEnvironmentVariable("PEAZIP_PEA") ?? "/opt/cwb_peazip/peazip_portable-11.4.0.LINUX.GTK2.x86_64/pea";
    var probe = FsInteropToolbox.RunWsl($"test -x '{pea}' && command -v xvfb-run");
    if (probe.ExitCode != 0) Assert.Ignore($"PeaZip's pea is not at {pea} (set PEAZIP_PEA), or xvfb-run is missing.");
    return pea;
  }

  private static byte[] LoadVector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PeaVectors.{name}")
      ?? throw new InvalidOperationException($"missing embedded vector {name}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
