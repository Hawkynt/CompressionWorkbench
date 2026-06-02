using System.Text;
using Compression.Registry;
using FileSystem.CephFs;

namespace Compression.Tests.CephFs;

[TestFixture]
public class CephFsDetectionTests {

  private static byte[] BuildMinimal(int payloadLen = 128) {
    var image = new byte[8 + payloadLen];
    Encoding.ASCII.GetBytes("CEPH").CopyTo(image.AsSpan(0, 4));
    for (var i = 0; i < payloadLen; i++) image[8 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var d = new CephFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("CephFs"));
    Assert.That(d.Extensions, Does.Contain(".ceph"));
    Assert.That(d.Extensions, Does.Contain(".rados"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("CEPH"u8.ToArray()));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var d = new CephFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "ceph-object.bin" }));
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly() {
    var d = new CephFsFormatDescriptor();
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("detection-only"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Stub")]
  public void Description_DocumentsStage0Rationale() {
    // Stage-0 confirmation: a CephFS volume is not a single image — metadata
    // lives in a RADOS pool, file data is striped across OSDs via CRUSH.
    // The Description MUST name those structural reasons so users / future
    // contributors understand why R/O promotion is not possible.
    var d = new CephFsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.Multiple(() => {
      Assert.That(desc, Does.Contain("stage-0"), "must self-identify Stage 0");
      Assert.That(desc, Does.Contain("rados"), "must name the RADOS object store");
      Assert.That(desc, Does.Contain("crush").Or.Contain("osd"), "must name CRUSH placement / OSDs");
      Assert.That(desc, Does.Contain("metadata pool").Or.Contain("mds"),
        "must name the metadata pool / MDS dependency");
    });
  }

  [Test, Category("Stub")]
  public void Metadata_DocumentsStage0Rationale() {
    // The synthetic metadata.ini entry must carry the same rationale so the
    // extracted image is self-documenting without needing the descriptor.
    var d = new CephFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 32));
    var entries = d.List(ms, password: null);
    var meta = entries.Single(e => e.Name == "metadata.ini");
    Assert.That(meta.OriginalSize, Is.GreaterThan(0));

    ms.Position = 0;
    using var r = new CephFsReader(ms);
    var metaEntry = r.Entries.Single(e => e.Name == "metadata.ini");
    var metaText = Encoding.UTF8.GetString(metaEntry.Data).ToLowerInvariant();
    Assert.Multiple(() => {
      Assert.That(metaText, Does.Contain("parse_status=detection-only"));
      Assert.That(metaText, Does.Contain("rationale="), "must carry a rationale field");
      Assert.That(metaText, Does.Contain("rados"), "rationale must name RADOS");
      Assert.That(metaText, Does.Contain("crush").Or.Contain("osd"),
        "rationale must name CRUSH / OSD placement");
      Assert.That(metaText, Does.Contain("promotion_status="), "must carry promotion status");
    });
  }
}
