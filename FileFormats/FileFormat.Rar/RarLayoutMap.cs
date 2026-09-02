#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Rar;

/// <summary>
/// Walks RAR5 or RAR4 block headers and emits the byte-level layout of the archive
/// as <see cref="DefragBlockInfo"/> tiles: signature, main header, file headers
/// (MetadataReserved), compressed data payloads (Used), service headers, and
/// end-of-archive marker.
/// </summary>
public static class RarLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;

    if (archive.Length < 7)
      yield break;

    // Detect RAR version from signature
    var sigBuf = new byte[8];
    var sigRead = archive.Read(sigBuf, 0, 8);
    if (sigRead < 7) yield break;
    archive.Position = 0;

    bool isRar5;
    int sigLen;
    if (sigRead >= 8 && sigBuf.AsSpan(0, 8).SequenceEqual(RarConstants.Rar5Signature)) {
      isRar5 = true;
      sigLen = 8;
    } else if (sigBuf.AsSpan(0, 7).SequenceEqual(RarConstants.Rar4Signature)) {
      isRar5 = false;
      sigLen = 7;
    } else {
      yield break;
    }

    // Signature tile
    yield return new DefragBlockInfo(0, sigLen, DefragBlockKind.MetadataReserved,
      FileName: isRar5 ? "RAR5 Signature" : "RAR4 Signature");

    archive.Position = sigLen;

    if (isRar5) {
      foreach (var tile in EnumerateRar5Blocks(archive))
        yield return tile;
    } else {
      foreach (var tile in EnumerateRar4Blocks(archive))
        yield return tile;
    }
  }

  private static IEnumerable<DefragBlockInfo> EnumerateRar5Blocks(Stream archive) {
    while (archive.Position < archive.Length) {
      var headerStart = archive.Position;

      // Read CRC (4 bytes)
      var crcBuf = new byte[4];
      if (archive.Read(crcBuf, 0, 4) < 4) break;

      // Read header size vint
      var sizeStartPos = archive.Position;
      int headerSize;
      try { headerSize = (int)RarVint.Read(archive, out _); }
      catch { break; }

      // Read header body
      if (headerSize <= 0 || archive.Position + headerSize > archive.Length) break;
      var body = new byte[headerSize];
      var totalRead = 0;
      while (totalRead < headerSize) {
        var n = archive.Read(body, totalRead, headerSize - totalRead);
        if (n == 0) break;
        totalRead += n;
      }
      if (totalRead < headerSize) break;

      var headerEnd = archive.Position;
      var headerLen = headerEnd - headerStart;

      // Parse type, flags, data size from body
      ParseRar5Header(body, out var headerType, out var headerFlags, out var extraSize, out var dataSize, out var offset);

      switch (headerType) {
        case RarConstants.HeaderTypeMain:
          yield return new DefragBlockInfo(headerStart, headerLen,
            DefragBlockKind.MetadataReserved, FileName: "Main Archive Header");
          break;

        case RarConstants.HeaderTypeFile: {
          // Emit header as metadata
          yield return new DefragBlockInfo(headerStart, headerLen,
            DefragBlockKind.MetadataReserved, FileName: $"File Header");

          // Emit data as Used with filename from header
          if (dataSize > 0) {
            var fileName = TryReadRar5FileName(body, offset);
            var method = TryReadRar5Method(body, offset);
            yield return new DefragBlockInfo(headerEnd, dataSize,
              DefragBlockKind.Used, FileName: fileName ?? "data",
              Classification: ClassifyRar5Method(method));
          }
          break;
        }

        case RarConstants.HeaderTypeService:
          yield return new DefragBlockInfo(headerStart, headerLen,
            DefragBlockKind.MetadataReserved, FileName: "Service Header");
          if (dataSize > 0) {
            yield return new DefragBlockInfo(headerEnd, dataSize,
              DefragBlockKind.Used, FileName: "Service Data");
          }
          break;

        case RarConstants.HeaderTypeEncryption:
          yield return new DefragBlockInfo(headerStart, headerLen,
            DefragBlockKind.MetadataReserved, FileName: "Encryption Header");
          break;

        case RarConstants.HeaderTypeEndArchive:
          yield return new DefragBlockInfo(headerStart, headerLen,
            DefragBlockKind.MetadataReserved, FileName: "End-of-archive");
          yield break;

        default:
          yield return new DefragBlockInfo(headerStart, headerLen,
            DefragBlockKind.MetadataReserved, FileName: $"Header (type {headerType})");
          break;
      }

      // Skip data area
      if (dataSize > 0)
        archive.Position = headerEnd + dataSize;
    }
  }

  private static IEnumerable<DefragBlockInfo> EnumerateRar4Blocks(Stream archive) {
    while (archive.Position < archive.Length) {
      var headerStart = archive.Position;

      // RAR4 block header: HEAD_CRC(2) + HEAD_TYPE(1) + HEAD_FLAGS(2) + HEAD_SIZE(2)
      var headerBuf = new byte[7];
      if (!TryReadExact(archive, headerBuf, 7)) break;

      var headType = headerBuf[2];
      var headFlags = (ushort)(headerBuf[3] | (headerBuf[4] << 8));
      var headSize = (ushort)(headerBuf[5] | (headerBuf[6] << 8));

      if (headType == RarConstants.Rar4TypeEnd) {
        yield return new DefragBlockInfo(headerStart, headSize,
          DefragBlockKind.MetadataReserved, FileName: "End-of-archive");
        yield break;
      }

      if (headType == RarConstants.Rar4TypeFile) {
        var remaining = headSize - 7;
        if (remaining < 25) {
          archive.Position = headerStart + headSize;
          continue;
        }

        var fileBuf = new byte[remaining];
        if (!TryReadExact(archive, fileBuf, remaining)) break;

        var packSize = (long)((uint)fileBuf[0] | ((uint)fileBuf[1] << 8) | ((uint)fileBuf[2] << 16) | ((uint)fileBuf[3] << 24));
        var method = fileBuf[18];
        var nameSize = (ushort)(fileBuf[19] | (fileBuf[20] << 8));

        // Large file support
        if ((headFlags & RarConstants.Rar4FlagLargeFile) != 0 && remaining >= 33) {
          var highPack = (long)((uint)fileBuf[25] | ((uint)fileBuf[26] << 8) | ((uint)fileBuf[27] << 16) | ((uint)fileBuf[28] << 24));
          packSize |= highPack << 32;
        }

        // Read filename
        var nameOffset = 25;
        if ((headFlags & RarConstants.Rar4FlagLargeFile) != 0) nameOffset += 8;

        var name = "";
        if (nameSize > 0 && nameOffset + nameSize <= remaining)
          name = System.Text.Encoding.ASCII.GetString(fileBuf, nameOffset, Math.Min(nameSize, remaining - nameOffset));

        // Emit file header tile
        yield return new DefragBlockInfo(headerStart, headSize,
          DefragBlockKind.MetadataReserved, FileName: $"File Header: {name}");

        // Emit compressed data tile
        var dataStart = headerStart + headSize;
        if (packSize > 0) {
          yield return new DefragBlockInfo(dataStart, packSize,
            DefragBlockKind.Used, FileName: name,
            Classification: ClassifyRar4Method(method));
          archive.Position = dataStart + packSize;
        }
      } else {
        // Non-file header
        var label = headType switch {
          RarConstants.Rar4TypeMarker => "Marker Header",
          RarConstants.Rar4TypeMain => "Main Archive Header",
          RarConstants.Rar4TypeComment => "Comment Header",
          RarConstants.Rar4TypeSubBlock => "Sub-block Header",
          _ => $"Header (type 0x{headType:X2})",
        };

        var remaining = headSize - 7;
        long addSize = 0;
        if ((headFlags & RarConstants.Rar4FlagAddSize) != 0 && remaining >= 4) {
          var addBuf = new byte[4];
          if (!TryReadExact(archive, addBuf, 4)) break;
          addSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(addBuf);
          remaining -= 4;
        }

        yield return new DefragBlockInfo(headerStart, headSize + addSize,
          DefragBlockKind.MetadataReserved, FileName: label);

        archive.Position = headerStart + headSize + addSize;
      }
    }
  }

  private static string? TryReadRar5FileName(byte[] body, int dataFieldsOffset) {
    try {
      var offset = dataFieldsOffset;
      ReadOnlySpan<byte> span = body;
      // File header fields after common: fileFlags(vint), unpSize(vint), attributes(vint),
      // mtime(uint32, if flag), dataCrc(uint32, if flag), comprInfo(vint), hostOs(vint), nameLen(vint), name
      var fileFlags = (int)RarVint.Read(span[offset..], out var c); offset += c;
      _ = RarVint.Read(span[offset..], out c); offset += c; // unpackedSize
      _ = RarVint.Read(span[offset..], out c); offset += c; // attributes
      if ((fileFlags & RarConstants.FileFlagTimeMtime) != 0) offset += 4; // mtime
      if ((fileFlags & RarConstants.FileFlagCrc32) != 0) offset += 4; // dataCrc
      _ = RarVint.Read(span[offset..], out c); offset += c; // compressionInfo
      _ = RarVint.Read(span[offset..], out c); offset += c; // hostOs
      var nameLen = (int)RarVint.Read(span[offset..], out c); offset += c;
      if (nameLen > 0 && offset + nameLen <= body.Length)
        return System.Text.Encoding.UTF8.GetString(body, offset, nameLen);
    } catch { /* best-effort */ }
    return null;
  }

  private static int TryReadRar5Method(byte[] body, int dataFieldsOffset) {
    try {
      var offset = dataFieldsOffset;
      ReadOnlySpan<byte> span = body;
      var fileFlags = (int)RarVint.Read(span[offset..], out var c); offset += c;
      _ = RarVint.Read(span[offset..], out c); offset += c;
      _ = RarVint.Read(span[offset..], out c); offset += c;
      if ((fileFlags & RarConstants.FileFlagTimeMtime) != 0) offset += 4;
      if ((fileFlags & RarConstants.FileFlagCrc32) != 0) offset += 4;
      var comprInfo = (int)RarVint.Read(span[offset..], out c);
      return comprInfo & 0x07; // low 3 bits = method 0-5
    } catch { return -1; }
  }

  private static DefragBlockClass ClassifyRar5Method(int method) => method switch {
    0 => DefragBlockClass.Frozen,  // Store
    1 or 2 => DefragBlockClass.Normal,
    3 or 4 => DefragBlockClass.Cold,
    5 => DefragBlockClass.Hot,     // Best
    _ => DefragBlockClass.Normal,
  };

  private static DefragBlockClass ClassifyRar4Method(int method) => method switch {
    RarConstants.Rar4MethodStore => DefragBlockClass.Frozen,
    RarConstants.Rar4MethodFastest or RarConstants.Rar4MethodFast => DefragBlockClass.Normal,
    RarConstants.Rar4MethodNormal => DefragBlockClass.Cold,
    RarConstants.Rar4MethodGood or RarConstants.Rar4MethodBest => DefragBlockClass.Hot,
    _ => DefragBlockClass.Normal,
  };

  private static void ParseRar5Header(byte[] body, out int headerType, out int headerFlags,
      out long extraSize, out long dataSize, out int offset) {
    ReadOnlySpan<byte> span = body;
    offset = 0;
    headerType = (int)RarVint.Read(span[offset..], out var consumed);
    offset += consumed;
    headerFlags = (int)RarVint.Read(span[offset..], out consumed);
    offset += consumed;
    extraSize = 0;
    dataSize = 0;
    if ((headerFlags & RarConstants.HeaderFlagExtraArea) != 0) {
      extraSize = (long)RarVint.Read(span[offset..], out consumed);
      offset += consumed;
    }
    if ((headerFlags & RarConstants.HeaderFlagDataArea) != 0) {
      dataSize = (long)RarVint.Read(span[offset..], out consumed);
      offset += consumed;
    }
  }

  private static bool TryReadExact(Stream stream, byte[] buffer, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var n = stream.Read(buffer, totalRead, count - totalRead);
      if (n == 0) return false;
      totalRead += n;
    }
    return true;
  }
}
