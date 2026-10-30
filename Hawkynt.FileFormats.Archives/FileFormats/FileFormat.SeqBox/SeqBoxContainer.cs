using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.SeqBox;

/// <summary>Metadata carried by a SeqBox block 0 (all fields optional).</summary>
/// <param name="FileName">FNM: the encoded file's name (UTF-8).</param>
/// <param name="SbxName">SNM: the container's own file name (UTF-8).</param>
/// <param name="FileSize">FSZ: the encoded file's size; trims the last block's 0x1A padding.</param>
/// <param name="FileTime">FDT: the encoded file's modification time, seconds since the Unix epoch.</param>
/// <param name="SbxTime">SDT: when the container was made, seconds since the Unix epoch.</param>
/// <param name="Hash">HSH: a multihash; <c>0x12 0x20</c> + 32 bytes is SHA-256.</param>
public sealed record SeqBoxMetadata(
  string? FileName = null,
  string? SbxName = null,
  long? FileSize = null,
  long? FileTime = null,
  long? SbxTime = null,
  byte[]? Hash = null);

/// <summary>
/// SeqBox (SBX) single-file container: the file is cut into fixed-size blocks, each with a
/// 16-byte header (<c>"SBx"</c>, version, CRC-16, 6-byte file UID, 32-bit sequence number, all
/// big-endian) so the file can be recovered from a damaged or unindexed disk by scanning for
/// blocks. Block 0 optionally carries TLV metadata; data blocks start at sequence 1, and the last
/// one is padded with 0x1A. The CRC is CRC-16/CCITT (polynomial 0x1021, MSB first) seeded with
/// the version number and taken over everything after the CRC field.
/// </summary>
/// <remarks>
/// Layout from the format author's description (github.com/MarcoPon/SeqBox, README); the
/// reference sbxenc/sbxdec were used only as a black-box oracle.
/// </remarks>
public static class SeqBoxContainer {
  /// <summary>Size of every block header.</summary>
  public const int HeaderSize = 16;

  /// <summary>The three signature bytes every block starts with.</summary>
  public static ReadOnlySpan<byte> Signature => "SBx"u8;

  /// <summary>Multihash prefix for SHA-256 (code 0x12, length 32).</summary>
  private static ReadOnlySpan<byte> Sha256Multihash => [0x12, 0x20];

  /// <summary>Block size of a SeqBox version, or 0 for an unknown version.</summary>
  public static int BlockSize(int version) => version switch {
    1 => 512,
    2 => 128,
    3 => 4096,
    4 => 488, // Amiga OFS data-block payload
    _ => 0,
  };

  /// <summary>Whether the stream starts with a valid SeqBox block (signature, known version, CRC).</summary>
  public static bool IsSeqBox(ReadOnlySpan<byte> head) {
    if (head.Length < HeaderSize || !head[..3].SequenceEqual(Signature))
      return false;
    var size = BlockSize(head[3]);
    return size > 0 && head.Length >= size && CrcOk(head[..size]);
  }

  private static ushort Crc(ReadOnlySpan<byte> blockAfterCrc, int version) {
    var crc = new Crc16Ccitt((ushort)version);
    crc.Update(blockAfterCrc);
    return (ushort)crc.Value;
  }

  private static bool CrcOk(ReadOnlySpan<byte> block)
    => BinaryPrimitives.ReadUInt16BigEndian(block[4..]) == Crc(block[6..], block[3]);

  /// <summary>A parsed container: version, UID, metadata (if block 0 is present) and the payload.</summary>
  public sealed record Parsed(int Version, byte[] Uid, SeqBoxMetadata? Metadata, int DataBlocks, long PayloadLength, byte[] Data);

