using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.MooseFs;

namespace Compression.Tests.MooseFs;

/// <summary>
/// Tests for the MooseFS master-metadata partial R/O reader.
/// MooseFS itself is a distributed FS and we don't have golden cluster
/// images, so these tests use synthetic MooseFS-shaped images built byte by
/// byte against the documented envelope: 8-byte signature, 16-byte counters,
/// stream of (8-byte tag, 8-byte BE length, payload) sections, 16-byte EOF
/// marker. This validates the envelope walker without making any claim
/// about the version-specific NODE / EDGE / CHNK record bodies.
/// </summary>
[TestFixture]
public class MooseFsTests {

  private static byte[] BuildHeader(string signature = "MFSM 2.0",
                                    ulong fileIdCounter = 100,
                                    ulong metadataVersion = 42) {
    if (signature.Length != 8)
      throw new ArgumentException("MooseFS signature must be exactly 8 ASCII bytes.", nameof(signature));
    var buf = new byte[8 + 16];
    Encoding.ASCII.GetBytes(signature).CopyTo(buf.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(8, 8), fileIdCounter);
    BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(16, 8), metadataVersion);
    return buf;
  }

  private static byte[] BuildSection(string tag, byte[] payload) {
    if (tag.Length != 8)
      throw new ArgumentException("MooseFS section tag must be exactly 8 ASCII bytes.", nameof(tag));
    var buf = new byte[8 + 8 + payload.Length];
    Encoding.ASCII.GetBytes(tag).CopyTo(buf.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(8, 8), (ulong)payload.Length);
    payload.CopyTo(buf.AsSpan(16));
    return buf;
  }

  private static byte[] EofMarker() => "[MFS EOF MARKER]"u8.ToArray();

  private static byte[] Concat(params byte[][] parts) {
    var total = parts.Sum(p => p.Length);
    var buf = new byte[total];
    var off = 0;
    foreach (var p in parts) {
      p.CopyTo(buf.AsSpan(off));
      off += p.Length;
    }
    return buf;
  }

  private static byte[] BuildMinimalImageWithSections(params (string Tag, int PayloadLen)[] sections) {
    var parts = new List<byte[]> { BuildHeader() };
    foreach (var (tag, len) in sections) {
      var payload = new byte[len];
      for (var i = 0; i < len; i++) payload[i] = (byte)(i & 0xFF);
      parts.Add(BuildSection(tag, payload));
    }
    parts.Add(EofMarker());
    return Concat(parts.ToArray());
  }

  // ============================================================
  // Descriptor sanity checks
  // ============================================================

  [Test, Category("HappyPath")]
  public void Descriptor_IdentifiesByMagic() {
    var d = new MooseFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("MooseFs"));
    Assert.That(d.Extensions, Does.Contain(".mfsm"));
    Assert.That(d.Extensions, Does.Not.Contain(".mfs"),
      "MooseFS must not claim .mfs — it collides with the Macintosh File System (FileSystem.Mfs).");
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("MFSM"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DoesNotAdvertiseCreateOrModify() {
    var d = new MooseFsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "MooseFS metadata.mfs is not writable from a single image — chunks live on chunk servers.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DescriptionStatesChunkServerLimitation() {
    var d = new MooseFsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("chunk server").Or.Contain("chunk-server").Or.Contain("chunkserver"),
      "Description must be honest about file data living on chunk servers.");
  }

  // ============================================================
  // Reader — equivalence classes for the parser entry
  // ============================================================

  [Test, Category("Boundary")]
  public void Reader_RejectsImageSmallerThanHeader() {
    using var ms = new MemoryStream(new byte[] { 0x4D, 0x46, 0x53 }); // "MFS", missing M
    Assert.Throws<InvalidDataException>(() => _ = new MooseFsReader(ms));
  }

  [Test, Category("Exception")]
  public void Reader_RejectsWrongMagic() {
    var buf = new byte[64];
    Encoding.ASCII.GetBytes("NOPENOPE").CopyTo(buf.AsSpan(0, 8));
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new MooseFsReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_HeaderOnly_ParsesSignatureAndCounters() {
    // Just header (8 bytes signature + 16 bytes counters) + EOF marker —
    // no sections. ParseStatus must be "ok" since the EOF marker IS the
    // legal end of the stream.
    var image = Concat(BuildHeader(), EofMarker());
    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Signature, Is.EqualTo("MFSM 2.0"));
    Assert.That(r.FileIdCounter, Is.EqualTo(100UL));
    Assert.That(r.MetadataVersion, Is.EqualTo(42UL));
    Assert.That(r.ParseStatus, Is.EqualTo("ok"));
    Assert.That(r.Sections, Is.Empty);
    Assert.That(r.Entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "metadata.ini", "moosefs-master.bin" }));
  }

  [Test, Category("Boundary")]
  public void Reader_TooShortForCounters_StaysHeaderOnly() {
    // Only signature, no room for 16-byte counters or sections.
    var image = "MFSM 2.0"u8.ToArray();
    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Signature, Is.EqualTo("MFSM 2.0"));
    Assert.That(r.FileIdCounter, Is.Null);
    Assert.That(r.MetadataVersion, Is.Null);
    Assert.That(r.ParseStatus, Is.EqualTo("header-only"));
  }

  // ============================================================
  // Reader — section index walk
  // ============================================================

  [Test, Category("HappyPath")]
  public void Reader_WalksAllSections_AndStopsAtEofMarker() {
    var image = BuildMinimalImageWithSections(
      ("SESS 1.0", 32),
      ("STAT 1.0", 16),
      ("NODE 1.0", 256),
      ("EDGE 1.0", 128),
      ("CHNK 1.0", 64));
    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);

    Assert.That(r.ParseStatus, Is.EqualTo("ok"));
    Assert.That(r.Sections, Has.Count.EqualTo(5));
    Assert.That(r.Sections.Select(s => s.Tag),
      Is.EqualTo(new[] { "SESS 1.0", "STAT 1.0", "NODE 1.0", "EDGE 1.0", "CHNK 1.0" }));
    Assert.That(r.Sections.Select(s => s.Length),
      Is.EqualTo(new long[] { 32, 16, 256, 128, 64 }));
  }

  [Test, Category("HappyPath")]
  public void Reader_SurfacesPerSectionPayloadAsEntry() {
    var image = BuildMinimalImageWithSections(
      ("NODE 1.0", 100),
      ("EDGE 1.0", 50));
    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);

    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("moosefs-master.bin"));
    Assert.That(names, Does.Contain("section_NODE_1_0.bin"));
    Assert.That(names, Does.Contain("section_EDGE_1_0.bin"));

    var node = r.Entries.First(e => e.Name == "section_NODE_1_0.bin");
    Assert.That(node.Size, Is.EqualTo(100));
    // First payload byte was set to (byte)(i & 0xFF) = 0 at i=0.
    Assert.That(node.Data[0], Is.EqualTo(0));
    Assert.That(node.Data[99], Is.EqualTo(99));
  }

  [Test, Category("Exception")]
  public void Reader_TruncatedSectionLength_MarksTruncated() {
    // Build header + a section that claims more bytes than the image actually has.
    var header = BuildHeader();
    var tag = "NODE 1.0"u8.ToArray();
    var lenBuf = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(lenBuf, 10_000UL); // claim 10k bytes
    var shortPayload = new byte[100]; // only 100 actually present
    var image = Concat(header, tag, lenBuf, shortPayload);

    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.ParseStatus, Is.EqualTo("truncated"));
    Assert.That(r.Sections, Is.Empty,
      "A length-overflow section must not be added to the section list.");
  }

  [Test, Category("Exception")]
  public void Reader_NoEofMarker_MarksTruncated() {
    // Valid sections but no [MFS EOF MARKER] trailing.
    var header = BuildHeader();
    var sec = BuildSection("NODE 1.0", new byte[64]);
    var image = Concat(header, sec); // no EOF marker
    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);
    Assert.That(r.ParseStatus, Is.EqualTo("truncated"));
    Assert.That(r.Sections, Has.Count.EqualTo(1),
      "The walker should still surface every successfully parsed section before noticing the missing EOF marker.");
  }

  [Test, Category("Exception")]
  public void Reader_NonAsciiSectionTag_MarksTruncated() {
    // Image with a "section tag" that's actually binary garbage — walker
    // must reject it rather than accepting random bytes as a tag.
    var header = BuildHeader();
    var garbageTag = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
    var lenBuf = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(lenBuf, 0UL);
    var image = Concat(header, garbageTag, lenBuf, EofMarker());
    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);
    Assert.That(r.ParseStatus, Is.EqualTo("truncated"));
    Assert.That(r.Sections, Is.Empty);
  }

  // ============================================================
  // metadata.ini content
  // ============================================================

  [Test, Category("HappyPath")]
  public void MetadataIni_ReportsHeaderAndSectionTable() {
    var image = BuildMinimalImageWithSections(
      ("NODE 1.0", 100),
      ("EDGE 1.0", 50));
    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);

    var ini = r.Entries.First(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(ini.Data);

    Assert.That(text, Does.Contain("parse_status=ok"));
    Assert.That(text, Does.Contain("signature=MFSM 2.0"));
    Assert.That(text, Does.Contain("magic_tag=MFSM"));
    Assert.That(text, Does.Contain("file_id_counter=100"));
    Assert.That(text, Does.Contain("metadata_version=42"));
    Assert.That(text, Does.Contain("section_count=2"));
    Assert.That(text, Does.Contain("NODE 1.0"));
    Assert.That(text, Does.Contain("EDGE 1.0"));
    Assert.That(text.ToLowerInvariant(), Does.Contain("chunk server"));
  }

  [Test, Category("HappyPath")]
  public void MetadataIni_HeaderOnly_FlagsHeaderOnlyStatus() {
    // Bare 8-byte signature, no counters or sections at all.
    var image = "MFSM 2.0"u8.ToArray();
    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);
    var ini = r.Entries.First(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(ini.Data);
    Assert.That(text, Does.Contain("parse_status=header-only"));
    Assert.That(text, Does.Contain("section_count=0"));
  }

  // ============================================================
  // Descriptor.List / Extract / OpenEntry — public surface
  // ============================================================

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsMetadataPlusSections() {
    var d = new MooseFsFormatDescriptor();
    var image = BuildMinimalImageWithSections(("NODE 1.0", 32));
    using var ms = new MemoryStream(image);
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("moosefs-master.bin"));
    Assert.That(names, Does.Contain("section_NODE_1_0.bin"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_WritesAllSurfacedEntries() {
    var d = new MooseFsFormatDescriptor();
    var image = BuildMinimalImageWithSections(("NODE 1.0", 32));
    var tempDir = Path.Combine(Path.GetTempPath(), "moosefs-extract-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    try {
      using var ms = new MemoryStream(image);
      d.Extract(ms, tempDir, password: null, files: null);

      Assert.That(File.Exists(Path.Combine(tempDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tempDir, "moosefs-master.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tempDir, "section_NODE_1_0.bin")), Is.True);
    } finally {
      try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_OpenEntry_ReturnsBoundedStream() {
    var d = (IArchiveFormatOperations)new MooseFsFormatDescriptor();
    var image = BuildMinimalImageWithSections(("NODE 1.0", 100));
    using var ms = new MemoryStream(image);
    using var s = d.OpenEntry(ms, "section_NODE_1_0.bin", password: null);
    Assert.That(s.Length, Is.EqualTo(100));
    var buf = new byte[200];
    var n = s.Read(buf, 0, buf.Length);
    Assert.That(n, Is.EqualTo(100),
      "Bounded entry stream must yield exactly the section payload, never adjacent bytes.");
  }

  [Test, Category("Exception")]
  public void Descriptor_OpenEntry_UnknownName_Throws() {
    var d = (IArchiveFormatOperations)new MooseFsFormatDescriptor();
    var image = BuildMinimalImageWithSections(("NODE 1.0", 32));
    using var ms = new MemoryStream(image);
    Assert.Throws<FileNotFoundException>(() => d.OpenEntry(ms, "does-not-exist.bin", password: null));
  }

  // ============================================================
  // Pre-1.6 signature path (no counters)
  // ============================================================

  [Test, Category("HappyPath")]
  public void Reader_LegacyMfsm15Signature_SkipsCounterBlock() {
    // For "MFSM 1.4" / "MFSM 1.5", section stream starts immediately after
    // the 8-byte signature — there are no counter bytes. We build a minimal
    // image and confirm the walker picks up sections from offset 8.
    var sig = "MFSM 1.5"u8.ToArray();
    var sec = BuildSection("NODE 1.0", new byte[16]);
    var image = Concat(sig, sec, EofMarker());
    using var ms = new MemoryStream(image);
    var r = new MooseFsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Signature, Is.EqualTo("MFSM 1.5"));
    Assert.That(r.ParseStatus, Is.EqualTo("ok"));
    Assert.That(r.Sections, Has.Count.EqualTo(1));
    Assert.That(r.Sections[0].Tag, Is.EqualTo("NODE 1.0"));
    Assert.That(r.Sections[0].Length, Is.EqualTo(16));
  }
}
