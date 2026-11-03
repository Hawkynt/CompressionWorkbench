using FileFormat.Kwaj;

namespace Compression.Tests.Kwaj;

/// <summary>
/// libmspack is the reference KWAJ decoder (cabextract's library; method 3 written by
/// Jeff Johnson). Its <c>msexpand</c> example is built from a pinned revision with gcc
/// and must turn every KWAJ file we write back into the original bytes.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class KwajLibmspackInteropTests {
  private const string Revision = "55d501976171397ccd5d5a7a1ca7da065b1d9a06";
  private const string Tool = $"/tmp/cwb-libmspack-{Revision}/msexpand";
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_kwaj_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static readonly Lazy<string?> BuildError = new(() => {
    if (!FsInteropToolbox.WslAvailable) return "WSL/Linux shell unavailable.";
    foreach (var tool in new[] { "gcc", "curl", "tar" })
      if (!FsInteropToolbox.WslHasTool(tool)) return $"'{tool}' is required to build libmspack's msexpand.";
    var dir = $"/tmp/cwb-libmspack-{Revision}";
    var r = FsInteropToolbox.RunWsl(
      $"test -x {Tool} && exit 0; set -e; rm -rf {dir}; mkdir -p {dir}; cd {dir}; " +
      $"curl -sSL https://github.com/kyz/libmspack/archive/{Revision}.tar.gz | tar xz --strip-components=2 libmspack-{Revision}/libmspack; " +
      "gcc -O1 -w -I mspack -I test -o msexpand examples/msexpand.c $(ls mspack/*.c | grep -v debug.c)");
    return r.ExitCode == 0 ? null : $"building msexpand failed:\n{r.StdOut}\n{r.StdErr}";
  });

  private static void RequireMsexpand() {
    if (BuildError.Value is { } error)
      Assert.Ignore(error);
  }

  private static byte[] Sample(string kind) => kind switch {
    "text" => [.. Enumerable.Range(0, 30_000).Select(i => (byte)"The quick brown fox jumps over the lazy dog. 0123456789\n"[i % 56])],
    "random" => RandomBytes(5_000),
    "tiny" => "x"u8.ToArray(),
    "empty" => [],
    "zeros" => new byte[10_000],
    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
  };

  private static byte[] RandomBytes(int n) {
    var b = new byte[n];
    new Random(5).NextBytes(b);
    return b;
  }

  [Test]
  public void OurKwaj_IsExpandedByLibmspack(
    [Values(KwajConstants.MethodStore, KwajConstants.MethodXor, KwajConstants.MethodLzss, KwajConstants.MethodLzHuffman, KwajConstants.MethodMsZip)] int method,
    [Values("text", "random", "tiny", "empty", "zeros")] string kind) {
    RequireMsexpand();
    var data = Sample(kind);
    var input = Path.Combine(this._tmpDir, "in.kwj");
    var output = Path.Combine(this._tmpDir, "out.bin");
    using (var fs = File.Create(input))
      KwajStream.Compress(new MemoryStream(data), fs, method, "in.bin");

    var r = FsInteropToolbox.RunWsl($"{Tool} {FsInteropToolbox.WinToWsl(input)} {FsInteropToolbox.WinToWsl(output)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"msexpand rejected method {method}:\n{r.StdOut}\n{r.StdErr}");
    Assert.That(File.ReadAllBytes(output), Is.EqualTo(data));
  }

  [Test]
  public void OurKwajLzh_EveryShortLength_IsExpandedByLibmspack() {
    RequireMsexpand();
    // One shell run over 200 files: every tail alignment of the end-marker-less stream.
    var source = RandomBytes(200);
    for (var n = 0; n < 200; ++n)
      using (var fs = File.Create(Path.Combine(this._tmpDir, $"f{n}.kwj")))
        KwajStream.Compress(new MemoryStream(source[..n]), fs, KwajConstants.MethodLzHuffman);

    var dir = FsInteropToolbox.WinToWsl(this._tmpDir);
    var r = FsInteropToolbox.RunWsl($"cd {dir} && for n in $(seq 0 199); do {Tool} f$n.kwj f$n.out || echo FAIL $n; done");
    Assert.That(r.StdOut, Does.Not.Contain("FAIL"), r.StdErr);
    for (var n = 0; n < 200; ++n)
      Assert.That(File.ReadAllBytes(Path.Combine(this._tmpDir, $"f{n}.out")), Is.EqualTo(source[..n]), $"length {n}");
  }
}
