#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ghost;

/// <summary>
/// True in-place mutation for Ghost 3.0+ record-stream images. Unlike
/// <see cref="GhostModifier"/> (which extracts + rebuilds), this modifier
/// preserves the existing record bytes at their original file offsets and
/// only ever appends. The Ghost record framing (FE EF header + 0x012F18D8
/// record stream + per-partition 32 KB block runs) was designed so the
/// END-of-image record sits at the tail of the file; an append pattern that
/// overwrites the END record and re-emits a fresh one at the new tail
/// keeps every byte before the original END record byte-identical.
/// </summary>
/// <remarks>
/// <para>
/// <b>Add.</b> The caller's input bytes are written as new partition records
/// inserted at the original END-record offset; a fresh END record is then
/// emitted at the new EOF. The image header, every existing partition
/// descriptor / FEEF / compressed-block span, and the synthesised
/// <c>track0.bin</c> body all stay byte-identical at their original offsets.
/// </para>
/// <para>
/// <b>Replace by name.</b> Implemented as Add + a REPLACE annotation. The
/// annotation is a normal 0x012F18D8-magic record carrying a sentinel-prefixed
/// body — the modified <see cref="GhostReader"/> applies it during entry
/// materialisation so the named entry's payload reflects the latest write,
/// while the original partition bytes stay intact on disk.
/// </para>
/// <para>
/// <b>Remove by name.</b> A REMOVE annotation tombstone is appended; the
/// modified reader treats the tombstone as "this entry no longer exists" but
/// the underlying partition bytes are still present at their original offsets.
/// This is by design — the operation is byte-preserving on the existing
/// payload bytes; callers needing forensic deletion go through
/// <see cref="GhostModifier"/>.
/// </para>
/// <para>
/// <b>Cipher state.</b> Each partition record opens with a fresh
/// <see cref="GhostCrc16Cipher"/> seeded from the password (see
/// <see cref="GhostWriter.WritePartition"/>). The cipher is therefore
/// per-record, not per-image, so no end-of-stream cipher snapshot is
/// needed — appending a new partition starts a brand-new cipher chain
/// the same way the original partitions did.
/// </para>
/// <para>
/// <b>Out of scope.</b> Pre-3.0 (Ghost 1.x / 2.x DOS-era) dump files have no
/// record stream so the in-place append semantics simply don't apply —
/// callers attempting to mutate one get a <see cref="NotSupportedException"/>
/// pointing them at the rebuild-based <see cref="GhostModifier"/>.
/// </para>
/// </remarks>
public static class GhostInPlaceModifier {

  /// <summary>
  /// Appends the supplied inputs to the archive as new partition records.
  /// The existing record bytes [0, original END-record offset) remain
  /// byte-identical after the call.
  /// </summary>
  /// <param name="archive">Read/write/seek stream holding a Ghost 3.0+ image.</param>
  /// <param name="inputs">Entries to append. <c>track0.bin</c> entries are written as Track-0 records.</param>
  /// <param name="password">Required when the source image is encrypted. Ignored otherwise.</param>
  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs, string? password = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var ctx = OpenForMutation(archive, password);

    // Overwrite the existing End record with our new records, then re-emit a
    // fresh End record. Everything BEFORE EndOffset stays byte-identical.
    archive.Position = ctx.EndOffset;
    archive.SetLength(ctx.EndOffset);

    var effectivePassword = ctx.IsEncrypted ? password : null;
    foreach (var (name, data) in FlatFiles(inputs)) {
      if (name.Equals("track0.bin", StringComparison.OrdinalIgnoreCase)) {
        WriteTrack0Record(archive, data, sectors: 63);
      } else {
        WritePartitionRecord(archive, data, ctx.Compression, effectivePassword, ctx.HeaderId);
      }
    }

