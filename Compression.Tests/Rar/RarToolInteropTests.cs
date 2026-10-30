using Compression.Registry;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// Both directions against RARLAB's rar/unrar and 7-Zip, when installed (see
/// <see cref="RarTools"/>): what we write must test OK and extract byte for byte in each of
/// them; what rar writes at test time (SFX modules, settings not in the committed fixtures)
/// must extract here.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class RarToolInteropTests {
  private static Dictionary<string, byte[]> Inputs() {
    var rnd = new Random(20261030);
    var text = string.Concat(Enumerable.Range(0, 2500).Select(i => $"line {i % 113}: the archive keeps every byte of this text\n"));
    var noise = new byte[40_000];
    rnd.NextBytes(noise);
    return new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["text.txt"] = System.Text.Encoding.ASCII.GetBytes(text),
      ["noise.bin"] = noise,
      ["empty.dat"] = [],
      ["dir/sub/small.txt"] = "hello rar\n"u8.ToArray(),
      ["Grüße-日本語.txt"] = "unicode name\n"u8.ToArray(),
    };
  }

  private static string WriteOurs(string dir, string archiveName, Dictionary<string, byte[]> files, Dictionary<string, string> fmt,
      string? password = null, bool encryptHeaders = false, long solidSize = 1, int? level = null, string? method = null) {
    var path = Path.Combine(dir, archiveName);
    var inputs = files.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value)).ToList();
    inputs.Add(new ArchiveInputInfo("dir", "dir", true));
    var options = new FormatCreateOptions {
      Password = password, EncryptFilenames = encryptHeaders, SolidSize = solidSize, Level = level, MethodName = method,
      FormatSpecific = new Dictionary<string, string>(fmt, StringComparer.OrdinalIgnoreCase),
    };
    using (var fs = File.Create(path))
      new RarFormatDescriptor().Create(fs, inputs, options);
    return path;
  }

  private static void AssertExtractsIn(string tool, string archive, string? password, Dictionary<string, byte[]> want) {
    var outDir = RarTools.TempDir("rarx");
    try {
      (int ExitCode, string Output) r;
      if (tool == "7z")
        r = RarTools.Run(RarTools.SevenZip!, ["x", "-y", password != null ? "-p" + password : "-p", "-o" + outDir, archive], null);
      else {
        var exe = tool == "unrar" ? RarTools.Unrar! : RarTools.Rar!;
        r = RarTools.Run(exe, ["x", "-y", "-idq", "-cfg-", password != null ? "-p" + password : "-p-", archive, outDir + Path.DirectorySeparatorChar], null);
      }
      Assert.That(r.ExitCode, Is.Zero, $"{tool} x {Path.GetFileName(archive)}: {r.Output}");
      var got = RarTools.Files(outDir);
      foreach (var (name, data) in want) {
        Assert.That(got.ContainsKey(name), Is.True, $"{tool}: '{name}' missing; got {string.Join(", ", got.Keys)}");
        Assert.That(got[name], Is.EqualTo(data), $"{tool}: {name}");
      }
    } finally {
      RarTools.TryDelete(outDir);
    }
  }

  private static IEnumerable<string> Tools() {
    if (RarTools.Unrar != null) yield return "unrar";
    if (RarTools.Rar != null) yield return "rar";
    if (RarTools.SevenZip != null) yield return "7z";
  }

  public static IEnumerable<TestCaseData> OurArchives() {
    var r5 = new Dictionary<string, string>();
    var r4 = new Dictionary<string, string> { [FormatOptionKeys.TargetCompatibility] = "Rar4" };
    yield return new TestCaseData("r5-store.rar", r5, null, false, 1L, null, "store").SetName("Rar5_Store");
    yield return new TestCaseData("r5-fast.rar", r5, null, false, 1L, 1, null).SetName("Rar5_Level1");
    yield return new TestCaseData("r5-best.rar", r5, null, false, 1L, 9, null).SetName("Rar5_Level9");
    yield return new TestCaseData("r5-solid.rar", r5, null, false, 0L, null, null).SetName("Rar5_Solid");
    yield return new TestCaseData("r5-enc.rar", r5, "secret", false, 1L, null, null).SetName("Rar5_Password");
    yield return new TestCaseData("r5-enc-solid.rar", r5, "geheim-ä", false, 0L, null, null).SetName("Rar5_Password_Solid_UnicodePassword");
    yield return new TestCaseData("r5-hp.rar", r5, "secret", true, 1L, null, null).SetName("Rar5_EncryptedHeaders");
    yield return new TestCaseData("r5-vol.part1.rar", new Dictionary<string, string>(r5) { [FormatOptionKeys.VolumeSize] = "16k" }, null, false, 1L, null, null).SetName("Rar5_Volumes");
    yield return new TestCaseData("r5-vol-solid.part1.rar", new Dictionary<string, string>(r5) { [FormatOptionKeys.VolumeSize] = "16k" }, null, false, 0L, null, null).SetName("Rar5_Volumes_Solid");
    yield return new TestCaseData("r5-vol-enc.part1.rar", new Dictionary<string, string>(r5) { [FormatOptionKeys.VolumeSize] = "16k" }, "secret", false, 1L, null, null).SetName("Rar5_Volumes_Password");
    yield return new TestCaseData("r5-vol-hp.part1.rar", new Dictionary<string, string>(r5) { [FormatOptionKeys.VolumeSize] = "16k" }, "secret", true, 1L, null, null).SetName("Rar5_Volumes_EncryptedHeaders");
    yield return new TestCaseData("r4-store.rar", r4, null, false, 1L, null, "store").SetName("Rar4_Store");
    yield return new TestCaseData("r4-lz.rar", r4, null, false, 1L, null, null).SetName("Rar4_Compressed");
    yield return new TestCaseData("r4-solid.rar", r4, null, false, 0L, null, null).SetName("Rar4_Solid");
    yield return new TestCaseData("r4-enc.rar", r4, "secret", false, 1L, null, null).SetName("Rar4_Password");
    yield return new TestCaseData("r4-vol.part1.rar", new Dictionary<string, string>(r4) { [FormatOptionKeys.VolumeSize] = "16k" }, null, false, 1L, null, null).SetName("Rar4_Volumes_NewNaming");
    yield return new TestCaseData("r4-vol-old.rar", new Dictionary<string, string>(r4) { [FormatOptionKeys.VolumeSize] = "16k" }, null, false, 1L, null, null).SetName("Rar4_Volumes_OldNaming");
    yield return new TestCaseData("r4-vol-enc.part1.rar", new Dictionary<string, string>(r4) { [FormatOptionKeys.VolumeSize] = "16k" }, "secret", false, 1L, null, null).SetName("Rar4_Volumes_Password");
  }

  [TestCaseSource(nameof(OurArchives))]
  public void Given_AnArchiveWeWrote_When_RarUnrarAnd7ZipExtractIt_Then_EveryFileComesOutByteExact(
      string name, Dictionary<string, string> fmt, string? password, bool encryptHeaders, long solidSize, int? level, string? method) {
    if (!Tools().Any()) Assert.Ignore("No rar, unrar or 7z found (set CWB_TOOL_RAR / CWB_TOOL_UNRAR / CWB_TOOL_7Z).");
    var dir = RarTools.TempDir("rarours");
    try {
      var files = Inputs();
      var archive = WriteOurs(dir, name, files, fmt, password, encryptHeaders, solidSize, level, method);
      if (fmt.ContainsKey(FormatOptionKeys.VolumeSize))
        Assert.That(Directory.GetFiles(dir).Length, Is.GreaterThan(1), "volumes written");
      foreach (var tool in Tools())
        AssertExtractsIn(tool, archive, password, files);
      // Our own reader agrees, from the same files.
      using var reader = RarReader.Open(archive, password);
      var got = RarConformanceSet.ExtractAll(reader);
      foreach (var (n, data) in files) Assert.That(got[n], Is.EqualTo(data), "ours: " + n);
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [Test]
  public void Given_ARar15TargetArchive_When_UnrarExtractsIt_Then_ItIsByteExact() {
    if (!Tools().Any()) Assert.Ignore("No rar, unrar or 7z found.");
    var dir = RarTools.TempDir("rar15");
    try {
      var files = Inputs().Where(kv => kv.Key.All(c => c < 0x80)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
      var archive = WriteOurs(dir, "r15.rar", files, new() { [FormatOptionKeys.TargetCompatibility] = "Rar1_5" });
      foreach (var tool in Tools()) AssertExtractsIn(tool, archive, null, files);
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  // ---- what rar writes at test time ----

  [TestCase("-ma5", 5)]
  [TestCase("-ma4", 4)]
  public void Given_ARarSfxModule_When_TheArchiveIsOpened_Then_ItIsFoundBehindThePeStubAndExtracts(string format, int version) {
    var rar = format == "-ma4" ? RarTools.Rar4 : RarTools.Rar;
    if (rar == null) Assert.Ignore($"No rar for {format} (set CWB_TOOL_RAR / CWB_TOOL_RAR4).");
    var sfxModule = Path.Combine(Path.GetDirectoryName(rar)!, "Default.SFX");
    var dir = RarTools.TempDir("rarsfx");
    try {
      var files = Inputs().Where(kv => !kv.Key.Contains('/')).ToDictionary();
      foreach (var (n, d) in files) File.WriteAllBytes(Path.Combine(dir, n), d);
      var args = new List<string> { "a", "-idq", "-cfg-", format, "-ep" };
      args.Add(File.Exists(sfxModule) ? "-sfx" + sfxModule : "-sfx");
      args.Add(Path.Combine(dir, "self.exe"));
      args.AddRange(files.Keys.Select(k => Path.Combine(dir, k)));
      var r = RarTools.Run(rar, args, dir);
      Assert.That(r.ExitCode, Is.Zero, r.Output);
      var exe = Path.Combine(dir, "self.exe");
      using (var reader = RarReader.Open(exe)) {
        Assert.That(reader.SfxSize, Is.GreaterThan(1000), "the PE module comes first");
        Assert.That(reader.Version, Is.EqualTo(version));
        var got = RarConformanceSet.ExtractAll(reader);
        foreach (var (n, d) in files) Assert.That(got[n], Is.EqualTo(d), n);
      }
      // The library's format detection takes the SFX route (PE overlay) to the same archive.
      var outDir = Path.Combine(dir, "out");
      Compression.Lib.ArchiveOperations.Extract(exe, outDir, null, null);
      foreach (var (n, d) in files) Assert.That(File.ReadAllBytes(Path.Combine(outDir, n)), Is.EqualTo(d), "via ArchiveOperations: " + n);
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [Test]
  public void Given_RarVolumesWithRecoveryVolumes_When_Read_Then_TheRevFilesAreIgnored() {
    if (RarTools.Rar == null) Assert.Ignore("No rar (set CWB_TOOL_RAR).");
    var dir = RarTools.TempDir("rarrev");
    try {
      var files = Inputs().Where(kv => !kv.Key.Contains('/')).ToDictionary();
      foreach (var (n, d) in files) File.WriteAllBytes(Path.Combine(dir, n), d);
      var r = RarTools.Run(RarTools.Rar, new[] { "a", "-idq", "-cfg-", "-ma5", "-ep", "-v20k", "-rv2", Path.Combine(dir, "set.rar") }.Concat(files.Keys.Select(k => Path.Combine(dir, k))), dir);
      Assert.That(r.ExitCode, Is.Zero, r.Output);
      var volumes = Directory.GetFiles(dir, "set*.rar").Order(StringComparer.Ordinal).ToArray();
      Assert.That(volumes, Has.Length.GreaterThan(1), "listing: " + string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));
      Assert.That(Directory.GetFiles(dir, "*.rev"), Is.Not.Empty);
      using var reader = RarReader.Open(volumes[0]);
      Assert.That(reader.MissingVolume, Is.Null);
      var got = RarConformanceSet.ExtractAll(reader);
      foreach (var (n, d) in files) Assert.That(got[n], Is.EqualTo(d), n);
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  /// <summary>
  /// A file past 4 GiB through the streaming paths: written by us without buffering it, read back
  /// with the CRC check, and tested by unrar when present. Memory stays bounded.
  /// </summary>
  [Test]
  [Explicit("Writes about 4.3 GiB of input through the codec; run on demand.")]
  [Category("Slow")]
  public void Given_AFileOver4GiB_When_WrittenAndReadBackStreaming_Then_ItRoundTripsInBoundedMemory() {
    const long size = (4L << 30) + 123_457;
    var dir = RarTools.TempDir("rarbig");
    try {
      var archive = Path.Combine(dir, "big.rar");
      uint crc;
      using (var fs = File.Create(archive))
      using (var w = new RarWriter(fs, method: 1, dictionarySizeLog: 22)) {
        var input = new PatternStream(size);
        w.AddFile("big.bin", input, size);
        crc = input.Crc;
      }
      using (var reader = RarReader.Open(archive)) {
        var e = reader.Entries.Single();
        Assert.That(e.Size, Is.EqualTo(size));
        Assert.That(e.Crc, Is.EqualTo(crc));
        var sink = new CountingStream();
        reader.Extract(0, sink);
        Assert.That(sink.Count, Is.EqualTo(size));
      }
      Assert.That(System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64, Is.LessThan(2L << 30), "bounded memory");
      if (RarTools.Unrar != null) {
        var t = RarTools.Run(RarTools.Unrar, ["t", "-idq", "-cfg-", archive], null, timeoutMs: 3_600_000);
        Assert.That(t.ExitCode, Is.Zero, t.Output);
      }
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  /// <summary>Deterministic, compressible bytes of a given length, with their CRC-32.</summary>
  private sealed class PatternStream(long length) : Stream {
    private long _pos;
    private uint _x = 12345;
    private readonly Compression.Core.Checksums.Crc32 _crc = new();
    public uint Crc => this._crc.Value;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => this._pos; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) {
      var n = (int)Math.Min(count, length - this._pos);
      for (var i = 0; i < n; ++i) {
        if ((i & 63) == 0) this._x = this._x * 1103515245 + 12345;
        buffer[offset + i] = (byte)("abcdefgh"[(int)((this._x >> 16) & 7)] + ((this._pos + i) % 61 == 0 ? 1 : 0));
      }
      this._crc.Update(buffer.AsSpan(offset, n));
      this._pos += n;
      return n;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  private sealed class CountingStream : Stream {
    public long Count;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => this.Count;
    public override long Position { get => this.Count; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => this.Count += count;
    public override void Write(ReadOnlySpan<byte> buffer) => this.Count += buffer.Length;
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }
}
