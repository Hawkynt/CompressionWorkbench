using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;

namespace FileFormat.Rar;

/// <summary>
/// Walks the blocks of one RAR 1.5-4.x volume (marker, main header, file and service
/// headers, end of archive), joining files split across volumes into one item.
/// </summary>
/// <remarks>
/// Block layout, the Unicode name encoding, the extended-time field and the <c>-hp</c>
/// header encryption follow 7-Zip's <c>CPP/7zip/Archive/Rar/RarHandler.cpp</c> (Igor Pavlov,
/// LGPL-2.1-or-later; <c>DecodeUnicodeFileName</c>, <c>ReadTime</c>, <c>GetNextItem</c>).
/// </remarks>
internal static class Rar4Parser {
  private const byte TypeMain = 0x73, TypeFile = 0x74, TypeOldComment = 0x75, TypeService = 0x7A, TypeEnd = 0x7B;

  static Rar4Parser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

  /// <summary>Reads one volume; returns whether the end block says another volume follows.</summary>
  public static bool? ReadVolume(Stream s, long signatureEnd, int volumeIndex, string? password,
      List<RarItem> items, RarArchiveInfo info, bool firstVolume) {
    s.Position = signatureEnd;
    var main = ReadPlainBlock(s) ?? throw new InvalidDataException("RAR volume has no main header.");
    if (main[2] != TypeMain)
      throw new InvalidDataException("RAR main header expected after the signature.");
    var mainFlags = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(3));
    // The CRC covers the 13 fixed bytes; a RAR 1.5-2.x comment embedded in the main header is outside it.
    if (!HeaderCrcOk(main, main.Length) && !(main.Length > 13 && HeaderCrcOk(main, 13)))
      throw new InvalidDataException("RAR main header checksum mismatch.");
    if (firstVolume) {
      info.Format = 4;
      info.IsVolume = (mainFlags & 0x0001) != 0;
      info.IsLocked = (mainFlags & 0x0004) != 0;
      info.IsSolid = (mainFlags & 0x0008) != 0;
      info.NewVolumeNaming = (mainFlags & 0x0010) != 0;
      info.HasRecoveryRecord = (mainFlags & 0x0040) != 0;
      info.HeadersEncrypted = (mainFlags & 0x0080) != 0;
      info.FirstVolume = !info.IsVolume || (mainFlags & 0x0100) != 0 || !info.NewVolumeNaming;
      if ((mainFlags & 0x0002) != 0 && main.Length > 13 + 7)
        info.Comment ??= OldComment(main.AsSpan(13));
    }
    var encrypted = (mainFlags & 0x0080) != 0;
    if (encrypted && password == null)
      throw new InvalidOperationException("The RAR archive encrypts its headers; a password is required.");

    var keys = new Dictionary<string, (byte[] Key, byte[] Iv)>();
    while (s.Position < s.Length) {
      byte[]? block;
      try {
        block = encrypted ? ReadEncryptedBlock(s, password!, keys) : ReadPlainBlock(s);
      } catch (EndOfStreamException) {
        return null;
      }
      if (block == null)
        return null;

      var type = block[2];
      // With -hp there is no password check value: a wrong password shows as headers that fail their CRC.
      if (encrypted && !HeaderCrcOk(block, block.Length) && type is not (TypeFile or TypeService))
        throw new InvalidDataException("The password is wrong for this RAR archive (encrypted headers fail their checksum).");
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(3));
      var headSize = block.Length;

      if (type == TypeEnd) {
        if (!HeaderCrcOk(block, headSize) && encrypted)
          throw new InvalidDataException("The password is wrong for this RAR archive.");
        return (flags & 0x0001) != 0;
      }

      if (type is TypeFile or TypeService) {
        var item = ParseFile(block, flags, type == TypeService);
        if (item == null) {
          // CRC mismatch: with encrypted headers this is the first sign of a wrong password.
          if (encrypted)
            throw new InvalidDataException("The password is wrong for this RAR archive.");
          throw new InvalidDataException("RAR file header checksum mismatch.");
        }
        var packSize = item.Value.PackSize;
        var dataOffset = s.Position;
        s.Position = dataOffset + packSize;
        // RAR 1.5 marks only the archive solid (MHD_SOLID); every file after the first continues
        // the window. RAR 2.0 and later flag each continuing file (LHD_SOLID).
        if (info.IsSolid && item.Value.Item.UnpackVersion <= 15 && !item.Value.Item.IsService && items.Any(i => !i.IsService))
          item.Value.Item.Solid = true;
        Attach(items, item.Value.Item, new RarPart(volumeIndex, dataOffset, packSize), flags);
        continue;
      }

