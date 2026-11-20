using System.Buffers.Binary;
using FileFormat.NowCompress;

namespace Compression.Tests.NowCompress;

/// <summary>
/// Now Compress: structural checks plus real archives. The only real archives found are commercial
/// Mac installers (the dexvert sample set), which cannot be redistributed, so those checks run on a
/// directory named by <c>CWB_NOWCOMPRESS_SAMPLES</c> (raw or MacBinary-wrapped).
/// </summary>
/// <remarks>
/// The format stores no checksum of the data, so each decoded resource fork is checked against its
/// own header (data offset + data length = map offset, map offset + map length = fork size): a
/// mis-decoded fork does not keep that invariant. The optional live interop check compares matching
/// data-fork paths. NowCompressOracleTests additionally compares every paired data/resource fork
/// against pinned native unar 1.10.7 results, independently of extraction filename sanitization.
/// </remarks>
[TestFixture]
public class NowCompressTests {

  [Test, Category("Exception")]
  public void GivenRandomBytes_WhenRead_ThenRefused() {
    var bytes = new byte[512];
    new Random(3).NextBytes(bytes);
    bytes[0] = 0; bytes[1] = 2;
    Assert.That(NowCompressArchive.IsNowCompress(bytes), Is.False);
    Assert.That(() => NowCompressArchive.Read(new MemoryStream(bytes)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenTooShortInput_WhenRead_ThenRefused()
    => Assert.That(() => NowCompressArchive.Read(new MemoryStream([0, 2, 0, 0x60])), Throws.InstanceOf<InvalidDataException>());

  private static IEnumerable<(string File, byte[] Bytes, int Offset)> Samples() {
    var samples = Environment.GetEnvironmentVariable("CWB_NOWCOMPRESS_SAMPLES");
    if (string.IsNullOrEmpty(samples) || !Directory.Exists(samples)) Assert.Ignore("CWB_NOWCOMPRESS_SAMPLES is not set.");
    foreach (var file in Directory.EnumerateFiles(samples!)) {
      var bytes = File.ReadAllBytes(file);
      var offset = bytes.Length > 140 && NowCompressArchive.IsNowCompress(bytes.AsSpan(128)) ? 128 : 0;
      if (NowCompressArchive.IsNowCompress(bytes.AsSpan(offset))) yield return (file, bytes, offset);
    }
  }

  [Test, Category("ThemVsUs")]
  public void GivenLocalSampleArchives_WhenDecoded_ThenEveryResourceForkIsSelfConsistent() {
    var forks = 0;
    foreach (var (file, bytes, offset) in Samples()) {
      foreach (var e in NowCompressArchive.Read(new MemoryStream(bytes, offset, bytes.Length - offset)).Where(x => x.ResourceSize >= 16)) {
        var r = e.Resource;
        var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(r);
        var mapOffset = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4));
        var dataLength = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(8));
        var mapLength = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(12));
        Assert.That((long)dataOffset + dataLength, Is.EqualTo(mapOffset), $"{file}: {e.Path} resource data does not meet its map");
        Assert.That((long)mapOffset + mapLength, Is.EqualTo(r.Length), $"{file}: {e.Path} resource map does not end the fork");
        ++forks;
      }
    }
    Assert.That(forks, Is.GreaterThan(0), "no resource fork found in the samples");
  }

  [Test, Category("ExternalInterop")]
  public void GivenLocalSampleArchives_WhenUnarCanExtractThem_ThenEveryDataForkMatches() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var compared = 0;
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_now_{Guid.NewGuid():N}");
    try {
      foreach (var (file, bytes, offset) in Samples()) {
        Directory.CreateDirectory(tmp);
        var raw = Path.Combine(tmp, Path.GetFileName(file) + ".now");
        File.WriteAllBytes(raw, bytes[offset..]);
        var outDir = Path.Combine(tmp, Path.GetFileName(file) + ".out");
        var r = FsInteropToolbox.RunWsl($"unar -q -f -k hidden -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(raw)}'");
        if (r.ExitCode != 0) continue; // Older unar 1.10.1 stops on these samples with "read more data than was available"
        foreach (var e in NowCompressArchive.Read(new MemoryStream(bytes, offset, bytes.Length - offset)).Where(x => !x.IsDirectory && x.DataSize > 0)) {
          var theirs = Path.Combine(outDir, e.Path.Replace('/', Path.DirectorySeparatorChar));
          if (!File.Exists(theirs)) continue;
          Assert.That(e.Data, Is.EqualTo(File.ReadAllBytes(theirs)), $"{file}: {e.Path}");
          ++compared;
        }
      }
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
    if (compared == 0) Assert.Inconclusive("unar extracted none of the samples, so nothing could be compared.");
  }

  // ── detection ────────────────────────────────────────────────────────

  private static Compression.Lib.FormatDetector.Format DetectFile(byte[] bytes) {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_now_{Guid.NewGuid():N}");
    File.WriteAllBytes(path, bytes);
    try {
      return Compression.Lib.FormatDetector.Detect(path);
    } finally {
      File.Delete(path);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void GivenLocalSampleArchives_WhenDetectedWithoutAName_ThenEveryOneIsNowCompress() {
    var detected = 0;
    foreach (var (file, bytes, offset) in Samples()) {
      Assert.That(DetectFile(bytes[offset..]), Is.EqualTo(Compression.Lib.FormatDetector.Format.NowCompress), file);
      ++detected;
    }
    Assert.That(detected, Is.EqualTo(11), "the pinned sample set has eleven archives");
  }

  // The authored five-folder archive (ReferenceVectors/README.md); its header sum is left 0.
  private static byte[] Written() {
    using var input = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("NowCompressVectors.macroman-names.now")
      ?? throw new FileNotFoundException("Embedded Now Compress vector 'macroman-names.now' is missing.");
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    return ms.ToArray();
  }

  private static void SetHeader(byte[] archive, ReadOnlySpan<byte> bytes2To7) {
    bytes2To7.CopyTo(archive.AsSpan(2));
    uint sum = 0;
    for (var i = 0; i < 20; ++i) sum += archive[i];
    BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(20), sum);
  }

  [Test, Category("HappyPath")]
  public void GivenTheAuthoredArchive_WhenDetectedWithoutAName_ThenNowCompress()
    => Assert.That(DetectFile(Written()), Is.EqualTo(Compression.Lib.FormatDetector.Format.NowCompress));

  // The header form one of the eleven samples has (00 02 00 10 00 00 80 00).
  [Test, Category("EdgeCase")]
  public void GivenTheOtherHeaderForm_WhenDetectedWithoutAName_ThenNowCompress() {
    var archive = Written();
    SetHeader(archive, [0x00, 0x10, 0x00, 0x00, 0x80, 0x00]);
    Assert.That(DetectFile(archive), Is.EqualTo(Compression.Lib.FormatDetector.Format.NowCompress));
    Assert.That(NowCompressArchive.Read(new MemoryStream(archive)), Has.Count.EqualTo(5));
  }

  [Test, Category("Exception")]
  public void GivenTheOtherHeaderFormWithAWrongHeaderSum_WhenDetected_ThenNotNowCompress() {
    var archive = Written();
    SetHeader(archive, [0x00, 0x10, 0x00, 0x00, 0x80, 0x00]);
    archive[23] ^= 1;
    Assert.That(DetectFile(archive), Is.Not.EqualTo(Compression.Lib.FormatDetector.Format.NowCompress));
  }

  [Test, Category("Exception")]
  public void GivenTheOtherHeaderFormWithAWrongRecordSum_WhenDetected_ThenNotNowCompress() {
    var archive = Written();
    SetHeader(archive, [0x00, 0x10, 0x00, 0x00, 0x80, 0x00]);
    archive[24 + 106 + 3] ^= 1;
    Assert.That(DetectFile(archive), Is.Not.EqualTo(Compression.Lib.FormatDetector.Format.NowCompress));
  }

  [Test, Category("Exception")]
  public void GivenRandomBytesStartingLikeNowCompress_WhenDetected_ThenNotNowCompress() {
    var rng = new Random(11);
    for (var n = 0; n < 200; ++n) {
      var bytes = new byte[512];
      rng.NextBytes(bytes);
      bytes[0] = 0; bytes[1] = 2;
      Assert.That(Compression.Lib.FormatDetector.DetectByMagic(bytes), Is.Not.EqualTo(Compression.Lib.FormatDetector.Format.NowCompress));
    }
  }
}
