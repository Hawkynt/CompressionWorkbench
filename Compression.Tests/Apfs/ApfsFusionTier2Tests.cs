using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// A Fusion container macOS made from two disk images (<c>diskutil apfs createContainer -main</c>
/// on a GitHub-hosted macOS 15 runner, <c>tools/foreign-os-oracles/apfs-fusion-capture.sh</c>):
/// every file's data lies on the tier-2 device, and the SHA-256 of what the script wrote is
/// the oracle (macOS wrote it through APFS); fsck_apfs called the container OK. The embedded pair is the small capture; set
/// <c>CWB_APFS_FUSION_DIR</c> to a directory holding a larger one (main.img, tier2.img,
/// manifest.json from the same script) to check that too.
/// </summary>
[TestFixture]
public sealed class ApfsFusionTier2Tests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_fusion_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static byte[] Embedded(string name) {
    using var resource = typeof(ApfsFusionTier2Tests).Assembly.GetManifestResourceStream("ApfsVectors." + name)
                         ?? throw new InvalidOperationException($"{name} is not embedded.");
    using var buffer = new MemoryStream();
    if (name.EndsWith(".gz", StringComparison.Ordinal)) {
      using var gzip = new GZipStream(resource, CompressionMode.Decompress);
      gzip.CopyTo(buffer);
    } else {
      resource.CopyTo(buffer);
    }
    return buffer.ToArray();
  }

  private static (byte[] Main, byte[] Tier2, Dictionary<string, (long Size, string Sha256)> Manifest) Small()
    => (Embedded("apfs-fusion-macos-main.img.gz"), Embedded("apfs-fusion-macos-tier2.img.gz"),
        ParseManifest(Embedded("apfs-fusion-macos-files.manifest")));

  private static Dictionary<string, (long, string)> ParseManifest(byte[] json) {
    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.EnumerateObject().ToDictionary(
      static p => p.Name,
      static p => (p.Value.GetProperty("size").GetInt64(), p.Value.GetProperty("sha256").GetString()!));
  }

  private static void AssertReadsEveryFile(ApfsReader reader, Dictionary<string, (long Size, string Sha256)> manifest) {
    var files = reader.Entries.Where(static e => !e.IsDirectory).ToDictionary(static e => e.Name);
    Assert.Multiple(() => {
      Assert.That(reader.IsFusion, Is.True);
      Assert.That(reader.HasTier2, Is.True);
      Assert.That(files.Keys, Is.SupersetOf(manifest.Keys));
      foreach (var (path, (size, sha256)) in manifest) {
        if (!files.TryGetValue(path, out var entry)) continue;
        var data = reader.Extract(entry);
        Assert.That(data.LongLength, Is.EqualTo(size), path);
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(sha256), path);
      }
    });
  }

  [Test, Category("HappyPath")]
  public void GivenBothDevicesOfAFusionContainerMacOsMade_WhenRead_ThenEveryFileHasTheBytesMacOsWrote() {
    var (main, tier2, manifest) = Small();
    using var reader = new ApfsReader(new MemoryStream(main, writable: false), new MemoryStream(tier2, writable: false), null);
    AssertReadsEveryFile(reader, manifest);
  }

  [Test, Category("HappyPath")]
  public void GivenTheMainDeviceFileWithItsTier2DeviceBesideIt_WhenOpened_ThenTheTier2DeviceIsFoundAndRead() {
    var (main, tier2, manifest) = Small();
    var mainPath = Path.Combine(this._tmp, "fusion-main.img");
    File.WriteAllBytes(mainPath, main);
    File.WriteAllBytes(Path.Combine(this._tmp, "fusion-tier2.img"), tier2);
    File.WriteAllBytes(Path.Combine(this._tmp, "unrelated.bin"), new byte[8192]);
    using var stream = File.OpenRead(mainPath);
    using var reader = new ApfsReader(stream, null, null);
    AssertReadsEveryFile(reader, manifest);
  }

  [Test, Category("ExceptionalCase")]
  public void GivenOnlyTheMainDevice_WhenAFileOnTier2IsExtracted_ThenItIsRefusedRatherThanReadFromTheMainDevice() {
    var (main, _, _) = Small();
    using var reader = new ApfsReader(new MemoryStream(main, writable: false));
    var file = reader.Entries.First(static e => !e.IsDirectory && e.Size > 0);
    Assert.That(reader.HasTier2, Is.False);
    Assert.Throws<NotSupportedException>(() => reader.Extract(file));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenADeviceThatIsNotTheTier2Partner_WhenGivenAsTier2_ThenItIsRejected() {
    var (main, _, _) = Small();
    Assert.Throws<InvalidDataException>(() => _ = new ApfsReader(new MemoryStream(main, writable: false), new MemoryStream(main, writable: false), null));
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenALargerCapturedFusionContainer_WhenRead_ThenEveryFileHasTheBytesMacOsWrote() {
    var dir = Environment.GetEnvironmentVariable("CWB_APFS_FUSION_DIR");
    if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "main.img")))
      Assert.Ignore("Set CWB_APFS_FUSION_DIR to a directory holding main.img, tier2.img and manifest.json from apfs-fusion-capture.sh.");
    using var main = File.OpenRead(Path.Combine(dir!, "main.img"));
    using var tier2 = File.OpenRead(Path.Combine(dir!, "tier2.img"));
    using var reader = new ApfsReader(main, tier2, null);
    AssertReadsEveryFile(reader, ParseManifest(File.ReadAllBytes(Path.Combine(dir!, "manifest.json"))));
  }
}
