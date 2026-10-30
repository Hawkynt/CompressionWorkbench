using System.Buffers.Binary;
using System.Diagnostics;
using Compression.Registry;
using FileFormat.Pzx;

namespace Compression.Tests.Pzx;

[TestFixture]
public class PzxTests {
  /// <summary>A two-block ZX TAP: a "TESTFILE" code header and a 5-byte "HELLO" data block.</summary>
  internal static byte[] SampleTap() {
    static byte[] Block(byte[] b) {
      byte x = 0;
      foreach (var c in b) x ^= c;
      var withSum = b.Append(x).ToArray();
      var r = new byte[2 + withSum.Length];
      BinaryPrimitives.WriteUInt16LittleEndian(r, (ushort)withSum.Length);
      withSum.CopyTo(r, 2);
      return r;
    }
    var header = new byte[18];
    header[0] = 0; header[1] = 3;
    "TESTFILE  "u8.CopyTo(header.AsSpan(2));
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 5);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 32768);
    return [.. Block(header), .. Block([0xFF, .. "HELLO"u8])];
  }

  /// <summary>What pzxtools' tap2pzx (raxoft/pzxtools @7bc8ff69, bin/tap2pzx.exe) writes for <see cref="SampleTap"/>.</summary>
  internal const string Tap2PzxOutput =
    "505a585402000000010050554c53080000007f9f78089b02df02444154412300000098000080b103020257035703ae06" +
    "ae0600035445535446494c4520200500008000009650554c5308000000978c78089b02df024441544117000000380000" +
    "80b103020257035703ae06ae06ff48454c4c4fbd";

  private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

  [Test]
  public void GivenTheSampleTap_WhenWritten_ThenTheBytesAreWhatTap2PzxWrites() {
    using var ms = new MemoryStream();
    PzxTape.WriteStandard(ms, PzxTape.SplitTap(SampleTap()));
    Assert.That(Convert.ToHexStringLower(ms.ToArray()), Is.EqualTo(Convert.ToHexStringLower(Hex(Tap2PzxOutput))));
  }

  [Test]
  public void GivenTap2PzxOutput_WhenRead_ThenTheTapComesBackAndBlocksAreListed() {
    var blocks = PzxTape.Read(Hex(Tap2PzxOutput));
    Assert.That(blocks.Select(b => b.Tag), Is.EqualTo(new[] { "PZXT", "PULS", "DATA", "PULS", "DATA" }));
    var names = new PzxFormatDescriptor().List(new MemoryStream(Hex(Tap2PzxOutput)), null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("tape.tap").And.Contain("metadata.ini").And.Contain("blocks/002_DATA.bin"));
    var dir = Path.Combine(Path.GetTempPath(), "cwb-pzx-" + Guid.NewGuid().ToString("N"));
    try {
      new PzxFormatDescriptor().Extract(new MemoryStream(Hex(Tap2PzxOutput)), dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "tape.tap")), Is.EqualTo(SampleTap()));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void GivenAHeaderWithTitleAndInfo_WhenParsed_ThenKeysAndValuesAreRead() {
    byte[] data = [1, 0, .. "My Tape"u8, 0, .. "Author"u8, 0, .. "Rak"u8, 0, .. "Year"u8];
    var (major, minor, title, info) = PzxTape.Header(new PzxBlock("PZXT", data));
    Assert.That((major, minor, title), Is.EqualTo((1, 0, "My Tape")));
    Assert.That(info, Is.EqualTo(new[] { ("Author", "Rak"), ("Year", "") }));
  }

  [Test]
  public void GivenANonStandardDataBlock_WhenChecked_ThenItIsNotTreatedAsTap() {
    var d = new byte[17];
    BinaryPrimitives.WriteUInt32LittleEndian(d, 0x80000008);
    d[6] = 2; d[7] = 2;
    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), 500);   // turbo loader timing
    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(10), 500);
    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(12), 1000);
    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(14), 1000);
    Assert.That(PzxTape.StandardData(new PzxBlock("DATA", d)), Is.Null);
  }

  [Test]
  public void GivenALoneRawBlock_WhenCreated_ThenItIsOneStandardDataBlock() {
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, [0xFF, 1, 2, 3]);
      using var ms = new MemoryStream();
      new PzxFormatDescriptor().Create(ms, [new ArchiveInputInfo(path, "PROBE.BIN", false)], new FormatCreateOptions());
      var blocks = PzxTape.Read(ms.ToArray());
      Assert.That(PzxTape.StandardData(blocks[^1]), Is.EqualTo(new byte[] { 0xFF, 1, 2, 3 }));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenNoHeaderBlock_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => PzxTape.Read("PULS\0\0\0\0"u8));

  [Test]
  public void GivenAnUnknownMajorVersion_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => PzxTape.Read([.. "PZXT"u8, 2, 0, 0, 0, 2, 0]));

  [Test]
  public void GivenATruncatedBlock_WhenRead_ThenInvalidDataAndListFallsBack() {
    var pzx = Hex(Tap2PzxOutput)[..^3];
    Assert.Throws<InvalidDataException>(() => PzxTape.Read(pzx));
    Assert.That(new PzxFormatDescriptor().List(new MemoryStream(pzx), null).Single().Name, Is.EqualTo("FULL.pzx"));
  }

  [Test]
  public void GivenABrokenTap_WhenSplit_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => PzxTape.SplitTap([10, 0, 1, 2]));
}

