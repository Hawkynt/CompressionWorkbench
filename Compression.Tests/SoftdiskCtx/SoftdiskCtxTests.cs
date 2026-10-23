using System.Text;
using Compression.Registry;
using FileFormat.SoftdiskCtx;

namespace Compression.Tests.SoftdiskCtx;

[TestFixture]
public class SoftdiskCtxTests {

  private static byte[] Text(string s) => Encoding.Latin1.GetBytes(s);

  public static IEnumerable<TestCaseData> Payloads() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty");
    yield return new TestCaseData(Text("Read the article and the notes in the next issue of SDPC.\r\n")).SetName("Words");
    yield return new TestCaseData(Text("==========\r\n----\r\n" + new string('*', 150) + "\r\n")).SetName("Repeats");
    yield return new TestCaseData(Text("lone\rCR and lone\nLF\r\n")).SetName("LoneLineEnds");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()).SetName("AllBytes");
    var rnd = new byte[3000];
    new Random(4).NextBytes(rnd);
    yield return new TestCaseData(rnd).SetName("Random");
  }

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void RoundTrip(byte[] data) {
    var packed = SoftdiskCtxCodec.Encode(data, "ARTICLE.TXT");
    var decoded = SoftdiskCtxCodec.Decode(packed, out var name);
    Assert.Multiple(() => {
      Assert.That(decoded, Is.EqualTo(data));
      Assert.That(name, Is.EqualTo("ARTICLE.TXT"));
    });
  }

  [Test, Category("HappyPath")]
  public void Encode_ShrinksEnglishText() {
    var text = Text(string.Concat(Enumerable.Repeat("This is the story of an issue and the people in it.\r\n", 50)));
    Assert.That(SoftdiskCtxCodec.Encode(text).Length, Is.LessThan(text.Length * 3 / 4));
  }

  [Test, Category("Spec")]
  public void Decode_HandAssembledFile() {
    var file = new List<byte>(SoftdiskCtxCodec.Magic.ToArray()) { (byte)'X', 0 };
    for (var i = 0; i < 30; ++i) file.AddRange(i == 0 ? Text(" the ") : [(byte)'w', 0]);
    for (var i = 0; i < 127; ++i) file.AddRange(i == 0 ? Text("ab") : Text("zz"));
    file.AddRange([(byte)'A', 0, 128, 13, 255, 35, (byte)'-', 255, 1]);
    var decoded = SoftdiskCtxCodec.Decode(file.ToArray(), out var name);
    Assert.Multiple(() => {
      Assert.That(name, Is.EqualTo("X"));
      Assert.That(decoded, Is.EqualTo(Text("A the ab\r\n-----\u0001")));
    });
  }

  [Test, Category("Exception")]
  public void Decode_BadMagic_Throws()
    => Assert.Throws<InvalidDataException>(() => SoftdiskCtxCodec.Decode(Text("\u0003CT002x\0"), out _));

  [Test, Category("Exception")]
  public void Decode_TruncatedTables_Throws()
    => Assert.Throws<InvalidDataException>(() => SoftdiskCtxCodec.Decode(Text("\u0003CT001\0 the \0"), out _));

  [Test, Category("HappyPath")]
  public void Descriptor_IsRegistered() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    Assert.That(FormatRegistry.GetById("SoftdiskCtx"), Is.Not.Null);
  }
}

