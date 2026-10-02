#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Verifies the <c>reconfigure</c> verb — the geometry maintenance operation — at the file
/// level: formats whose relayout cannot keep everything they carry refuse, and every refusal
/// leaves the original byte for byte as it was. The verified success path is covered by
/// <c>MaintenanceVerbsTests</c> with a relayout that is lossless by construction.
/// </summary>
[TestFixture]
public class ReconfigureOperationTests {

  private string _work = "";

  [SetUp]
  public void SetUp() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    _work = Path.Combine(Path.GetTempPath(), "cwb_reconfig_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(_work);
  }

  [TearDown]
  public void TearDown() {
    if (Directory.Exists(_work)) try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
  }

  private string MakeSourceFile(string name, byte[] data) {
    var p = Path.Combine(_work, name);
    File.WriteAllBytes(p, data);
    return p;
  }

  private string CreateImage(FormatDetector.Format format, string ext,
      IReadOnlyDictionary<string, string> formatSpecific,
      params (string Name, byte[] Data)[] files) {
    var imgPath = Path.Combine(_work, "img_" + Guid.NewGuid().ToString("N")[..6] + ext);
    var inputs = files
      .Select(f => new ArchiveInput(MakeSourceFile(f.Name, f.Data), f.Name))
      .ToList();
    ArchiveOperations.Create(imgPath, inputs, new CompressionOptions(), format, formatSpecific);
    return imgPath;
  }

  private static Dictionary<string, byte[]> ReadAll(string path) {
    var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_read_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tempDir);
      ArchiveOperations.Extract(path, tempDir, password: null, files: null);
      foreach (var f in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tempDir, f).Replace('\\', '/');
        map[rel] = File.ReadAllBytes(f);
      }
    } finally {
      if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
    }
    return map;
  }

  /// <summary>Cluster size in bytes from the BPB: bytes-per-sector × sectors-per-cluster.</summary>
  private static int ReadBpbClusterSize(string path) {
    var boot = new byte[32];
    using (var fs = File.OpenRead(path)) {
      fs.ReadExactly(boot);
    }
    int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
    int sectorsPerCluster = boot[13];
    return bytesPerSector * sectorsPerCluster;
  }

  /// <summary>
  /// FAT's relayout writes a fresh volume from extracted files: a new serial, no label, no
  /// attributes, fresh folder times. That is not a geometry change that keeps the data, so
  /// it is refused, and the image stays byte for byte as it was.
  /// </summary>
  [Test, Category("Exception")]
  public void Reconfigure_Fat_IsRefusedAndTheImageIsUntouched() {
    var img = CreateImage(FormatDetector.Format.Fat, ".img",
      new Dictionary<string, string> { ["ImageSize"] = "1.44 MB (3.5\" HD)" },
      ("DATA.BIN", new byte[6000]), ("README.TXT", "the quick brown fox\n"u8.ToArray()));
    var original = File.ReadAllBytes(img);

    Assert.That(() => ReconfigureOperation.Reconfigure(img, new Dictionary<string, string> { ["ClusterSize"] = "2 KB" }),
      Throws.TypeOf<NotSupportedException>().With.Message.Contains("losslessly"));
    Assert.That(File.ReadAllBytes(img), Is.EqualTo(original));
    Assert.That(Directory.GetFiles(_work, "*.tmp*"), Is.Empty, "no staged file may be left behind");
  }

  /// <summary>
  /// NTFS's relayout drops the label, serial, security descriptors, streams, reparse points,
  /// times and attributes (the finding of the real-volume evidence matrix), so it is refused.
  /// </summary>
  [Test, Category("Exception")]
  public void Reconfigure_Ntfs_IsRefusedAndTheImageIsUntouched() {
    string img;
    try {
      img = CreateImage(FormatDetector.Format.Ntfs, ".ntfs",
        new Dictionary<string, string> { ["ImageSize"] = "16 MB", ["ClusterSize"] = "4 KB", ["MftRecordSize"] = "1 KB" },
        ("data.bin", new byte[9000]));
    } catch (Exception ex) {
      Assert.Ignore($"NTFS image could not be created with the requested geometry: {ex.Message}");
      return;
    }
    var original = File.ReadAllBytes(img);

    Assert.That(() => ReconfigureOperation.Reconfigure(img, new Dictionary<string, string> { ["MftRecordSize"] = "2 KB" }),
      Throws.TypeOf<NotSupportedException>());
    Assert.That(File.ReadAllBytes(img), Is.EqualTo(original));
  }

  [Test]
  public void Reconfigure_NonCreatableFormat_Throws() {
    // A plain text file is not a creatable container format.
    var notAnImage = MakeSourceFile("notes.txt", "hello"u8.ToArray());
    Assert.That(() => ReconfigureOperation.Reconfigure(notAnImage,
        new Dictionary<string, string> { ["ClusterSize"] = "2 KB" }),
      Throws.InstanceOf<NotSupportedException>());
  }

  [Test]
  public void Reconfigure_MissingFile_Throws() {
    Assert.That(() => ReconfigureOperation.Reconfigure(
        Path.Combine(_work, "does-not-exist.img"),
        new Dictionary<string, string> { ["ClusterSize"] = "2 KB" }),
      Throws.InstanceOf<FileNotFoundException>());
  }
}
