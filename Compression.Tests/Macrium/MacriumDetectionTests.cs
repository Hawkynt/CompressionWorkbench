using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Compression.Core.Streams;
using Compression.Registry;
using FileFormat.Macrium;
using FileFormat.Zstd;

namespace Compression.Tests.Macrium;

/// <summary>
/// Acceptance gate for <see cref="MacriumFormatDescriptor"/> covering both
/// (a) Reflect X (.mrimgx / .mrbakx) R/O metadata via the MIT-licensed
/// vendor spec footer + metadata-block-chain layout, and
/// (b) legacy .mrimg Stage-0 detection-only fall-through.
/// </summary>
[TestFixture]
public class MacriumDetectionTests {

  // ---- Reflect X synthetic builder ---------------------------------------

  private const int MetadataHeaderSize = 32;
  private const int FooterSize = 20;

  /// <summary>
  /// Build a synthetic Reflect X image: a fake disk-content prefix, a chain
  /// of N metadata blocks (last one zstd-compressed $JSON when requested),
  /// and the 20-byte footer pointing at the first block.
  /// </summary>
  private static byte[] BuildReflectXImage(
      int diskContentLen = 1024,
      string? jsonText = null,
      bool encryptJson = false,
      params (string name, byte[] payload, bool compressed, bool encrypted)[] extraBlocks) {

    using var ms = new MemoryStream();

    // Fake disk content (the bytes Macrium would compress + encrypt in real
    // Reflect X images). Our metadata-only reader never decodes this.
    var disk = new byte[diskContentLen];
    for (var i = 0; i < disk.Length; ++i)
      disk[i] = (byte)((i * 7 + 13) & 0xFF);
    ms.Write(disk, 0, disk.Length);

    var firstMetadataOffset = ms.Position;

    // Collect blocks: $JSON first, then extras, then a terminal $AUXDATA.
    var blocks = new System.Collections.Generic.List<(string name, byte[] payload, bool compressed, bool encrypted, bool last)>();
    if (jsonText is not null) {
      byte[] jsonBytes;
      var compressed = false;
      if (encryptJson) {
        // Simulate "encrypted" payload — opaque bytes; reader must NOT try
        // to decompress.
        jsonBytes = Encoding.UTF8.GetBytes(jsonText);
      } else {
        // Real zstd-compressed JSON, matching what Reflect X writes.
        using var raw = new MemoryStream(Encoding.UTF8.GetBytes(jsonText));
        using var compressedMs = new MemoryStream();
        using (var zs = new ZstdStream(compressedMs, CompressionStreamMode.Compress, leaveOpen: true)) {
          raw.CopyTo(zs);
        }
        jsonBytes = compressedMs.ToArray();
        compressed = true;
      }
      blocks.Add(("$JSON", jsonBytes, compressed, encryptJson, false));
    }

    foreach (var b in extraBlocks)
      blocks.Add((b.name, b.payload, b.compressed, b.encrypted, false));

    // Always finish with a terminal $AUXDATA block so the chain walker has
    // a well-defined stop condition even when no extras / no $JSON were
    // requested.
    blocks.Add(("$AUXDATA", new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, false, false, true));

    foreach (var (name, payload, compressed, encrypted, last) in blocks) {
      // 32-byte header: name(8) + length(4 LE) + md5(16) + flags(1) + pad(3).
      var nameBytes = new byte[8];
      var raw = Encoding.ASCII.GetBytes(name);
      System.Buffer.BlockCopy(raw, 0, nameBytes, 0, System.Math.Min(raw.Length, 8));
      for (var i = raw.Length; i < 8; ++i) nameBytes[i] = (byte)' ';
      ms.Write(nameBytes, 0, 8);

      var lenBytes = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)payload.Length);
      ms.Write(lenBytes, 0, 4);

      // Synthetic MD5 — reader records it but doesn't validate.
      var hash = new byte[16];
      for (var i = 0; i < 16; ++i) hash[i] = (byte)(payload.Length + i);
      ms.Write(hash, 0, 16);

      byte flags = 0;
      if (last) flags |= 0x01;
      if (compressed) flags |= 0x02;
      if (encrypted) flags |= 0x04;
      ms.WriteByte(flags);
      ms.Write(new byte[3], 0, 3); // padding

