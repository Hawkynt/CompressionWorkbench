using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Rar;

/// <summary>
/// Reads the archive format of RAR 1.3x and 1.4x (signature <c>RE~^</c>): a 7-byte main
/// header, then per file a 21-byte header, its name and its packed data. Files carry a 16-bit
/// rotating checksum instead of a CRC-32; compressed data is the RAR 1.5 format's (unpack 15).
/// </summary>
/// <remarks>
/// Layout from RAR 1.40's own <c>TECHNOTE.DOC</c> (rung 3: the format author's document,
/// shipped with RAR 1.40 beta, 1994): main header HEAD_ID(4) HEAD_LEN(2) HEAD_FLAGS(1)
/// [COMM_LEN(2) comment] [EXT1_LEN(2) EXT1]; file header PACK_LEN(4) UNP_LEN(4) CHECK_SUM(2)
/// HEAD_LEN(2) FTIME(4) ATTR(1) FLAGS(1) VER(1) NAME_LEN(1) METHOD(1) [FILE_COMM_LEN(2)
/// comment] name. The checksum rule (add the byte, rotate left by one, 16 bits) was confirmed
/// against every file of archives RAR 1.40 wrote under DOSBox.
/// </remarks>
internal static class Rar14Parser {
  public static bool? ReadVolume(Stream s, long signaturePos, int volumeIndex, List<RarItem> items, RarArchiveInfo info, bool firstVolume) {
    s.Position = signaturePos;
    Span<byte> head = stackalloc byte[7];
    if (!ReadExact(s, head)) throw new InvalidDataException("Truncated RAR 1.4 main header.");
    var mainLen = BinaryPrimitives.ReadUInt16LittleEndian(head[4..]);
    var flags = head[6];
    if (mainLen < 7) throw new InvalidDataException("Corrupt RAR 1.4 main header.");
    if (firstVolume) {
      info.Format = 4;
      info.IsVolume = (flags & 0x01) != 0;
      info.IsLocked = (flags & 0x04) != 0;
      info.IsSolid = (flags & 0x08) != 0;
      if ((flags & 0x02) != 0 && (flags & 0x10) == 0 && mainLen >= 9) {
        var rest = new byte[mainLen - 7];
        if (ReadExact(s, rest)) {
          var len = BinaryPrimitives.ReadUInt16LittleEndian(rest);
          if (2 + len <= rest.Length) info.Comment = Encoding.GetEncoding(437).GetString(rest, 2, len);
        }
      }
    }
    s.Position = signaturePos + mainLen;

    var header = new byte[21];
    while (s.Position < s.Length) {
      var at = s.Position;
      if (!ReadExact(s, header)) { info.Truncated = true; return null; }
      var packSize = BinaryPrimitives.ReadUInt32LittleEndian(header);
      var unpSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
      var check = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
      var headLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
      var time = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
      var attr = header[16];
      var fileFlags = header[17];
      var ver = header[18];
      var nameLen = header[19];
      var method = header[20];
      if (headLen < 21 + nameLen || method > 5 || at + headLen + packSize > s.Length) {
        info.Truncated = true;
        return null;
      }
      var rest = new byte[headLen - 21];
      if (!ReadExact(s, rest)) { info.Truncated = true; return null; }
      var namePos = (fileFlags & 0x08) != 0 && rest.Length >= 2 ? 2 + BinaryPrimitives.ReadUInt16LittleEndian(rest) : 0;
      if (namePos + nameLen > rest.Length) { info.Truncated = true; return null; }
      var name = Encoding.GetEncoding(437).GetString(rest, namePos, nameLen).Replace('\\', '/');
      var item = new RarItem {
        Name = name,
        IsDirectory = (attr & 0x10) != 0,
        Size = unpSize,
        Method = method,
        UnpackVersion = 15,
        CryptVersion = 13,
        WindowBits = 16,
        Attributes = attr,
        HostOs = 0,
        IsEncrypted = (fileFlags & 0x04) != 0,
        Solid = info.IsSolid && items.Any(i => !i.IsService),
        Checksum14 = check,
        Modified = Rar4Parser.DosTimeOf(time),
      };
      var part = new RarPart(volumeIndex, at + headLen, packSize);
      s.Position = at + headLen + packSize;
      if ((fileFlags & 0x01) != 0) {
        var prev = items.Count > 0 ? items[^1] : null;
        if (prev is { SplitAfter: true } && prev.Name == name) {
          prev.Parts.Add(part);
          prev.SplitAfter = (fileFlags & 0x02) != 0;
          prev.Checksum14 = check;
          continue;
        }
        item.SplitBefore = true;
      }
      item.Parts.Add(part);
      item.SplitAfter = (fileFlags & 0x02) != 0;
      items.Add(item);
    }
    // RAR 1.4 has no end block: a volume continues when its last file does.
    return items.Count > 0 && items[^1].SplitAfter;
  }

  /// <summary>RAR 1.4's file checksum: add each byte, then rotate the 16-bit sum left by one.</summary>
  public static ushort Checksum(ushort sum, ReadOnlySpan<byte> data) {
    foreach (var b in data) {
      sum = (ushort)(sum + b);
      sum = (ushort)((sum << 1) | (sum >> 15));
    }
    return sum;
  }

  private static bool ReadExact(Stream s, Span<byte> b) {
    var done = 0;
    while (done < b.Length) {
      var n = s.Read(b[done..]);
      if (n <= 0) return false;
      done += n;
    }
    return true;
  }
}