/// <summary>
/// pzxtools (Patrik Rak, MIT) as the oracle, both ways: tap2pzx writes byte-identical PZX for the
/// TAP files we encode, and pzx2txt lists our tapes with the data bodies we wrote; tapes tap2pzx
/// writes come back as the original TAP from our reader. The tools come from <c>CWB_PZXTOOLS</c>
/// (a directory holding tap2pzx and pzx2txt); skipped when unset unless <c>CWB_REQUIRE_PZXTOOLS=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class PzxExternalConformanceTests {
  private static string RequireTools() {
    var dir = Environment.GetEnvironmentVariable("CWB_PZXTOOLS");
    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
    const string hint = "pzxtools not configured: set CWB_PZXTOOLS to a directory with tap2pzx and pzx2txt (https://github.com/raxoft/pzxtools).";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_PZXTOOLS") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  private static (string Out, int Exit) Run(string dir, string tool, params string[] args) {
    var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
    var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    TestContext.Out.WriteLine($"{tool} {string.Join(' ', args)} -> {p.ExitCode}\n{output}");
    return (output, p.ExitCode);
  }

  private static byte[] RandomTap(int seed) {
    var random = new Random(seed);
    var blocks = new List<byte[]>();
    for (var i = 0; i < 6; ++i) {
      var b = new byte[random.Next(1, 3000)];
      random.NextBytes(b);
      b[0] = (byte)(i % 2 == 0 ? 0x00 : 0xFF);
      blocks.Add(b);
    }
    return PzxTape.JoinTap(blocks);
  }

  [TestCase(1)]
  [TestCase(2)]
  public void GivenATap_WhenBothWeAndTap2PzxEncodeIt_ThenTheBytesAreIdentical(int seed) {
    var dir = RequireTools();
    var work = Path.Combine(Path.GetTempPath(), "cwb-pzxx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try {
      var tap = RandomTap(seed);
      var tapPath = Path.Combine(work, "in.tap");
      File.WriteAllBytes(tapPath, tap);
      var theirs = Path.Combine(work, "theirs.pzx");
      Assert.That(Run(dir, "tap2pzx", "-o", theirs, tapPath).Exit, Is.EqualTo(0));
      using var ours = new MemoryStream();
      PzxTape.WriteStandard(ours, PzxTape.SplitTap(tap));
      Assert.That(ours.ToArray(), Is.EqualTo(File.ReadAllBytes(theirs)));

      var back = PzxTape.Read(File.ReadAllBytes(theirs)).Select(PzxTape.StandardData).Where(d => d != null).Select(d => d!).ToList();
      Assert.That(PzxTape.JoinTap(back), Is.EqualTo(tap));
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void GivenOurPzx_WhenPzx2TxtDumpsIt_ThenEveryBodyIsOurBlock() {
    var dir = RequireTools();
    var work = Path.Combine(Path.GetTempPath(), "cwb-pzxx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try {
      var blocks = PzxTape.SplitTap(RandomTap(3));
      var ours = Path.Combine(work, "ours.pzx");
      using (var fs = File.Create(ours)) PzxTape.WriteStandard(fs, blocks, "CWB oracle");
      var (txt, exit) = Run(dir, "pzx2txt", ours);
      Assert.That(exit, Is.EqualTo(0));
      Assert.That(txt, Does.StartWith("PZX 1.0"));
      // pzx2txt prints each body as hex, possibly over several BODY lines per block.
      var bodies = string.Concat(txt.Split('\n').Where(l => l.StartsWith("BODY ", StringComparison.Ordinal)).Select(l => l[5..].Trim()));
      Assert.That(bodies, Is.EqualTo(string.Concat(blocks.Select(Convert.ToHexString))));
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }
}
