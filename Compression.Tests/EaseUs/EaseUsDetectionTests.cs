using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.EaseUs;

namespace Compression.Tests.EaseUs;

/// <summary>
/// R/O metadata acceptance gate for <see cref="EaseUsFormatDescriptor"/>:
/// pins the IMGF / FIMG detection magic, the extended 12-byte
/// forensic-carving signature, the embedded UTF-16LE source-path
/// extraction, the zlib-substream scan, the trailer IMGF + 0xFF padding
/// scan, and the honest "R/O metadata — vendor-only engine"
/// Description. EaseUS Todo Backup (.pbd) is a proprietary closed-source
/// container; the on-disk spec has never been published, the block
/// tables + AES-256 key envelope remain undocumented, and only the
/// vendor's own engine can extract content. The full rationale is
/// captured in the descriptor's XML doc and in the synthetic
/// metadata.ini surfaced by the reader.
/// </summary>
[TestFixture]
public class EaseUsDetectionTests {

  /// <summary>
  /// Builds a synthetic .pbd image with a real IMGF header, optional UTF-16LE
  /// source-path string, optional zlib substream headers, and the trailer
  /// IMGF + 0xFF padding convention — enough for the reader's header /
  /// path / zlib / trailer scans to fire on the same shape as a real file.
  /// </summary>
  private static byte[] BuildImage(
    string magic = "IMGF",
    uint headerWord = 0x0000052Cu,
    uint versionWord = 0x00020000u,
    string? sourcePath = "G:\\backup\\msi laptop\\snapshot_2024_Full_v1.pbd",
    int zlibStreamCount = 2,
    bool addTrailerImgf = true,
    int trailingFfPadding = 16,
    int bodyPad = 256
  ) {
    var magicBytes = Encoding.ASCII.GetBytes(magic);
    if (magicBytes.Length != 4)
      throw new ArgumentException("Magic must be exactly 4 bytes.", nameof(magic));

    var body = new List<byte>();
    body.AddRange(magicBytes);
    Span<byte> tmp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(tmp, headerWord);
    body.AddRange(tmp.ToArray());
    BinaryPrimitives.WriteUInt32LittleEndian(tmp, versionWord);
    body.AddRange(tmp.ToArray());

    // Embedded UTF-16LE source path (right after the 12-byte header).
    if (!string.IsNullOrEmpty(sourcePath)) {
      foreach (var ch in sourcePath) {
        body.Add((byte)ch);
        body.Add(0x00);
      }
      // Terminator + small filler so the path is bounded by non-printable bytes.
      body.AddRange(new byte[] { 0x00, 0x00, 0x01, 0x02, 0x03, 0x04 });
    }

    // Optional zlib substream headers — use the common 0x78 0x9C flavour
    // followed by a few bytes of filler so the scanner clearly sees them.
    for (var i = 0; i < zlibStreamCount; i++) {
      body.Add(0x78);
      body.Add(0x9C);
      body.AddRange(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE });
    }

    // Body filler to give the file some bulk.
    for (var i = 0; i < bodyPad; i++) body.Add((byte)(i & 0x7F));

