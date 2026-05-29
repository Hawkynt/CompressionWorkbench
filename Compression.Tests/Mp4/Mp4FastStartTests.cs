#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mp4;
using Compression.Registry;

namespace Compression.Tests.Mp4;

[TestFixture]
public class Mp4FastStartTests {

  /// <summary>
  /// Builds a minimal MP4 file: ftyp + mdat + moov (moov-at-end layout).
  /// The moov contains a single trak → mdia → minf → stbl → stco with one
  /// chunk offset pointing into mdat. This is the non-fast-start layout
  /// that players must download entirely before starting playback.
  /// </summary>
  private static byte[] BuildMoovAtEnd(out long originalStcoOffset) {
    // ftyp: 20 bytes (size:4 + type:4 + major_brand:4 + minor_version:4 + compatible_brand:4)
    var ftyp = BuildAtom("ftyp", [
      .."isom"u8, // major brand
      ..new byte[4], // minor version
      .."isom"u8, // compatible brand
    ]);

    // mdat: 8 bytes header + 100 bytes of dummy media data
    var mdatPayload = new byte[100];
    Array.Fill(mdatPayload, (byte)0xAB);
    var mdat = BuildAtom("mdat", mdatPayload);

    // stco: version(1) + flags(3) + entry_count(4) + one 4-byte offset
    // The offset points to the start of mdat body (ftyp.length + mdat header size = 20 + 8 = 28)
    var mdatBodyOffset = (uint)(ftyp.Length + 8); // offset where mdat body starts
    var stcoBody = new byte[4 + 4 + 4]; // version+flags, count, one offset
    BinaryPrimitives.WriteUInt32BigEndian(stcoBody.AsSpan(4), 1); // entry count
    BinaryPrimitives.WriteUInt32BigEndian(stcoBody.AsSpan(8), mdatBodyOffset);
    var stco = BuildAtom("stco", stcoBody);

    // stsz: version(1) + flags(3) + sample_size(4) + sample_count(4)
    var stszBody = new byte[12];
    BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(4), (uint)mdatPayload.Length); // fixed size
    BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(8), 1); // one sample
    var stsz = BuildAtom("stsz", stszBody);

    // stsc: version(1) + flags(3) + entry_count(4) + one record (first_chunk:4, samples_per_chunk:4, desc_idx:4)
    var stscBody = new byte[4 + 4 + 12];
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(4), 1); // 1 record
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(8), 1); // first_chunk (1-based)
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(12), 1); // samples_per_chunk
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(16), 1); // sample_description_index
    var stsc = BuildAtom("stsc", stscBody);

    // stsd: version(1) + flags(3) + entry_count(4) — minimal, no actual codec entries
    var stsdBody = new byte[8];
    var stsd = BuildAtom("stsd", stsdBody);

    // stbl = container of stsd + stsc + stsz + stco
    var stbl = BuildContainerAtom("stbl", [stsd, stsc, stsz, stco]);

    // dinf: empty container (normally has dref; minimal for tests)
    var dinf = BuildContainerAtom("dinf", []);

    // minf = container of dinf + stbl
    var minf = BuildContainerAtom("minf", [dinf, stbl]);

    // hdlr: version(1) + flags(3) + pre_defined(4) + handler_type(4) + reserved(12) + name
    var hdlrBody = new byte[4 + 4 + 4 + 12 + 5]; // + "vide\0"
    "vide"u8.CopyTo(hdlrBody.AsSpan(8));
    "vide\0"u8.CopyTo(hdlrBody.AsSpan(24));
    var hdlr = BuildAtom("hdlr", hdlrBody);

    // mdhd: version 0 — creation(4) + modification(4) + timescale(4) + duration(4) + lang(2) + predefined(2)
    var mdhdBody = new byte[4 + 4 + 4 + 4 + 4 + 2 + 2]; // = 24
    BinaryPrimitives.WriteUInt32BigEndian(mdhdBody.AsSpan(12), 1000); // timescale
    BinaryPrimitives.WriteUInt32BigEndian(mdhdBody.AsSpan(16), 1000); // duration
    var mdhd = BuildAtom("mdhd", mdhdBody);

    // mdia = container of mdhd + hdlr + minf
    var mdia = BuildContainerAtom("mdia", [mdhd, hdlr, minf]);

    // tkhd: version 0 — version(1) + flags(3) + creation(4) + modification(4) + track_id(4) + reserved(4) + duration(4) + ...
    var tkhdBody = new byte[4 + 4 + 4 + 4 + 4 + 4 + 8 + 2 + 2 + 2 + 2 + 36 + 4 + 4]; // = 84
    tkhdBody[3] = 1; // flags: track enabled
    BinaryPrimitives.WriteUInt32BigEndian(tkhdBody.AsSpan(12), 1); // track_id
    BinaryPrimitives.WriteUInt32BigEndian(tkhdBody.AsSpan(20), 1000); // duration
    var tkhd = BuildAtom("tkhd", tkhdBody);

    // trak = container of tkhd + mdia
    var trak = BuildContainerAtom("trak", [tkhd, mdia]);

    // mvhd: version 0 minimal (108 bytes body)
    var mvhdBody = new byte[108]; // all zeros is valid enough for testing
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(12), 1000); // timescale
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(16), 1000); // duration
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(20), 0x00010000); // rate (1.0)
    BinaryPrimitives.WriteUInt16BigEndian(mvhdBody.AsSpan(24), 0x0100); // volume (1.0)
    // Identity matrix at offset 36: row1=[0x10000,0,0], row2=[0,0x10000,0], row3=[0,0,0x40000000]
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(36), 0x00010000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(52), 0x00010000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(68), 0x40000000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(104), 2); // next_track_id
    var mvhd = BuildAtom("mvhd", mvhdBody);

    // moov = container of mvhd + trak
    var moov = BuildContainerAtom("moov", [mvhd, trak]);

    // Assemble: ftyp + mdat + moov (non-fast-start)
    var file = new byte[ftyp.Length + mdat.Length + moov.Length];
    ftyp.CopyTo(file, 0);
    mdat.CopyTo(file, ftyp.Length);
    moov.CopyTo(file, ftyp.Length + mdat.Length);

    originalStcoOffset = mdatBodyOffset;
    return file;
  }

  /// <summary>
  /// Builds an MP4 with moov already before mdat (fast-start layout).
  /// </summary>
  private static byte[] BuildMoovAtFront(out long originalStcoOffset) {
    var ftyp = BuildAtom("ftyp", [
      .."isom"u8,
      ..new byte[4],
      .."isom"u8,
    ]);

    // stco with one offset — will point past moov into mdat body
    var stcoBody = new byte[4 + 4 + 4];
    BinaryPrimitives.WriteUInt32BigEndian(stcoBody.AsSpan(4), 1);
    // Placeholder offset — will be set after we know moov size
    var stco = BuildAtom("stco", stcoBody);

    var stszBody = new byte[12];
    BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(4), 100);
    BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(8), 1);
    var stsz = BuildAtom("stsz", stszBody);

    var stscBody = new byte[4 + 4 + 12];
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(8), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(16), 1);
    var stsc = BuildAtom("stsc", stscBody);

    var stsdBody = new byte[8];
    var stsd = BuildAtom("stsd", stsdBody);
    var stbl = BuildContainerAtom("stbl", [stsd, stsc, stsz, stco]);
    var dinf = BuildContainerAtom("dinf", []);
    var minf = BuildContainerAtom("minf", [dinf, stbl]);

    var hdlrBody = new byte[4 + 4 + 4 + 12 + 5];
    "vide"u8.CopyTo(hdlrBody.AsSpan(8));
    "vide\0"u8.CopyTo(hdlrBody.AsSpan(24));
    var hdlr = BuildAtom("hdlr", hdlrBody);

    var mdhdBody = new byte[24];
    BinaryPrimitives.WriteUInt32BigEndian(mdhdBody.AsSpan(12), 1000);
    BinaryPrimitives.WriteUInt32BigEndian(mdhdBody.AsSpan(16), 1000);
    var mdhd = BuildAtom("mdhd", mdhdBody);
    var mdia = BuildContainerAtom("mdia", [mdhd, hdlr, minf]);

    var tkhdBody = new byte[84];
    tkhdBody[3] = 1;
    BinaryPrimitives.WriteUInt32BigEndian(tkhdBody.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt32BigEndian(tkhdBody.AsSpan(20), 1000);
    var tkhd = BuildAtom("tkhd", tkhdBody);
    var trak = BuildContainerAtom("trak", [tkhd, mdia]);

    var mvhdBody = new byte[108];
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(12), 1000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(16), 1000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(20), 0x00010000);
    BinaryPrimitives.WriteUInt16BigEndian(mvhdBody.AsSpan(24), 0x0100);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(36), 0x00010000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(52), 0x00010000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(68), 0x40000000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(104), 2);
    var mvhd = BuildAtom("mvhd", mvhdBody);
    var moov = BuildContainerAtom("moov", [mvhd, trak]);

    var mdatPayload = new byte[100];
    Array.Fill(mdatPayload, (byte)0xAB);
    var mdat = BuildAtom("mdat", mdatPayload);

    // Now set stco offset: ftyp + moov + mdat header = body offset
    var mdatBodyOffset = (uint)(ftyp.Length + moov.Length + 8);

    // Find stco offset position inside moov and patch it
    var moovOffset = ftyp.Length;
    var file = new byte[ftyp.Length + moov.Length + mdat.Length];
    ftyp.CopyTo(file, 0);
    moov.CopyTo(file, ftyp.Length);
    mdat.CopyTo(file, ftyp.Length + moov.Length);

    // Walk moov to find stco and patch the offset
    PatchStcoInFile(file, moovOffset, moovOffset + moov.Length, mdatBodyOffset);

    originalStcoOffset = mdatBodyOffset;
    return file;
  }

  private static void PatchStcoInFile(byte[] data, int start, int end, uint offset) {
    var pos = start + 8; // skip moov header
    while (pos + 8 <= end) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      if (size < 8 || pos + size > end) break;
      var type = Encoding.ASCII.GetString(data, pos + 4, 4);
      if (type == "stco") {
        // body: version(1)+flags(3)+count(4)+offsets
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(pos + 8 + 8), offset);
        return;
      }
      if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "dinf" or "edts" or "udta")
        PatchStcoInFile(data, pos, pos + size, offset);
      pos += size;
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_MoovAtEnd_MovesMoovBeforeMdat() {
    var file = BuildMoovAtEnd(out _);
    using var stream = new MemoryStream(file);

    var fastStart = new Mp4FastStart();
    fastStart.Optimize(stream);

    // Re-walk atoms — moov should now be before mdat.
    stream.Position = 0;
    var atoms = Mp4FastStart.WalkTopLevelAtoms(stream);
    var moovIdx = atoms.FindIndex(a => a.Type == "moov");
    var mdatIdx = atoms.FindIndex(a => a.Type == "mdat");

    Assert.That(moovIdx, Is.GreaterThanOrEqualTo(0), "moov atom should exist");
    Assert.That(mdatIdx, Is.GreaterThanOrEqualTo(0), "mdat atom should exist");
    Assert.That(moovIdx, Is.LessThan(mdatIdx), "moov should be before mdat after optimization");
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_MoovAtEnd_StcoOffsetsPatched() {
    var file = BuildMoovAtEnd(out var originalOffset);
    using var stream = new MemoryStream(file);

    var fastStart = new Mp4FastStart();
    fastStart.Optimize(stream);

    // Read moov size from the optimized file.
    stream.Position = 0;
    var atoms = Mp4FastStart.WalkTopLevelAtoms(stream);
    var moov = atoms.First(a => a.Type == "moov");
    var mdat = atoms.First(a => a.Type == "mdat");

    // Find stco inside moov and read the patched offset.
    var data = stream.ToArray();
    var stcoOffset = FindStcoOffset(data, (int)moov.Offset, (int)(moov.Offset + moov.Size));
    Assert.That(stcoOffset, Is.GreaterThan(0), "stco should exist in moov");

    var patchedOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(stcoOffset));

    // The patched offset should point to mdat body (mdat.Offset + 8).
    var expectedMdatBody = (uint)(mdat.Offset + 8);
    Assert.That(patchedOffset, Is.EqualTo(expectedMdatBody),
      $"stco offset should point to mdat body at {expectedMdatBody}, but was {patchedOffset}");
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_MoovAtEnd_FileRemainsValid() {
    var file = BuildMoovAtEnd(out _);
    var originalLength = file.Length;
    using var stream = new MemoryStream(file);

    var fastStart = new Mp4FastStart();
    fastStart.Optimize(stream);

    // File length should be unchanged.
    Assert.That(stream.Length, Is.EqualTo(originalLength), "File length should not change");

    // All atoms should still be parseable.
    stream.Position = 0;
    var atoms = Mp4FastStart.WalkTopLevelAtoms(stream);
    Assert.That(atoms.Count, Is.GreaterThanOrEqualTo(3), "Should have at least ftyp + moov + mdat");

    // Verify atom types.
    var types = atoms.Select(a => a.Type).ToList();
    Assert.That(types, Does.Contain("ftyp"));
    Assert.That(types, Does.Contain("moov"));
    Assert.That(types, Does.Contain("mdat"));

    // Verify total coverage equals file length.
    var totalSize = atoms.Sum(a => a.Size);
    Assert.That(totalSize, Is.EqualTo(originalLength), "Atoms should cover entire file");
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_MoovAlreadyFirst_IsNoOp() {
    var file = BuildMoovAtFront(out _);
    var original = file.ToArray(); // snapshot
    using var stream = new MemoryStream(file);

    var fastStart = new Mp4FastStart();
    fastStart.Optimize(stream);

    Assert.That(stream.ToArray(), Is.EqualTo(original), "File should not be modified when moov is already before mdat");
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_MoovAtEnd_MdatDataPreserved() {
    var file = BuildMoovAtEnd(out _);

    // Extract mdat payload from the original file.
    using var origStream = new MemoryStream(file, writable: false);
    var origAtoms = Mp4FastStart.WalkTopLevelAtoms(origStream);
    var origMdat = origAtoms.First(a => a.Type == "mdat");
    var origMdatBody = new byte[origMdat.Size - origMdat.HeaderSize];
    origStream.Position = origMdat.Offset + origMdat.HeaderSize;
    origStream.ReadExactly(origMdatBody);

    // Optimize.
    using var stream = new MemoryStream(file);
    new Mp4FastStart().Optimize(stream);

    // Extract mdat payload from the optimized file.
    stream.Position = 0;
    var newAtoms = Mp4FastStart.WalkTopLevelAtoms(stream);
    var newMdat = newAtoms.First(a => a.Type == "mdat");
    var newMdatBody = new byte[newMdat.Size - newMdat.HeaderSize];
    stream.Position = newMdat.Offset + newMdat.HeaderSize;
    stream.ReadExactly(newMdatBody);

    Assert.That(newMdatBody, Is.EqualTo(origMdatBody), "mdat media data should be preserved after optimization");
  }

  [Category("HappyPath")]
  [Test]
  public void EnumerateChunks_ClassifiesAtomTypes() {
    var file = BuildMoovAtEnd(out _);
    using var stream = new MemoryStream(file);

    var layoutMap = new Mp4LayoutMap();
    var chunks = layoutMap.EnumerateChunks(stream).ToList();

    Assert.That(chunks.Count, Is.GreaterThanOrEqualTo(3));

    var ftyp = chunks.First(c => c.FileName != null && c.FileName.Contains("ftyp"));
    Assert.That(ftyp.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));

    var mdat = chunks.First(c => c.FileName != null && c.FileName.Contains("mdat"));
    Assert.That(mdat.Kind, Is.EqualTo(DefragBlockKind.Used));

    var moov = chunks.First(c => c.FileName != null && c.FileName.Contains("moov"));
    Assert.That(moov.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Category("HappyPath")]
  [Test]
  public void EnumerateChunks_CoversEntireFile() {
    var file = BuildMoovAtEnd(out _);
    using var stream = new MemoryStream(file);

    var layoutMap = new Mp4LayoutMap();
    var chunks = layoutMap.EnumerateChunks(stream).ToList();

    var totalSize = chunks.Sum(c => c.Length);
    Assert.That(totalSize, Is.EqualTo(file.Length), "Chunks should cover entire file");
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_WithCo64_OffsetsPatched() {
    // Build a variant with co64 instead of stco.
    var file = BuildMoovAtEndWithCo64(out var originalOffset);
    using var stream = new MemoryStream(file);

    new Mp4FastStart().Optimize(stream);

    stream.Position = 0;
    var atoms = Mp4FastStart.WalkTopLevelAtoms(stream);
    var moov = atoms.First(a => a.Type == "moov");
    var mdat = atoms.First(a => a.Type == "mdat");

    Assert.That(moov.Offset, Is.LessThan(mdat.Offset), "moov should be before mdat");

    // Find co64 inside moov and check offset.
    var data = stream.ToArray();
    var co64OffsetPos = FindCo64Offset(data, (int)moov.Offset, (int)(moov.Offset + moov.Size));
    Assert.That(co64OffsetPos, Is.GreaterThan(0), "co64 should exist in moov");

    var patchedOffset = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(co64OffsetPos));
    var expectedMdatBody = (ulong)(mdat.Offset + 8);
    Assert.That(patchedOffset, Is.EqualTo(expectedMdatBody));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_ImplementsInterfaces() {
    var descriptor = new Mp4FormatDescriptor();
    Assert.That(descriptor, Is.InstanceOf<IFileInternalLayoutMap>());
    Assert.That(descriptor, Is.InstanceOf<IFileInternalChunkMover>());
  }

  // ── Co64 variant builder ──────────────────────────────────────────────

  private static byte[] BuildMoovAtEndWithCo64(out long originalOffset) {
    var ftyp = BuildAtom("ftyp", [
      .."isom"u8,
      ..new byte[4],
      .."isom"u8,
    ]);

    var mdatPayload = new byte[100];
    Array.Fill(mdatPayload, (byte)0xCD);
    var mdat = BuildAtom("mdat", mdatPayload);

    var mdatBodyOffset = (ulong)(ftyp.Length + 8);

    // co64 instead of stco
    var co64Body = new byte[4 + 4 + 8]; // version+flags, count, one 8-byte offset
    BinaryPrimitives.WriteUInt32BigEndian(co64Body.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt64BigEndian(co64Body.AsSpan(8), mdatBodyOffset);
    var co64 = BuildAtom("co64", co64Body);

    var stszBody = new byte[12];
    BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(4), 100);
    BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(8), 1);
    var stsz = BuildAtom("stsz", stszBody);

    var stscBody = new byte[4 + 4 + 12];
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(8), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(16), 1);
    var stsc = BuildAtom("stsc", stscBody);

    var stsdBody = new byte[8];
    var stsd = BuildAtom("stsd", stsdBody);
    var stbl = BuildContainerAtom("stbl", [stsd, stsc, stsz, co64]);
    var dinf = BuildContainerAtom("dinf", []);
    var minf = BuildContainerAtom("minf", [dinf, stbl]);

    var hdlrBody = new byte[4 + 4 + 4 + 12 + 5];
    "vide"u8.CopyTo(hdlrBody.AsSpan(8));
    "vide\0"u8.CopyTo(hdlrBody.AsSpan(24));
    var hdlr = BuildAtom("hdlr", hdlrBody);

    var mdhdBody = new byte[24];
    BinaryPrimitives.WriteUInt32BigEndian(mdhdBody.AsSpan(12), 1000);
    BinaryPrimitives.WriteUInt32BigEndian(mdhdBody.AsSpan(16), 1000);
    var mdhd = BuildAtom("mdhd", mdhdBody);
    var mdia = BuildContainerAtom("mdia", [mdhd, hdlr, minf]);

    var tkhdBody = new byte[84];
    tkhdBody[3] = 1;
    BinaryPrimitives.WriteUInt32BigEndian(tkhdBody.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt32BigEndian(tkhdBody.AsSpan(20), 1000);
    var tkhd = BuildAtom("tkhd", tkhdBody);
    var trak = BuildContainerAtom("trak", [tkhd, mdia]);

    var mvhdBody = new byte[108];
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(12), 1000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(16), 1000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(20), 0x00010000);
    BinaryPrimitives.WriteUInt16BigEndian(mvhdBody.AsSpan(24), 0x0100);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(36), 0x00010000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(52), 0x00010000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(68), 0x40000000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(104), 2);
    var mvhd = BuildAtom("mvhd", mvhdBody);
    var moov = BuildContainerAtom("moov", [mvhd, trak]);

    var file = new byte[ftyp.Length + mdat.Length + moov.Length];
    ftyp.CopyTo(file, 0);
    mdat.CopyTo(file, ftyp.Length);
    moov.CopyTo(file, ftyp.Length + mdat.Length);

    originalOffset = (long)mdatBodyOffset;
    return file;
  }

  // ── Atom builder helpers ──────────────────────────────────────────────

  private static byte[] BuildAtom(string type, byte[] body) {
    var size = 8 + body.Length;
    var atom = new byte[size];
    BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)size);
    Encoding.ASCII.GetBytes(type, 0, 4, atom, 4);
    body.CopyTo(atom, 8);
    return atom;
  }

  private static byte[] BuildContainerAtom(string type, byte[][] children) {
    var totalChildSize = children.Sum(c => c.Length);
    var size = 8 + totalChildSize;
    var atom = new byte[size];
    BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)size);
    Encoding.ASCII.GetBytes(type, 0, 4, atom, 4);
    var offset = 8;
    foreach (var child in children) {
      child.CopyTo(atom, offset);
      offset += child.Length;
    }
    return atom;
  }

  /// <summary>
  /// Recursively finds the stco atom inside a range and returns the offset of
  /// the first entry in the offset table.
  /// </summary>
  private static int FindStcoOffset(byte[] data, int start, int end) {
    var pos = start + 8; // skip container header
    while (pos + 8 <= end) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      if (size < 8 || pos + size > end) break;
      var type = Encoding.ASCII.GetString(data, pos + 4, 4);
      if (type == "stco") {
        // body: version+flags(4) + count(4) + offsets
        return pos + 8 + 8; // first offset entry
      }
      if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "dinf") {
        var inner = FindStcoOffset(data, pos, pos + size);
        if (inner > 0) return inner;
      }
      pos += size;
    }
    return -1;
  }

  private static int FindCo64Offset(byte[] data, int start, int end) {
    var pos = start + 8;
    while (pos + 8 <= end) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      if (size < 8 || pos + size > end) break;
      var type = Encoding.ASCII.GetString(data, pos + 4, 4);
      if (type == "co64")
        return pos + 8 + 8; // first offset entry (8 bytes)
      if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "dinf") {
        var inner = FindCo64Offset(data, pos, pos + size);
        if (inner > 0) return inner;
      }
      pos += size;
    }
    return -1;
  }
}