  /// <summary>
  /// Parses and verifies a whole container. Every block must carry the signature, the first
  /// block's version and UID and a valid CRC; data blocks must be numbered 1, 2, 3, … in file
  /// order. With FSZ the payload is trimmed to it; with an HSH SHA-256 the payload is verified.
  /// Without FSZ the trailing 0x1A padding cannot be told from data and is kept, as sbxdec does.
  /// </summary>
  /// <exception cref="InvalidDataException">Any block is damaged, missing or out of order, or the hash does not match.</exception>
  public static Parsed Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    Span<byte> head = stackalloc byte[4];
    if (stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4 || !head[..3].SequenceEqual(Signature))
      throw new InvalidDataException("Not a SeqBox container (missing 'SBx' signature).");
    int version = head[3], blockSize = BlockSize(version);
    if (blockSize == 0)
      throw new InvalidDataException($"SeqBox block version {version} is not supported.");

    var block = new byte[blockSize];
    head.CopyTo(block);
    var filled = 4;
    byte[]? uid = null;
    SeqBoxMetadata? metadata = null;
    using var payload = new MemoryStream();
    long expectedSeq = 1;
    var blockIndex = 0;
    var dataBlocks = 0;
    while (true) {
      var got = stream.ReadAtLeast(block.AsSpan(filled), blockSize - filled, throwOnEndOfStream: false);
      if (got + filled == 0) break;
      if (got + filled < blockSize) {
        if (blockIndex == 0)
          throw new InvalidDataException("SeqBox container is shorter than one block.");
        break; // sbxdec ignores a trailing partial block
      }
      filled = 0;

      if (!block.AsSpan(0, 3).SequenceEqual(Signature) || block[3] != version)
        throw new InvalidDataException($"SeqBox block {blockIndex} has no valid signature.");
      if (!CrcOk(block))
        throw new InvalidDataException($"SeqBox block {blockIndex} fails its CRC.");
      var blockUid = block.AsSpan(6, 6).ToArray();
      if (uid is null) uid = blockUid;
      else if (!blockUid.AsSpan().SequenceEqual(uid))
        throw new InvalidDataException($"SeqBox block {blockIndex} belongs to another file (UID mismatch).");

      var seq = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(12));
      if (seq == 0) {
        if (blockIndex != 0)
          throw new InvalidDataException($"SeqBox metadata block found at position {blockIndex}.");
        metadata = ParseMetadata(block.AsSpan(HeaderSize));
      } else {
        if (seq != expectedSeq)
          throw new InvalidDataException($"SeqBox block {expectedSeq} is missing or out of order (found {seq}).");
        ++expectedSeq;
        ++dataBlocks;
        payload.Write(block, HeaderSize, blockSize - HeaderSize);
      }
      ++blockIndex;
    }

    var data = payload.ToArray();
    if (metadata?.FileSize is { } size) {
      // The size has to land in the last data block: anything else means blocks are missing
      // from the end or the metadata belongs to a different file.
      var dataSize = blockSize - HeaderSize;
      var fits = dataBlocks == 0 ? size == 0 : size > (long)(dataBlocks - 1) * dataSize && size <= data.Length;
      if (!fits)
        throw new InvalidDataException($"SeqBox file size {size} does not fit the {dataBlocks} data blocks present.");
      data = data[..(int)size];
    }
    if (metadata?.Hash is { Length: 34 } hash && hash.AsSpan(0, 2).SequenceEqual(Sha256Multihash)
        && !SHA256.HashData(data).AsSpan().SequenceEqual(hash.AsSpan(2)))
      throw new InvalidDataException("SeqBox SHA-256 does not match the decoded data.");

    return new(version, uid!, metadata, dataBlocks, data.Length, data);
  }

  private static SeqBoxMetadata ParseMetadata(ReadOnlySpan<byte> area) {
    string? fnm = null, snm = null;
    long? fsz = null, fdt = null, sdt = null;
    byte[]? hsh = null;
    var p = 0;
    while (p + 4 <= area.Length) {
      var id = area.Slice(p, 3);
      if (id[0] == 0x1A && id[1] == 0x1A && id[2] == 0x1A) break;
      int len = area[p + 3];
      if (p + 4 + len > area.Length)
        throw new InvalidDataException("SeqBox metadata entry runs past the block.");
      var value = area.Slice(p + 4, len);
      switch (Encoding.ASCII.GetString(id)) {
        case "FNM": fnm = Encoding.UTF8.GetString(value); break;
        case "SNM": snm = Encoding.UTF8.GetString(value); break;
        case "FSZ": fsz = ReadBigEndian(value); break;
        case "FDT": fdt = ReadBigEndian(value); break;
        case "SDT": sdt = ReadBigEndian(value); break;
        case "HSH": hsh = value.ToArray(); break;
        // PID and unknown ids are skipped: the TLV length makes that safe.
      }
      p += 4 + len;
    }
    return new(fnm, snm, fsz, fdt, sdt, hsh);
  }

  private static long ReadBigEndian(ReadOnlySpan<byte> value) {
    if (value.Length is 0 or > 8)
      throw new InvalidDataException("SeqBox numeric metadata must be 1-8 bytes.");
    long v = 0;
    foreach (var b in value) v = (v << 8) | b;
    return v;
  }

  /// <summary>
  /// Writes a container the way sbxenc does: block 0 with FNM, SNM, FSZ, FDT, SDT and a SHA-256
  /// HSH (unless <paramref name="withMetadata"/> is false), then the data in sequence-numbered
  /// blocks, the last padded with 0x1A. An empty file is the metadata block alone.
  /// </summary>
  public static void Write(Stream output, ReadOnlySpan<byte> data, string fileName, int version = 1,
      byte[]? uid = null, DateTimeOffset? fileTime = null, string? sbxName = null,
      DateTimeOffset? sbxTime = null, bool withMetadata = true) {
    ArgumentNullException.ThrowIfNull(output);
    var blockSize = BlockSize(version);
    if (blockSize == 0)
      throw new ArgumentOutOfRangeException(nameof(version), version, "SeqBox versions are 1 (512), 2 (128), 3 (4096) and 4 (488).");
    if (uid is { Length: not 6 })
      throw new ArgumentException("A SeqBox UID is 6 bytes.", nameof(uid));
    uid ??= RandomNumberGenerator.GetBytes(6);
    var dataSize = blockSize - HeaderSize;
    var block = new byte[blockSize];

    if (withMetadata) {
      var meta = new MemoryStream();
      void Tlv(string id, ReadOnlySpan<byte> value) {
        if (value.Length > 255)
          throw new NotSupportedException($"SeqBox metadata '{id}' is longer than 255 bytes.");
        meta.Write(Encoding.ASCII.GetBytes(id));
        meta.WriteByte((byte)value.Length);
        meta.Write(value);
      }
      static byte[] Be(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }
      Tlv("FNM", Encoding.UTF8.GetBytes(fileName));
      if (sbxName is not null) Tlv("SNM", Encoding.UTF8.GetBytes(sbxName));
      Tlv("FSZ", Be(data.Length));
      Tlv("FDT", Be((fileTime ?? DateTimeOffset.UnixEpoch).ToUnixTimeSeconds()));
      Tlv("SDT", Be((sbxTime ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()));
      Tlv("HSH", [.. Sha256Multihash, .. SHA256.HashData(data)]);
      if (meta.Length > dataSize)
        throw new NotSupportedException($"SeqBox metadata ({meta.Length} bytes) does not fit a version-{version} block.");
      EmitBlock(output, block, version, uid, 0, meta.ToArray());
    }

    uint seq = 0;
    for (var offset = 0; offset < data.Length; offset += dataSize)
      EmitBlock(output, block, version, uid, ++seq, data.Slice(offset, Math.Min(dataSize, data.Length - offset)));
  }

  private static void EmitBlock(Stream output, byte[] block, int version, byte[] uid, uint seq, ReadOnlySpan<byte> content) {
    block.AsSpan().Fill(0x1A);
    Signature.CopyTo(block);
    block[3] = (byte)version;
    uid.CopyTo(block, 6);
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(12), seq);
    content.CopyTo(block.AsSpan(HeaderSize));
    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(4), Crc(block.AsSpan(6), version));
    output.Write(block);
  }
}