    // Trailer IMGF marker + some random bytes.
    if (addTrailerImgf) {
      body.AddRange(Encoding.ASCII.GetBytes("IMGF"));
      body.AddRange(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    // Trailing 0xFF padding.
    for (var i = 0; i < trailingFfPadding; i++) body.Add(0xFF);

    return body.ToArray();
  }

  // ---------------------------------------------------------------------
  // Detection / magic recognition.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByImgfMagic() {
    var d = new EaseUsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("EaseUsPbd"));
    Assert.That(d.Extensions, Does.Contain(".pbd"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".pbd"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("IMGF"u8.ToArray()));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo("FIMG"u8.ToArray()));
    Assert.That(d.MagicSignatures[1].Offset, Is.EqualTo(0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries_ImgfMagic() {
    var d = new EaseUsFormatDescriptor();
    using var ms = new MemoryStream(BuildImage(magic: "IMGF"));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "easeus-backup.pbd" }));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries_FimgMagic() {
    var d = new EaseUsFormatDescriptor();
    using var ms = new MemoryStream(BuildImage(magic: "FIMG"));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "easeus-backup.pbd" }));
  }

  [Test, Category("HappyPath")]
  public void Reader_RecordsImgfMagicVariant() {
    using var ms = new MemoryStream(BuildImage(magic: "IMGF"));
    var r = new EaseUsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.MagicVariant, Is.EqualTo("IMGF"));
  }

  [Test, Category("HappyPath")]
  public void Reader_RecordsFimgMagicVariant() {
    using var ms = new MemoryStream(BuildImage(magic: "FIMG"));
    var r = new EaseUsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.MagicVariant, Is.EqualTo("FIMG"));
  }

  [Test, Category("HappyPath")]
  public void Reader_DetectsExtendedRStudioSignature() {
    // IMGF + 2C 05 00 00 + 00 00 02 00 == the R-Studio carving signature.
    using var ms = new MemoryStream(BuildImage(
      magic: "IMGF", headerWord: 0x0000052Cu, versionWord: 0x00020000u));
    var r = new EaseUsReader(ms);
    Assert.That(r.ExtendedSignatureMatch, Is.True,
      "The R-Studio 12-byte 'IMGF 2C 05 00 00 00 00 02 00' signature should match.");
  }

  [Test, Category("HappyPath")]
  public void Reader_DistinguishesNonExtendedHeader() {
    // Same IMGF magic but different header word — extended-signature check must reject.
    using var ms = new MemoryStream(BuildImage(
      magic: "IMGF", headerWord: 0x12345678u, versionWord: 0x00020000u));
    var r = new EaseUsReader(ms);
    Assert.That(r.MagicVariant, Is.EqualTo("IMGF"));
    Assert.That(r.ExtendedSignatureMatch, Is.False);
    Assert.That(r.HeaderWord, Is.EqualTo(0x12345678u));
  }

  // ---------------------------------------------------------------------
  // Header / version parse.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Reader_ParsesVersionWordSplit() {
    using var ms = new MemoryStream(BuildImage(versionWord: 0x00030001u));
    var r = new EaseUsReader(ms);
    Assert.That(r.VersionWord, Is.EqualTo(0x00030001u));
    Assert.That(r.VersionMajor, Is.EqualTo((ushort)0x0003));
    Assert.That(r.VersionMinor, Is.EqualTo((ushort)0x0001));
  }

  // ---------------------------------------------------------------------
  // Embedded UTF-16LE source-path extraction.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Reader_ExtractsEmbeddedSourcePath() {
    const string path = "G:\\backup\\msi laptop\\snapshot_2024_Full_v1.pbd";
    using var ms = new MemoryStream(BuildImage(sourcePath: path));
    var r = new EaseUsReader(ms);
    Assert.That(r.EmbeddedSourcePath, Is.EqualTo(path));
    Assert.That(r.EmbeddedSourcePathOffset, Is.EqualTo(EaseUsReader.HeaderSize));
  }

  [Test, Category("HappyPath")]
  public void Reader_ReportsEmptyPath_WhenNoneEmbedded() {
    using var ms = new MemoryStream(BuildImage(sourcePath: null));
    var r = new EaseUsReader(ms);
    Assert.That(r.EmbeddedSourcePath, Is.EqualTo(""));
    Assert.That(r.EmbeddedSourcePathOffset, Is.EqualTo(-1));
  }

  // ---------------------------------------------------------------------
  // Zlib substream scan.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Reader_CountsZlibSubstreamHeaders() {
    using var ms = new MemoryStream(BuildImage(zlibStreamCount: 3));
    var r = new EaseUsReader(ms);
    Assert.That(r.ZlibStreamCount, Is.GreaterThanOrEqualTo(3),
      "Reader must locate at least the 3 injected 0x78 0x9C zlib substream headers.");
    Assert.That(r.FirstZlibOffsets.Count, Is.LessThanOrEqualTo(EaseUsReader.MaxZlibOffsetsRecorded));
  }

  [Test, Category("Boundary")]
  public void Reader_ReportsZeroZlibStreams_WhenAbsent() {
    using var ms = new MemoryStream(BuildImage(
      zlibStreamCount: 0, sourcePath: null, bodyPad: 64, addTrailerImgf: false, trailingFfPadding: 0));
    var r = new EaseUsReader(ms);
    Assert.That(r.ZlibStreamCount, Is.EqualTo(0));
    Assert.That(r.FirstZlibOffsets.Count, Is.EqualTo(0));
  }

  // ---------------------------------------------------------------------
  // Trailer (IMGF marker + 0xFF padding) scan.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Reader_DetectsTrailerImgf_AndPaddingCount() {
    using var ms = new MemoryStream(BuildImage(addTrailerImgf: true, trailingFfPadding: 32));
    var r = new EaseUsReader(ms);
    Assert.That(r.TrailerImgfPresent, Is.True,
      "Trailer scan should locate the closing IMGF marker before the 0xFF padding.");
    Assert.That(r.TrailingFfPadding, Is.EqualTo(32));
  }

  [Test, Category("Boundary")]
  public void Reader_ReportsZeroPadding_WhenTrailerAbsent() {
    using var ms = new MemoryStream(BuildImage(addTrailerImgf: false, trailingFfPadding: 0));
    var r = new EaseUsReader(ms);
    Assert.That(r.TrailerImgfPresent, Is.False);
    Assert.That(r.TrailingFfPadding, Is.EqualTo(0));
  }

  // ---------------------------------------------------------------------
  // Sad / boundary cases.
  // ---------------------------------------------------------------------

  [Test, Category("Sad")]
  public void Reader_RejectsMissingMagic() {
    var img = new byte[64];
    img[0] = 0xDE; img[1] = 0xAD; img[2] = 0xBE; img[3] = 0xEF;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new EaseUsReader(ms));
  }

  [Test, Category("Boundary")]
  public void Reader_RejectsTooSmall() {
    using var ms = new MemoryStream(new byte[8]);  // < 12 bytes can't hold the IMGF header.
    Assert.Throws<InvalidDataException>(() => _ = new EaseUsReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsNullStream() {
    Assert.Throws<ArgumentNullException>(() => _ = new EaseUsReader(null!));
  }

  // ---------------------------------------------------------------------
  // Description / metadata.ini contract.
  // ---------------------------------------------------------------------

  [Test, Category("Stub")]
  public void Description_FlagsRoMetadata_AndForbidsCreateModify() {
    var d = new EaseUsFormatDescriptor();
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("r/o metadata"),
      $"EaseUS PBD Description must flag the R/O metadata treatment honestly. Got: '{d.Description}'.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
  }

  /// <summary>
  /// Locks in the R/O metadata honest disclosure: the vendor name, the
  /// closed-source nature, the IMGF magic, and the four upgrade-blocker
  /// families (proprietary block tables, AES key envelope, vendor-only
  /// engine, no public spec) must remain cited.
  /// </summary>
  [Test, Category("Stub")]
  public void Description_PinsRoMetadataDisclosure_AndUpgradeBlockers() {
    var d = new EaseUsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("r/o metadata"),
      "R/O metadata treatment must be explicitly pinned in the Description.");
    Assert.That(desc, Does.Contain("imgf"),
      "Real IMGF magic must be cited (not the placeholder 'PBD' magic).");
    Assert.That(desc, Does.Contain("proprietary"),
      "Honest reason must mention the proprietary nature.");
    Assert.That(desc, Does.Contain("easeus"),
      "Vendor name must be retained for downstream search/tagging.");
    Assert.That(desc, Does.Contain(".pbd"),
      "Extension must be referenced in the Description.");
    Assert.That(
      desc.Contains("vendor") || desc.Contains("aes") ||
      desc.Contains("block table") || desc.Contains("no public") ||
      desc.Contains("closed-source") || desc.Contains("engine"),
      Is.True,
      $"Description must cite at least one upgrade-blocker family. Got: '{d.Description}'.");
  }

  /// <summary>
  /// The metadata.ini surface is part of the R/O contract — downstream
  /// forensic tooling parses <c>parse_status</c>, <c>magic_variant</c>,
  /// <c>source_path</c>, <c>zlib_substream_count</c>, and
  /// <c>upgrade_blockers</c> to surface the honest "we read the header
  /// but not the content" message. Lock those keys against silent drift.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_DocumentsRoFields_AndUpgradeBlockers() {
    const string path = "G:\\backup\\msi laptop\\snapshot_2024_Full_v1.pbd";
    using var ms = new MemoryStream(BuildImage(sourcePath: path, zlibStreamCount: 2, trailingFfPadding: 8));
    var r = new EaseUsReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("parse_status=header-metadata"));
    Assert.That(text, Does.Contain("stage=ro-metadata"));
    Assert.That(text, Does.Contain("magic_variant=IMGF"));
    Assert.That(text, Does.Contain("extended_signature_match=true"));
    Assert.That(text, Does.Contain("header_word=0x"));
    Assert.That(text, Does.Contain("version_word=0x"));
    Assert.That(text, Does.Contain("version_major="));
    Assert.That(text, Does.Contain($"source_path={path}"));
    Assert.That(text, Does.Contain("source_path_offset="));
    Assert.That(text, Does.Contain("zlib_substream_count="));
    Assert.That(text, Does.Contain("trailer_imgf_present="));
    Assert.That(text, Does.Contain("trailing_ff_padding=8"));
    Assert.That(text, Does.Contain("upgrade_blockers="));
    Assert.That(text, Does.Contain("references="));
    Assert.That(text, Does.Contain("vendor="));
    Assert.That(text, Does.Contain("extension=.pbd"));

    // Pin the named blockers so a silent edit can't strip them.
    Assert.That(text, Does.Contain("proprietary-block-tables"));
    Assert.That(text, Does.Contain("aes-key-envelope"));
    Assert.That(text, Does.Contain("vendor-only-engine"));
    Assert.That(text, Does.Contain("no-public-spec"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesBothSyntheticEntries() {
    var d = new EaseUsFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), "easeus_pbd_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tmp);
    try {
      var image = BuildImage();
      using var ms = new MemoryStream(image);
      d.Extract(ms, tmp, password: null, files: null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "easeus-backup.pbd")), Is.True);
      var blobLen = new FileInfo(Path.Combine(tmp, "easeus-backup.pbd")).Length;
      Assert.That(blobLen, Is.EqualTo(image.Length));
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best-effort */ }
    }
  }
}
