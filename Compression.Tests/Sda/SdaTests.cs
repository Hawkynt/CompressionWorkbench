using System.Text;
using Compression.Registry;
using FileFormat.Sda;

namespace Compression.Tests.Sda;

/// <summary>
/// C64 ARC / SDA. Real SDA files come from 1980s–90s C64 software of unclear licence, so they are not
/// committed: the comparison with cbmconvert (the oracle; GPL, used as a black box) runs on a directory
/// named by <c>CWB_SDA_SAMPLES</c>. Every mode is also covered by archives this test builds itself
/// from the layout in docs/C64-ARC-ON-DISK.md, which cbmconvert must extract to the same bytes.
/// </summary>
[TestFixture]
public class SdaTests {

  // ── a test-only encoder for every mode ──────────────────────────────────────

  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _current, _count;

    public void Bit(int bit) {
      this._current |= (bit & 1) << this._count;
      if (++this._count == 8) this.Flush();
    }

    public void LowFirst(int value, int count) {
      for (var i = 0; i < count; ++i) this.Bit(value >> i);
    }

    public void HighFirst(int value, int count) {
      for (var i = count - 1; i >= 0; --i) this.Bit(value >> i);
    }

    public byte[] ToArray() {
      if (this._count > 0) this.Flush();
      return [.. this._bytes];
    }

