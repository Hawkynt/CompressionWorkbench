using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Deflate;
using Compression.Registry;
using FileFormat.AlZip;

namespace Compression.Tests.AlZip;

/// <summary>
/// ALZip's obfuscated deflate (method 3). The code-length orders are the ones XADMaster's
/// <c>CalculateSillyTable</c> prints for each key; <c>ours-obfuscated.alz</c> is our writer's output and
/// <c>unar-obfuscated.txt</c> is what The Unarchiver extracted from it; ALZip 5.52 and Bandizip 7.38
/// extracted the same bytes (see ReferenceVectors/README.md).
/// </summary>
[TestFixture]
public sealed class AlZipObfuscatedDeflateTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AlZipVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded ALZip vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>XADMaster's order for each key 0..15, printed by its own function (compiled with gcc).</summary>
  private static readonly int[][] XadOrders = [
    [18, 3, 6, 13, 12, 15, 14, 9, 0, 11, 4, 5, 16, 7, 8, 17, 2, 1, 10],
    [1, 18, 7, 10, 9, 16, 4, 14, 13, 3, 15, 5, 6, 11, 8, 0, 17, 2, 12],
    [2, 5, 18, 11, 14, 13, 8, 17, 10, 3, 4, 15, 6, 7, 16, 1, 0, 9, 12],
    [3, 6, 9, 17, 15, 18, 13, 12, 2, 14, 4, 5, 10, 7, 0, 16, 1, 11, 8],
    [4, 17, 10, 13, 18, 7, 16, 9, 2, 3, 14, 5, 6, 15, 0, 1, 8, 11, 12],
    [5, 8, 16, 14, 17, 18, 11, 1, 13, 3, 4, 9, 6, 2, 15, 0, 10, 7, 12],
    [6, 9, 12, 17, 18, 15, 16, 1, 2, 13, 4, 5, 14, 3, 0, 7, 10, 11, 8],
    [7, 15, 13, 16, 17, 10, 0, 18, 2, 3, 8, 5, 1, 14, 4, 9, 6, 11, 12],
    [8, 11, 16, 17, 14, 15, 0, 1, 18, 3, 4, 13, 2, 5, 6, 9, 10, 7, 12],
    [9, 12, 15, 16, 18, 6, 17, 1, 2, 14, 4, 0, 13, 3, 8, 5, 10, 11, 7],
    [10, 15, 16, 13, 14, 7, 0, 17, 2, 3, 18, 1, 4, 5, 8, 9, 6, 11, 12],
    [11, 14, 15, 17, 5, 16, 0, 1, 13, 3, 8, 18, 2, 7, 4, 9, 10, 6, 12],
    [12, 15, 18, 13, 6, 9, 16, 1, 2, 17, 0, 3, 14, 7, 8, 5, 10, 11, 4],
    [13, 14, 16, 4, 15, 10, 0, 12, 2, 7, 17, 1, 6, 18, 8, 9, 5, 11, 3],
    [14, 17, 12, 5, 8, 15, 0, 1, 16, 11, 2, 13, 6, 7, 18, 9, 10, 3, 4],
    [15, 18, 3, 14, 9, 12, 11, 1, 6, 16, 0, 5, 17, 7, 8, 13, 10, 2, 4],
  ];

  internal static readonly DateTime FixedTime = new(2024, 5, 6, 7, 8, 10);

  private static byte[] Words(int seed, int length) {
    var r = new Random(seed);
    string[] words = ["alpha", "beta", "gamma", "archive", "volume", "obfuscated", "deflate", "alz", "member", "block", "order"];
    var text = new StringBuilder();
    while (text.Length < length) text.Append(words[r.Next(words.Length)]).Append(r.Next(9) == 0 ? '\n' : ' ');
    return Encoding.ASCII.GetBytes(text.ToString(0, length));
  }

  /// <summary>One member per key (size mod 16), a multi-block member, an incompressible one and an empty one.</summary>
  internal static List<(string Name, byte[] Data)> Inputs() {
    var files = new List<(string, byte[])>();
    for (var key = 0; key < 16; ++key)
      files.Add(($"key{key:D2}.txt", Words(key, 3000 + key)));
    files.Add(("big/text.txt", Words(99, 200_000 + 7)));
    var noise = new byte[5000];
    new Random(3).NextBytes(noise);
    files.Add(("noise.bin", noise));
    files.Add(("empty.txt", []));
    return files;
  }

  internal static byte[] OursArchive() {
    using var ms = new MemoryStream();
    using (var w = new AlZipWriter(ms, leaveOpen: true, obfuscated: true)) {
      w.AddDirectory("big", FixedTime);
      foreach (var (name, data) in Inputs())
        w.AddFile(name, data, FixedTime);
    }
    return ms.ToArray();
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_EachKey_When_OrderIsComputed_Then_MatchesXadMaster() {
    for (var key = 0; key < 16; ++key)
      Assert.That(AlZipObfuscatedDeflate.CodeLengthOrder(key), Is.EqualTo(XadOrders[key]), $"key {key}");
  }

  [TestCase(16L, 0)]
  [TestCase(31L, 15)]
  [TestCase(0x1_0000_0003L, 3)]
  [TestCase(long.MaxValue, 15)]
  [Category("Boundary")]
  public void Given_ALargeSize_When_OrderIsComputed_Then_OnlyTheLowFourBitsCount(long size, int key)
    => Assert.That(AlZipObfuscatedDeflate.CodeLengthOrder(size), Is.EqualTo(XadOrders[key]));

  [Test, Category("EdgeCase")]
  public void Given_ANegativeSize_When_OrderIsComputed_Then_Refused()
    => Assert.Throws<ArgumentOutOfRangeException>(() => AlZipObfuscatedDeflate.CodeLengthOrder(-1));

  [Test, Category("ThirdPartyVector")]
  public void Given_OurObfuscatedArchive_When_Extracted_Then_MatchesUnar() {
    var expected = Encoding.UTF8.GetString(Vector("unar-obfuscated.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split('\t'))
      .ToDictionary(p => p[0], p => (Size: long.Parse(p[1]), Sha: p[2]));
    using var reader = new AlZipReader(new MemoryStream(Vector("ours-obfuscated.alz")));
    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Select(e => e.FileName), Is.EquivalentTo(expected.Keys));
    foreach (var e in files) {
      var data = reader.Extract(e);
      Assert.That(data.LongLength, Is.EqualTo(expected[e.FileName].Size), e.FileName);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(expected[e.FileName].Sha), e.FileName);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_SameInputs_When_Written_Then_ByteIdenticalToWhatUnarRead()
    => Assert.That(OursArchive(), Is.EqualTo(Vector("ours-obfuscated.alz")));

  [Test]
  public void Given_OurObfuscatedArchive_When_Listed_Then_CompressibleMembersAreMethod3AndPlainInflateCannotReadThem() {
    var bytes = Vector("ours-obfuscated.alz");
    using var reader = new AlZipReader(new MemoryStream(bytes));
    var byName = reader.Entries.ToDictionary(e => e.FileName);
    Assert.That(byName["noise.bin"].Method, Is.Zero, "an incompressible member is stored");
    Assert.That(byName["empty.txt"].Method, Is.Zero);
    foreach (var e in reader.Entries.Where(e => e.FileName.EndsWith(".txt") && e.OriginalSize > 0)) {
      Assert.That(e.MethodName, Is.EqualTo("Obfuscated deflate"), e.FileName);
      var packed = bytes.AsSpan((int)e.DataOffset, (int)e.CompressedSize).ToArray();
      byte[]? plain = null;
      try { plain = DeflateDecompressor.Decompress(packed); } catch (InvalidDataException) { }
      Assert.That(plain, Is.Not.EqualTo(reader.Extract(e)), $"{e.FileName} would also inflate as method 2");
    }
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(15)]
  [TestCase(16)]
  [TestCase(17)]
  [TestCase(65535)]
  [TestCase(65536)]
  [TestCase(300_001)]
  [Category("Boundary")]
  public void Given_ASize_When_RoundTripped_Then_ByteIdentical(int size) {
    var data = Words(size, size);
    var packed = AlZipObfuscatedDeflate.Compress(data);
    Assert.That(AlZipObfuscatedDeflate.Decompress(packed, size), Is.EqualTo(data));
  }

  [Test]
  public void Given_TheMaximumLevel_When_RoundTripped_Then_ByteIdentical() {
    var data = Words(5, 70_003);
    var packed = AlZipObfuscatedDeflate.Compress(data, DeflateCompressionLevel.Maximum);
    Assert.That(AlZipObfuscatedDeflate.Decompress(packed, data.Length), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void Given_TheWrongSize_When_Decoded_Then_NotTheOriginal() {
    var data = Words(8, 4000);
    var packed = AlZipObfuscatedDeflate.Compress(data);
    byte[]? wrong = null;
    try { wrong = AlZipObfuscatedDeflate.Decompress(packed, data.Length + 1); } catch (InvalidDataException) { }
    Assert.That(wrong, Is.Not.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void Given_ACorruptMethod3Member_When_Extracted_Then_Refused() {
    var bytes = OursArchive();
    using var probe = new AlZipReader(new MemoryStream(bytes));
    var e = probe.Entries.First(x => x.FileName == "key05.txt");
    bytes[(int)e.DataOffset + e.CompressedSize / 2] ^= 0x5A;
    using var reader = new AlZipReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries.First(x => x.FileName == "key05.txt")));
  }

  [Test, Category("EdgeCase")]
  public void Given_BothBzip2AndObfuscated_When_AWriterIsMade_Then_Refused()
    => Assert.Throws<ArgumentException>(() => _ = new AlZipWriter(new MemoryStream(), bzip2: true, obfuscated: true));

  [Test]
  public void Given_TheObfuscatedMethod_When_CreatedThroughTheDescriptor_Then_OurReaderExtractsIt() {
    var inputs = Inputs();
    using var ms = new MemoryStream();
    new AlZipFormatDescriptor().Create(ms, [.. inputs.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))], new FormatCreateOptions("obfuscated-deflate"));
    ms.Position = 0;
    using var reader = new AlZipReader(ms);
    Assert.That(reader.Entries.Count(e => e.Method == 3), Is.EqualTo(17));
    foreach (var (name, data) in inputs)
      Assert.That(reader.Extract(reader.Entries.First(e => e.FileName == name)), Is.EqualTo(data), name);
  }

  [TestCase(18)]
  [TestCase(20)]
  [Category("EdgeCase")]
  public void Given_AnOrderOfTheWrongLength_When_Set_Then_Refused(int length) {
    var order = Enumerable.Range(0, length).ToArray();
    Assert.Throws<ArgumentException>(() => _ = new DeflateDecompressor(new MemoryStream()) { CodeLengthOrder = order });
    Assert.Throws<ArgumentException>(() => _ = new DeflateCompressor(new MemoryStream()) { CodeLengthOrder = order });
  }

  [Test, Category("EdgeCase")]
  public void Given_AnOrderWithARepeatedOrOutOfRangeSymbol_When_Set_Then_Refused() {
    int[] repeated = [.. Enumerable.Range(0, 18), 0];
    int[] outOfRange = [.. Enumerable.Range(0, 18), 19];
    Assert.Throws<ArgumentException>(() => _ = new DeflateDecompressor(new MemoryStream()) { CodeLengthOrder = repeated });
    Assert.Throws<ArgumentException>(() => _ = new DeflateDecompressor(new MemoryStream()) { CodeLengthOrder = outOfRange });
  }

  [Test]
  public void Given_NoOrder_When_Asked_Then_TheRfc1951OrderIsUsed() {
    Assert.That(new DeflateDecompressor(new MemoryStream()).CodeLengthOrder.ToArray(), Is.EqualTo(DeflateConstants.CodeLengthOrder.ToArray()));
    Assert.That(new DeflateCompressor(new MemoryStream()).CodeLengthOrder.ToArray(), Is.EqualTo(DeflateConstants.CodeLengthOrder.ToArray()));
  }
}

/// <summary>
/// Third-party decoders extracting a method-3 archive our writer has just produced: The Unarchiver
/// (<c>unar</c>), and ALZip 5.52 itself under wine (see <c>alzip552-extract.sh</c>).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class AlZipObfuscatedDeflateInteropTests {

  private static string Script() => FsInteropToolbox.WinToWsl(Path.Combine(
    Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "AlZip", "alzip552-extract.sh"));

  private static readonly Lazy<bool> Shell = new(() => FsInteropToolbox.WslAvailable || !OperatingSystem.IsWindows());
  private static readonly Lazy<bool> Unar = new(() => Shell.Value && FsInteropToolbox.RunWsl("command -v unar").ExitCode == 0);
  private static readonly Lazy<bool> Alzip552 = new(() => Shell.Value && FsInteropToolbox.RunWsl($"bash {Script()} --check").ExitCode == 0);

  private static Dictionary<string, string> Expected()
    => AlZipObfuscatedDeflateTests.Inputs().ToDictionary(f => f.Name, f => Convert.ToHexStringLower(SHA256.HashData(f.Data)), StringComparer.Ordinal);

  private static Dictionary<string, string> Parse(string sha256sumOutput)
    => sha256sumOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split("  ./", 2))
      .Where(p => p.Length == 2)
      .ToDictionary(p => p[1], p => p[0], StringComparer.Ordinal);

  private static string WriteOurs() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-alz3-" + Guid.NewGuid().ToString("N") + ".alz");
    File.WriteAllBytes(path, AlZipObfuscatedDeflateTests.OursArchive());
    return path;
  }

  [Test]
  public void GivenOurMethod3Archive_WhenUnarExtractsIt_ThenEveryFileComesBack() {
    if (!Unar.Value) Assert.Ignore("unar is not installed.");
    var path = WriteOurs();
    try {
      var run = FsInteropToolbox.RunWsl(
        $"set -e; T=$(mktemp -d); unar -q -D -o $T '{FsInteropToolbox.WinToWsl(path)}' >/dev/null; cd $T; find . -type f -print0 | sort -z | xargs -0 -r sha256sum; rm -rf $T");
      Assert.That(run.ExitCode, Is.Zero, run.StdOut + run.StdErr);
      Assert.That(Parse(run.StdOut), Is.EquivalentTo(Expected()));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenOurMethod3Archive_WhenAlzip552ExtractsIt_ThenEveryFileComesBack() {
    if (!Alzip552.Value) Assert.Ignore("ALZip 5.52 is not installed in its wine prefix (see alzip552-extract.sh).");
    var path = WriteOurs();
    try {
      var run = FsInteropToolbox.RunWsl($"bash {Script()} '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(run.ExitCode, Is.Zero, run.StdOut + run.StdErr);
      Assert.That(Parse(run.StdOut), Is.EquivalentTo(Expected()));
    } finally {
      File.Delete(path);
    }
  }
}