/// <summary>
/// Files Softdisk itself published (the telparia.com format-sample collection) and the
/// independent ctxer decompressor: our decoding must equal ctxer's for every sample, our
/// encoder's tables must equal the ones Softdisk's files carry, and ctxer must decode what
/// we write.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class SoftdiskCtxReferenceTests {
  private const string Samples = "https://telparia.com/fileFormatSamples/document/softdiskTextCompressed/";
  private const string CtxerRevision = "master";
  private const string Dir = "/tmp/cwb-ctxer";
  private static readonly string[] Names = ["102FIRE.CTX", "103CNTS.CTX", "107DISK.CTX", "110COMIC.CTX", "110STATE.CTX",
    "89CAPTN.CTX", "89ISSUES.CTX", "90CNTNTS.CTX", "91PUBCRN.CTX", "USRGUIDE.CTX", "WARRANTY.CTX"];

  private static readonly Lazy<string?> Setup = new(() => {
    if (!FsInteropToolbox.WslAvailable) return "WSL/Linux shell unavailable.";
    foreach (var tool in new[] { "gcc", "curl" })
      if (!FsInteropToolbox.WslHasTool(tool)) return $"'{tool}' is required.";
    var files = string.Join(' ', Names);
    var r = FsInteropToolbox.RunWsl(
      $"test -x {Dir}/ctx_decompress && exit 0; set -e; rm -rf {Dir}; mkdir -p {Dir}; cd {Dir}; " +
      $"curl -sSfL -o ctx_decompress.c https://git.fsfe.org/art1pirat/ctxer/raw/branch/{CtxerRevision}/src/ctx_decompress.c; " +
      "gcc -O1 -w -o ctx_decompress ctx_decompress.c; " +
      $"for f in {files}; do curl -sSfL --max-time 60 -o $f {Samples}$f; done");
    return r.ExitCode == 0 ? null : $"preparing ctxer and the samples failed (offline?):\n{r.StdOut}\n{r.StdErr}";
  });

  private static byte[] Fetch(string name, string localDir) {
    var local = Path.Combine(localDir, name);
    var r = FsInteropToolbox.RunWsl($"cp {Dir}/{name} {FsInteropToolbox.WinToWsl(local)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), r.StdErr);
    return File.ReadAllBytes(local);
  }

  [Test]
  public void SoftdiskFiles_DecodeLikeCtxer() {
    if (Setup.Value is { } error) Assert.Ignore(error);
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_ctx_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      foreach (var name in Names) {
        var packed = Fetch(name, dir);
        var reference = Path.Combine(dir, name + ".txt");
        var r = FsInteropToolbox.RunWsl($"{Dir}/ctx_decompress {Dir}/{name} > {FsInteropToolbox.WinToWsl(reference)} 2>/dev/null");
        Assert.That(r.ExitCode, Is.EqualTo(0), name);
        Assert.That(SoftdiskCtxCodec.Decode(packed, out _), Is.EqualTo(File.ReadAllBytes(reference)), name);
      }
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void EncoderTables_AreSoftdisksOwn() {
    if (Setup.Value is { } error) Assert.Ignore(error);
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_ctx_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var softdisk = Fetch("102FIRE.CTX", dir);
      var tablesStart = Array.IndexOf(softdisk, (byte)0, 6) + 1;
      var ours = SoftdiskCtxCodec.Encode([], "");
      var tablesLength = ours.Length - 7;
      Assert.That(ours.AsSpan(7).ToArray(), Is.EqualTo(softdisk.AsSpan(tablesStart, tablesLength).ToArray()));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void OurCtx_IsDecodedByCtxer() {
    if (Setup.Value is { } error) Assert.Ignore(error);
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_ctx_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      // Repeats and printable text only: ctxer reads the escape-literal form differently.
      var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
        "Read the notes in the SDPC issue and on the disk.\r\n" + new string('=', 40) + "\r\n", 20)));
      var packed = Path.Combine(dir, "ours.ctx");
      var output = Path.Combine(dir, "ours.txt");
      File.WriteAllBytes(packed, SoftdiskCtxCodec.Encode(text, "OURS.TXT"));
      var r = FsInteropToolbox.RunWsl($"{Dir}/ctx_decompress {FsInteropToolbox.WinToWsl(packed)} > {FsInteropToolbox.WinToWsl(output)} 2>/dev/null");
      Assert.That(r.ExitCode, Is.EqualTo(0));
      Assert.That(File.ReadAllBytes(output), Is.EqualTo(text));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