      ms.Write(payload, 0, payload.Length);
    }

    // 20-byte footer: uint64 first_metadata_block_offset LE + "MACRIUM_FILE".
    var footerOffset = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(footerOffset, (ulong)firstMetadataOffset);
    ms.Write(footerOffset, 0, 8);
    ms.Write("MACRIUM_FILE"u8.ToArray(), 0, 12);

    return ms.ToArray();
  }

  // ---- Legacy .mrimg synthetic builders ----------------------------------

  private static byte[] BuildLegacyAsciiTagged(int payloadLen = 256) {
    var image = new byte[16 + payloadLen];
    Encoding.ASCII.GetBytes("MR_BACKUP").CopyTo(image.AsSpan(0, 9));
    image[9] = 0x12; image[10] = 0x34; image[11] = 0x56; image[12] = 0x78;
    for (var i = 0; i < payloadLen; ++i) image[16 + i] = (byte)(i & 0xFF);
    return image;
  }

  private static byte[] BuildLegacyBinaryTagged(int payloadLen = 256) {
    var image = new byte[16 + payloadLen];
    Encoding.ASCII.GetBytes("MACX").CopyTo(image.AsSpan(0, 4));
    image[4] = 0xDE; image[5] = 0xAD; image[6] = 0xBE; image[7] = 0xEF;
    for (var i = 0; i < payloadLen; ++i) image[16 + i] = (byte)(i & 0xFF);
    return image;
  }

  // ---- Descriptor pins ---------------------------------------------------

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByExtensionAndLegacyMagic() {
    var d = new MacriumFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Macrium"));
    Assert.That(d.DisplayName, Is.EqualTo("Macrium Reflect"));
    Assert.That(d.Extensions, Does.Contain(".mrimg"));
    Assert.That(d.Extensions, Does.Contain(".mrimgx"));
    Assert.That(d.Extensions, Does.Contain(".mrbakx"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".mrimgx"));
    // Legacy community-RE tags retained for stream-without-filename detection.
    var legacyTags = d.MagicSignatures.Select(s => Encoding.ASCII.GetString(s.Bytes)).ToList();
    Assert.That(legacyTags, Does.Contain("MR_BACKUP"));
    Assert.That(legacyTags, Does.Contain("MACX"));
    // R/W promotion: descriptor IS IArchiveCreatable now (Reflect X writer
    // emits valid containers per the MIT-licensed vendor spec).
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Capabilities_ReadWriteSurface() {
    // WORM for Reflect X (via vendor spec) + Stage-0 for legacy: list / extract / test / create.
    // Modify (in-place mutation that preserves untouched bytes at original offsets) is NOT
    // wired — rebuild-based "modify" (extract → mutate → re-create) is functionally available
    // via ModifyRebuilder helper but stays at the CLI/UI wrapper layer, not the descriptor.
    var d = new MacriumFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Family_IsArchive() {
    var d = new MacriumFormatDescriptor();
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
  }

  // ---- Reflect X R/O metadata surface ------------------------------------

  [Test, Category("HappyPath")]
  public void ReflectX_Reader_ParsesFooterAndChain() {
    var image = BuildReflectXImage(
      diskContentLen: 4096,
      jsonText: "{\"_header\":{\"imageid\":\"abc123\"},\"disks\":[]}",
      encryptJson: false);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms);

    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Variant, Is.EqualTo("mrimgx"));
    Assert.That(r.Tag, Is.EqualTo("MACRIUM_FILE"));
    Assert.That(r.FirstMetadataBlockOffset, Is.EqualTo(4096));
    Assert.That(r.Blocks, Has.Count.EqualTo(2));
    Assert.That(r.Blocks[0].Name, Is.EqualTo("$JSON"));
    Assert.That(r.Blocks[0].IsCompressed, Is.True);
    Assert.That(r.Blocks[0].IsEncrypted, Is.False);
    Assert.That(r.Blocks[0].IsLast, Is.False);
    Assert.That(r.Blocks[1].Name, Is.EqualTo("$AUXDATA"));
    Assert.That(r.Blocks[1].IsLast, Is.True);
  }

  [Test, Category("HappyPath")]
  public void ReflectX_List_SurfacesMetadataIniJsonBlockEntriesAndRawImage() {
    var image = BuildReflectXImage(
      diskContentLen: 512,
      jsonText: "{\"_header\":{\"imageid\":\"xyz\"}}",
      encryptJson: false);
    var d = new MacriumFormatDescriptor();
    using var ms = new MemoryStream(image);
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("metadata.json"));
    Assert.That(names, Does.Contain("macrium-image.bin"));
    Assert.That(names.Any(n => n.StartsWith("block-00.")), Is.True);
    Assert.That(names.Any(n => n.Contains("JSON")), Is.True);
    Assert.That(names.Any(n => n.Contains("AUXDATA")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void ReflectX_JsonBlock_IsDecompressedFromZstd() {
    var jsonPayload = "{\"_header\":{\"imageid\":\"deadbeef\",\"backup_guid\":\"00000000-0000-0000-0000-000000000001\"},\"disks\":[{\"disk_number\":0,\"partitions\":[{\"partition_number\":1,\"block_size\":65536,\"block_count\":100}]}]}";
    var image = BuildReflectXImage(jsonText: jsonPayload, encryptJson: false);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms);
    var jsonEntry = r.Entries.FirstOrDefault(e => e.Name == "metadata.json");
    Assert.That(jsonEntry, Is.Not.Null, "Reflect X reader must expose decompressed $JSON as metadata.json.");
    var roundTripped = Encoding.UTF8.GetString(jsonEntry!.Data);
    Assert.That(roundTripped, Is.EqualTo(jsonPayload),
      "metadata.json must contain the verbatim decompressed $JSON payload — no fabrication, no truncation.");
  }

  [Test, Category("HappyPath")]
  public void ReflectX_EncryptedJsonBlock_IsNotDecompressed() {
    // When $JSON is flagged encrypted we must NOT pretend we can read it —
    // surface it only as the opaque block-NN entry.
    var image = BuildReflectXImage(
      jsonText: "secret-but-not-really",
      encryptJson: true);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "metadata.json"), Is.False,
      "Encrypted $JSON must NOT be surfaced as decompressed metadata.json — that would be dishonest.");
    Assert.That(r.Entries.Any(e => e.Name.Contains("JSON")), Is.True,
      "Encrypted $JSON must still be surfaced as an opaque block-NN.$JSON.bin entry.");
  }

  [Test, Category("HappyPath")]
  public void ReflectX_MetadataIni_PinsFooterAndBlockSummary() {
    var image = BuildReflectXImage(
      diskContentLen: 2048,
      jsonText: "{}",
      encryptJson: false);
    using var ms = new MemoryStream(image);
    using var r = new MacriumReader(ms);
    var meta = r.Entries.First(e => e.Name == "metadata.ini");
    var ini = Encoding.UTF8.GetString(meta.Data);
    Assert.That(ini, Does.Contain("parse_status=ro-metadata"));
    Assert.That(ini, Does.Contain("stage=1"));
    Assert.That(ini, Does.Contain("variant=mrimgx"));
    Assert.That(ini, Does.Contain("footer_magic=MACRIUM_FILE"));
    Assert.That(ini, Does.Contain("first_metadata_block_offset=2048"));
    Assert.That(ini, Does.Contain("block_00=$JSON"));
    Assert.That(ini, Does.Contain("block_01=$AUXDATA"));
    // The synthetic builder emits no $INDEX/$TRACK0 blocks so sector
    // reconstruction can't run and rw_promotion stays "blocked" (NOT
    // "blocked-encrypted" since the JSON has no _encryption.enable=true).
    Assert.That(ini, Does.Contain("rw_promotion=blocked"));
    Assert.That(ini, Does.Contain("sector_reconstruction=no-$INDEX-block"));
    Assert.That(ini, Does.Contain("encrypted=0"));
    Assert.That(ini, Does.Contain("mrimgx_file_layout"));
    // R/W capability lines name AES + PBKDF2 + zstd + img_to_vhdx honestly.
    Assert.That(ini, Does.Contain("AES-CBC"));
    Assert.That(ini, Does.Contain("PBKDF2"));
    Assert.That(ini, Does.Contain("zstd"));
  }

  // ---- Legacy .mrimg Stage-0 fall-through --------------------------------

  [Test, Category("HappyPath")]
  public void Legacy_AsciiTagged_FallsBackToStage0() {
    var d = new MacriumFormatDescriptor();
    using var ms = new MemoryStream(BuildLegacyAsciiTagged(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "macrium-image.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Legacy_BinaryTagged_FallsBackToStage0() {
    var d = new MacriumFormatDescriptor();
    using var ms = new MemoryStream(BuildLegacyBinaryTagged(payloadLen: 128));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "macrium-image.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Legacy_Reader_ExposesAsciiTagAndStage0() {
    using var ms = new MemoryStream(BuildLegacyAsciiTagged(payloadLen: 64));
    using var r = new MacriumReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Variant, Is.EqualTo("mrimg-legacy"));
    Assert.That(r.Tag, Is.EqualTo("MR_BACKUP"));
    Assert.That(r.Blocks, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Legacy_Reader_ExposesBinaryTagAndStage0() {
    using var ms = new MemoryStream(BuildLegacyBinaryTagged(payloadLen: 64));
    using var r = new MacriumReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Variant, Is.EqualTo("mrimg-legacy"));
    Assert.That(r.Tag, Is.EqualTo("MACX"));
    Assert.That(r.Blocks, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Legacy_MetadataIni_DocumentsStage0Blockers() {
    using var ms = new MemoryStream(BuildLegacyAsciiTagged(payloadLen: 64));
    using var reader = new MacriumReader(ms);
    var meta = reader.Entries.First(e => e.Name == "metadata.ini");
    var ini = Encoding.UTF8.GetString(meta.Data).ToLowerInvariant();
    Assert.That(ini, Does.Contain("stage=0"));
    Assert.That(ini, Does.Contain("variant=mrimg-legacy"));
    Assert.That(ini, Does.Contain("ro_promotion=blocked"));
    Assert.That(ini, Does.Contain("proprietary"));
    Assert.That(ini, Does.Contain("ccooper21"));
    Assert.That(ini, Does.Contain("eula"));
    Assert.That(ini, Does.Contain("macrium"));
  }

  // ---- Descriptor description must name both variants honestly -----------

  [Test, Category("Stub")]
  public void Description_NamesBothVariantsAndBlockers() {
    var d = new MacriumFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain(".mrimgx"));
    Assert.That(desc, Does.Contain(".mrimg"));
    Assert.That(desc, Does.Contain("reflect x"),
      "Description must distinguish Reflect X R/O metadata from legacy Stage-0.");
    Assert.That(desc, Does.Contain("stage 0").Or.Contain("detection-only"),
      "Description must surface the legacy Stage-0 path.");
    Assert.That(desc, Does.Contain("ro").Or.Contain("metadata"),
      "Description must surface the Reflect X R/O metadata promotion.");
    Assert.That(desc, Does.Contain("aes"));
    Assert.That(desc, Does.Contain("pbkdf2"));
    Assert.That(desc, Does.Contain("incremental").Or.Contain("differential").Or.Contain("chain"));
    Assert.That(desc, Does.Contain("spec").Or.Contain("mit").Or.Contain("vendor"));
  }

  // ---- Exceptional cases -------------------------------------------------

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsMissingMarker() {
    var bogus = new byte[64];
    for (var i = 0; i < bogus.Length; ++i) bogus[i] = (byte)i;
    using var ms = new MemoryStream(bogus);
    Assert.That(() => _ = new MacriumReader(ms), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsTooSmallFile() {
    var tiny = new byte[8];
    Encoding.ASCII.GetBytes("MACX").CopyTo(tiny.AsSpan(0, 4));
    using var ms = new MemoryStream(tiny);
    Assert.That(() => _ = new MacriumReader(ms), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsNullStream() {
    Assert.That(() => _ = new MacriumReader(null!), Throws.InstanceOf<ArgumentNullException>());
  }

  [Test, Category("ExceptionalCase")]
  public void ReflectX_Reader_StopsOnCorruptChainCursor() {
    // Build a Reflect X image, then deliberately corrupt the $JSON block's
    // length field so the chain walker can't reach the next block. Reader
    // must NOT crash — it must stop at the truncation point and surface
    // what it parsed.
    var image = BuildReflectXImage(diskContentLen: 256, jsonText: "{}", encryptJson: false);
    // Truncation: overwrite the $JSON length (first block's bytes 8..11) with
    // a huge value. First block header starts at offset 256.
    var lenSpan = image.AsSpan(256 + 8, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(lenSpan, uint.MaxValue);
    using var ms = new MemoryStream(image);
    Assert.That(() => {
      using var r = new MacriumReader(ms);
      // Footer is still valid, so Variant must be set; chain walk simply
      // stops before producing a usable $JSON.
      Assert.That(r.Variant, Is.EqualTo("mrimgx"));
      Assert.That(r.ValidHeader, Is.True);
    }, Throws.Nothing);
  }
}
