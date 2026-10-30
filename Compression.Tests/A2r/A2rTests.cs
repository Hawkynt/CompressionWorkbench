using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Compression.Registry;
using FileFormat.A2r;

namespace Compression.Tests.A2r;

[TestFixture]
public class A2rTests {
  internal static byte[] Chunk(string id, byte[] body) {
    var c = new byte[8 + body.Length];
    Encoding.ASCII.GetBytes(id, c);
    BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(4), (uint)body.Length);
    body.CopyTo(c, 8);
    return c;
  }

  internal static byte[] File3(params byte[][] chunks)
    => [.. "A2R3"u8, 0xFF, 0x0A, 0x0D, 0x0A, .. chunks.SelectMany(c => c)];

  internal static byte[] Rwcp(uint ps, params byte[][] entries)
    => [.. A2rImage.CaptureHeader(1, ps), .. entries.SelectMany(e => (byte[])[(byte)'C', .. e]), (byte)'X'];

  private static byte[] Sample3() => File3(
    Chunk("INFO", A2rImage.Info(3, "CWB test", 5, false, true)),
    Chunk("RWCP", Rwcp(62_500,
      A2rImage.RwcpEntry(3, 0, [300, 600], A2rImage.EncodeFlux([64, 128, 700, 64])),
      A2rImage.RwcpEntry(1, 1, [300], A2rImage.EncodeFlux([96, 96])))),
    Chunk("SLVD", [.. A2rImage.CaptureHeader(2, 62_500), (byte)'X']),
    Chunk("META", "title\tFlux test\n"u8.ToArray()));

  private static List<(string Name, byte[] Data)> Members(byte[] a2r) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-a2r-" + Guid.NewGuid().ToString("N"));
    try {
      new A2rFormatDescriptor().Extract(new MemoryStream(a2r), dir, null, null);
      return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .Select(p => (Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllBytes(p))).OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  internal static byte[] CreateFrom(IEnumerable<(string Name, byte[] Data)> members) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-a2rc-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var inputs = members.Select((m, i) => {
        var p = Path.Combine(dir, i.ToString());
        File.WriteAllBytes(p, m.Data);
        return new ArchiveInputInfo(p, m.Name, false);
      }).ToList();
      using var ms = new MemoryStream();
      new A2rFormatDescriptor().Create(ms, inputs, new FormatCreateOptions());
      return ms.ToArray();
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenAnA2r3_WhenListed_ThenChunksAndCapturesAppearInFileOrder() {
    var names = Members(Sample3()).Select(m => m.Name).ToList();
    Assert.That(names, Is.EqualTo(new[] {
      "chunks/00_INFO.bin", "chunks/01_RWCP/0000_loc00000_xtiming.bin", "chunks/01_RWCP/0001_loc00001_timing.bin",
      "chunks/01_RWCP/header.bin", "chunks/02_SLVD/header.bin", "chunks/03_META.bin", "metadata.ini",
    }));
    var ini = Encoding.UTF8.GetString(Members(Sample3()).Single(m => m.Name == "metadata.ini").Data);
    Assert.That(ini, Does.Contain("version=3").And.Contain("creator=CWB test").And.Contain("drive_type=5")
      .And.Contain("synchronized=1").And.Contain("chunk_01_RWCP_entries=2").And.Contain("chunk_01_RWCP_ps_per_tick=62500"));
  }

  [Test]
  public void GivenItsOwnMembers_WhenCreated_ThenTheFileIsByteIdentical() {
    var a2r = Sample3();
    Assert.That(CreateFrom(Members(a2r)), Is.EqualTo(a2r));
  }

  [Test]
  public void GivenFluxBeyond255Ticks_WhenPacked_ThenRunsOf255CarryIt() {
    var packed = A2rImage.EncodeFlux([64, 520, 255, 254]);
    Assert.That(packed, Is.EqualTo(new byte[] { 64, 255, 255, 10, 255, 0, 254 }));
    Assert.That(A2rImage.DecodeFlux(packed), Is.EqualTo(new uint[] { 64, 520, 255, 254 }));
  }

  [Test]
  public void GivenTheDescriptor_WhenACaptureIsReplacedAndAnotherRemoved_ThenTheImageFollows() {
    var ms = new MemoryStream();
    ms.Write(Sample3());
    var d = new A2rFormatDescriptor();
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, A2rImage.RwcpEntry(3, 0, [10, 20], A2rImage.EncodeFlux([5, 5, 5, 5])));
      d.Add(ms, [new ArchiveInputInfo(path, "chunks/01_RWCP/0000_loc00000_xtiming.bin", false)]);
      d.Remove(ms, ["chunks/01_RWCP/0001_loc00001_timing.bin", "chunks/03_META.bin"]);
    } finally {
      File.Delete(path);
    }
    var image = A2rImage.Read(ms.ToArray());
    Assert.That(image.Chunks.Select(c => c.Id), Is.EqualTo(new[] { "INFO", "RWCP", "SLVD" }));
    var entry = image.Chunks[1].Entries!.Single();
    Assert.That(A2rImage.DecodeFlux(entry.AsSpan(4 + 8 + 4)), Is.EqualTo(new uint[] { 5, 5, 5, 5 }));
    Assert.Throws<NotSupportedException>(() => d.Remove(ms, ["chunks/00_INFO.bin"]));
  }

  [Test]
  public void GivenAnA2r2_WhenRebuilt_ThenStrmEntriesAndTheFfEndSurvive() {
    var strm = (byte[])[.. A2rImage.StrmEntry(1, 4, 51_200, A2rImage.EncodeFlux([32, 64])), .. A2rImage.StrmEntry(3, 8, 51_000, [32]), 0xFF];
    byte[] a2r = [.. "A2R2"u8, 0xFF, 0x0A, 0x0D, 0x0A, .. Chunk("INFO", A2rImage.Info(2, "CWB", 1, true, false)), .. Chunk("STRM", strm)];
    var members = Members(a2r);
    Assert.That(members.Select(m => m.Name), Is.EqualTo(new[] {
      "chunks/00_INFO.bin", "chunks/01_STRM/0000_loc00004_timing.bin", "chunks/01_STRM/0001_loc00008_xtiming.bin", "metadata.ini",
    }));
    Assert.That(CreateFrom(members), Is.EqualTo(a2r));
  }

  [Test]
  public void GivenAnArbitraryFile_WhenOffered_ThenItIsDeclined() {
    var d = new A2rFormatDescriptor();
    Assert.That(d.CanAccept(new ArchiveInputInfo("PROBE.BIN", "PROBE.BIN", false), out _), Is.False);
    Assert.That(d.CanAccept(new ArchiveInputInfo("x", "chunks/04_RWCP/0007_loc00002_xtiming.bin", false), out _), Is.True);
  }

  [Test]
  public void GivenACaptureWithTrailingBytes_WhenAdded_ThenRejected() {
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, [.. A2rImage.RwcpEntry(3, 0, [], [1, 2]), 9]);
      var ms = new MemoryStream(Sample3().ToArray());
      Assert.Throws<ArgumentException>(() => new A2rFormatDescriptor().Add(ms, [new ArchiveInputInfo(path, "chunks/01_RWCP/0005_x.bin", false)]));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenAMissingEndMark_WhenRead_ThenInvalidData() {
    var body = Rwcp(62_500, A2rImage.RwcpEntry(3, 0, [], [1]))[..^1];
    Assert.Throws<InvalidDataException>(() => A2rImage.Read(File3(Chunk("RWCP", body))));
  }

  [Test]
  public void GivenAnUnknownCaptureType_WhenRead_ThenInvalidData() {
    var entry = A2rImage.RwcpEntry(3, 0, [], [1]);
    entry[0] = 7;
    Assert.Throws<InvalidDataException>(() => A2rImage.Read(File3(Chunk("RWCP", Rwcp(62_500, entry)))));
  }

  [Test]
  public void GivenATruncatedChunk_WhenListed_ThenItFallsBackToTheWholeFile() {
    var a2r = Sample3()[..^3];
    Assert.Throws<InvalidDataException>(() => A2rImage.Read(a2r));
    Assert.That(new A2rFormatDescriptor().List(new MemoryStream(a2r), null).Single().Name, Is.EqualTo("FULL.a2r"));
  }

  [TestCase("A2R1")]
  [TestCase("A2R4")]
  [TestCase("WOZ2")]
  public void GivenAnotherSignature_WhenRead_ThenInvalidData(string magic) {
    byte[] data = [.. Encoding.ASCII.GetBytes(magic), 0xFF, 0x0A, 0x0D, 0x0A];
    Assert.Throws<InvalidDataException>(() => A2rImage.Read(data));
  }

  [Test]
  public void GivenAnUnknownChunk_WhenRebuilt_ThenItIsKeptVerbatim() {
    var a2r = File3(Chunk("INFO", A2rImage.Info(3, "x", 3, false, false)), Chunk("ZZZ!", [1, 2, 3]));
    var members = Members(a2r);
    Assert.That(members.Select(m => m.Name), Does.Contain("chunks/01_x5A5A5A21.bin"));
    Assert.That(CreateFrom(members), Is.EqualTo(a2r));
  }
}

