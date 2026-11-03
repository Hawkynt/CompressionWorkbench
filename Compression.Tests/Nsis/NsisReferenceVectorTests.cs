using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Lib;
using FileFormat.Nsis;

namespace Compression.Tests.Nsis;

/// <summary>
/// Real installers compiled by makensis 2.51 and 3.10 (see ReferenceVectors/README.md), checked
/// against what 7-Zip extracts from them (oracle.tsv).
/// </summary>
[TestFixture]
public class NsisReferenceVectorTests {

  private static readonly string[] Fixtures = [
    "nsis3-zlib-nonsolid.exe", "nsis3-zlib-solid.exe", "nsis3-bzip2-nonsolid.exe", "nsis3-bzip2-solid.exe",
    "nsis3-lzma-nonsolid.exe", "nsis3-lzma-solid.exe", "nsis3-lzma-solid-ansi.exe",
    "nsis2-zlib-nonsolid.exe", "nsis2-zlib-solid.exe", "nsis2-bzip2-solid.exe", "nsis2-lzma-nonsolid.exe",
    // WriteUninstaller: uninst.exe is the exe stub with the uninstaller's icon patch, then its header.
    "nsis3-lzma-solid-uninst.exe", "nsis3-zlib-nonsolid-uninst.exe", "nsis3-stored-uninst.exe",
    "nsis2-bzip2-solid-uninst.exe", "nsis2-lzma-nonsolid-uninst.exe",
    // 7-Zip 4.42's own installer: solid LZMA behind the x86 branch filter, plus Uninstall.exe.
    "7z442.exe",
  ];