    private void Flush() {
      this._bytes.Add((byte)this._current);
      this._current = this._count = 0;
    }
  }

  private static byte[] RunLength(byte[] data, int marker, int maxRun = 256) {
    var o = new List<byte>();
    for (var i = 0; i < data.Length;) {
      var run = 1;
      while (i + run < data.Length && data[i + run] == data[i] && run < maxRun) ++run;
      if (run >= 4 || data[i] == marker) {
        o.Add((byte)marker);
        o.Add((byte)(run == 256 ? 0 : run));
        o.Add(data[i]);
        i += run;
      } else o.Add(data[i++]);
    }
    return [.. o];
  }

  /// <summary>
  /// 0xFF gets a 1-bit code, every other byte a 9-bit one. The lengths never grow with the byte
  /// value: cbmconvert 2.1.5 sorts the table by length with a shell sort whose index underflows
  /// (it crashes) whenever an entry has to move, so only an already sorted table can be checked
  /// against it.
  /// </summary>
  private static byte[] Squeeze(byte[] data) {
    var w = new BitWriter();
    for (var s = 0; s < 256; ++s) {
      if (s == 0xFF) {
        w.LowFirst(1, 5);
        w.LowFirst(0, 1);
      } else {
        w.LowFirst(9, 5);
        w.LowFirst(1 | (s << 1), 9);
      }
    }
    foreach (var b in data) {
      if (b == 0xFF) w.LowFirst(0, 1);
      else w.LowFirst(1 | (b << 1), 9);
    }
    return w.ToArray();
  }

  private static byte[] Crunch(byte[] data, (int Check, int Size)? trailer) {
    var w = new BitWriter();
    int width = 9, period = 256, countdown = 254, next = 258;
    void Emit(int code) {
      w.HighFirst(code, width);
      if (width < 12 && --countdown == 0) {
        period <<= 1;
        ++width;
        countdown = period;
      }
    }
    var table = new Dictionary<(int, byte), int>();
    if (data.Length > 0) {
      var current = (int)data[0];
      for (var i = 1; i < data.Length; ++i) {
        if (table.TryGetValue((current, data[i]), out var code)) {
          current = code;
          continue;
        }
        Emit(current);
        if (next < 4096) table[(current, data[i])] = next++;
        current = data[i];
      }
      Emit(current);
    }
    w.HighFirst(256, width);
    if (trailer is { } t) {
      w.HighFirst(t.Check, 16);
      w.HighFirst(t.Size, 24);
      w.HighFirst(0, 16);
    }
    return w.ToArray();
  }

  private static byte[] Entry(int mode, string name, char type, byte[] data, int version = 2) {
    var check = SdaArchive.Checksum(data, version);
    var body = mode switch {
      0 => data,
      1 => RunLength(data, 0xFE, version == 1 ? 255 : 256),
      2 => Squeeze(data),
      3 => Crunch(RunLength(data, 0xFE), null),
      4 => Squeeze(RunLength(data, 0xFE)),
      5 => Crunch(RunLength(data, 0xFE), (check, data.Length)),
      _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    var h = new List<byte> { (byte)version, (byte)mode, (byte)check, (byte)(check >> 8),
      (byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16), 0, 0, (byte)type, (byte)name.Length };
    h.AddRange(Encoding.ASCII.GetBytes(name));
    if (version == 2) h.AddRange([0, 0x21, 0x1F]); // record length, date 1995-09-01
    if (mode == 1) h.Add(0xFE);
    h.AddRange(body);
    var blocks = (h.Count + 253) / 254;
    if (mode != 5) {
      h[7] = (byte)blocks;
      h[8] = (byte)(blocks >> 8);
    }
    while (h.Count < blocks * 254) h.Add(0);
    return [.. h];
  }

  /// <summary>A minimal SDA shell: load address, one BASIC SYS line naming one block of extractor, then the entries.</summary>
  private static byte[] Sda(params byte[][] entries) {
    var o = new List<byte> { 0x01, 0x08, 0x0D, 0x08, 7, 0, 0x9E };
    o.AddRange("(2063)"u8.ToArray());
    while (o.Count < 254) o.Add(0);
    foreach (var e in entries) o.AddRange(e);
    return [.. o];
  }

  private static byte[] Sample(int seed, int length) {
    var r = new Random(seed);
    var text = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog; peter piper picked eleven pecks. ");
    var o = new byte[length];
    for (var i = 0; i < length;) {
      if (r.Next(5) == 0) {
        var run = r.Next(3, 300);
        var b = (byte)r.Next(256);
        for (var k = 0; k < run && i < length; ++k) o[i++] = b;
      } else o[i] = text[i++ % text.Length];
    }
    if (length > 10) o[5] = 0xFE; // the marker byte itself must survive
    return o;
  }

  public static IEnumerable<TestCaseData> Modes() {
    foreach (var mode in new[] { 0, 1, 2, 3, 4, 5 })
      foreach (var length in new[] { 0, 1, 253, 9000 })
        yield return new TestCaseData(mode, length).SetName($"Mode{mode}_Length{length}");
  }

  [TestCaseSource(nameof(Modes)), Category("RoundTrip")]
  public void GivenEntryInEveryMode_WhenRead_ThenDataAndChecksumAreRecovered(int mode, int length) {
    var data = Sample(mode * 31 + length, length);
    var file = Sda(Entry(mode, "FILE ONE", 'S', data), Entry(0, "SECOND", 'P', "x"u8.ToArray()));
    var entries = SdaArchive.List(file);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "file one.seq", "second.prg" }));
    Assert.That(entries[0].Size, Is.EqualTo(length));
    Assert.That(SdaArchive.Extract(file, entries[0]), Is.EqualTo(data));
    Assert.That(SdaArchive.Extract(file, entries[1]), Is.EqualTo("x"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void GivenVersion1Archive_WhenRead_ThenPlainSumChecksumAndCount255Apply() {
    var data = Enumerable.Repeat((byte)'a', 700).Concat("bc"u8.ToArray()).ToArray();
    var entry = Entry(1, "OLD", 'P', data, version: 1);
    var entries = SdaArchive.List(entry); // a version-1 archive without an extractor
    Assert.That(SdaArchive.Extract(entry, entries[0]), Is.EqualTo(data));
    Assert.That(entries[0].LastModified, Is.Null);
  }

  [Test, Category("HappyPath")]
  public void GivenVersion2Entry_ThenDateAndPetsciiNameAreMapped() {
    var file = Sda(Entry(0, "XLINK2.1/C-128", 'U', "abc"u8.ToArray()));
    var e = SdaArchive.List(file).Single();
    Assert.That(e.Name, Is.EqualTo("xlink2.1.c-128.usr"));
    Assert.That(e.LastModified, Is.EqualTo(new DateTime(1995, 9, 1)));
  }

  [Test, Category("Boundary")]
  public void GivenSdaHeaderLineNumbers_ThenFirstEntryOffsetFollowsTheExtractorSize() {
    Assert.That(SdaArchive.FindFirstEntry([0x01, 0x08, 0x0D, 0x08, 13, 0, 0x9E, (byte)'(', (byte)'2']), Is.EqualTo(1778));
    Assert.That(SdaArchive.FindFirstEntry([0x01, 0x08, 0x0D, 0x08, 15, 0, 0x9E, (byte)'(', (byte)'2']), Is.EqualTo(2286));
    Assert.That(SdaArchive.FindFirstEntry([0x01, 0x1C, 0x0D, 0x1C, 15, 0, 0x9E, (byte)'(', (byte)'7']), Is.EqualTo(2285));
    Assert.That(SdaArchive.FindFirstEntry([0x02, 0x00]), Is.EqualTo(0));
    Assert.That(SdaArchive.FindFirstEntry([0x4D, 0x5A]), Is.EqualTo(-1));
  }

  [Test, Category("Exception")]
  public void GivenChecksumMismatch_WhenExtracted_ThenRefused() {
    var file = Sda(Entry(3, "TEXT", 'S', Sample(1, 2000)));
    file[254 + 2] ^= 1;
    var e = SdaArchive.List(file).Single();
    Assert.That(() => SdaArchive.Extract(file, e), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenTruncatedEntry_WhenExtracted_ThenRefused() {
    var file = Sda(Entry(0, "BIG", 'P', Sample(2, 5000)));
    var cut = file[..2000];
    var e = SdaArchive.List(cut).Single();
    Assert.That(() => SdaArchive.Extract(cut, e), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenNonArchive_WhenListed_ThenRefused() {
    Assert.That(() => SdaArchive.List("MZ not an sda"u8), Throws.InstanceOf<InvalidDataException>());
    Assert.That(() => SdaArchive.List([0x01, 0x08, 0x0D, 0x08, 7, 0, 0x9E]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Contract")]
  public void GivenDescriptor_ThenItIsReadOnlyAndListsThroughTheRegistryApi() {
    var d = new SdaFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    var list = d.List(new MemoryStream(Sda(Entry(3, "README", 'S', Sample(3, 1000)))), null);
    Assert.That(list.Single().Name, Is.EqualTo("readme.seq"));
    Assert.That(list.Single().Method, Is.EqualTo("Crunched"));
  }

  // ── cbmconvert ───────────────────────────────────────────────────────────────

  private static void RequireCbmconvert() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("cbmconvert")) Assert.Ignore("cbmconvert is not installed.");
  }

  /// <summary>Extracts with cbmconvert's native output and returns name → bytes.</summary>
  private static Dictionary<string, byte[]> Cbmconvert(byte[] archive, string name) {
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sda_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(Path.Combine(tmp, "out"));
      var path = Path.Combine(tmp, name);
      File.WriteAllBytes(path, archive);
      var r = FsInteropToolbox.RunWsl($"cd '{FsInteropToolbox.WinToWsl(Path.Combine(tmp, "out"))}' && cbmconvert -v1 -N -a '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdErr);
      return Directory.GetFiles(Path.Combine(tmp, "out")).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }

  [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4), TestCase(5)]
  [Category("ExternalInterop")]
  public void GivenSynthesisedArchive_WhenCbmconvertExtractsIt_ThenItAgreesWithUs(int mode) {
    RequireCbmconvert();
    var data = Sample(100 + mode, 7000);
    var file = Sda(Entry(mode, "DATA", 'S', data), Entry(3, "PROG", 'P', Sample(7, 3000)));
    var theirs = Cbmconvert(file, "test.sda");
    foreach (var e in SdaArchive.List(file)) {
      Assert.That(theirs.ContainsKey(e.Name), Is.True, $"cbmconvert wrote {string.Join(", ", theirs.Keys)}");
      Assert.That(SdaArchive.Extract(file, e), Is.EqualTo(theirs[e.Name]), e.Name);
    }
  }

  [Test, Category("ThemVsUs")]
  public void GivenLocalSdaSamples_WhenExtractedByUsAndCbmconvert_ThenEveryFileMatches() {
    var samples = Environment.GetEnvironmentVariable("CWB_SDA_SAMPLES");
    if (string.IsNullOrEmpty(samples) || !Directory.Exists(samples)) Assert.Ignore("CWB_SDA_SAMPLES is not set.");
    RequireCbmconvert();
    var compared = 0;
    foreach (var path in Directory.EnumerateFiles(samples)) {
      var file = File.ReadAllBytes(path);
      var theirs = Cbmconvert(file, Path.GetFileName(path));
      foreach (var e in SdaArchive.List(file)) {
        Assert.That(SdaArchive.Extract(file, e), Is.EqualTo(theirs[e.Name]), $"{path}: {e.Name}");
        ++compared;
      }
    }
    Assert.That(compared, Is.GreaterThan(0));
    TestContext.Out.WriteLine($"compared {compared} files with cbmconvert");
  }
}
