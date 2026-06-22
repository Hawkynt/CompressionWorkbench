#pragma warning disable CS1591
using Compression.Lib;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// Verifies the UFS creation schema: the <c>VolumeLabel</c> option is written to
/// the superblock's <c>fs_volname</c> field (struct fs offset 680) and reads back,
/// and file contents survive — proving the knob is real, not cosmetic.
/// </summary>
[TestFixture]
public class UfsSchemaTests {

  [Test]
  public void VolumeLabel_IsWrittenToSuperblock_AndFilesRoundTrip() {
    var work = Path.Combine(Path.GetTempPath(), "cwb_ufs_schema_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var payload = "ufs volume-label probe\n"u8.ToArray();
      var src = Path.Combine(work, "README.TXT");
      File.WriteAllBytes(src, payload);
      var img = Path.Combine(work, "vol.ufs");

      ArchiveOperations.Create(img, [new ArchiveInput(src, "README.TXT")],
        new CompressionOptions(), FormatDetector.Format.Ufs,
        new Dictionary<string, string> { ["VolumeLabel"] = "MYUFSVOL" });

      using var fs = File.OpenRead(img);
      var reader = new UfsReader(fs);

      Assert.That(reader.VolumeName, Is.EqualTo("MYUFSVOL"),
        "fs_volname must carry the requested volume label");

      var entry = reader.Entries.First(e => !e.IsDirectory && e.Name.Contains("README", StringComparison.OrdinalIgnoreCase));
      Assert.That(reader.Extract(entry), Is.EqualTo(payload), "file content must survive");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void DescriptorPublishesVolumeLabelSchema() {
    var schema = (Compression.Registry.IFormatOptionsSchema)new UfsFormatDescriptor();
    Assert.That(schema.OptionsSchema.Select(o => o.Key), Does.Contain("VolumeLabel"));
  }
}