  private static byte[] Load(string name) {
    using var resource = typeof(NsisReferenceVectorTests).Assembly.GetManifestResourceStream("NsisVectors." + name + ".gz")
      ?? throw new InvalidOperationException($"Missing embedded fixture {name}.");
    using var gz = new GZipStream(resource, CompressionMode.Decompress); // BCL as the test-side oracle
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, (long Size, string Sha)> Oracle(string fixture) {
    using var resource = typeof(NsisReferenceVectorTests).Assembly.GetManifestResourceStream("NsisVectors.oracle.tsv")!;
    using var reader = new StreamReader(resource);
    var result = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
    while (reader.ReadLine() is { } line) {
      if (line.StartsWith('#') || line.Length == 0)
        continue;
      var f = line.Split('\t');
      if (f[0] == fixture)
        result[f[1]] = (long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), f[3]);
    }
    return result;
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCaseSource(nameof(Fixtures))]
  public void GivenRealInstaller_WhenListed_ThenNamesAndSizesMatch7Zip(string fixture) {
    var oracle = Oracle(fixture);
    Assert.That(oracle, Is.Not.Empty);

    var entries = new NsisFormatDescriptor().List(new MemoryStream(Load(fixture)), null);

    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(oracle.Keys));
    foreach (var e in entries)
      Assert.That(e.OriginalSize, Is.EqualTo(oracle[e.Name].Size), e.Name);
  }

  [TestCaseSource(nameof(Fixtures))]
  public void GivenRealInstaller_WhenExtracted_ThenEveryFileMatches7Zip(string fixture) {
    var oracle = Oracle(fixture);
    var dir = Path.Combine(Path.GetTempPath(), "cwb-nsis-" + Guid.NewGuid().ToString("N"));
    try {
      new NsisFormatDescriptor().Extract(new MemoryStream(Load(fixture)), dir, null, null);
      var written = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), p => Sha(File.ReadAllBytes(p)));
      Assert.That(written.Keys, Is.EquivalentTo(oracle.Keys));
      foreach (var (name, sha) in written)
        Assert.That(sha, Is.EqualTo(oracle[name].Sha), name);
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    }
  }

  [TestCase("nsis3-zlib-nonsolid.exe", NsisCompression.Zlib, false, true, true)]
  [TestCase("nsis3-bzip2-solid.exe", NsisCompression.BZip2, true, true, true)]
  [TestCase("nsis3-lzma-solid-ansi.exe", NsisCompression.Lzma, true, false, true)]
  [TestCase("nsis2-zlib-nonsolid.exe", NsisCompression.Zlib, false, false, false)]
  [TestCase("nsis2-bzip2-solid.exe", NsisCompression.BZip2, true, false, false)]
  public void GivenRealInstaller_WhenOpened_ThenCompressorAndDialectAreRecognised(string fixture, NsisCompression method, bool solid, bool unicode, bool nsis3) {
    using var reader = new NsisReader(new MemoryStream(Load(fixture)));

    Assert.Multiple(() => {
      Assert.That(reader.Compression, Is.EqualTo(method));
      Assert.That(reader.IsSolid, Is.EqualTo(solid));
      Assert.That(reader.IsUnicode, Is.EqualTo(unicode));
      Assert.That(reader.IsNsis3, Is.EqualTo(nsis3));
      Assert.That(reader.FirstHeaderOffset % 512, Is.Zero);
    });
  }

  [Test]
  public void GivenSevenZipsOwnInstaller_WhenOpened_ThenTheBranchFilteredLzmaStreamIsRecognised() {
    using var reader = new NsisReader(new MemoryStream(Load("7z442.exe")));

    Assert.Multiple(() => {
      Assert.That(reader.Compression, Is.EqualTo(NsisCompression.Lzma));
      Assert.That(reader.IsSolid, Is.True);
      Assert.That(reader.HasFilterFlag, Is.True);
      Assert.That(reader.Entries.Count(e => e.IsUninstaller), Is.EqualTo(1));
    });
  }

  [Test]
  public void GivenBranchFilteredInstaller_WhenOnlyTheUninstallerIsExtracted_ThenItMatches7Zip() {
    var oracle = Oracle("7z442.exe");
    using var reader = new NsisReader(new MemoryStream(Load("7z442.exe")));
    var uninstaller = reader.Entries.Single(e => e.IsUninstaller);

    var data = reader.Extract(uninstaller);

    Assert.That(Sha(data), Is.EqualTo(oracle[uninstaller.FileName].Sha));
  }

  [TestCase("nsis3-lzma-solid-uninst.exe")]
  [TestCase("nsis3-zlib-nonsolid-uninst.exe")]
  public void GivenInstallerWithUninstaller_WhenOnlyTheUninstallerIsExtracted_ThenItStartsWithThePatchedStub(string fixture) {
    var full = Load(fixture);
    using var reader = new NsisReader(new MemoryStream(full));
    var uninstaller = reader.Entries.Single(e => e.IsUninstaller);

    var data = reader.Extract(uninstaller);

    Assert.Multiple(() => {
      Assert.That(uninstaller.FileName, Is.EqualTo("uninst.exe"));
      Assert.That(uninstaller.UninstallerPatchSize, Is.GreaterThan(0));
      Assert.That(Sha(data), Is.EqualTo(Oracle(fixture)["uninst.exe"].Sha));
      // The icon patch changes the stub, which therefore differs from the installer's.
      Assert.That(data.AsSpan(0, (int)reader.FirstHeaderOffset).SequenceEqual(full.AsSpan(0, (int)reader.FirstHeaderOffset)), Is.False);
    });
  }

  [Test]
  public void GivenUninstallerPatchPointingPastTheStub_WhenExtracted_ThenItIsRefusedNotWritten() {
    // SetCompress off: the header and every block are stored, so the icon patch can be edited in
    // place. Its first record is redirected beyond the end of the exe stub.
    var full = Load("nsis3-stored-uninst.exe");
    var damaged = (byte[])full.Clone();
    using (var probe = new NsisReader(new MemoryStream(full))) {
      var uninstaller = probe.Entries.Single(e => e.IsUninstaller);
      var dataStart = probe.FirstHeaderOffset + 28 + 4 + probe.HeaderLength;
      var record = (int)(dataStart + uninstaller.DataOffset + 4);
      Assert.That(BitConverter.ToUInt32(full, record - 4), Is.EqualTo((uint)uninstaller.UninstallerPatchSize), "stored patch block");
      BitConverter.TryWriteBytes(damaged.AsSpan(record + 4), (uint)probe.FirstHeaderOffset);
    }

    using var reader = new NsisReader(new MemoryStream(damaged));
    var entry = reader.Entries.Single(e => e.IsUninstaller);

    Assert.Throws<InvalidDataException>(() => reader.Extract(entry));
  }

  [Test]
  public void GivenSolidInstaller_WhenOnlyTheLastFileIsExtracted_ThenItMatches7Zip() {
    var oracle = Oracle("nsis3-lzma-solid.exe");
    using var reader = new NsisReader(new MemoryStream(Load("nsis3-lzma-solid.exe")));
    var last = reader.Entries[^1];

    var data = reader.Extract(last);

    Assert.That(Sha(data), Is.EqualTo(oracle[last.FileName].Sha));
  }

  [Test]
  public void GivenRealInstaller_WhenListed_ThenFileTimesAreReported() {
    var entries = new NsisFormatDescriptor().List(new MemoryStream(Load("nsis3-lzma-nonsolid.exe")), null);
    Assert.That(entries.All(e => e.LastModified is { Year: >= 2020 }), Is.True);
  }

  [Test]
  public void GivenRealInstallerOnDisk_WhenDetected_ThenNsisIsChosen() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-nsis-" + Guid.NewGuid().ToString("N") + ".exe");
    try {
      File.WriteAllBytes(path, Load("nsis3-zlib-solid.exe"));
      Assert.That(FormatDetector.Detect(path), Is.EqualTo(FormatDetector.Format.Nsis));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenInstallerCutInsideItsData_WhenListed_ThenAPartialMetadataEntryIsReturned() {
    var full = Load("nsis3-lzma-solid.exe");
    using var probe = new NsisReader(new MemoryStream(full));
    var cut = full.AsSpan(0, (int)probe.FirstHeaderOffset + 40).ToArray();

    var entries = new NsisFormatDescriptor().List(new MemoryStream(cut), null);

    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [Test]
  public void GivenFirstHeaderOffTheAlignment_WhenOpened_ThenNoInstallerIsFound() {
    var full = Load("nsis3-zlib-nonsolid.exe");
    var shifted = new byte[full.Length + 1];
    full.CopyTo(shifted.AsSpan(1));

    Assert.Throws<InvalidDataException>(() => _ = new NsisReader(new MemoryStream(shifted)));
  }

  [Test]
  public void GivenNoStream_WhenOpened_ThenArgumentNullException()
    => Assert.Throws<ArgumentNullException>(() => _ = new NsisReader(null!));

  [Test]
  public void GivenEmptyStream_WhenOpened_ThenInvalidDataException()
    => Assert.Throws<InvalidDataException>(() => _ = new NsisReader(new MemoryStream()));

  [Test]
  public void GivenUnknownEntryName_WhenExtractedToMemory_ThenNoBytes() {
    var bytes = new NsisFormatDescriptor().ExtractEntryToMemory(new MemoryStream(Load("nsis3-zlib-solid.exe")), "nope.txt", null);
    Assert.That(bytes, Is.Empty);
  }
}
