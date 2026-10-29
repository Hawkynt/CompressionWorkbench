using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Uc2;

namespace Compression.Tests.Uc2;

/// <summary>
/// UltraCompressor II: archives the original UC2 r2 / 2.3 / 2.37b wrote (Uc2/ReferenceVectors),
/// the format's golden vectors, our writer, and unuc2 (an independent decoder) as the oracle for
/// what we write.
/// </summary>
[TestFixture]
public class Uc2Tests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_uc2_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── checksum and bitstream vectors (format.md §5, §6.10) ────────────

  [TestCase("", 0xA55A)]
  [TestCase("a", 0xA53B)]
  [TestCase("ab", 0xC73B)]
  [TestCase("abc", 0xC758)]
  [TestCase("abcd", 0xA358)]
  [TestCase("123456789", 0xAD63)]
  [Category("HappyPath")]
  public void GivenKnownInput_WhenChecksummed_ThenMatchesTheFormatsVector(string input, int expected)
    => Assert.That(Uc2Archive.Fletch(System.Text.Encoding.ASCII.GetBytes(input)), Is.EqualTo((ushort)expected));

  [Test, Category("HappyPath")]
  public void GivenTheEmptyRawCdir_WhenChecksummed_ThenIsA55E() {
    byte[] raw = [0x04, .. new byte[21]];
    Assert.That(Uc2Archive.Fletch(raw), Is.EqualTo((ushort)0xA55E));
  }

  [Test, Category("HappyPath")]
  public void GivenTheGoldenStreamForA_WhenDecodedWithNoMaster_ThenYieldsA() {
    byte[] stream = [0xBF, 0xA0, 0x01, 0x7A, 0x00, 0x00];
    Assert.That(Uc2Ultra.Decode(stream, 0, stream.Length, new byte[512], 1), Is.EqualTo("A"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void GivenUc2sEmptyCdirStream_WhenDecodedToItsEnd_ThenYieldsTheEmptyCdir() {
    byte[] stream = [0x09, 0xB2, 0xCF, 0xD2, 0x80, 0xDE, 0x00, 0x40];
    byte[] expected = [0x04, .. new byte[21]];
    Assert.That(Uc2Ultra.Decode(stream, 0, stream.Length, new byte[512], -1), Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void GivenA_WhenEncodedWithDefaultTrees_ThenProducesTheGoldenStream() {
    Assert.That(Uc2Ultra.Encode("A"u8, new byte[512]), Is.EqualTo(new byte[] { 0xBF, 0xA0, 0x01, 0x7A, 0x00, 0x00 }));
    Assert.That(Uc2Ultra.Encode([], new byte[512]), Is.EqualTo(new byte[] { 0x00, 0x00 }));
  }

  // ── archives written by the original UC2 ────────────────────────────

  private static readonly string[] Vectors = ["r2_def.uc2", "r2_prot.uc2", "r2_rev.uc2", "r2_tf.uc2", "r2_tst.uc2", "r2_tt.uc2", "v23_def.uc2", "v237b_lfn.uc2", "v237b_tt.uc2"];

  [TestCaseSource(nameof(Vectors)), Category("ThemVsUs")]
  public void GivenArchiveWrittenByUc2_WhenEveryFileIsExtracted_ThenItsSha256MatchesTheRecordedSum(string vector) {
    var expected = Sums().Where(s => s.Archive == vector && s.Revision == 0).ToDictionary(s => s.Name, s => s.Sha8);
    Assert.That(expected, Is.Not.Empty);
    var archive = Uc2Archive.Read(new MemoryStream(LoadVector(vector)));
    var files = archive.Entries.Where(e => !e.IsDirectory).ToList();

    Assert.That(files.Select(e => e.Path), Is.EquivalentTo(expected.Keys));
    foreach (var e in files)
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(Uc2Archive.Extract(archive, e)))[..16], Is.EqualTo(expected[e.Path]), $"{vector}: {e.Path}");
  }

  [Test, Category("ThemVsUs")]
  public void GivenRevisionedArchive_WhenListed_ThenOnlyTheNewestRevisionAppears() {
    var archive = Uc2Archive.Read(new MemoryStream(LoadVector("r2_rev.uc2")));
    var newest = Sums().Single(s => s.Archive == "r2_rev.uc2" && s.Name == "VERS.TXT" && s.Revision == 0);
    var e = archive.Entries.Single(x => x.Path == "VERS.TXT");
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(Uc2Archive.Extract(archive, e)))[..16], Is.EqualTo(newest.Sha8));
  }

  [Test, Category("Exception")]
  public void GivenDamagedFileStream_WhenExtracted_ThenTheChecksumRefusesIt() {
    var bytes = LoadVector("r2_def.uc2");
    var probe = Uc2Archive.Read(new MemoryStream(bytes));
    var text = probe.Entries.Single(e => e.Path == "TEXT.TXT");
    // The first stream bit (MSB of the first word's high byte) says "a block follows";
    // clearing it ends the stream before the recorded size.
    bytes[text.Offset + 1] ^= 0x80;

    var archive = Uc2Archive.Read(new MemoryStream(bytes));
    Assert.That(() => Uc2Archive.Extract(archive, archive.Entries.Single(e => e.Path == "TEXT.TXT")), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenDamagedHeader_WhenRead_ThenRefused() {
    var bytes = LoadVector("r2_def.uc2");
    bytes[0] = (byte)'X';
    Assert.That(() => Uc2Archive.Read(new MemoryStream(bytes)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenUltraCryptArchive_WhenRead_ThenRefusedAsUnsupported() {
    byte[] bytes = [(byte)'U', (byte)'E', (byte)'2', .. new byte[40]];
    Assert.That(() => Uc2Archive.Read(new MemoryStream(bytes)), Throws.InstanceOf<NotSupportedException>());
  }

  // ── our writer ──────────────────────────────────────────────────────

  private static List<Uc2Archive.Input> SampleInputs() {
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 400)));
    var noise = new byte[70_000];
    new Random(7).NextBytes(noise);
    var t = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Local);
    return [
      new("HELLO.TXT", "hello uc2\r\n"u8.ToArray(), t),
      new("Long File Name.text", text, t),
      new("sub/deeper/noise.bin", noise, t),
      new("sub/empty.dat", [], t),
      new("zeros.bin", new byte[100_000], t),
    ];
  }

  private byte[] WriteSample() {
    using var ms = new MemoryStream();
    Uc2Archive.Write(ms, SampleInputs());
    return ms.ToArray();
  }

  [Test, Category("RoundTrip")]
  public void GivenFilesAndFolders_WhenWrittenAndRead_ThenPathsAndBytesRoundTrip() {
    var archive = Uc2Archive.Read(new MemoryStream(this.WriteSample()));
    foreach (var input in SampleInputs()) {
      var e = archive.Entries.Single(x => x.Path == input.Path);
      Assert.That(Uc2Archive.Extract(archive, e), Is.EqualTo(input.Data), input.Path);
      Assert.That(e.LastModified, Is.EqualTo(input.LastModified));
    }
    Assert.That(archive.Entries.Where(e => e.IsDirectory).Select(e => e.Path), Is.EquivalentTo(new[] { "sub", "sub/deeper" }));
  }

  [Test, Category("Boundary")]
  public void GivenNoFiles_WhenWritten_ThenAnEmptyArchiveReadsBack() {
    using var ms = new MemoryStream();
    Uc2Archive.Write(ms, []);
    Assert.That(Uc2Archive.Read(new MemoryStream(ms.ToArray())).Entries, Is.Empty);
  }

  [Test, Category("Boundary")]
  public void GivenNamesThatCollideIn83_WhenWritten_ThenEachKeepsItsOwnContent() {
    var t = DateTime.Now;
    using var ms = new MemoryStream();
    Uc2Archive.Write(ms, [new("readme.first.txt", [1], t), new("readme.second.txt", [2], t), new("README.TXT", [3], t)]);
    var archive = Uc2Archive.Read(new MemoryStream(ms.ToArray()));
    Assert.That(Uc2Archive.Extract(archive, archive.Entries.Single(e => e.Path == "readme.first.txt")), Is.EqualTo(new byte[] { 1 }));
    Assert.That(Uc2Archive.Extract(archive, archive.Entries.Single(e => e.Path == "readme.second.txt")), Is.EqualTo(new byte[] { 2 }));
    Assert.That(Uc2Archive.Extract(archive, archive.Entries.Single(e => e.Path == "README.TXT")), Is.EqualTo(new byte[] { 3 }));
    Assert.That(archive.Entries.Select(e => e.ShortName).Distinct().Count(), Is.EqualTo(3));
  }

  [Test, Category("Exception")]
  public void GivenPassword_WhenCreated_ThenRefused() {
    Assert.That(() => new Uc2FormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1])], new FormatCreateOptions { Password = "x" }),
      Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("ExternalInterop")]
  public void GivenOurArchive_WhenUnuc2ExtractsIt_ThenEveryFileMatches() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unuc2")) Assert.Ignore("unuc2 (github.com/jan-bobrowski/unuc2) is not installed.");
    var path = Path.Combine(this._tmpDir, "OURS.UC2");
    File.WriteAllBytes(path, this.WriteSample());
    var outDir = Path.Combine(this._tmpDir, "out");
    Directory.CreateDirectory(outDir);

    var t = FsInteropToolbox.RunWsl($"unuc2 -t '{FsInteropToolbox.WinToWsl(path)}'");
    Assert.That(t.ExitCode, Is.EqualTo(0), $"unuc2 -t rejected our archive:\n{t.StdOut}\n{t.StdErr}");
    var r = FsInteropToolbox.RunWsl($"unuc2 -f -d '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"unuc2 rejected our archive:\n{r.StdOut}\n{r.StdErr}");
    var archive = Uc2Archive.Read(File.OpenRead(path));
    foreach (var input in SampleInputs()) {
      // unuc2 extracts under the 8.3 names, or the long name where the archive records one.
      var candidates = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
        .Where(f => string.Equals(Path.GetFileName(f), Path.GetFileName(input.Path), StringComparison.OrdinalIgnoreCase)
                 || string.Equals(Path.GetFileName(f), archive.Entries.Single(e => e.Path == input.Path).ShortName, StringComparison.OrdinalIgnoreCase))
        .ToList();
      Assert.That(candidates, Is.Not.Empty, $"unuc2 produced no {input.Path}");
      Assert.That(candidates.Any(c => File.ReadAllBytes(c).AsSpan().SequenceEqual(input.Data)), $"{input.Path} differs");
    }
  }

  // ── helpers ─────────────────────────────────────────────────────────

  private static IEnumerable<(string Archive, string Sha8, int Revision, string Name)> Sums() {
    var text = System.Text.Encoding.UTF8.GetString(LoadVector("sums.txt"));
    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var parts = line.Split(' ', 4);
      yield return (parts[0], parts[1], int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), parts[3]);
    }
  }

  private static byte[] LoadVector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Uc2Vectors.{name}")
      ?? throw new InvalidOperationException($"missing embedded vector {name}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