/// <summary>
/// Third-party readers as the oracle, one per version. A2R3: Greaseweazle (Keir Fraser, public
/// domain) reads RWCP captures; it writes a sector image to SCP, we carry the SCP's flux into an A2R3
/// we create, and Greaseweazle decodes our A2R back to the identical sector image, also after one
/// track was replaced. A2R2: a2rchery (4am, MIT) verifies our file and, rewriting it from its own
/// parse, reproduces it byte for byte. Tools come from <c>CWB_GW</c> and <c>CWB_A2RCHERY</c>
/// (the script); skipped when unset unless <c>CWB_REQUIRE_A2R=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class A2rExternalConformanceTests {
  private static string Require(string variable, string what) {
    var tool = Environment.GetEnvironmentVariable(variable);
    if (!string.IsNullOrEmpty(tool)) return tool;
    var hint = $"{what} not configured: set {variable}.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_A2R") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  private static (int Code, string Out) Run(string exe, params string[] args) {
    var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    // Both tools may write to either pipe: drain them concurrently so neither fills up.
    var stderr = p.StandardError.ReadToEndAsync();
    var stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    TestContext.Out.WriteLine($"{exe} {string.Join(' ', args)} -> {p.ExitCode}\n{stdout}{stderr.Result}");
    return (p.ExitCode, stdout + stderr.Result);
  }

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-a2rx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static byte[] Disk(int seed) {
    var d = new byte[737280];
    new Random(seed).NextBytes(d);
    return d;
  }

  /// <summary>Greaseweazle's SCP of a 720K sector image, as per-track (revolution durations, flux) at 25 ns ticks.</summary>
  private Dictionary<int, (uint[] Revs, List<uint> Flux)> GwFlux(string gw, byte[] disk, string name) {
    var img = Path.Combine(this._tmp, name + ".img");
    var scp = Path.Combine(this._tmp, name + ".scp");
    File.WriteAllBytes(img, disk);
    Assert.That(Run(gw, "convert", "--format", "ibm.720", img, scp).Code, Is.EqualTo(0));
    var s = File.ReadAllBytes(scp);
    Assert.That(s[11], Is.Zero, "Greaseweazle writes 25 ns SCP resolution");
    var revs = s[5];
    var tracks = new Dictionary<int, (uint[], List<uint>)>();
    for (var t = 0; t < 168; ++t) {
      var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(16 + 4 * t));
      if (off == 0) continue;
      var durations = new uint[revs];
      var flux = new List<uint>();
      for (var r = 0; r < revs; ++r) {
        var h = off + 4 + 12 * r;
        durations[r] = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(h));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(h + 4));
        var at = off + (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(h + 8));
        var acc = 0u;
        for (var i = 0; i < count; ++i) {
          var v = BinaryPrimitives.ReadUInt16BigEndian(s.AsSpan(at + 2 * i));
          if (v == 0) { acc += 65536; continue; }
          flux.Add(acc + v);
          acc = 0;
        }
      }
      tracks[t] = (durations, flux);
    }
    return tracks;
  }

  private static byte[] Capture(int location, (uint[] Revs, List<uint> Flux) track) {
    var indices = new List<uint>();
    var at = 0u;
    foreach (var r in track.Revs) indices.Add(at += r);
    return A2rImage.RwcpEntry(3, location, indices, A2rImage.EncodeFlux(track.Flux));
  }

  private string OurA2r(Dictionary<int, (uint[] Revs, List<uint> Flux)> tracks, string name) {
    var members = new List<(string, byte[])> {
      ("chunks/00_INFO.bin", A2rImage.Info(3, "CompressionWorkbench", 5, false, false)),
      ("chunks/01_RWCP/header.bin", A2rImage.CaptureHeader(1, 25_000)),
      ("chunks/02_META.bin", "title\tGreaseweazle round trip\n"u8.ToArray()),
    };
    foreach (var (t, track) in tracks) members.Add(($"chunks/01_RWCP/{t:D4}.bin", Capture(t, track)));
    var path = Path.Combine(this._tmp, name + ".a2r");
    File.WriteAllBytes(path, A2rTests.CreateFrom(members));
    return path;
  }

  private byte[] GwImg(string gw, string a2r) {
    var img = Path.Combine(this._tmp, Path.GetFileNameWithoutExtension(a2r) + "-back.img");
    Assert.That(Run(gw, "convert", "--format", "ibm.720", a2r, img).Code, Is.EqualTo(0));
    return File.ReadAllBytes(img);
  }

  [Test]
  public void GivenFluxCarriedIntoOurA2r3_WhenGreaseweazleDecodes_ThenTheSectorsAreTheOriginals() {
    var gw = Require("CWB_GW", "Greaseweazle (https://github.com/keirf/greaseweazle)");
    var disk = Disk(11);
    var ours = this.OurA2r(this.GwFlux(gw, disk, "disk"), "ours");
    Assert.That(this.GwImg(gw, ours), Is.EqualTo(disk));
  }

  [Test]
  public void GivenOneCaptureReplacedFromAnotherDisk_WhenGreaseweazleDecodes_ThenExactlyThatTrackChanged() {
    var gw = Require("CWB_GW", "Greaseweazle (https://github.com/keirf/greaseweazle)");
    var a = Disk(12);
    var b = Disk(13);
    var ours = this.OurA2r(this.GwFlux(gw, a, "a"), "edited");
    var fluxB = this.GwFlux(gw, b, "b");

    // Captures are numbered by SCP track in OurA2r, and the listing numbers them in file order:
    // track 5 (cylinder 2, head 1) is the sixth capture.
    List<string> listed;
    using (var read = File.OpenRead(ours))
      listed = new A2rFormatDescriptor().List(read, null).Select(e => e.Name).ToList();
    var name = listed.Single(n => n.StartsWith("chunks/01_RWCP/0005_", StringComparison.Ordinal));
    Assert.That(name, Is.EqualTo("chunks/01_RWCP/0005_loc00005_xtiming.bin"));
    var part = Path.Combine(this._tmp, "track5.bin");
    File.WriteAllBytes(part, Capture(5, fluxB[5]));
    using (var fs = File.Open(ours, FileMode.Open, FileAccess.ReadWrite))
      new A2rFormatDescriptor().Add(fs, [new ArchiveInputInfo(part, name, false)]);

    var expected = (byte[])a.Clone();
    Array.Copy(b, 5 * 9 * 512, expected, 5 * 9 * 512, 9 * 512);
    Assert.That(this.GwImg(gw, ours), Is.EqualTo(expected));
  }

  [Test]
  public void GivenOurA2r2_WhenA2rcheryVerifiesAndRewritesIt_ThenItAgreesByteForByte() {
    var script = Require("CWB_A2RCHERY", "a2rchery (https://github.com/a2-4am/a2rchery, the a2rchery.py script)");
    var python = Environment.GetEnvironmentVariable("CWB_PYTHON") is { Length: > 0 } py ? py : "python3";
    var rng = new Random(5);
    var members = new List<(string, byte[])> {
      ("metadata.ini", "version=2\n"u8.ToArray()),
      ("chunks/00_INFO.bin", A2rImage.Info(2, "CompressionWorkbench", 1, false, true)),
      ("chunks/02_META.bin", "title\tFlux test\nlanguage\tEnglish\nrequires_machine\t2e|2c\n"u8.ToArray()),
    };
    for (var q = 0; q < 3; ++q) {
      var flux = Enumerable.Range(0, 400).Select(_ => (uint)rng.Next(28, 300)).ToList();
      members.Add(($"chunks/01_STRM/{q:D4}.bin", A2rImage.StrmEntry(q == 2 ? 3 : 1, q * 4, (uint)flux.Take(300).Sum(f => f), A2rImage.EncodeFlux(flux))));
    }
    var ours = Path.Combine(this._tmp, "ours.a2r");
    var bytes = A2rTests.CreateFrom(members);
    File.WriteAllBytes(ours, bytes);

    var verify = Run(python, script, "verify", ours);
    Assert.That(verify.Code, Is.EqualTo(0));
    var dump = Run(python, script, "dump", ours).Out;
    Assert.That(dump, Does.Contain("Track 1.00").And.Contain("xtiming capture").And.Contain("Flux test").And.Contain("5.25-inch").And.Not.Contain("Error"));

    // a2rchery's edit command re-emits INFO, STRM and META from its own parse.
    Assert.That(Run(python, script, "edit", "-m", "notes:", ours).Code, Is.EqualTo(0));
    Assert.That(File.ReadAllBytes(ours), Is.EqualTo(bytes));
  }
}
