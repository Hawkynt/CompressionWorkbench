#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;

namespace FileFormat.Acronis;

/// <summary>
/// True in-place R/W modifier for Acronis classic .tib slices — the on-disk surface that
/// turns the format from WORM / R-only into R/W via record-stream append.
/// </summary>
/// <remarks>
/// <para>
/// <b>On-disk invariant.</b> Every operation (<see cref="Add"/>, <see cref="Replace"/>,
/// <see cref="Remove"/>) is an EOF-only append: the existing bytes <c>[0, oldLength)</c> are
/// preserved byte-identical, and a fresh record batch + a fresh <c>EndTrailer + 12-byte
/// file-system trailer + 48-byte mirror footer</c> trio is appended at the new EOF. The reader's
/// trailer lookup ALWAYS reads the last 60 bytes, so the latest batch's trailer wins; the prior
/// batch's trailer/footer bytes remain mid-stream as inert anchors that
/// <see cref="AcronisRecordReader.ReadAll"/> sniffs and skips via the file-system magic
/// (<c>2C 8A E1 94</c>) at offset +8 of the 12-byte trailer block.
/// </para>
/// <para>
/// <b>Record framing</b> (per the existing reverse-engineered shapes):
/// </para>
/// <list type="bullet">
///   <item><description><b>Listing(103)</b>, <b>FirstFileMetaRecord(102)</b>, <b>FileMetaA/B/C(1/2/5)</b>:
///     1-byte type tag + raw-deflate body + 4-byte trailing checksum (zeros are accepted).</description></item>
///   <item><description><b>RecordIndex(108)</b>, <b>Blob(109)</b>: 1-byte type tag + 2-byte zlib header
///     (<c>0x78 0x9C</c>) + raw-deflate body + 4-byte Adler-32 trailer.</description></item>
///   <item><description><b>EndTrailer(104)</b>: 1-byte type tag only.</description></item>
/// </list>
/// <para>
/// <b>Per-name latest-wins + tombstones.</b> Add emits a Listing record carrying the new entry
/// (with a <c>MetaOffset</c> pointing at the entry's just-appended FirstFileMetaRecord(102) anchor);
/// Replace emits a fresh Listing carrying the same name but a new <c>MetaOffset</c> pointing at a
/// fresh 102 anchor + fresh chain; Remove emits a Listing whose entry carries
/// <see cref="AcronisReader.TombstoneMetaOffset"/> as the <c>MetaOffset</c>. The reader's
/// per-name latest-wins gate (in <see cref="AcronisReader"/>) treats the tombstone as
/// "entry removed" and drops it from the live entry view.
/// </para>
/// <para>
/// <b>Synthesised FileMeta bodies.</b> Each new chain emits a minimal but real
/// <see cref="AcronisFileMetaBody"/> for the 102 anchor carrying an
/// <see cref="AcronisItemCommonAttribute"/> with the file's name (so
/// <see cref="AcronisReader.DecodedNamesByEntry"/> surfaces it just like for native chains), and
/// the 1/2/5 records carry tiny ASCII markers consistent with the existing test fixtures so
/// the chain walk anchors cleanly. The chain walk's <c>MetaOffset</c> → 102 lookup makes the
/// anchored 102 the authoritative entry root, and the next 108 in archive order becomes the
/// entry's RecordIndex; the existing per-blob MD5 check still gates extraction.
/// </para>
/// </remarks>
public static class AcronisInPlaceModifier {

  // 12-byte file-system trailer magic at +8 (same constants the existing reader/trailer parser use).
  private static ReadOnlySpan<byte> FileSystemTrailerMagic => [0x2C, 0x8A, 0xE1, 0x94];
  private const int FileSystemTrailerLength = 12;
  private const int FooterLength = 48;

  /// <summary>
  /// Appends one or more new files into <paramref name="image"/> at end-of-stream. Existing
  /// bytes are byte-identical; the freshly appended batch carries a Listing record per file
  /// followed by the file's 102 → 1 → 2 → 5 chain, RecordIndex(108) and a single Blob(109) per
  /// file. A fresh EndTrailer + 12-byte fs trailer + 48-byte mirror footer is written at the
  /// new EOF.
  /// </summary>
  public static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Acronis in-place modify requires a seekable read/write stream.", nameof(image));
    if (inputs.Count == 0) return;

    var ctx = OpenForAppend(image);
    image.Position = image.Length;

    var headerLength = ctx.HeaderLength;
    foreach (var input in inputs) {
      if (input.IsDirectory) continue; // classic .tib FileMeta carries files; directories are implied by Listing paths
      var content = input.ReadContent();
      var (path, name) = SplitArchiveName(input.ArchiveName);
      AppendOneFileBatch(image, headerLength, path, name, content);
    }