    WriteEndRecord(archive);
  }

  /// <summary>
  /// Appends a REPLACE annotation with the new payload, plus (when the
  /// payload looks like a brand-new partition / track0) the underlying data
  /// record. The existing record bytes stay byte-identical at their
  /// original offsets.
  /// </summary>
  /// <param name="archive">Read/write/seek stream holding a Ghost 3.0+ image.</param>
  /// <param name="entryName">Logical name of the entry to replace (e.g. <c>partition1.bin</c>).</param>
  /// <param name="newData">New payload bytes.</param>
  /// <param name="password">Required when the source image is encrypted. Ignored otherwise.</param>
  public static void Replace(Stream archive, string entryName, byte[] newData, string? password = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    ArgumentNullException.ThrowIfNull(newData);

    var ctx = OpenForMutation(archive, password);

    archive.Position = ctx.EndOffset;
    archive.SetLength(ctx.EndOffset);

    var body = BuildAnnotationBody(GhostConstants.AnnotationOpReplace, entryName, newData);
    WriteAnnotationRecord(archive, body);
    WriteEndRecord(archive);
  }

  /// <summary>
  /// Appends a REMOVE annotation tombstone. The existing record bytes stay
  /// byte-identical at their original offsets; the modified reader treats
  /// the tombstoned name as no longer present.
  /// </summary>
  /// <param name="archive">Read/write/seek stream holding a Ghost 3.0+ image.</param>
  /// <param name="entryName">Logical name of the entry to tombstone.</param>
  /// <param name="password">Required when the source image is encrypted. Ignored otherwise.</param>
  public static void Remove(Stream archive, string entryName, string? password = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);

    var ctx = OpenForMutation(archive, password);

    archive.Position = ctx.EndOffset;
    archive.SetLength(ctx.EndOffset);

    var body = BuildAnnotationBody(GhostConstants.AnnotationOpRemove, entryName, []);
    WriteAnnotationRecord(archive, body);
    WriteEndRecord(archive);
  }

  // ── Internal helpers ──────────────────────────────────────────────

  private sealed class MutationContext {
    public byte Compression { get; init; }
    public bool IsEncrypted { get; init; }
    public long EndOffset { get; init; }
    public uint HeaderId { get; init; }
  }

  private static MutationContext OpenForMutation(Stream archive, string? password) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException(
        "Ghost in-place modify: archive stream must be readable, writable and seekable.",
        nameof(archive));

    archive.Position = 0;
    var r = new GhostReader(archive, password: password);

    if (r.GenerationHint != GhostGenerationHint.Modern11Plus)
      throw new NotSupportedException(
        "Ghost in-place modify: only Ghost 3.0+ (modern 0x012F18D8 record stream) images " +
        "support append-based mutation. Pre-3.0 (Ghost 1.x / 2.x DOS-era) dump files use a " +
        "different framing with no record stream — use the rebuild-based GhostModifier or " +
        "Symantec Ghost Explorer for those images.");

    if (r.IsEncrypted && string.IsNullOrEmpty(password))
      throw new InvalidDataException(
        "Ghost in-place modify: image is encrypted; supply the password parameter.");

    if (r.EndRecordOffset < 0)
      throw new InvalidDataException(
        "Ghost in-place modify: image has no end-of-image record; refusing to mutate a " +
        "truncated container.");

    return new MutationContext {
      Compression = r.HeaderCompression,
      IsEncrypted = r.IsEncrypted,
      EndOffset = r.EndRecordOffset,
      HeaderId = r.HeaderId
    };
  }

  // ── Record writers (inline so we don't accidentally re-emit the file header) ──

  private static void WriteTrack0Record(Stream archive, byte[] track0Data, byte sectors) {
    var body = new byte[6 + track0Data.Length];
    body[0] = 0x06;
    body[1] = sectors;
    track0Data.CopyTo(body.AsSpan(6));
    WriteRecord(archive, GhostConstants.RecordTypeTrack0, body);
  }

  private static void WritePartitionRecord(Stream archive, byte[] partitionData,
      byte compression, string? password, uint headerId) {
    Span<byte> descBody = stackalloc byte[20];
    descBody.Clear();
    WriteRecord(archive, GhostConstants.RecordTypePartition, descBody.ToArray());

    Span<byte> feef = stackalloc byte[GhostConstants.HeaderSize];
    feef.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(feef[..2], GhostConstants.FileMagic);
    feef[2] = GhostConstants.PartitionHeaderSubType;
    feef[3] = compression;
    BinaryPrimitives.WriteUInt32LittleEndian(feef.Slice(4, 4), headerId);
    archive.Write(feef);

    var cipher = string.IsNullOrEmpty(password) ? null : new GhostCrc16Cipher(password);

    var pos = 0;
    while (pos < partitionData.Length) {
      var chunk = Math.Min(GhostConstants.BlockSize, partitionData.Length - pos);
      WriteBlock(archive, partitionData.AsSpan(pos, chunk), compression, cipher);
      pos += chunk;
    }
  }

  private static void WriteBlock(Stream archive, ReadOnlySpan<byte> data,
      byte compression, GhostCrc16Cipher? cipher) {
    var blockData = compression switch {
      GhostConstants.CompressionNone => data.ToArray(),
      GhostConstants.CompressionFast => GhostFastLz.Compress(data),
      GhostConstants.CompressionHigh3 or GhostConstants.CompressionHigh4 or
        GhostConstants.CompressionHigh5 or GhostConstants.CompressionHigh6 or
        GhostConstants.CompressionHigh7 or GhostConstants.CompressionHigh8 or
        GhostConstants.CompressionHigh9 => GhostZlib.Compress(data, compression),
      _ => throw new InvalidOperationException(
        $"Ghost in-place modify: unsupported compression byte {compression}.")
    };

    if (cipher != null) cipher.Encrypt(blockData);

    var storedLen = blockData.Length + 2;
    if (storedLen > 0xFFFF)
      throw new InvalidDataException(
        $"Ghost in-place modify: block too large for stored_len ({blockData.Length}).");

    Span<byte> lenBuf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, (ushort)storedLen);
    archive.Write(lenBuf);
    archive.Write(blockData);
  }

  private static void WriteRecord(Stream archive, ushort recType, byte[] body) {
    if (body.Length > 0xFFFF)
      throw new InvalidDataException(
        $"Ghost in-place modify: record body too large ({body.Length}).");
    Span<byte> hdr = stackalloc byte[GhostConstants.RecordHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[..4], recType);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), GhostConstants.RecordMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(8, 2), (ushort)body.Length);
    archive.Write(hdr);
    if (body.Length > 0) archive.Write(body);
  }

  private static byte[] BuildAnnotationBody(byte op, string entryName, byte[] payload) {
    var nameBytes = Encoding.UTF8.GetBytes(entryName);
    if (nameBytes.Length > 0xFFFF)
      throw new InvalidDataException(
        $"Ghost in-place modify: entry name '{entryName}' is too long ({nameBytes.Length} bytes).");

    var body = new byte[4 + 1 + 2 + nameBytes.Length + 4 + payload.Length];
    var s = body.AsSpan();
    BinaryPrimitives.WriteUInt32LittleEndian(s[..4], GhostConstants.AnnotationMagic);
    s[4] = op;
    BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(5, 2), (ushort)nameBytes.Length);
    nameBytes.CopyTo(s.Slice(7, nameBytes.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(7 + nameBytes.Length, 4), (uint)payload.Length);
    payload.CopyTo(s.Slice(7 + nameBytes.Length + 4, payload.Length));
    return body;
  }

  private static void WriteAnnotationRecord(Stream archive, byte[] body) {
    if (body.Length > 0xFFFF)
      throw new InvalidDataException(
        $"Ghost in-place modify: annotation body too large ({body.Length} bytes — the 16-bit " +
        "body-length field on the record header would overflow). Split the operation into " +
        "multiple records.");

    Span<byte> hdr = stackalloc byte[GhostConstants.RecordHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[..4], GhostConstants.RecordTypeAnnotation);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), GhostConstants.RecordMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(8, 2), (ushort)body.Length);
    archive.Write(hdr);
    archive.Write(body);
  }

  private static void WriteEndRecord(Stream archive) {
    Span<byte> body = stackalloc byte[24];
    body.Clear();
    Span<byte> hdr = stackalloc byte[GhostConstants.RecordHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[..4], GhostConstants.RecordTypeEnd);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), GhostConstants.RecordMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(8, 2), (ushort)body.Length);
    archive.Write(hdr);
    archive.Write(body);
  }
}
