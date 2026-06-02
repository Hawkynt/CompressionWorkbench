using System.Text;
using Compression.Registry;
using FileSystem.Ext;
using FileSystem.Lustre;

namespace Compression.Tests.Lustre;

[TestFixture]
public class LustreDetectionTests {

  // ── Legacy detection-only header dump (Stage-0 path, preserved) ─────────

  private static byte[] BuildMinimalLegacy(int payloadLen = 128) {
    var image = new byte[16 + payloadLen];
    Encoding.ASCII.GetBytes("LUSTRE").CopyTo(image.AsSpan(0, 6));
    for (var i = 0; i < payloadLen; i++) image[16 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IdentifiesByMagicAndExtension() {
    var d = new LustreFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Lustre"));
    Assert.That(d.Extensions, Does.Contain(".lustre"));
    Assert.That(d.Extensions, Does.Contain(".ost"));
    Assert.That(d.Extensions, Does.Contain(".mdt"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("LUSTRE"u8.ToArray()));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo(new byte[] { 0x4C, 0x55, 0x73, 0x74 }));
    // Promoted R/O — exposes multi-entry + directory capabilities; still no Create/Modify.
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Legacy_Dump_ListsMetadataAndRawObject() {
    var d = new LustreFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalLegacy(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "lustre-object.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Legacy_Dump_MetadataDocumentsStageZeroPath() {
    var d = new LustreFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalLegacy());
    var stream = ((IArchiveFormatOperations)d).OpenEntry(ms, "metadata.ini", password: null);
    using var reader = new StreamReader(stream);
    var meta = reader.ReadToEnd();
    Assert.That(meta, Does.Contain("parse_status=detection-only"));
    Assert.That(meta, Does.Contain("magic_tag=LUSTRE"));
  }

  [Test, Category("ExceptionalCase")]
  public void NoMagic_NoExt4_Throws() {
    var d = new LustreFormatDescriptor();
    var noise = new byte[1024];
    Array.Fill(noise, (byte)0xAA);
    using var ms = new MemoryStream(noise);
    Assert.Throws<InvalidDataException>(() => d.List(ms, password: null));
  }

  [Test, Category("Stub")]
  public void Description_FlagsLdiskfsDelegationAndOutOfScope() {
    var d = new LustreFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("ldiskfs"));
    Assert.That(desc, Does.Contain("ext4"));
    Assert.That(desc, Does.Contain("out of scope"));
  }

  // ── ldiskfs (ext4) R/O delegation path ──────────────────────────────────

  private static byte[] BuildLdiskfsImage(string volumeLabel, params (string Name, byte[] Data)[] files) {
    var writer = new ExtWriter();
    foreach (var (name, data) in files) writer.AddFile(name, data);
    // ext4 + journal, 4 KB blocks, volume label set (Lustre convention: "lustre-OST0000" / "MGS" / "lustre-MDT0000").
    return writer.Build(
      blockSize: 4096,
      totalBlocks: 4096,
      version: ExtWriter.ExtVersion.Ext4,
      journal: true,
      volumeLabel: volumeLabel,
      inodeSize: 256);
  }

  [Test, Category("HappyPath")]
  public void Ldiskfs_Image_RecognisedAndDelegated() {
    var d = new LustreFormatDescriptor();
    var data = BuildLdiskfsImage("lustre-OST0000",
      ("CONFIGS/mountdata", "lustre OST config"u8.ToArray()),
      ("OBJECTS/0_0", new byte[256]));

    using var ms = new MemoryStream(data);
    var entries = d.List(ms, password: null);

    // metadata.ini + raw ldiskfs image + the two files surfaced under ldiskfs/.
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("lustre-object.bin"));
    Assert.That(names.Any(n => n.StartsWith("ldiskfs/", StringComparison.Ordinal)), Is.True,
      "Expected at least one ldiskfs/* entry from ext4 reader delegation.");
  }

  [Test, Category("HappyPath")]
  public void Ldiskfs_Metadata_DocumentsPartialRoStatus() {
    var d = new LustreFormatDescriptor();
    var data = BuildLdiskfsImage("lustre-MDT0000",
      ("CONFIGS/mountdata", "lustre MDT config"u8.ToArray()));

    using var ms = new MemoryStream(data);
    using var meta = ((IArchiveFormatOperations)d).OpenEntry(ms, "metadata.ini", password: null);
    using var sr = new StreamReader(meta);
    var text = sr.ReadToEnd();
    Assert.That(text, Does.Contain("parse_status=partial-ldiskfs"));
    Assert.That(text, Does.Contain("backing_fs=ldiskfs (ext4-compatible)"));
    Assert.That(text, Does.Contain("ldiskfs_volume_label=lustre-MDT0000"));
    Assert.That(text, Does.Contain("not the Lustre logical view").IgnoreCase
      .Or.Contain("NOT the Lustre logical view"));
  }

  [Test, Category("HappyPath")]
  public void Ldiskfs_Extract_ReturnsFileBytesFromDelegatedReader() {
    var d = new LustreFormatDescriptor();
    var payload = new byte[1024];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
    var data = BuildLdiskfsImage("lustre-OST0000", ("OBJECTS/0_42", payload));

    using var ms = new MemoryStream(data);
    var entries = d.List(ms, password: null);
    var objectEntry = entries.FirstOrDefault(e => e.Name == "ldiskfs/OBJECTS/0_42");
    Assert.That(objectEntry, Is.Not.Null,
      "Expected ldiskfs/OBJECTS/0_42 entry surfaced via ext4 reader delegation. "
      + "Names: " + string.Join(",", entries.Select(e => e.Name)));

    using var ms2 = new MemoryStream(data);
    using var stream = ((IArchiveFormatOperations)d).OpenEntry(ms2, "ldiskfs/OBJECTS/0_42", password: null);
    using var mem = new MemoryStream();
    stream.CopyTo(mem);
    Assert.That(mem.ToArray(), Is.EqualTo(payload),
      "ldiskfs delegation must return the exact file bytes the ext4 reader produces.");
  }

  [Test, Category("BoundaryCase")]
  public void Ldiskfs_Reader_PreservesRawImageAsLustreObjectBin() {
    var d = new LustreFormatDescriptor();
    var data = BuildLdiskfsImage("MGS", ("CONFIGS/mountdata", "x"u8.ToArray()));

    using var ms = new MemoryStream(data);
    var entries = d.List(ms, password: null);
    var raw = entries.First(e => e.Name == "lustre-object.bin");
    Assert.That(raw.OriginalSize, Is.EqualTo(data.Length),
      "Raw image must be surfaced byte-for-byte so forensic / re-mount callers retain the original.");
  }

  [Test, Category("BoundaryCase")]
  public void Ldiskfs_DoesNotShadowGenericExt4_Detection() {
    // Critical invariant: the Lustre descriptor must NOT register ext4 magic in
    // its MagicSignatures — otherwise FormatDetector would mis-route plain ext4
    // images through Lustre. Confidence + content match would steal ext detection.
    var d = new LustreFormatDescriptor();
    foreach (var sig in d.MagicSignatures) {
      // ext4 magic is 0xEF53 LE at offset 1080 — neither tag should be at that offset.
      Assert.That(sig.Offset, Is.EqualTo(0),
        "Lustre magic must remain at offset 0 (LUSTRE/LUst) so ext4 superblock magic isn't shadowed.");
    }
  }
}
