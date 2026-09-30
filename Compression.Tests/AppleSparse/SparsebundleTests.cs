using System.Globalization;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.AppleSparse;

namespace Compression.Tests.AppleSparse;

[TestFixture]
public class SparsebundleTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // ── Descriptor ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new SparsebundleFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(desc.Id, Is.EqualTo("Sparsebundle"));
      Assert.That(desc.DefaultExtension, Is.EqualTo(".sparsebundle"));
      Assert.That(desc.Extensions, Does.Contain(".sparsebundle"));
      Assert.That(desc.Category, Is.EqualTo(FormatCategory.Archive));
      Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
      Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
      Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
    });
  }

  [Test, Category("EquivalenceClass")]
  public void Descriptor_Description_DocumentsTarTransport() {
    var desc = new SparsebundleFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("TAR transport"));
  }

  // ── Plist parsing ──────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Plist_ParsesBandSizeAndTotalSize() {
    var xml = BuildPlist(bandSize: 4096, totalSize: 16384);
    var dict = InfoPlistParser.ParseTopLevelDict(Encoding.UTF8.GetBytes(xml));
    Assert.Multiple(() => {
      Assert.That(dict["band-size"], Is.EqualTo("4096"));
      Assert.That(dict["size"], Is.EqualTo("16384"));
      Assert.That(InfoPlistParser.GetInt64(dict, "band-size"), Is.EqualTo(4096));
      Assert.That(InfoPlistParser.GetInt64(dict, "size"), Is.EqualTo(16384));
    });
  }

  [Test, Category("ErrorHandling")]
  public void Plist_GarbageInput_ReturnsEmpty() {
    var dict = InfoPlistParser.ParseTopLevelDict("not xml at all"u8.ToArray());
    Assert.That(dict, Is.Empty);
  }

  // ── Reader from disk ───────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Reader_OpensBundle_AndReadsBands() {
    using var bundle = BuildSyntheticBundle(bandSize: 1024, virtualSize: 4096, populateBands: [
      (0, RandomBytes(1024, seed: 1)),
      (2, RandomBytes(1024, seed: 2)),
    ]);

    var r = new SparsebundleReader(bundle.Path);
    Assert.Multiple(() => {
      Assert.That(r.BandSize, Is.EqualTo(1024));
      Assert.That(r.VirtualSize, Is.EqualTo(4096));
      Assert.That(r.BackingStoreVersion, Is.EqualTo(1));
    });

    var disk = r.ExtractDisk();
    // Band 0: random; Band 1: zeros (missing); Band 2: random; Band 3: zeros (missing)
    Assert.That(disk.AsSpan(0, 1024).ToArray(), Is.EqualTo(RandomBytes(1024, seed: 1)));
    Assert.That(disk.AsSpan(1024, 1024).ToArray(), Is.EqualTo(new byte[1024]),
      "Missing band should read as zeros");
    Assert.That(disk.AsSpan(2048, 1024).ToArray(), Is.EqualTo(RandomBytes(1024, seed: 2)));
    Assert.That(disk.AsSpan(3072, 1024).ToArray(), Is.EqualTo(new byte[1024]),
      "Missing trailing band should read as zeros");
  }

  [Test, Category("HappyPath")]
  public void Reader_TryFromPath_AcceptsBundleRootOrInfoPlist() {
    using var bundle = BuildSyntheticBundle(bandSize: 512, virtualSize: 512, populateBands: [(0, new byte[512])]);
    Assert.Multiple(() => {
      Assert.That(SparsebundleReader.TryFromPath(bundle.Path), Is.Not.Null);
      Assert.That(SparsebundleReader.TryFromPath(Path.Combine(bundle.Path, "Info.plist")), Is.Not.Null);
      Assert.That(SparsebundleReader.TryFromPath(Path.Combine(bundle.Path, "nonexistent")), Is.Null);
    });
  }

  [Test, Category("ErrorHandling")]
  public void Reader_MissingInfoPlist_Throws() {
    var tmp = Path.Combine(Path.GetTempPath(), "cwb_sb_bad_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(Path.Combine(tmp, "bands"));
      Assert.Throws<FileNotFoundException>(() => _ = new SparsebundleReader(tmp));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void Reader_MissingBands_Throws() {
    var tmp = Path.Combine(Path.GetTempPath(), "cwb_sb_bad_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmp);
      File.WriteAllText(Path.Combine(tmp, "Info.plist"), BuildPlist(1024, 1024));
      Assert.Throws<DirectoryNotFoundException>(() => _ = new SparsebundleReader(tmp));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Stream_ReadsAcrossBands() {
    using var bundle = BuildSyntheticBundle(bandSize: 512, virtualSize: 2048, populateBands: [
      (0, RandomBytes(512, seed: 10)),
      (1, RandomBytes(512, seed: 11)),
      (2, RandomBytes(512, seed: 12)),
      (3, RandomBytes(512, seed: 13)),
    ]);
    var reader = new SparsebundleReader(bundle.Path);
    using var s = new SparsebundleStream(reader);
    var buf = new byte[2048];
    Assert.That(s.Length, Is.EqualTo(2048));
    s.Position = 0;
    s.ReadExactly(buf);

    Assert.Multiple(() => {
      Assert.That(buf.AsSpan(0, 512).ToArray(), Is.EqualTo(RandomBytes(512, seed: 10)));
      Assert.That(buf.AsSpan(512, 512).ToArray(), Is.EqualTo(RandomBytes(512, seed: 11)));
      Assert.That(buf.AsSpan(1024, 512).ToArray(), Is.EqualTo(RandomBytes(512, seed: 12)));
      Assert.That(buf.AsSpan(1536, 512).ToArray(), Is.EqualTo(RandomBytes(512, seed: 13)));
    });
  }

  // ── Descriptor list/extract via FileStream over Info.plist ────────

  [Test, Category("HappyPath")]
  public void Descriptor_List_FromInfoPlistFileStream_SurfacesBundleEntries() {
    using var bundle = BuildSyntheticBundle(bandSize: 1024, virtualSize: 2048, populateBands: [
      (0, RandomBytes(1024, seed: 21)),
    ]);
    var infoPath = Path.Combine(bundle.Path, "Info.plist");
    using var fs = new FileStream(infoPath, FileMode.Open, FileAccess.Read, FileShare.Read);

    var desc = new SparsebundleFormatDescriptor();
    var entries = desc.List(fs, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_FromMemoryStream_FallsBackToPlistMetadata() {
    var xml = BuildPlist(bandSize: 1024, totalSize: 4096);
    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
    var desc = new SparsebundleFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "disk.img"), Is.True);
    var diskImg = entries.First(e => e.Name == "disk.img");
    Assert.That(diskImg.OriginalSize, Is.EqualTo(4096));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_FromInfoPlistFileStream_WritesDiskImg() {
    var bandData = RandomBytes(1024, seed: 31);
    using var bundle = BuildSyntheticBundle(bandSize: 1024, virtualSize: 1024, populateBands: [(0, bandData)]);
    var infoPath = Path.Combine(bundle.Path, "Info.plist");
    using var fs = new FileStream(infoPath, FileMode.Open, FileAccess.Read, FileShare.Read);

    var tmp = Path.Combine(Path.GetTempPath(), "cwb_sb_extract_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmp);
      var desc = new SparsebundleFormatDescriptor();
      desc.Extract(fs, tmp, null, null);
      var diskImg = Path.Combine(tmp, "disk.img");
      Assert.That(File.Exists(diskImg), Is.True);
      Assert.That(File.ReadAllBytes(diskImg), Is.EqualTo(bandData));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_CreateAndExtract_RawDisk_PreservesBytesAndSparseBands() {
    var disk = new byte[4096];
    new Random(42).NextBytes(disk.AsSpan(1024, 1024));
    using var archive = new MemoryStream();
    var desc = new SparsebundleFormatDescriptor();
    desc.Create(archive, [ArchiveInputInfo.InMemory("disk.img", disk)], new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BandSize"] = "1024" },
    });
    archive.Position = 0;
    var entries = desc.List(archive, null);
    Assert.That(entries.Any(e => e.Name == "bands/1"), Is.True);

    archive.Position = 0;
    var output = Path.Combine(Path.GetTempPath(), "cwb_sb_tar_" + Guid.NewGuid().ToString("N"));
    try {
      desc.Extract(archive, output, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(output, "bands", "1")), Is.EqualTo(disk.AsSpan(1024, 1024).ToArray()));
      Assert.That(File.Exists(Path.Combine(output, "Info.bckup")), Is.True);
      Assert.That(File.Exists(Path.Combine(output, "token")), Is.True);
      Assert.That(new SparsebundleReader(output).ExtractDisk(), Is.EqualTo(disk));
    } finally {
      if (Directory.Exists(output)) Directory.Delete(output, true);
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_BundleMembers_PreservesMetadataAndBandBytes() {
    var plist = Encoding.UTF8.GetBytes(BuildPlist(512, 777));
    var backup = "backup-metadata"u8.ToArray();
    var band = RandomBytes(512, seed: 73);
    using var archive = new MemoryStream();
    var desc = new SparsebundleFormatDescriptor();
    desc.Create(archive, [
      ArchiveInputInfo.InMemory("Info.plist", plist),
      ArchiveInputInfo.InMemory("Info.bckup", backup),
      ArchiveInputInfo.InMemory("token", Array.Empty<byte>()),
      ArchiveInputInfo.InMemory("bands/a", band),
      ArchiveInputInfo.InMemory("com.apple.TimeMachine.MachineID.plist", "machine"u8.ToArray()),
    ], new FormatCreateOptions());

    archive.Position = 0;
    var output = Path.Combine(Path.GetTempPath(), "cwb_sb_members_" + Guid.NewGuid().ToString("N"));
    try {
      desc.Extract(archive, output, null, null);
      Assert.Multiple(() => {
        Assert.That(File.ReadAllBytes(Path.Combine(output, "Info.plist")), Is.EqualTo(plist));
        Assert.That(File.ReadAllBytes(Path.Combine(output, "Info.bckup")), Is.EqualTo(backup));
        Assert.That(File.ReadAllBytes(Path.Combine(output, "bands", "a")), Is.EqualTo(band));
        Assert.That(File.ReadAllBytes(Path.Combine(output, "com.apple.TimeMachine.MachineID.plist")), Is.EqualTo("machine"u8.ToArray()));
      });
    } finally {
      if (Directory.Exists(output)) Directory.Delete(output, true);
    }
  }

  private static Dictionary<string, byte[]> CreateMembers(byte[] disk, int bandSize) {
    using var archive = new MemoryStream();
    new SparsebundleFormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory("disk.img", disk)], new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BandSize"] = bandSize.ToString(CultureInfo.InvariantCulture) },
    });
    var output = Path.Combine(Path.GetTempPath(), "cwb_sb_members_" + Guid.NewGuid().ToString("N"));
    try {
      archive.Position = 0;
      new SparsebundleFormatDescriptor().Extract(archive, output, null, null);
      return Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(output, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
    } finally {
      if (Directory.Exists(output)) Directory.Delete(output, true);
    }
  }

  [Test, Category("EquivalenceClass")]
  public void GivenARawDisk_WhenCreating_ThenInfoPlistCarriesTheKeysHdiutilWrites() {
    var members = CreateMembers(RandomBytes(3000, seed: 5), 1024);
    var plist = Encoding.UTF8.GetString(members["Info.plist"]);
    Assert.Multiple(() => {
      Assert.That(plist, Does.Contain("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\""));
      foreach (var key in new[] { "CFBundleInfoDictionaryVersion", "band-size", "bundle-backingstore-version", "diskimage-bundle-type", "size" })
        Assert.That(plist, Does.Contain($"<key>{key}</key>"), key);
      Assert.That(plist, Does.Contain("<integer>1024</integer>"));
      Assert.That(plist, Does.Contain("<integer>3000</integer>"));
      Assert.That(members["Info.bckup"], Is.EqualTo(members["Info.plist"]), "Info.bckup is the backup copy of Info.plist");
      Assert.That(members["token"], Is.Empty);
    });
  }

  [TestCase(4096, 1024, new[] { "bands/0", "bands/1", "bands/2", "bands/3" }, TestName = "GivenADiskOfWholeBands_WhenCreating_ThenEveryBandIsFull")]
  [TestCase(3000, 1024, new[] { "bands/0", "bands/1", "bands/2" }, TestName = "GivenAPartialLastBand_WhenCreating_ThenTheLastBandIsShort")]
  [TestCase(17 * 1024, 1024, new[] { "bands/0", "bands/10" }, TestName = "GivenSeventeenBands_WhenCreating_ThenBandNamesAreLowercaseHex")]
  [Category("BoundaryCase")]
  public void Create_SplitsTheDiskIntoHexNamedBands(int diskSize, int bandSize, string[] mustExist) {
    var disk = RandomBytes(diskSize, seed: diskSize);
    var members = CreateMembers(disk, bandSize);
    Assert.Multiple(() => {
      foreach (var band in mustExist) Assert.That(members.ContainsKey(band), Is.True, band);
      var last = "bands/" + ((diskSize - 1) / bandSize).ToString("x", CultureInfo.InvariantCulture);
      Assert.That(members[last].Length, Is.EqualTo(diskSize - (diskSize - 1) / bandSize * bandSize));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenAnAllZeroDisk_WhenCreating_ThenNoBandIsStored() {
    var members = CreateMembers(new byte[4096], 1024);
    Assert.That(members.Keys.Where(k => k.StartsWith("bands/", StringComparison.Ordinal)), Is.Empty);
  }

  [TestCase(0, TestName = "GivenABandSizeOfZero_WhenCreating_ThenItIsRejected")]
  [TestCase(-1, TestName = "GivenANegativeBandSize_WhenCreating_ThenItIsRejected")]
  [Category("Exceptional")]
  public void Create_RejectsNonPositiveBandSizes(int bandSize)
    => Assert.That(() => CreateMembers(new byte[16], bandSize), Throws.TypeOf<ArgumentOutOfRangeException>());

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_FromPlistAndDisk_RebuildsBandsAndKeepsPlist() {
    var disk = RandomBytes(2048, seed: 91);
    var plist = Encoding.UTF8.GetBytes(BuildPlist(1024, disk.Length));
    using var archive = new MemoryStream();
    var desc = new SparsebundleFormatDescriptor();
    desc.Create(archive, [
      ArchiveInputInfo.InMemory("Info.plist", plist),
      ArchiveInputInfo.InMemory("Info.bckup", plist),
      ArchiveInputInfo.InMemory("disk.img", disk),
    ], new FormatCreateOptions());

    archive.Position = 0;
    var output = Path.Combine(Path.GetTempPath(), "cwb_sb_rebuild_" + Guid.NewGuid().ToString("N"));
    try {
      desc.Extract(archive, output, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(output, "Info.plist")), Is.EqualTo(plist));
      Assert.That(new SparsebundleReader(output).ExtractDisk(), Is.EqualTo(disk));
    } finally {
      if (Directory.Exists(output)) Directory.Delete(output, true);
    }
  }

  // ── Helpers ────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a synthetic <c>*.sparsebundle</c> on disk with the requested
  /// geometry and band contents. Bands are written into <c>bands/</c> using
  /// the hex-lowercase virtual-band-index naming convention used by
  /// <c>hdiutil</c>.
  /// </summary>
  private static DisposableDirectory BuildSyntheticBundle(long bandSize, long virtualSize, (long Index, byte[] Data)[] populateBands) {
    var root = Path.Combine(Path.GetTempPath(),
      "cwb_sb_" + Guid.NewGuid().ToString("N")[..8] + ".sparsebundle");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(Path.Combine(root, "bands"));

    File.WriteAllText(Path.Combine(root, "Info.plist"), BuildPlist(bandSize, virtualSize));
    File.WriteAllText(Path.Combine(root, "Info.bckup"), BuildPlist(bandSize, virtualSize));
    File.WriteAllBytes(Path.Combine(root, "token"), Array.Empty<byte>());

    foreach (var (idx, data) in populateBands) {
      var name = idx.ToString("x", CultureInfo.InvariantCulture);
      File.WriteAllBytes(Path.Combine(root, "bands", name), data);
    }

    return new DisposableDirectory(root);
  }

  private static string BuildPlist(long bandSize, long totalSize) => $"""
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0">
<dict>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>band-size</key>
  <integer>{bandSize}</integer>
  <key>bundle-backingstore-version</key>
  <integer>1</integer>
  <key>diskimage-bundle-type</key>
  <string>com.apple.diskimage.sparsebundle</string>
  <key>size</key>
  <integer>{totalSize}</integer>
</dict>
</plist>
""";

  private static byte[] RandomBytes(int length, int seed) {
    var buf = new byte[length];
    new Random(seed).NextBytes(buf);
    return buf;
  }

  private sealed class DisposableDirectory(string path) : IDisposable {
    public string Path { get; } = path;
    public void Dispose() {
      try { if (Directory.Exists(this.Path)) Directory.Delete(this.Path, recursive: true); } catch { /* best-effort */ }
    }
  }
}
