using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.Zip;

/// <summary>
/// Rewrites a ZIP archive from its own central directory, record by record, so that every
/// local header, extra field, comment, attribute, flag and timestamp is carried across as
/// stored. Two maintenance operations are built on it: <see cref="Repack"/> copies every
/// entry's data verbatim and drops the bytes no record points at; <see cref="Recompress"/>
/// additionally re-deflates the entries it can re-encode exactly.
/// </summary>
/// <remarks>
/// <para>Field offsets are those of PKWARE's APPNOTE.TXT §4.3.7 (local file header) and
/// §4.3.12 (central directory header). An entry is re-encoded only when the result is
/// smaller and its stored form can be reproduced without guesswork: not encrypted (its
/// encryption header is keyed to the data), Stored or Deflated, sizes not held in a ZIP64
/// extra field, and a CRC-32 that matches the decoded bytes. Everything else is copied
/// exactly as it was.</para>
/// <para>Bytes ahead of the first entry are dropped when they are the zeroed remains of a
/// removed entry. Anything else there — a self-extractor stub, or a file the archive was
/// appended to — is refused rather than relocated.</para>
/// </remarks>
internal static class ZipRawRewriter {
  private const int LocalHeaderFixedLength = 30;

  /// <summary>Copies every entry the directory lists, verbatim, and nothing else.</summary>
  public static void Repack(Stream source, Stream destination) => Rewrite(source, destination, recompress: false);

  /// <summary>As <see cref="Repack"/>, re-deflating at maximum effort every entry that gets smaller.</summary>
  public static int Recompress(Stream source, Stream destination) => Rewrite(source, destination, recompress: true);

  private static int Rewrite(Stream source, Stream destination, bool recompress) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(destination);
    var (records, _, comment) = ZipRawDirectory.Read(source);
    destination.Position = 0;
    destination.SetLength(0);
    CopyPreamble(source, destination, records);

    var reencoded = 0;
    var buffer = new byte[1 << 16];
    foreach (var record in records) {
      source.Position = record.LocalHeaderOffset;
      var header = ZipRawRecord.ReadExactly(source, LocalHeaderFixedLength);
      if (BinaryPrimitives.ReadUInt32LittleEndian(header) != ZipConstants.LocalFileHeaderSignature)
        throw new InvalidDataException($"No local header where the directory places \"{record.Name}\".");
      var localName = ZipRawRecord.ReadExactly(source, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)));
      var localExtra = ZipRawRecord.ReadExactly(source, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)));
      var dataStart = source.Position;
      var compressedSize = record.CompressedSize;

      var replacement = recompress ? TryReencode(source, record, localExtra, dataStart, compressedSize) : null;
      record.LocalHeaderOffset = destination.Position;

      if (replacement is { } data) {
        // Same entry, new payload: method, sizes, CRC and flags change in both headers; the
        // name, extra fields, times, attributes and comment do not.
        var flags = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)) & ~ZipConstants.FlagDataDescriptor);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Math.Max(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)), ZipConstants.VersionNeeded20));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)ZipCompressionMethod.Deflate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), data.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), (uint)data.Bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), (uint)data.UncompressedSize);

        var central = record.Fixed;
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(6), Math.Max(BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(6)), ZipConstants.VersionNeeded20));
        record.Flags = (ushort)(record.Flags & ~ZipConstants.FlagDataDescriptor);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(10), (ushort)ZipCompressionMethod.Deflate);
        BinaryPrimitives.WriteUInt32LittleEndian(central.AsSpan(20), (uint)data.Bytes.Length);

        destination.Write(header);
        destination.Write(localName);
        destination.Write(localExtra);
        destination.Write(data.Bytes);
        ++reencoded;
        continue;
      }

      source.Position = dataStart;
      var length = compressedSize + DescriptorLength(source, record, dataStart + compressedSize);
      destination.Write(header);
      destination.Write(localName);
      destination.Write(localExtra);
      Copy(source, destination, length, buffer);
    }

    ZipRawDirectory.Write(destination, records, [], comment);
    destination.Flush();
    return reencoded;
  }

  /// <summary>
  /// The entry re-deflated, or null when it is not one that can be re-encoded exactly or
  /// the result would not be smaller.
  /// </summary>
  private static (byte[] Bytes, uint Crc, long UncompressedSize)? TryReencode(
      Stream source, ZipRawRecord record, byte[] localExtra, long dataStart, long compressedSize) {
    var central = record.Fixed;
    var method = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(10));
    if ((record.Flags & ZipConstants.FlagEncrypted) != 0) return null;
    if (method is not ((ushort)ZipCompressionMethod.Store or (ushort)ZipCompressionMethod.Deflate)) return null;
    if (record.HasZip64Sizes || HasZip64Field(localExtra)) return null;
    var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(24));
    if (compressedSize > int.MaxValue || uncompressed > int.MaxValue / 2) return null;

    source.Position = dataStart;
    var stored = ZipRawRecord.ReadExactly(source, (int)compressedSize);
    byte[] plain;
    try {
      plain = method == (ushort)ZipCompressionMethod.Store ? stored : DeflateDecompressor.Decompress(stored);
    } catch (InvalidDataException) {
      return null;
    }
    var crc = Crc32.Compute(plain);
    if (plain.LongLength != uncompressed || crc != BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(16)))
      return null;

    var deflated = DeflateCompressor.Compress(plain, DeflateCompressionLevel.Maximum);
    if (deflated.Length >= compressedSize) return null;
    if (!DeflateDecompressor.Decompress(deflated).AsSpan().SequenceEqual(plain)) return null;
    return (deflated, crc, plain.LongLength);
  }

  private static bool HasZip64Field(byte[] extra) {
    for (var pos = 0; pos + 4 <= extra.Length;) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(pos)) == ZipConstants.Zip64ExtraFieldTag) return true;
      pos += 4 + BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(pos + 2));
    }
    return false;
  }

  private static void CopyPreamble(Stream source, Stream destination, IReadOnlyList<ZipRawRecord> records) {
    if (records.Count == 0) return;
    var first = records.Min(static r => r.LocalHeaderOffset);
    if (first <= 0) return;
    source.Position = 0;
    var preamble = ZipRawRecord.ReadExactly(source, checked((int)first));
    if (preamble.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
      throw new NotSupportedException(
        "The archive carries data ahead of its first entry (a self-extractor stub or a file it was appended to); "
        + "rewriting it would have to relocate that data. Refused, nothing was changed.");
  }

  private static long DescriptorLength(Stream source, ZipRawRecord record, long at) {
    if ((record.Flags & ZipConstants.FlagDataDescriptor) == 0) return 0;
    var sizes = record.HasZip64Sizes ? 16 : 8;
    if (at + 4 > source.Length) return 0;
    source.Position = at;
    Span<byte> signature = stackalloc byte[4];
    source.ReadExactly(signature);
    source.Position = at;
    var signed = BinaryPrimitives.ReadUInt32LittleEndian(signature) == ZipConstants.DataDescriptorSignature;
    return (signed ? 4 : 0) + 4 + sizes;
  }

  private static void Copy(Stream source, Stream destination, long count, byte[] buffer) {
    while (count > 0) {
      var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
      if (read <= 0) throw new EndOfStreamException("The archive ends inside an entry's data.");
      destination.Write(buffer, 0, read);
      count -= read;
    }
  }
}
