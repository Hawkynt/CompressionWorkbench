using System.Text;
using Compression.Registry;
using FileSystem.GlusterFs;

namespace Compression.Tests.GlusterFs;

[TestFixture]
public class GlusterFsDetectionTests {

  private static byte[] BuildMinimal(int payloadLen = 128) {
    var image = new byte[8 + payloadLen];
    image[0] = 0xCA; image[1] = 0xFE; image[2] = 0x5B; image[3] = 0xAB;
    for (var i = 0; i < payloadLen; i++) image[8 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var d = new GlusterFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("GlusterFs"));
    Assert.That(d.Extensions, Does.Contain(".gluster"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xCA, 0xFE, 0x5B, 0xAB }));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var d = new GlusterFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "gluster-brick.bin" }));
  }

  [Test, Category("Stub")]
  public void Description_FlagsStage0Permanent() {
    var d = new GlusterFsFormatDescriptor();
    var lower = d.Description.ToLowerInvariant();
    Assert.That(lower, Does.Contain("detection-only"));
    Assert.That(lower, Does.Contain("stage 0"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Stub")]
  public void Description_DocumentsHonestFallback() {
    // The honest-fallback rule: a GlusterFS brick is a normal directory on a
    // local POSIX FS and state lives in xattrs — no on-disk image, no R/O
    // promotion possible from a single image stream.
    var d = new GlusterFsFormatDescriptor();
    var lower = d.Description.ToLowerInvariant();
    Assert.That(lower, Does.Contain("brick"));
    Assert.That(lower, Does.Contain("director"));   // "directory" or "directories"
    Assert.That(lower, Does.Contain("xattr"));
    Assert.That(lower, Does.Contain("no on-disk image"));
    Assert.That(lower, Does.Contain("probe"));      // magic is workbench probe, not real
  }

  [Test, Category("Stub")]
  public void Metadata_DeclaresStage0PermanentAndExplainsWhyROBlocked() {
    // The synthetic metadata.ini must spell out (a) stage=0, (b) stage is
    // permanent, (c) the concrete reason R/O promotion is blocked. This is the
    // contract the parent-task pipeline checks against when triaging FSes.
    var d = new GlusterFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 64));
    var workDir = Path.Combine(Path.GetTempPath(), $"GlusterFsMeta_{Guid.NewGuid():N}");
    Directory.CreateDirectory(workDir);
    try {
      d.Extract(ms, workDir, password: null, files: ["metadata.ini"]);
      var path = Path.Combine(workDir, "metadata.ini");
      Assert.That(File.Exists(path), Is.True);
      var text = File.ReadAllText(path, Encoding.UTF8);
      Assert.That(text, Does.Contain("stage=0"));
      Assert.That(text, Does.Contain("stage_permanent=true"));
      Assert.That(text, Does.Contain("ro_blocked_reason="));
      Assert.That(text, Does.Contain("xattr"));
      Assert.That(text, Does.Contain("no on-disk image format"));
      Assert.That(text, Does.Contain("magic_kind=workbench-internal probe"));
    } finally {
      try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
    }
  }
}
