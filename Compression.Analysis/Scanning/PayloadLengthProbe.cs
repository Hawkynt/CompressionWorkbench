using System.Buffers.Binary;

namespace Compression.Analysis.Scanning;

/// <summary>
/// Per-format length inference for <see cref="PayloadCarver"/>. Given the starting
/// offset of a recognised format inside a larger buffer, returns how many bytes of
/// payload make up that format. Formats without a probe return <c>null</c> and the
/// carver falls back to "bytes until the next scanner hit" or "bytes to end of
/// buffer".
/// </summary>
public static class PayloadLengthProbe {

  /// <summary>
  /// Tries to compute the payload length for <paramref name="formatId"/> starting
  /// at <paramref name="offset"/> in <paramref name="data"/>. Returns <c>null</c>
  /// when the format has no probe implementation, or when the payload appears
  /// truncated.
  /// </summary>
  public static long? TryProbe(ReadOnlySpan<byte> data, long offset, string formatId) {
    if (offset < 0 || offset >= data.Length) return null;

    return formatId switch {
      "Wav" or "Avi" or "Webp" or "Aiff" => ProbeRiff(data, offset),
      "Mp4" or "Mkv" or "Heif" or "Avif" => ProbeIsoBmff(data, offset),
      "Png" => ProbePng(data, offset),
      "Jpeg" or "JpegArchive" or "Mpo" => ProbeJpeg(data, offset),
      "Zip" or "Apk" or "Jar" or "Docx" or "Xlsx" or "Pptx" or "Odt" or "Ods" or "Odp"
        or "Epub" or "Cbz" or "Appx" or "NuPkg" or "Kmz" or "Maff" or "Crx"
        or "Xpi" or "Ipa" or "Ear" or "War" => ProbeZip(data, offset),
      "Gzip" => ProbeGzip(data, offset),
      "Bzip2" => ProbeBzip2(data, offset),
      "Xz" => ProbeXz(data, offset),
      "Tar" => ProbeTar(data, offset),
      "Ogg" => ProbeOgg(data, offset),
      "Flac" => ProbeFlac(data, offset),
      "Gif" => ProbeGif(data, offset),
      "Pdf" => ProbePdf(data, offset),
      "Bmp" => ProbeBmp(data, offset),
      _ => null,
    };
  }

  // GIF ends with 0x3B (trailer). Scan forward.
  private static long? ProbeGif(ReadOnlySpan<byte> data, long offset) {
    for (var i = offset + 6; i < data.Length; ++i) {
      if (data[(int)i] == 0x3B) return i + 1 - offset;
    }
    return null;
  }

  // PDF ends with "%%EOF" followed by optional \r\n. Scan forward for last occurrence.
  private static long? ProbePdf(ReadOnlySpan<byte> data, long offset) {
    ReadOnlySpan<byte> marker = "%%EOF"u8;
    var lastEnd = -1L;
    for (var i = offset; i + marker.Length <= data.Length; ++i) {
      var ok = true;
      for (var j = 0; j < marker.Length; ++j) {
        if (data[(int)(i + j)] != marker[j]) { ok = false; break; }
      }
      if (ok) {
        lastEnd = i + marker.Length;
        // Consume trailing newline(s).
        while (lastEnd < data.Length && (data[(int)lastEnd] == 0x0D || data[(int)lastEnd] == 0x0A))
          lastEnd++;
      }
    }
    return lastEnd > 0 ? lastEnd - offset : null;
  }

