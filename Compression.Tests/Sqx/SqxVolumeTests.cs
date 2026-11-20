using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Sqx;

namespace Compression.Tests.Sqx;

/// <summary>
/// A three-volume SQX set written by Squeez CommandLine 5.2 (<c>mvol.sqx</c>, <c>mvol.001</c>,
/// <c>mvol.002</c>; stored, 140000-byte volumes; <c>huge.bin</c> split over all three): opened at its
/// first volume it reads every file whole; a volume missing, or a volume opened alone, refuses
/// the split file rather than returning part of it. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public sealed class SqxVolumeTests {
  private static readonly Dictionary<string, (long Size, string Sha)> Originals = new() {
    ["text.txt"] = (85890, "f69ec22b6ebc823e64d5da710dd3f19b2413989154e2860edbfab82bc92c37eb"),
    ["huge.bin"] = (200000, "97ca86bac3a100ae3553051e58b00c67d99428a4f1af57998b0bf7970fbf0721"),
    ["small.bin"] = (9, "d557e35c42841ee1fae0f3953fef1bbd042969d7d5d1ac3123214ce840195554"),
  };

  private static string Stage(params string[] volumes) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sqxvol_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    foreach (var name in volumes) {
      using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SqxVolumes." + name)
        ?? throw new InvalidOperationException("Missing resource SqxVolumes." + name);
      using var file = File.Create(Path.Combine(dir, name));
      stream.CopyTo(file);
    }
    return dir;
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_TheWholeSet_When_OpenedAtTheFirstVolume_Then_EveryFileIsTheOriginal() {
    var dir = Stage("mvol.sqx", "mvol.001", "mvol.002");
    try {
      using var reader = new SqxReader(File.OpenRead(Path.Combine(dir, "mvol.sqx")));
      Assert.That(reader.VolumeCount, Is.EqualTo(3));
      Assert.That(reader.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "text.txt", "huge.bin", "small.bin" }));
      foreach (var e in reader.Entries) {
        var data = reader.ExtractEntry(e);
        Assert.That((data.LongLength, Convert.ToHexStringLower(SHA256.HashData(data))), Is.EqualTo(Originals[e.FileName]), e.FileName);
      }
      Assert.That(reader.Entries.Single(e => e.FileName == "huge.bin").CompressedSize, Is.EqualTo(200000));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_TheWholeSet_When_ExtractedThroughTheDescriptor_Then_EveryFileIsWritten() {
    var dir = Stage("mvol.sqx", "mvol.001", "mvol.002");
    var output = Path.Combine(dir, "out");
    try {
      using (var stream = File.OpenRead(Path.Combine(dir, "mvol.sqx")))
        new SqxFormatDescriptor().Extract(stream, output, null, null);
      foreach (var (name, (_, sha)) in Originals)
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, name)))), Is.EqualTo(sha), name);
    } finally { Directory.Delete(dir, true); }
  }

  /// <summary>Compressed and solid sets Squeez CommandLine writes on the spot (CWB_SQC: its Linux binary, run in WSL).</summary>
  [TestCase("SQX1", "", true), TestCase("SQX2", "", true), TestCase("SQX2", "-s -mx", true)]
  [Category("EndToEnd")]
  public void Given_ACompressedSetFromSqueez_When_OpenedAtTheFirstVolume_Then_EveryFileIsTheOriginal(string format, string switches, bool volumes) {
    var sqc = Environment.GetEnvironmentVariable("CWB_SQC");
    if (string.IsNullOrEmpty(sqc)) Assert.Ignore("Set CWB_SQC to Squeez CommandLine 5.2 for Linux (sqc_linux.tgz of the SQX SDK mirror).");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sqxvol_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var rnd = new Random(21);
      var files = new Dictionary<string, byte[]> {
        ["noise.bin"] = Enumerable.Range(0, 320_000).Select(_ => (byte)rnd.Next(256)).ToArray(),
        ["words.txt"] = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 15_000).Select(i => $"word {i % 977} {i * 31 % 1013}\n"))),
        ["last.bin"] = [1, 2, 3],
      };
      foreach (var (name, data) in files) File.WriteAllBytes(Path.Combine(dir, name), data);
      var w = FsInteropToolbox.WinToWsl(dir);
      // Squeez writes its volumes in a Linux directory (it fails on the Windows mount)
      var tmp = $"/tmp/cwb_sqxvol_{Guid.NewGuid():N}";
      var run = FsInteropToolbox.RunWsl($"mkdir -p {tmp} && cp {w}/noise.bin {w}/words.txt {w}/last.bin {tmp}/ && cd {tmp} && timeout 120 {sqc} a -q -ep -fmt {format} {switches} {(volumes ? "-mv 140000" : "")} {tmp}/set.sqx {tmp}/noise.bin {tmp}/words.txt {tmp}/last.bin < /dev/null; echo RC=$?; cp {tmp}/set.* {w}/; rm -rf {tmp}");
      Assert.That(run.StdOut, Does.Contain("RC=0"), run.StdOut + run.StdErr);
      Assert.That(File.Exists(Path.Combine(dir, "set.001")), Is.EqualTo(volumes), "a multi-volume set");
      using var reader = new SqxReader(File.OpenRead(Path.Combine(dir, "set.sqx")));
      Assert.That(reader.VolumeCount > 1, Is.EqualTo(volumes));
      var results = new List<string>();
      foreach (var e in reader.Entries) {
        try {
          results.Add($"{e.FileName}: {(reader.ExtractEntry(e).AsSpan().SequenceEqual(files[e.FileName]) ? "same" : "DIFFERENT")}");
        } catch (Exception x) {
          results.Add($"{e.FileName}: {x.GetType().Name} {x.Message}");
        }
      }
      Assert.That(results, Is.All.EndsWith("same"), string.Join("\n", results));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Exception")]
  public void Given_AMissingVolume_When_Opened_Then_ItIsNamed() {
    var dir = Stage("mvol.sqx", "mvol.002");
    try {
      using var stream = File.OpenRead(Path.Combine(dir, "mvol.sqx"));
      var e = Assert.Throws<InvalidDataException>(() => _ = new SqxReader(stream, leaveOpen: true));
      Assert.That(e!.Message, Does.Contain("mvol.001"));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Exception")]
  public void Given_TheFirstVolumeAlone_When_TheSplitFileIsExtracted_Then_ItIsRefused() {
    var dir = Stage("mvol.sqx");
    try {
      using var reader = new SqxReader(new MemoryStream(File.ReadAllBytes(Path.Combine(dir, "mvol.sqx"))));
      Assert.That(reader.ExtractEntry(reader.Entries[0]), Has.Length.EqualTo(85890));
      Assert.Throws<NotSupportedException>(() => reader.ExtractEntry(reader.Entries[1]));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Exception")]
  public void Given_AMiddleVolumeAlone_When_ItsSplitFileIsExtracted_Then_ItIsRefused() {
    var dir = Stage("mvol.001");
    try {
      using var reader = new SqxReader(new MemoryStream(File.ReadAllBytes(Path.Combine(dir, "mvol.001"))));
      Assert.Throws<NotSupportedException>(() => reader.ExtractEntry(reader.Entries[0]));
    } finally { Directory.Delete(dir, true); }
  }
}