      if (type == TypeOldComment && info.Comment == null && firstVolume)
        info.Comment = OldComment(block);

      // Any other block: skip its data area (ADD_SIZE after the 7-byte header).
      if ((flags & 0x8000) != 0 && headSize >= 11)
        s.Position += BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(7));
    }
    return null;
  }

  private static void Attach(List<RarItem> items, RarItem item, RarPart part, ushort flags) {
    var splitBefore = (flags & 0x01) != 0;
    var splitAfter = (flags & 0x02) != 0;
    if (splitBefore) {
      var at = items.FindLastIndex(i => i.IsService == item.IsService);
      var prev = at >= 0 ? items[at] : null;
      if (prev != null && prev.SplitAfter && prev.Name == item.Name) {
        prev.Parts.Add(part);
        prev.SplitAfter = splitAfter;
        prev.Crc = item.Crc;
        prev.Size = item.Size;
        return;
      }
      item.SplitBefore = true;
    }
    item.Parts.Add(part);
    item.SplitAfter = splitAfter;
    if (item.IsService)
      item.Owner = items.FindLastIndex(i => !i.IsService);
    items.Add(item);
  }

  private static (RarItem Item, long PackSize)? ParseFile(byte[] h, ushort flags, bool service) {
    if (h.Length < 32) return null;
    ReadOnlySpan<byte> p = h.AsSpan(7);
    long packSize = BinaryPrimitives.ReadUInt32LittleEndian(p);
    long unpSize = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
    var host = p[8];
    var crc = BinaryPrimitives.ReadUInt32LittleEndian(p[9..]);
    var dosTime = BinaryPrimitives.ReadUInt32LittleEndian(p[13..]);
    var unpVer = p[17];
    var method = p[18];
    var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(p[19..]);
    var attr = BinaryPrimitives.ReadUInt32LittleEndian(p[21..]);
    var pos = 25;
    if ((flags & 0x0100) != 0) {
      if (p.Length < pos + 8) return null;
      packSize |= (long)BinaryPrimitives.ReadUInt32LittleEndian(p[pos..]) << 32;
      unpSize |= (long)BinaryPrimitives.ReadUInt32LittleEndian(p[(pos + 4)..]) << 32;
      pos += 8;
    }
    if (p.Length < pos + nameSize) return null;
    var nameField = p.Slice(pos, nameSize);
    pos += nameSize;

    var item = new RarItem {
      IsService = service,
      HostOs = host,
      Crc = crc,
      UnpackVersion = unpVer,
      Method = method - 0x30,
      Attributes = attr,
      IsEncrypted = (flags & 0x0004) != 0,
      Solid = (flags & 0x0010) != 0,
      Size = unpSize,
      WindowBits = 16 + ((flags >> 5) & 7),
      Modified = DosTime(dosTime),
    };
    item.IsDirectory = !service && ((flags & 0x00E0) == 0x00E0
      || (host is 0 or 1 or 2 && (attr & 0x10) != 0)
      || (host is 3 or 5 && (attr & 0xF000) == 0x4000));
    item.Name = service ? Encoding.ASCII.GetString(nameField) : DecodeName(nameField, (flags & 0x0200) != 0, host is 3 or 5);
    if (service) item.ServiceName = item.Name;
    if (host == 3 && (attr & 0xF000) == 0xA000)
      item.Link = RarLinkKind.UnixSymlink;

    if ((flags & 0x0400) != 0) {
      if (p.Length < pos + 8) return null;
      item.Salt = p.Slice(pos, 8).ToArray();
      pos += 8;
    }
    if (service && item.Name == "STM" && (flags & 0x1000) == 0 && p.Length > pos) {
      // The stream name follows, as UTF-16LE.
      var rest = p[pos..];
      if ((rest.Length & 1) == 0)
        item.StreamName = Encoding.Unicode.GetString(rest).TrimEnd('\0');
    }
    if ((flags & 0x1000) != 0 && p.Length >= pos + 2) {
      var aMask = p[pos] >> 4;
      var b = p[pos + 1];
      pos += 2;
      var mMask = b >> 4;
      var cMask = b & 0xF;
      var rest = p[pos..];
      if ((mMask & 8) != 0 && item.Modified is { } m)
        item.Modified = ExtTime(m, mMask, ref rest);
      if ((cMask & 8) != 0 && rest.Length >= 4) {
        var t = DosTime(BinaryPrimitives.ReadUInt32LittleEndian(rest));
        rest = rest[4..];
        if (t is { } ct) item.Created = ExtTime(ct, cMask, ref rest);
      }
      if ((aMask & 8) != 0 && rest.Length >= 4) {
        var t = DosTime(BinaryPrimitives.ReadUInt32LittleEndian(rest));
        rest = rest[4..];
        if (t is { } at) item.Accessed = ExtTime(at, aMask, ref rest);
      }
    }
    // Old file comments (LHD_COMMENT) sit inside the header but outside its CRC.
    if (!HeaderCrcOk(h, h.Length) && !HeaderCrcOk(h, Math.Min(h.Length, 7 + pos)))
      return null;
    return (item, packSize);
  }

  private static DateTimeOffset ExtTime(DateTimeOffset dos, int mask, ref ReadOnlySpan<byte> rest) {
    var digits = mask & 3;
    long sub = 0;
    if (digits <= rest.Length) {
      for (var i = 0; i < digits; ++i)
        sub |= (long)rest[i] << (8 * (3 - digits + i));
      rest = rest[digits..];
    }
    var t = dos.AddTicks(sub);
    if ((mask & 4) != 0) t = t.AddSeconds(1);
    return t;
  }

  /// <summary>A DOS date/time, which RAR 4 stores in local time.</summary>
  internal static DateTimeOffset? DosTimeOf(uint v) => DosTime(v);

  private static DateTimeOffset? DosTime(uint v) {
    var sec = (int)(v & 0x1F) * 2;
    var min = (int)((v >> 5) & 0x3F);
    var hour = (int)((v >> 11) & 0x1F);
    var day = (int)((v >> 16) & 0x1F);
    var month = (int)((v >> 21) & 0x0F);
    var year = 1980 + (int)(v >> 25);
    if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || min > 59 || sec > 59)
      return null;
    var local = new DateTime(year, month, day, hour, min, sec, DateTimeKind.Local);
    return new DateTimeOffset(local);
  }

  /// <summary>
  /// A RAR 4 file name: OEM (code page 437) bytes, or with <c>LHD_UNICODE</c> the OEM name, a
  /// zero and RAR's compressed UTF-16 form (or UTF-8 when no zero separates them).
  /// </summary>
  internal static string DecodeName(ReadOnlySpan<byte> field, bool unicode, bool unixHost = false) {
    var zero = field.IndexOf((byte)0);
    var oem = zero >= 0 ? field[..zero] : field;
    string name;
    if (!unicode && unixHost && StrictUtf8.TryGetString(oem) is { } utf8)
      // RAR for Unix stores names in the system charset without LHD_UNICODE; today that is UTF-8.
      name = utf8;
    else if (!unicode)
      name = Encoding.GetEncoding(437).GetString(oem);
    else if (zero < 0)
      name = Encoding.UTF8.GetString(field);
    else {
      name = DecodeUnicode(oem, field[(zero + 1)..]);
      if (name.Length == 0) name = Encoding.GetEncoding(437).GetString(oem);
    }
    return name.Replace('\\', '/');
  }

  private static class StrictUtf8 {
    private static readonly Encoding Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
    public static string? TryGetString(ReadOnlySpan<byte> b) {
      try { return Strict.GetString(b); } catch (DecoderFallbackException) { return null; }
    }
  }

  private static string DecodeUnicode(ReadOnlySpan<byte> name, ReadOnlySpan<byte> enc) {
    if (enc.Length == 0) return "";
    var maxLen = Math.Min(Math.Max(name.Length, enc.Length) * 2 + 2, 0x400);
    var output = new char[maxLen];
    int encPos = 0, decPos = 0, flagBits = 0;
    var flags = 0;
    var highBits = enc[encPos++] << 8;
    while (encPos < enc.Length && decPos < maxLen) {
      if (flagBits == 0) {
        flags = enc[encPos++];
        flagBits = 8;
      }
      if (encPos >= enc.Length) break;
      int len = enc[encPos++];
      flagBits -= 2;
      var mode = (flags >> flagBits) & 3;
      if (mode != 3) {
        if (mode == 1) len += highBits;
        else if (mode == 2) {
          if (encPos >= enc.Length) break;
          len += enc[encPos++] << 8;
        }
        output[decPos++] = (char)len;
      } else if ((len & 0x80) != 0) {
        if (encPos >= enc.Length) break;
        var correction = enc[encPos++];
        for (len = (len & 0x7F) + 2; len > 0 && decPos < maxLen && decPos < name.Length; --len, ++decPos)
          output[decPos] = (char)(((name[decPos] + correction) & 0xFF) + highBits);
      } else {
        for (len += 2; len > 0 && decPos < maxLen && decPos < name.Length; --len, ++decPos)
          output[decPos] = (char)name[decPos];
      }
    }
    return new string(output, 0, decPos);
  }

  /// <summary>An old-style (RAR 2.x) archive comment: stored text only; compressed ones are not decoded.</summary>
  private static string? OldComment(ReadOnlySpan<byte> block) {
    // HEAD_CRC(2) TYPE(1) FLAGS(2) SIZE(2) UNP_SIZE(2) UNP_VER(1) METHOD(1) COMM_CRC(2) data
    if (block.Length < 13 || block[2] != TypeOldComment) return null;
    var unp = BinaryPrimitives.ReadUInt16LittleEndian(block[7..]);
    var method = block[10];
    var data = block[13..];
    if (method != 0x30 || data.Length < unp) return null;
    return Encoding.GetEncoding(437).GetString(data[..unp]);
  }

  private static bool HeaderCrcOk(byte[] block, int size)
    => BinaryPrimitives.ReadUInt16LittleEndian(block) == (ushort)(Crc32.Compute(block.AsSpan(2, size - 2)) & 0xFFFF);

  private static byte[]? ReadPlainBlock(Stream stream) {
    var common = new byte[7];
    if (!TryReadExact(stream, common))
      return null;
    var headSize = Math.Max((int)BinaryPrimitives.ReadUInt16LittleEndian(common.AsSpan(5)), 7);
    var block = new byte[headSize];
    common.CopyTo(block, 0);
    if (!TryReadExact(stream, block.AsSpan(7)))
      throw new EndOfStreamException();
    return block;
  }

  /// <summary>
  /// A block of an archive with encrypted headers: an 8-byte salt, then the block AES-128-CBC
  /// encrypted with the RAR 2.9 key of that salt, padded to 16 bytes.
  /// </summary>
  private static byte[]? ReadEncryptedBlock(Stream stream, string password, Dictionary<string, (byte[] Key, byte[] Iv)> keys) {
    var salt = new byte[8];
    var first = new byte[16];
    if (!TryReadExact(stream, salt))
      return null;
    if (!TryReadExact(stream, first))
      throw new EndOfStreamException();
    var id = Convert.ToHexString(salt);
    if (!keys.TryGetValue(id, out var keyIv)) {
      keyIv = KeyDerivation.Rar3DeriveKey(password, salt);
      keys[id] = keyIv;
    }
    var head = AesCryptor.DecryptCbcNoPaddingAny(first, keyIv.Key, keyIv.Iv);
    if (head[2] is < 0x72 or > 0x7B)
      throw new InvalidDataException("The password is wrong for this RAR archive (encrypted headers do not decrypt to a block).");
    var headSize = Math.Max((int)BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(5)), 7);
    var padded = (headSize + 15) & ~15;
    var cipher = new byte[padded];
    first.CopyTo(cipher, 0);
    if (!TryReadExact(stream, cipher.AsSpan(16)))
      throw new EndOfStreamException();
    return AesCryptor.DecryptCbcNoPaddingAny(cipher, keyIv.Key, keyIv.Iv).AsSpan(0, headSize).ToArray();
  }

  private static bool TryReadExact(Stream stream, Span<byte> buffer) {
    var done = 0;
    while (done < buffer.Length) {
      var n = stream.Read(buffer[done..]);
      if (n <= 0) return done == 0 ? false : throw new EndOfStreamException();
      done += n;
    }
    return true;
  }
}
