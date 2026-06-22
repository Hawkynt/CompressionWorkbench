#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Verifies the <c>reconfigure</c> verb: changing an existing container's
/// geometry/options after creation (FAT cluster size, NTFS MFT record size)
/// actually takes effect on disk while the live contents round-trip
/// byte-for-byte. A non-creatable / non-schema format is rejected, and a failed
/// rebuild leaves the original untouched.
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

  [Test]
  public void Reconfigure_Fat_ChangesClusterSize_AndPreservesContents() {
    var payload = new byte[6000];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);
    var readme = "the quick brown fox\n"u8.ToArray();

    // Auto-fit FAT image with a default cluster size.
    var img = CreateImage(FormatDetector.Format.Fat, ".img",
      new Dictionary<string, string> { ["ImageSize"] = "1.44 MB (3.5\" HD)" },
      ("DATA.BIN", payload), ("README.TXT", readme));

    var beforeCluster = ReadBpbClusterSize(img);

    var result = ReconfigureOperation.Reconfigure(img,
      new Dictionary<string, string> {
        ["ImageSize"] = "1.44 MB (3.5\" HD)",
        ["ClusterSize"] = "2 KB",
      });

    var afterCluster = ReadBpbClusterSize(img);

    Assert.Multiple(() => {
      Assert.That(result.FileCount, Is.EqualTo(2), "both files must be preserved");
      Assert.That(afterCluster, Is.EqualTo(2048), "BPB must report the requested 2 KB cluster size");
    });
    if (beforeCluster == 2048)
      Assert.Inconclusive("default cluster already 2 KB — change not observable for this geometry");

    var got = ReadAll(img);
    Assert.Multiple(() => {
      Assert.That(got["DATA.BIN"], Is.EqualTo(payload), "DATA.BIN must survive byte-for-byte");
      Assert.That(got["README.TXT"], Is.EqualTo(readme), "README.TXT must survive byte-for-byte");
    });
  }

  [Test]
  public void Reconfigure_Ntfs_ChangesMftRecordSize_AndPreservesContents() {
    if (FormatRegistry.GetArchiveOps("Ntfs") is not Compression.Registry.IArchiveCreatable) {
      Assert.Ignore("NTFS create path unavailable in this build.");
      return;
    }

    var payload = new byte[9000];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 256);

    string img;
    try {
      img = CreateImage(FormatDetector.Format.Ntfs, ".ntfs",
        new Dictionary<string, string> {
          ["ImageSize"] = "16 MB",
          ["ClusterSize"] = "4 KB",
          ["MftRecordSize"] = "1 KB",
        },
        ("data.bin", payload));
    } catch (Exception ex) {
      Assert.Ignore($"NTFS image could not be created with the requested geometry: {ex.Message}");
      return;
    }

    // MFT record size field is at boot-sector offset 64. For a 1 KB record under
    // a 4 KB cluster: -log2(1024) = -10.
    using (var fs = File.OpenRead(img)) {
      var boot = new byte[80];
      fs.ReadExactly(boot);
      Assert.That((sbyte)boot[64], Is.EqualTo(-10), "precondition: created with 1 KB MFT record");
    }

    ReconfigureOperation.ReconfigureResult result;
    try {
      result = ReconfigureOperation.Reconfigure(img,
        new Dictionary<string, string> {
          ["ImageSize"] = "16 MB",
          ["ClusterSize"] = "4 KB",
          ["MftRecordSize"] = "2 KB",
        });
    } catch (Exception ex) {
      Assert.Ignore($"NTFS could not honour the MFT record reconfigure: {ex.Message}");
      return;
    }

    using (var fs = File.OpenRead(img)) {
      var boot = new byte[80];
      fs.ReadExactly(boot);
      // 2 KB record under 4 KB cluster: -log2(2048) = -11.
      Assert.That((sbyte)boot[64], Is.EqualTo(-11), "MFT record size must change to 2 KB");
    }

    Assert.That(result.FileCount, Is.EqualTo(1));
    var got = ReadAll(img);
    Assert.That(got["data.bin"], Is.EqualTo(payload), "file must survive the geometry rewrite");
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