  // BMP: uint32 at offset 2 is the total file size in bytes.
  private static long? ProbeBmp(ReadOnlySpan<byte> data, long offset) {
    if (offset + 6 > data.Length) return null;
    var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(offset + 2)..]);
    if (size < 14 || offset + size > data.Length) return null;
    return size;
  }

  // RIFF: "RIFF" + uint32-LE size + "WAVE"/"AVI "/"WEBP"/…; payload length = 8 + size.
  private static long? ProbeRiff(ReadOnlySpan<byte> data, long offset) {
    if (offset + 8 > data.Length) return null;
    var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(offset + 4)..]);
    var total = 8L + size;
    if (offset + total > data.Length) return data.Length - offset;
    return total;
  }

  // ISOBMFF: outermost box size is a uint32 BE at offset 0 (or 1 → 64-bit at +8).
  // A well-formed file is a sequence of boxes; we walk them to find where the top-level
  // sequence ends. Simpler: just trust the first box's size when it's the whole file.
  private static long? ProbeIsoBmff(ReadOnlySpan<byte> data, long offset) {
    var pos = offset;
    long total = 0;
    while (pos + 8 <= data.Length) {
      var sz = (long)BinaryPrimitives.ReadUInt32BigEndian(data[(int)pos..]);
      var type = data.Slice((int)(pos + 4), 4);
      // Stop walking when we hit obviously non-box bytes.
      if (!IsLikelyBoxType(type)) break;
      if (sz == 1) {
        if (pos + 16 > data.Length) break;
        sz = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(int)(pos + 8)..]);
      } else if (sz == 0) {
        sz = data.Length - pos;
      }
      if (sz < 8 || pos + sz > data.Length) break;
      total += sz;
      pos += sz;
    }
    return total > 0 ? total : null;
  }

  private static bool IsLikelyBoxType(ReadOnlySpan<byte> type) {
    for (var i = 0; i < 4; ++i) {
      var b = type[i];
      if (!(b >= 0x20 && b <= 0x7E)) return false;
    }
    return true;
  }

  // PNG: 8-byte magic + chunks, each = 4-byte BE length + 4-byte type + data + 4-byte CRC.
  // End is the chunk whose type == "IEND".
  private static long? ProbePng(ReadOnlySpan<byte> data, long offset) {
    if (offset + 8 > data.Length) return null;
    var pos = offset + 8;
    while (pos + 12 <= data.Length) {
      var chunkLen = BinaryPrimitives.ReadUInt32BigEndian(data[(int)pos..]);
      var type = data.Slice((int)(pos + 4), 4);
      pos += 8 + chunkLen + 4;   // type + data + CRC
      if (pos > data.Length) return data.Length - offset;
      if (type[0] == 'I' && type[1] == 'E' && type[2] == 'N' && type[3] == 'D')
        return pos - offset;
    }
    return data.Length - offset;
  }

  // JPEG: scan forward for FFD9 (EOI). Entropy-coded segments byte-stuff real 0xFF
  // bytes as 0xFF 0x00, so scanning for a literal 0xFF 0xD9 reliably finds the real EOI.
  private static long? ProbeJpeg(ReadOnlySpan<byte> data, long offset) {
    for (var i = offset + 2; i + 1 < data.Length; ++i) {
      if (data[(int)i] == 0xFF && data[(int)(i + 1)] == 0xD9)
        return i + 2 - offset;
    }
    return null;
  }

  // ZIP: scan for End-Of-Central-Directory signature 0x06054B50 (PK\x05\x06) and
  // include its variable-length comment.
  private static long? ProbeZip(ReadOnlySpan<byte> data, long offset) {
    for (var i = offset; i + 22 <= data.Length; ++i) {
      if (data[(int)i] == 'P' && data[(int)(i + 1)] == 'K' &&
          data[(int)(i + 2)] == 0x05 && data[(int)(i + 3)] == 0x06) {
        var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(i + 20)..]);
        var end = i + 22 + commentLen;
        if (end > data.Length) end = data.Length;
        return end - offset;
      }
    }
    return null;
  }

  // GZIP: fallback probe — scan forward for the next gzip header (allows carving one
  // member from a stream of concatenated members). For single-member payloads the
  // caller's buffer-end fallback takes over.
  private static long? ProbeGzip(ReadOnlySpan<byte> data, long offset) {
    for (var i = offset + 2; i + 2 < data.Length; ++i) {
      if (data[(int)i] == 0x1F && data[(int)(i + 1)] == 0x8B && data[(int)(i + 2)] == 0x08)
        return i - offset;
    }
    return null;
  }

  // BZIP2: block ends with 0x17 0x72 0x45 0x38 0x50 0x90 (π/2 signature) + 4-byte CRC.
  // Uncompressed position-wise, so scan for that byte pattern.
  private static long? ProbeBzip2(ReadOnlySpan<byte> data, long offset) {
    Span<byte> endMarker = stackalloc byte[] { 0x17, 0x72, 0x45, 0x38, 0x50, 0x90 };
    for (var i = offset + 4; i + endMarker.Length + 4 <= data.Length; ++i) {
      var ok = true;
      for (var j = 0; j < endMarker.Length; ++j)
        if (data[(int)(i + j)] != endMarker[j]) { ok = false; break; }
      if (ok) return i + endMarker.Length + 4 - offset;
    }
    return null;
  }

  // XZ: stream footer ends with "YZ\0\0" and a 12-byte footer record.
  private static long? ProbeXz(ReadOnlySpan<byte> data, long offset) {
    for (var i = offset + 6; i + 4 <= data.Length; ++i) {
      if (data[(int)i] == 'Y' && data[(int)(i + 1)] == 'Z')
        return i + 2 - offset;
    }
    return null;
  }

  // TAR: walk 512-byte records; terminates on two consecutive all-zero blocks (1024 bytes).
  private static long? ProbeTar(ReadOnlySpan<byte> data, long offset) {
    var pos = offset;
    while (pos + 512 <= data.Length) {
      if (IsZeroBlock(data, pos) && (pos + 1024 > data.Length || IsZeroBlock(data, pos + 512)))
        return pos + 1024 - offset > data.Length - offset
          ? data.Length - offset
          : pos + 1024 - offset;
      // Non-zero record: header's size field lives at offset 124..135 in octal ASCII.
      if (!TryParseOctal(data.Slice((int)(pos + 124), 12), out var size)) return null;
      var fileBlocks = (size + 511) / 512;
      pos += 512 + 512 * fileBlocks;
    }
    return null;
  }

  private static bool IsZeroBlock(ReadOnlySpan<byte> data, long pos) {
    for (var i = 0; i < 512; ++i)
      if (data[(int)(pos + i)] != 0) return false;
    return true;
  }

  private static bool TryParseOctal(ReadOnlySpan<byte> field, out long value) {
    value = 0;
    foreach (var b in field) {
      if (b == 0 || b == ' ') break;
      if (b < '0' || b > '7') { value = 0; return false; }
      value = (value << 3) | (uint)(b - '0');
    }
    return true;
  }

  // OGG: walk pages (OggS magic). Packet end when a page has flags bit 2 = EOS (0x04).
  private static long? ProbeOgg(ReadOnlySpan<byte> data, long offset) {
    var pos = offset;
    while (pos + 27 <= data.Length) {
      if (data[(int)pos] != 'O' || data[(int)(pos + 1)] != 'g' ||
          data[(int)(pos + 2)] != 'g' || data[(int)(pos + 3)] != 'S') break;
      var flags = data[(int)(pos + 5)];
      var segCount = data[(int)(pos + 26)];
      if (pos + 27 + segCount > data.Length) break;
      var payload = 0;
      for (var i = 0; i < segCount; ++i) payload += data[(int)(pos + 27 + i)];
      pos += 27 + segCount + payload;
      if ((flags & 0x04) != 0) return pos - offset;  // EOS
    }
    return pos > offset ? pos - offset : null;
  }

  // FLAC: fallback — scan forward for next fLaC magic or buffer end.
  private static long? ProbeFlac(ReadOnlySpan<byte> data, long offset) {
    for (var i = offset + 4; i + 4 <= data.Length; ++i) {
      if (data[(int)i] == 0x66 && data[(int)(i + 1)] == 0x4C &&
          data[(int)(i + 2)] == 0x61 && data[(int)(i + 3)] == 0x43)
        return i - offset;
    }
    return null;
  }
}
