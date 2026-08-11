using System.Text;
using Compression.Registry;
using FileFormat.Aomei;

namespace Compression.Tests.Aomei;

/// <summary>
/// Stage 0 acceptance gate for <see cref="AomeiFormatDescriptor"/>:
/// pins the detection magic, the synthetic surface shape, and the
/// honest "detection-only / Stage-0 confirmed" Description. AOMEI
/// Backupper (.adi) is confirmed to remain Stage-0 — the format is
/// closed source and no public byte-level spec describes the
/// chunk record layout, file catalog tree, dedup hash table,
/// AES-256 framing, or the internal chunk compression codec.
/// </summary>
[TestFixture]
public class AomeiDetectionTests {

  private static byte[] BuildMinimal(string tag, int payloadLen = 128) {
    var tagBytes = Encoding.ASCII.GetBytes(tag);
    var image = new byte[tagBytes.Length + payloadLen];
    tagBytes.CopyTo(image.AsSpan(0, tagBytes.Length));
    for (var i = 0; i < payloadLen; i++) image[tagBytes.Length + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesIdAndExtensions() {
    var d = new AomeiFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Aomei"));
    Assert.That(d.DisplayName, Is.EqualTo("AOMEI Backupper"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".adi"));
    Assert.That(d.Extensions, Does.Contain(".adi"));
    Assert.That(d.Extensions, Does.Contain(".api"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_PinsThreeMagicSignatures_AtOffsetZero() {
    var d = new AomeiFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(3));
    Assert.That(d.MagicSignatures.All(m => m.Offset == 0), Is.True);

    var bytes = d.MagicSignatures.Select(m => Encoding.ASCII.GetString(m.Bytes)).ToList();
    Assert.That(bytes, Does.Contain("ADI "));
    Assert.That(bytes, Does.Contain("AOMI"));
    Assert.That(bytes, Does.Contain("ABDISKIMG"));
  }

  [Test, Category("HappyPath")]
  public void Reader_AcceptsAdiTag_AndSurfacesTwoEntries() {
    using var ms = new MemoryStream(BuildMinimal("ADI ", payloadLen: 256));
    var r = new AomeiReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.MagicTag, Is.EqualTo("ADI "));
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "aomei-image.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Reader_AcceptsAomiTag() {
    using var ms = new MemoryStream(BuildMinimal("AOMI", payloadLen: 64));
    var r = new AomeiReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.MagicTag, Is.EqualTo("AOMI"));
  }

  [Test, Category("HappyPath")]
  public void Reader_AcceptsAbDiskImgTag() {
    using var ms = new MemoryStream(BuildMinimal("ABDISKIMG", payloadLen: 32));
    var r = new AomeiReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.MagicTag, Is.EqualTo("ABDISKIMG"));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsMetadataAndRawImage_ViaDescriptor() {
    var d = new AomeiFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal("ADI ", payloadLen: 512));
    var entries = d.List(ms, password: null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "metadata.ini", "aomei-image.bin" }));
    var image = entries.Single(e => e.Name == "aomei-image.bin");
    Assert.That(image.OriginalSize, Is.EqualTo(4 + 512));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsMissingMagic() {
    var img = new byte[64];
    img[0] = 0xDE; img[1] = 0xAD; img[2] = 0xBE; img[3] = 0xEF;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new AomeiReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsTooSmall() {
    using var ms = new MemoryStream(new byte[2]);
    Assert.Throws<InvalidDataException>(() => _ = new AomeiReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsTruncatedAbDiskImgTag() {
    // 4-byte prefix "ABDI" looks neither like "ADI " nor "AOMI" nor "ABDISKIMG"
    // when the full 9-byte tag is truncated. Must throw.
    var img = Encoding.ASCII.GetBytes("ABDI");
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new AomeiReader(ms));
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly_AndStage0() {
    var d = new AomeiFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("detection-only"),
      $"AOMEI Description must flag Stage 0 honestly. Got: '{d.Description}'.");
    Assert.That(desc, Does.Contain("stage-0 confirmed"),
      "Stage-0 outcome must be explicitly pinned in the Description.");
    Assert.That(desc, Does.Contain("proprietary"),
      "Honest reason must mention the proprietary nature.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  /// <summary>
  /// Locks in the Stage-0 confirmation outcome. If anyone later flips
  /// the Description to advertise real file walking, this test fails
  /// and forces them to update the docs trail. Captures the named
  /// upgrade blockers from the investigation: chunk-record layout,
  /// catalog tree, dedup hash table, AES framing, chunk codec.
  /// </summary>
  [Test, Category("Stub")]
  public void Description_CitesUpgradeBlockers() {
    var d = new AomeiFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(
      desc.Contains("chunk") || desc.Contains("catalog") ||
      desc.Contains("dedup") || desc.Contains("aes"),
      Is.True,
      $"Description must cite at least one AOMEI-coupling blocker. Got: '{d.Description}'.");
  }

  /// <summary>
  /// The metadata.ini surface is part of the Stage-0 contract — downstream
  /// forensic tooling parses <c>parse_status</c>, <c>stage</c>, and
  /// <c>upgrade_blockers</c> to surface the honest "this is opaque" message.
  /// Lock those keys against silent drift.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_DocumentsStage0_AndUpgradeBlockers() {
    using var ms = new MemoryStream(BuildMinimal("ADI ", payloadLen: 64));
    var r = new AomeiReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("parse_status=detection-only"));
    Assert.That(text, Does.Contain("stage=0"));
    Assert.That(text, Does.Contain("upgrade_blockers="));
    Assert.That(text, Does.Contain("references="));
    Assert.That(text, Does.Contain("magic_tag=ADI "));
    // Pin the named blockers so a silent edit can't strip them.
    Assert.That(text, Does.Contain("chunk-record-layout"));
    Assert.That(text, Does.Contain("catalog-tree-encoding"));
    Assert.That(text, Does.Contain("dedup-hash-table"));
    Assert.That(text, Does.Contain("aes-key-derivation"));
    Assert.That(text, Does.Contain("chunk-compression-codec"));
  }
}
