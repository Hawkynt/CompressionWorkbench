using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.C64Tap;

namespace Compression.Tests.C64Tap;

[TestFixture]
public class C64TapTests {
  internal static byte[] Tap(byte version, byte[] pulses) {
    using var ms = new MemoryStream();
    C64TapFormatDescriptor.Write(ms, new C64TapFormatDescriptor.Header(false, version, 0, 0), pulses);
    return ms.ToArray();
  }

  /// <summary>Short pulses around long version-1 pulses (0 + 24-bit cycle count).</summary>
  internal static byte[] LongPulses(int longCount, int cycles) {
    var list = new List<byte>();
    for (var i = 0; i < longCount; ++i) {
      for (var k = 0; k < 50; ++k) list.Add(0x30);
      list.Add(0);
      list.Add((byte)cycles); list.Add((byte)(cycles >> 8)); list.Add((byte)(cycles >> 16));
    }
    return [.. list];
  }

  [Test]
  public void GivenAHeader_WhenWritten_ThenTheLayoutIsTheStandardOne() {
    var tap = Tap(1, [1, 2, 3]);
    Assert.That(tap.AsSpan(0, 12).SequenceEqual("C64-TAPE-RAW"u8), Is.True);
    Assert.That(tap[12], Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(tap.AsSpan(16)), Is.EqualTo(3));
    Assert.That(tap.Length, Is.EqualTo(23));
  }

  [Test]
  public void GivenVersion1LongPulses_WhenDecoded_ThenTheCycleCountsAreUsed() {
    var (h, p) = C64TapFormatDescriptor.Read(Tap(1, [0x30, 0, 0xA0, 0x86, 0x01, 0x40]));
    Assert.That(C64TapFormatDescriptor.Durations(h, p), Is.EqualTo(new long[] { 384, 100000, 512 }));
  }

  [Test]
  public void GivenVersion0_WhenAZeroAppears_ThenItIsAnOverflowNotACount() {
    var (h, p) = C64TapFormatDescriptor.Read(Tap(0, [0x30, 0, 0xA0]));
    Assert.That(C64TapFormatDescriptor.Durations(h, p), Is.EqualTo(new long[] { 384, 2048, 1280 }));
  }

  [Test]
  public void GivenTheDescriptor_WhenCreatedEditedAndExtracted_ThenPulsesAndHeaderRoundTrip() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-c64tap-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var pulsesPath = Path.Combine(dir, "p");
      File.WriteAllBytes(pulsesPath, [0x30, 0x31, 0x32]);
      var iniPath = Path.Combine(dir, "i");
      File.WriteAllText(iniPath, "version=0\nplatform=1\nvideo=1\n");
      var d = new C64TapFormatDescriptor();
      using var ms = new MemoryStream();
      d.Create(ms, [new ArchiveInputInfo(pulsesPath, "PROBE.BIN", false), new ArchiveInputInfo(iniPath, "metadata.ini", false)], new FormatCreateOptions());
      Assert.That(ms.ToArray()[12..15], Is.EqualTo(new byte[] { 0, 1, 1 }));
      File.WriteAllBytes(pulsesPath, [0x40]);
      d.Add(ms, [new ArchiveInputInfo(pulsesPath, "pulses.bin", false)]);
      var outDir = Path.Combine(dir, "out");
      d.Extract(new MemoryStream(ms.ToArray()), outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "pulses.bin")), Is.EqualTo(new byte[] { 0x40 }));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "metadata.ini")), Does.Contain("platform=1 (VIC-20)"));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [TestCase(new byte[] { 0x43, 0x36, 0x34 })]                 // too short
  public void GivenTooShortInput_WhenRead_ThenInvalidData(byte[] data)
    => Assert.Throws<InvalidDataException>(() => C64TapFormatDescriptor.Read(data));

  [Test]
  public void GivenAnUnknownVersion_WhenRead_ThenInvalidData() {
    var tap = Tap(1, [1]);
    tap[12] = 7;
    Assert.Throws<InvalidDataException>(() => C64TapFormatDescriptor.Read(tap));
  }

  [Test]
  public void GivenADataSizeBeyondTheFile_WhenRead_ThenInvalidDataAndListFallsBack() {
    var tap = Tap(1, [1, 2, 3])[..^1];
    Assert.Throws<InvalidDataException>(() => C64TapFormatDescriptor.Read(tap));
    Assert.That(new C64TapFormatDescriptor().List(new MemoryStream(tap), null).Single().Name, Is.EqualTo("FULL.tap"));
  }
}

/// <summary>
/// MAME castool (mame-tools, "cbm" format) as the oracle for the pulse semantics: it renders our tapes
/// to audio, and the audio has to last as long as our decoding of the pulse stream says — which only
/// holds if version 1's 24-bit long pulses and version 0's overflow byte mean the same to both of us.
/// Skipped when castool is absent unless <c>CWB_REQUIRE_FLOPTOOL=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class C64TapExternalConformanceTests {
  private const double PalClock = 985248;

  [TestCase((byte)1)]
  [TestCase((byte)0)]
  public void GivenOurTap_WhenCastoolRendersIt_ThenTheAudioLastsAsLongAsOurPulses(byte version) {
    if (!(FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("castool"))) {
      const string hint = "castool not installed: `sudo apt-get install -y mame-tools`.";
      if (Environment.GetEnvironmentVariable("CWB_REQUIRE_FLOPTOOL") == "1") Assert.Fail(hint);
      Assert.Ignore(hint);
    }
    var dir = Path.Combine(Path.GetTempPath(), "cwb-c64tapx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var tapBytes = C64TapTests.Tap(version, C64TapTests.LongPulses(40, 100_000));
      var tap = Path.Combine(dir, "ours.tap");
      File.WriteAllBytes(tap, tapBytes);
      var wav = Path.Combine(dir, "ours.wav");
      var r = FsInteropToolbox.RunWsl($"castool convert cbm {FsInteropToolbox.WinToWsl(tap)} {FsInteropToolbox.WinToWsl(wav)}");
      TestContext.Out.WriteLine($"{r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
      Assert.That(File.Exists(wav), Is.True, "castool wrote no audio");

      var w = File.ReadAllBytes(wav);
      var rate = BinaryPrimitives.ReadInt32LittleEndian(w.AsSpan(24));
      var byteRate = BinaryPrimitives.ReadInt32LittleEndian(w.AsSpan(28));
      var dataIndex = w.AsSpan().IndexOf("data"u8);
      var dataSize = BinaryPrimitives.ReadInt32LittleEndian(w.AsSpan(dataIndex + 4));
      var seconds = dataSize / (double)byteRate;

      var (h, p) = C64TapFormatDescriptor.Read(tapBytes);
      var expected = C64TapFormatDescriptor.Durations(h, p).Sum() / PalClock;
      TestContext.Out.WriteLine($"rate {rate}, audio {seconds:F3} s, pulses {expected:F3} s");
      Assert.That(seconds, Is.EqualTo(expected).Within(0.05 * expected + 0.2));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
