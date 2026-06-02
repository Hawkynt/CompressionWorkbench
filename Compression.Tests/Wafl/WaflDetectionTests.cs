using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Wafl;

namespace Compression.Tests.Wafl;

/// <summary>
/// Stage 0 acceptance gate for <see cref="WaflFormatDescriptor"/>:
/// pins the detection magic, surface entry shape, and the honest
/// "detection-only / Stage-0 confirmed" Description. WAFL is
/// confirmed to remain Stage-0 — the public spec (Hitz 1994 +
/// NetApp patents) is structurally informative but not byte-precise
/// enough for a single-image R/O reader. The full investigation
/// rationale is captured in <see cref="WaflFormatDescriptor"/> XML doc
/// and the synthetic metadata.ini surfaced by the reader.
/// </summary>
[TestFixture]
public class WaflDetectionTests {

  private static byte[] BuildMinimal(uint version = 0x100, int payloadLen = 128) {
    var image = new byte[8 + payloadLen];
    Encoding.ASCII.GetBytes("wafd").CopyTo(image.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), version);
    for (var i = 0; i < payloadLen; i++) image[8 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var d = new WaflFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Wafl"));
    Assert.That(d.Extensions, Does.Contain(".wafl"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("wafd"u8.ToArray()));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var d = new WaflFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(version: 0x200, payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "wafl-volume.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesVersion_AndSurfacesFullImage() {
    using var ms = new MemoryStream(BuildMinimal(version: 0x300, payloadLen: 512));
    var r = new WaflReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Version, Is.EqualTo(0x300u));
    var volume = r.Entries.Single(e => e.Name == "wafl-volume.bin");
    Assert.That(volume.Size, Is.EqualTo(8 + 512));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsMissingMagic() {
    var img = new byte[64];
    img[0] = 0xDE; img[1] = 0xAD; img[2] = 0xBE; img[3] = 0xEF;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new WaflReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsTooSmall() {
    using var ms = new MemoryStream(new byte[4]);
    Assert.Throws<InvalidDataException>(() => _ = new WaflReader(ms));
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly() {
    var d = new WaflFormatDescriptor();
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("detection-only"),
      $"WAFL Description must flag Stage 0 honestly. Got: '{d.Description}'.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  /// <summary>
  /// Locks in the Stage-0 confirmation outcome from the R/O promotion
  /// investigation. If anyone later flips the Description to advertise
  /// real file walking, this test fails and forces them to update the
  /// docs trail in <c>docs/wafl-stage0-rationale.md</c> and the README.
  /// Captures the four upgrade blockers documented in the investigation:
  /// FBN/VBN/PVBN translation, FlexVol container mapping, RAID-DP stripe
  /// walk, NVRAM consistency-point replay.
  /// </summary>
  [Test, Category("Stub")]
  public void Description_PinsStage0Confirmation_AndUpgradeBlockers() {
    var d = new WaflFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("stage-0 confirmed"),
      "Stage-0 outcome must be explicitly pinned in the Description.");
    Assert.That(desc, Does.Contain("detection-only"),
      "Honest detection-only marker must be retained.");
    Assert.That(desc, Does.Contain("proprietary"),
      "Honest reason must mention the proprietary nature.");

    // The upgrade-blocker bag — at least one of each family must be cited so
    // a casual reader knows why we can't ship R/O without a multi-week
    // reverse-engineering effort against the live ONTAP volume manager.
    Assert.That(
      desc.Contains("flexvol") || desc.Contains("raid-dp") ||
      desc.Contains("nvram") || desc.Contains("vbn"),
      Is.True,
      $"Description must cite at least one ONTAP-coupling blocker. Got: '{d.Description}'.");
  }

  /// <summary>
  /// The metadata.ini surface is part of the Stage-0 contract — downstream
  /// forensic tooling parses <c>parse_status</c>, <c>stage</c>, and
  /// <c>upgrade_blockers</c> to surface the honest "this is opaque" message.
  /// Lock those keys against silent drift.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_DocumentsStage0_AndUpgradeBlockers() {
    using var ms = new MemoryStream(BuildMinimal(version: 0x100, payloadLen: 64));
    var r = new WaflReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("parse_status=detection-only"));
    Assert.That(text, Does.Contain("stage=0"));
    Assert.That(text, Does.Contain("upgrade_blockers="));
    Assert.That(text, Does.Contain("references="));
    // Pin the named blockers so a silent edit can't strip them.
    Assert.That(text, Does.Contain("flexvol"));
    Assert.That(text, Does.Contain("raid-dp"));
    Assert.That(text, Does.Contain("nvram"));
  }
}
