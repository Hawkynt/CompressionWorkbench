using System.Buffers.Binary;
using System.Reflection;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Rar;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// RAR 4 archives written by RAR 6.24 and libarchive's test archives (see
/// Rar/ReferenceVectors/README.md): every file must decode to its stored CRC-32, and the
/// filters RAR chose must be the ones applied.
/// </summary>
[TestFixture]
public class Rar4ReferenceTests {

  private static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"RarVectors.{name}")
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private sealed record PackedFile(string Name, byte[] Packed, int Size, uint Crc, int WindowBits, bool Solid, int Method);

  /// <summary>Minimal RAR 4 block walk: the packed data of each file header.</summary>
  private static List<PackedFile> Files(byte[] archive) {
    var files = new List<PackedFile>();
    var pos = 7;
    while (pos + 7 <= archive.Length) {
      var type = archive[pos + 2];
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(pos + 3));
      var headSize = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(pos + 5));
      long add = (flags & 0x8000) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(pos + 7)) : 0;
      if (type == 0x74) {
        var h = archive.AsSpan(pos);
        var packSize = BinaryPrimitives.ReadUInt32LittleEndian(h[7..]);
        var unpSize = BinaryPrimitives.ReadUInt32LittleEndian(h[11..]);
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        var method = h[25];
        var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(h[26..]);
        var nameAt = (flags & 0x100) != 0 ? 40 : 32;
        var name = System.Text.Encoding.ASCII.GetString(h.Slice(nameAt, nameSize));
        var dict = (flags >> 5) & 7;
        files.Add(new(name, archive.AsSpan(pos + headSize, (int)packSize).ToArray(), (int)unpSize, crc, 16 + dict, (flags & 0x10) != 0, method));
        add = packSize;
      }
      if (type == 0x7B) break;
      pos += headSize + (int)add;
    }
    return files;
  }

  private static List<string> DecodeAll(string vector) {
    var decoder = new Rar3Decoder();
    foreach (var f in Files(Vector(vector))) {
      if (f.Method == 0x30) continue;
      if (!f.Solid) decoder = new Rar3Decoder();
      var data = decoder.Decompress(f.Packed, f.Size, f.WindowBits);
      Assert.That(Crc32.Compute(data), Is.EqualTo(f.Crc), $"{vector}: {f.Name}");
    }
    return decoder.AppliedFilters;
  }

  public static IEnumerable<TestCaseData> PlainVectors() {
    foreach (var name in new[] { "rar624-lz.rar", "rar624-best.rar", "rar624-solid.rar", "rar624-itanium.rar", "rar624-ppmd.rar", "libarchive-compress-normal.rar", "libarchive-compress-best.rar" })
      yield return new TestCaseData(name).SetName($"Decodes_{name}");
  }

  [TestCaseSource(nameof(PlainVectors)), Category("Spec")]
  public void Decodes(string vector) => DecodeAll(vector);

  [TestCase("rar624-e8.rar", "E8")]
  [TestCase("rar624-delta.rar", "Delta")]
  [TestCase("rar624-audio.rar", "Audio")]
  [TestCase("rar624-rgb.rar", "Rgb")]
  [TestCase("libarchive-filter.rar", "E8E9")]
  [Category("Spec")]
  public void Filter_IsAppliedAndFileMatchesItsCrc(string vector, string filter)
    => Assert.That(DecodeAll(vector), Does.Contain(filter));

  [Test, Category("Spec")]
  public void Reader_ExtractsEveryVector() {
    foreach (var name in new[] { "rar624-lz.rar", "rar624-best.rar", "rar624-ppmd.rar", "rar624-solid.rar", "rar624-e8.rar", "rar624-delta.rar", "rar624-audio.rar", "rar624-rgb.rar", "libarchive-filter.rar" }) {
      using var reader = new RarReader(new MemoryStream(Vector(name)));
      for (var i = 0; i < reader.Entries.Count; ++i)
        if (!reader.Entries[i].IsDirectory)
          Assert.DoesNotThrow(() => reader.Extract(i), $"{name}: {reader.Entries[i].Name}");
    }
  }

  [Test, Category("Exception")]
  public void Decoder_TruncatedData_Throws() {
    var f = Files(Vector("rar624-lz.rar"))[0];
    Assert.Throws<InvalidDataException>(() => new Rar3Decoder().Decompress(f.Packed.AsSpan(0, f.Packed.Length / 3), f.Size, f.WindowBits));
  }

  public static IEnumerable<TestCaseData> EncoderInputs() {
    yield return new TestCaseData(new byte[] { 42 }).SetName("OneByte");
    yield return new TestCaseData(Enumerable.Range(0, 1000).Select(i => (byte)(i % 10)).ToArray()).SetName("ShortPeriod");
    yield return new TestCaseData(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 400)))).SetName("Text");
    var rnd = new Random(3);
    var noise = new byte[20000];
    rnd.NextBytes(noise);
    yield return new TestCaseData(noise).SetName("Noise");
    var far = new byte[600_000];
    rnd.NextBytes(far.AsSpan(0, 1000));
    far.AsSpan(0, 1000).CopyTo(far.AsSpan(300_000));
    far.AsSpan(0, 1000).CopyTo(far.AsSpan(599_000));
    yield return new TestCaseData(far).SetName("FarMatches");
  }

  [TestCaseSource(nameof(EncoderInputs)), Category("RoundTrip")]
  public void Encoder_RoundTrips(byte[] data) {
    var packed = new Rar3Encoder(20).Compress(data);
    Assert.That(new Rar3Decoder().Decompress(packed, data.Length, 20), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Encoder_SolidSequence_RoundTrips() {
    var encoder = new Rar3Encoder(20);
    var decoder = new Rar3Decoder();
    var parts = new[] { "alpha alpha alpha beta"u8.ToArray(), "beta beta gamma alpha alpha"u8.ToArray(), new byte[5000] };
    foreach (var p in parts)
      Assert.That(decoder.Decompress(encoder.Compress(p), p.Length, 20), Is.EqualTo(p));
  }
}

/// <summary>unrar and bsdtar must read the RAR 4 archives we write.</summary>
[TestFixture]
[Category("ExternalInterop")]
public class Rar4WriterUnrarInteropTests {
  private static string? Missing() {
    if (!FsInteropToolbox.WslAvailable) return "WSL/Linux shell unavailable.";
    return FsInteropToolbox.WslHasTool("unrar") ? null : "'unrar' is required (apt install unrar).";
  }

  [TestCase(false)]
  [TestCase(true)]
  public void OurArchive_TestsOkAndExtractsIdentically(bool solid) {
    if (Missing() is { } why) Assert.Ignore(why);
    var rnd = new Random(8);
    var files = new Dictionary<string, byte[]> {
      ["text.txt"] = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i % 97} of the file\n"))),
      ["noise.bin"] = Enumerable.Range(0, 30000).Select(_ => (byte)rnd.Next(256)).ToArray(),
      ["far.bin"] = [.. Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7 % 251)), .. new byte[100]],
    };
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_rar4_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var archive = Path.Combine(dir, "ours.rar");
      using (var fs = File.Create(archive))
      using (var writer = new Rar4Writer(fs, method: RarConstants.Rar4MethodNormal, windowBits: 20, solid: solid))
        foreach (var (name, data) in files) writer.AddFile(name, data);
      var outDir = Path.Combine(dir, "out");
      var r = FsInteropToolbox.RunWsl($"unrar t -idq {FsInteropToolbox.WinToWsl(archive)} && mkdir -p {FsInteropToolbox.WinToWsl(outDir)} && unrar x -idq {FsInteropToolbox.WinToWsl(archive)} {FsInteropToolbox.WinToWsl(outDir)}/");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
      foreach (var (name, data) in files)
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, name)), Is.EqualTo(data), name);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