    AppendBatchEpilogue(image, ctx);
  }

  /// <summary>
  /// Replaces the content of an existing entry by appending a fresh chain. The Replace semantic
  /// is: the OLD Listing record + OLD chain remain byte-identical at their original offsets; a
  /// NEW Listing record carrying the same name (with a fresh <c>MetaOffset</c> pointing at the
  /// freshly-appended 102 anchor) is emitted, plus the freshly-appended 102 → 1 → 2 → 5 → 108 →
  /// 109 chain for <paramref name="newData"/>. The reader's per-name latest-wins gate (in
  /// <see cref="AcronisReader"/>) picks up the new content; the old chain is no longer
  /// reachable through the live entry view.
  /// </summary>
  public static void Replace(Stream image, string entryName, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryName);
    ArgumentNullException.ThrowIfNull(newData);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Acronis in-place modify requires a seekable read/write stream.", nameof(image));

    var ctx = OpenForAppend(image);
    image.Position = image.Length;

    var (path, name) = SplitArchiveName(entryName);
    AppendOneFileBatch(image, ctx.HeaderLength, path, name, newData);

    AppendBatchEpilogue(image, ctx);
  }

  /// <summary>
  /// Appends a tombstone Listing record removing <paramref name="entryName"/> from the live
  /// entry view. The on-disk semantic is: a Listing record carrying the entry's name with
  /// <see cref="AcronisReader.TombstoneMetaOffset"/> as the <c>MetaOffset</c> sentinel. The
  /// reader's per-name latest-wins gate treats this as "entry removed" and drops it. The OLD
  /// Listing + OLD chain remain byte-identical at their original offsets — Remove is byte-
  /// preserving on the payload, not a forensic wipe.
  /// </summary>
  public static void Remove(Stream image, string entryName) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryName);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Acronis in-place modify requires a seekable read/write stream.", nameof(image));

    var ctx = OpenForAppend(image);
    image.Position = image.Length;

    var (path, name) = SplitArchiveName(entryName);
    var listing = BuildListingPayload([(path, name, 0L, AcronisReader.TombstoneMetaOffset)]);
    WriteRawDeflateRecord(image, AcronisRecordType.Listing, listing);

    AppendBatchEpilogue(image, ctx);
  }

  // ----- batch helpers -----

  private readonly record struct AppendContext(int HeaderLength, byte[] HeaderBytes, long OriginalMetaOffset);

  /// <summary>
  /// Reads the OLD volume header + trailer to compute the values we mirror into the new
  /// trailer/footer. The OLD trailer's <c>MetadataOffset</c> stays the canonical metadata
  /// origin — every subsequent batch's record walk starts there, traversing through prior
  /// batches' mid-stream EndTrailer blocks via <see cref="AcronisRecordReader.ReadAll"/>'s
  /// skip-on-fs-magic path.
  /// </summary>
  private static AppendContext OpenForAppend(Stream image) {
    image.Position = 0;
    var header = AcronisVolumeHeader.Read(image);
    if (header.Version != AcronisVolumeVersion.Windows)
      throw new NotSupportedException($"Acronis in-place modify only supports Windows-format slices (version 0); got {header.Version}.");
    var trailer = AcronisSliceTrailer.TryRead(image, header)
                  ?? throw new InvalidDataException("Acronis in-place modify: image has no readable trailer.");
    if (trailer.Form != AcronisSliceForm.FileSystem)
      throw new NotSupportedException($"Acronis in-place modify only supports FileSystem-form slices; got {trailer.Form}.");

    var rawHeader = new byte[header.HeaderLength];
    image.Position = 0;
    image.ReadExactly(rawHeader);
    return new AppendContext(header.HeaderLength, rawHeader, trailer.MetadataOffset);
  }

  /// <summary>
  /// Closes the appended batch by writing an <see cref="AcronisRecordType.EndTrailer"/> tag, a
  /// fresh 12-byte file-system trailer (carrying the <em>original</em> metadata offset so the
  /// reader's record walk still starts where it always did), and a fresh 48-byte mirror footer
  /// whose last 32 bytes are the byte-reversed volume header. The reader picks up the latest
  /// trailer/footer pair by reading the final 60 bytes of the stream.
  /// </summary>
  private static void AppendBatchEpilogue(Stream image, AppendContext ctx) {
    image.WriteByte((byte)AcronisRecordType.EndTrailer);

    Span<byte> trailer = stackalloc byte[FileSystemTrailerLength];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, ctx.OriginalMetaOffset);
    FileSystemTrailerMagic.CopyTo(trailer[8..]);
    image.Write(trailer);

    Span<byte> footer = stackalloc byte[FooterLength];
    BinaryPrimitives.WriteInt64LittleEndian(footer, image.Length + FooterLength);
    // Bytes 16..48 are the reversed volume header.
    for (var i = 0; i < ctx.HeaderBytes.Length && i < 32; i++)
      footer[16 + (31 - i)] = ctx.HeaderBytes[i];
    image.Write(footer);
  }

  /// <summary>
  /// Appends a single file's record batch: Listing record carrying the file's metadata (with
  /// <c>MetaOffset</c> pointing at the freshly-appended 102 anchor), then the 102 → 1 → 2 → 5
  /// chain with a synthesised ItemCommon attribute carrying the file's name, then the
  /// RecordIndex(108) and a single Blob(109) carrying the file's content.
  /// </summary>
  private static void AppendOneFileBatch(Stream image, int headerLength, string path, string name, byte[] content) {
    // 1) Emit the per-file FileMeta chain FIRST so the Listing's MetaOffset can point at the 102.
    var fileMetaStart = image.Position;
    var meta102RelativeOffset = fileMetaStart - headerLength;

    // 102 carries a real attribute body with ItemCommon → name; reader's DecodedNamesByEntry
    // surfaces this exactly the same way it surfaces native chains.
    var meta102Body = BuildItemCommonAttributeStream(name);
    WriteRawDeflateRecord(image, AcronisRecordType.FirstFileMetaRecord, meta102Body);

    // 1/2/5 carry tiny ASCII markers — symmetric with the existing AcronisExtractionTests fixture.
    // Real on-disk bodies are still partially-undecoded resident attribute streams; these markers
    // preserve the chain walk's "next 102/1/2/5/108" sequence without claiming false decode depth.
    WriteRawDeflateRecord(image, AcronisRecordType.FileMetaA, Encoding.ASCII.GetBytes($"cwb-meta1:{name}"));
    WriteRawDeflateRecord(image, AcronisRecordType.FileMetaB, Encoding.ASCII.GetBytes($"cwb-meta2:{name}"));
    WriteRawDeflateRecord(image, AcronisRecordType.FileMetaC, Encoding.ASCII.GetBytes($"cwb-meta5:{name}"));

    // 2) Emit the Blob(109) first so we know its absolute position for the RecordIndex handle.
    var blobAbsolute = image.Position;
    WriteZlibRecord(image, AcronisRecordType.Blob, content);
    var md5 = MD5.HashData(content);

    // 3) Emit the RecordIndex(108) referencing the single blob handle.
    var indexPayload = BuildRecordIndexPayload(
      totalSize: content.LongLength,
      handles: [(0L, blobAbsolute - headerLength, md5)]);
    WriteZlibRecord(image, AcronisRecordType.RecordIndex, indexPayload);

    // 4) Emit the Listing record AFTER the chain so its on-disk position is later than the
    // 102 it points at — anchors the per-name latest-wins ordering by Start position too.
    var listing = BuildListingPayload([(path, name, content.LongLength, meta102RelativeOffset)]);
    WriteRawDeflateRecord(image, AcronisRecordType.Listing, listing);
  }

  /// <summary>
  /// Splits an archive-name (e.g. <c>"dir/sub/file.txt"</c>) into its path component
  /// (<c>"dir/sub/"</c>) and leaf name (<c>"file.txt"</c>) for the Listing record's
  /// path / name fields.
  /// </summary>
  private static (string Path, string Name) SplitArchiveName(string archiveName) {
    var normalized = archiveName.Replace('\\', '/');
    var lastSlash = normalized.LastIndexOf('/');
    if (lastSlash < 0) return ("", normalized);
    return (normalized[..(lastSlash + 1)], normalized[(lastSlash + 1)..]);
  }

  // ----- payload builders (mirror the existing AcronisRecordReader parsers) -----

  /// <summary>
  /// Builds the uncompressed body of a Listing record (type 103) — one entry per tuple in
  /// <paramref name="entries"/>.
  /// </summary>
  private static byte[] BuildListingPayload(IReadOnlyList<(string Path, string Name, long FileSize, long MetaOffset)> entries) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
    w.Write((uint)entries.Count);
    foreach (var (path, name, fileSize, metaOffset) in entries) {
      WriteCountedUtf16(w, path);
      w.Write(0u); // unknown uint32 (matches reader's skip)
      WriteCountedUtf16(w, name);
      WriteCountedUtf16(w, ""); // shortName — empty
      WriteUInt48(w, 0); w.Write((ushort)0); // time + 16-bit pad
      w.Write(0u);                            // unknown uint32
      WriteUInt48(w, (ulong)fileSize); w.Write((ushort)0);
      WriteUInt48(w, (ulong)fileSize); w.Write((ushort)0);
      WriteUInt48(w, (ulong)metaOffset); w.Write((ushort)0);
      w.Write(new byte[38]); // 38-byte tail of unknown fields
    }
    w.Flush();
    return ms.ToArray();
  }

  /// <summary>
  /// Builds a minimal ItemCommon (id 0x10) attribute stream that the existing
  /// <see cref="AcronisFileMetaBodyDecoder"/> picks up as a real attribute body — surfaces the
  /// file's name through <see cref="AcronisReader.DecodedNamesByEntry"/>.
  /// </summary>
  private static byte[] BuildItemCommonAttributeStream(string name) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);

    // Attribute stream: uint32 attributeCount + N×{uint32 idAndFlags, uint16 size, byte[size] body}.
    w.Write(1u); // exactly one ItemCommon attribute

    // 6-byte attribute header.
    w.Write((uint)AcronisAttributeId.ItemCommon); // idAndFlags (low 23 bits = id, bit 23 = dedup flag = 0)
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var bodySize = checked((ushort)(44 + nameBytes.Length)); // 44-byte fixed header + UTF-16LE name
    w.Write(bodySize);

    // 44-byte fixed header per AcronisItemCommonAttribute.
    w.Write((ushort)name.Length);           // nameLength (UTF-16 code units)
    w.Write((ushort)0);                     // altNameLength = 0
    w.Write(0u);                            // dosAttributes — synthesized image carries no real Win32 attrs
    w.Write(0UL);                           // creationTime — unset
    w.Write(0UL);                           // lastWriteTime
    w.Write(0UL);                           // lastAccessTime
    w.Write(0UL);                           // changeTime
    w.Write(0u);                            // trailer dword

    // UTF-16LE name (no alt name).
    if (nameBytes.Length > 0) w.Write(nameBytes);

    w.Flush();
    return ms.ToArray();
  }

  /// <summary>
  /// Builds the uncompressed body of a RecordIndex record (type 108).
  /// </summary>
  private static byte[] BuildRecordIndexPayload(long totalSize, IReadOnlyList<(long startOffset, long recordOffset, byte[] md5)> handles) {
    using var ms = new MemoryStream();
    // 8-byte magic.
    ms.Write([0x01, 0x02, 0x00, 0x10, 0x01, 0x00, 0x00, 0x00]);
    // uint48 totalSize + 2 padding.
    WriteUInt48Bytes(ms, (ulong)totalSize); ms.WriteByte(0); ms.WriteByte(0);
    // uint32 numHandles.
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)handles.Count);
    ms.Write(u32);
    foreach (var h in handles) {
      WriteUInt48Bytes(ms, (ulong)h.startOffset); ms.WriteByte(0); ms.WriteByte(0);
      WriteUInt48Bytes(ms, (ulong)h.recordOffset); ms.WriteByte(0); ms.WriteByte(0);
      ms.Write(h.md5);
    }
    return ms.ToArray();
  }

  // ----- record writers (mirror the existing reader's framing) -----

  /// <summary>
  /// Writes a raw-deflate record (1-byte type + raw deflate body + 4-byte zero checksum) at the
  /// stream's current position.
  /// </summary>
  private static void WriteRawDeflateRecord(Stream image, AcronisRecordType type, byte[] payload) {
    image.WriteByte((byte)type);
    using (var def = new DeflateStream(image, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload, 0, payload.Length);
    // 4-byte trailing checksum slot — reader does not validate; mirror the test fixtures' zero fill.
    Span<byte> sum = stackalloc byte[4];
    image.Write(sum);
  }

  /// <summary>
  /// Writes a zlib-wrapped record (1-byte type + 2-byte zlib header <c>0x78 0x9C</c> + raw
  /// deflate body + 4-byte big-endian Adler-32 trailer) at the stream's current position.
  /// </summary>
  private static void WriteZlibRecord(Stream image, AcronisRecordType type, byte[] payload) {
    image.WriteByte((byte)type);
    image.WriteByte(0x78);
    image.WriteByte(0x9C);
    using (var def = new DeflateStream(image, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload, 0, payload.Length);
    Span<byte> adlerBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(adlerBuf, ComputeAdler32(payload));
    image.Write(adlerBuf);
  }

  private static uint ComputeAdler32(byte[] data) {
    const uint Mod = 65521;
    uint a = 1, b = 0;
    foreach (var x in data) {
      a = (a + x) % Mod;
      b = (b + a) % Mod;
    }
    return (b << 16) | a;
  }

  private static void WriteCountedUtf16(BinaryWriter w, string s) {
    w.Write((uint)s.Length);
    if (s.Length > 0) w.Write(Encoding.Unicode.GetBytes(s));
  }

  private static void WriteUInt48(BinaryWriter w, ulong v) {
    for (var i = 0; i < 6; i++) w.Write((byte)((v >> (i * 8)) & 0xFF));
  }

  private static void WriteUInt48Bytes(MemoryStream s, ulong v) {
    for (var i = 0; i < 6; i++) s.WriteByte((byte)((v >> (i * 8)) & 0xFF));
  }
}
