using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Rar;

/// <summary>
/// Walks the headers of one RAR 5 volume (technote <c>https://www.rarlab.com/technote.htm</c>,
/// "RAR 5.0 archive format"), joining files split across volumes into one item.
/// </summary>
/// <remarks>
/// Header reading, the encrypted-header layout and the extra-record parsing follow the
/// technote and 7-Zip's <c>CPP/7zip/Archive/Rar/Rar5Handler.cpp</c> (Igor Pavlov,
/// LGPL-2.1-or-later): with <c>-hp</c> every header after the archive encryption header
/// starts with its own 16-byte IV and is AES-256-CBC encrypted to a 16-byte multiple.
/// </remarks>
internal static class Rar5Parser {
  private const int MaxHeaderSize = 2 * 1024 * 1024;

  private sealed class HeaderReader(Stream s) {
    public byte[] Buf = [];
    public int Pos;
    public int End;

    public ulong Vint() {
      ulong v = 0;
      for (var i = 0; i < 10; ++i) {
        if (this.Pos >= this.End) throw new InvalidDataException("Truncated RAR 5 header.");
        var b = this.Buf[this.Pos++];
        v |= (ulong)(b & 0x7F) << (7 * i);
        if ((b & 0x80) == 0) return v;
      }
      throw new InvalidDataException("Malformed RAR 5 variable-length integer.");
    }

    public byte[] Bytes(int n) {
      if (n < 0 || this.Pos + n > this.End) throw new InvalidDataException("Truncated RAR 5 header.");
      var r = this.Buf.AsSpan(this.Pos, n).ToArray();
      this.Pos += n;
      return r;
    }

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(this.Bytes(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(this.Bytes(8));
    public byte U8() => this.Bytes(1)[0];
    public Stream Stream => s;
  }

  private sealed record Header(ulong Type, ulong Flags, long ExtraSize, long DataSize, long DataOffset, HeaderReader Reader, int BodyStart);

  /// <summary>
  /// Reads one volume. Returns whether the end header says more volumes follow
  /// (null when the volume has no end header).
  /// </summary>
  public static bool? ReadVolume(Stream s, long signatureEnd, int volumeIndex, Rar5Crypto? crypto,
      List<RarItem> items, RarArchiveInfo info, bool firstVolume) {
    s.Position = signatureEnd;
    byte[]? headerKey = null;
    var expectMain = true;
    while (s.Position < s.Length) {
      Header h;
      try {
        h = ReadHeader(s, headerKey);
      } catch (EndOfStreamException) {
        info.Truncated = true;
        return null;
      } catch (InvalidDataException) when (!expectMain && (headerKey == null || items.Count > 0)) {
        // A damaged header after the main one ends the walk, as unrar stops there too; what
        // was read stays listed and extractable.
        info.Truncated = true;
        return null;
      }

      if (h.Type == 4) {
        // Archive encryption header: everything after it is encrypted.
        var r = h.Reader;
        var version = r.Vint();
        var encFlags = r.Vint();
        if (version != 0)
          throw new NotSupportedException($"RAR 5 encryption version {version} is not supported.");
        var kdf = r.U8();
        var salt = r.Bytes(16);
        byte[]? check = (encFlags & 1) != 0 ? r.Bytes(12) : null;
        info.HeadersEncrypted = true;
        if (crypto == null)
          throw new InvalidOperationException("The RAR archive encrypts its headers; a password is required.");
        var keys = crypto.Keys(salt, kdf);
        if (Rar5Crypto.PasswordMatches(check, keys.Check) == false)
          throw new InvalidDataException("The password is wrong for this RAR archive.");
        headerKey = keys.Key;
        continue;
      }

      if (expectMain) {
        if (h.Type != 1)
          throw new InvalidDataException($"RAR 5 volume starts with header type {h.Type}, not the main header.");
        expectMain = false;
        ReadMain(h, info, firstVolume);
        SkipData(s, h);
        continue;
      }

      switch (h.Type) {
        case 2:
        case 3:
          try {
            ReadFile(s, h, volumeIndex, items, info);
          } catch (InvalidDataException) {
            info.Truncated = true;
            return null;
          }
          break;
        case 5: {
          // Some writers leave the end-of-archive flags out; that reads as "last volume".
          var endFlags = h.Reader.Pos < h.Reader.End ? h.Reader.Vint() : 0;
          return (endFlags & 1) != 0;
        }
        default:
          SkipData(s, h);
          break;
      }
    }
    return null;
  }

  private static void SkipData(Stream s, Header h) => s.Position = h.DataOffset + h.DataSize;

  private static Header ReadHeader(Stream s, byte[]? key) {
    var start = s.Position;
    byte[] raw;
    int headerLen;
    if (key != null) {
      var iv = ReadExact(s, 16);
      var first = ReadExact(s, 16);
      using var aes = Aes.Create();
      aes.Key = key;
      var plain = aes.DecryptCbc(first, iv, PaddingMode.None);
      var (size, sizeLen) = ReadVintAt(plain, 4, 3);
      headerLen = 4 + sizeLen + (int)size;
      if (size < 2 || headerLen > MaxHeaderSize)
        throw new InvalidDataException("Corrupt encrypted RAR 5 header (wrong password?).");
      var padded = (headerLen + 15) & ~15;
      var cipher = new byte[padded];
      first.CopyTo(cipher, 0);
      ReadExact(s, cipher.AsSpan(16));
      raw = aes.DecryptCbc(cipher, iv, PaddingMode.None);
    } else {
      var head = new byte[7];
      var got = ReadSome(s, head);
      if (got == 0) throw new EndOfStreamException();
      if (got < 7) {
        // A header must hold at least CRC, a one-byte size and type+flags.
        if (got < 6) throw new EndOfStreamException();
      }
      var (size, sizeLen) = ReadVintAt(head, 4, 3);
      headerLen = 4 + sizeLen + (int)size;
      if (size < 2 || headerLen > MaxHeaderSize)
        throw new InvalidDataException("Corrupt RAR 5 header size.");
      raw = new byte[headerLen];
      head.AsSpan(0, Math.Min(got, headerLen)).CopyTo(raw);
      if (got > headerLen) {
        s.Position = start + headerLen;
      } else
        ReadExact(s, raw.AsSpan(got));
    }

    var crc = BinaryPrimitives.ReadUInt32LittleEndian(raw);
    if (Crc32.Compute(raw.AsSpan(4, headerLen - 4)) != crc)
      throw new InvalidDataException(key != null
        ? "RAR 5 header checksum mismatch in encrypted headers (wrong password or damaged archive)."
        : "RAR 5 header checksum mismatch.");

    var r = new HeaderReader(s) { Buf = raw, End = headerLen };
    r.Pos = 4;
    _ = r.Vint(); // size
    var type = r.Vint();
    var flags = r.Vint();
    long extra = 0, data = 0;
    if ((flags & 1) != 0) extra = (long)r.Vint();
    if ((flags & 2) != 0) data = (long)r.Vint();
    if (extra < 0 || extra > r.End - r.Pos || data < 0)
      throw new InvalidDataException("Corrupt RAR 5 header sizes.");
    return new Header(type, flags, extra, data, s.Position, r, r.Pos);
  }

  private static (ulong Value, int Length) ReadVintAt(byte[] buf, int pos, int max) {
    ulong v = 0;
    for (var i = 0; i < max && pos + i < buf.Length; ++i) {
      var b = buf[pos + i];
      v |= (ulong)(b & 0x7F) << (7 * i);
      if ((b & 0x80) == 0) return (v, i + 1);
    }
    throw new InvalidDataException("Malformed RAR 5 header size.");
  }

  private static void ReadMain(Header h, RarArchiveInfo info, bool firstVolume) {
    var r = h.Reader;
    var flags = r.Vint();
    var isVolume = (flags & 1) != 0;
    long volNumber = 0;
    if ((flags & 2) != 0) volNumber = (long)r.Vint();
    if (firstVolume) {
      info.Format = 5;
      info.IsVolume = isVolume;
      info.IsSolid = (flags & 4) != 0;
      info.IsLocked = (flags & 0x10) != 0;
      info.VolumeNumber = (int)volNumber;
      info.FirstVolume = volNumber == 0;
    }
    if ((flags & 8) != 0) info.HasRecoveryRecord = true;
    // Extra area: locator (quick open, recovery record) and metadata; neither changes
    // what the archive holds, so the locator is only noted.
    var extraEnd = r.Pos + (int)h.ExtraSize;
    while (r.Pos < extraEnd) {
      var size = (int)r.Vint();
      var recEnd = r.Pos + size;
      if (size <= 0 || recEnd > extraEnd) break;
      var type = r.Vint();
      if (type == 1) {
        var lf = r.Vint();
        if ((lf & 1) != 0 && r.Vint() != 0) info.HasQuickOpen = true;
      }
      r.Pos = recEnd;
    }
  }

  private static void ReadFile(Stream s, Header h, int volumeIndex, List<RarItem> items, RarArchiveInfo info) {
    var r = h.Reader;
    var item = new RarItem { IsRar5 = true, IsService = h.Type == 3 };
    var fileFlags = r.Vint();
    var unpacked = (long)r.Vint();
    item.Attributes = (uint)r.Vint();
    uint? unixMtime = null;
    if ((fileFlags & 2) != 0) unixMtime = r.U32();
    if ((fileFlags & 4) != 0) item.Crc = r.U32();
    var comp = r.Vint();
    item.HostOs = (int)r.Vint();
    var nameLen = (int)r.Vint();
    var nameBytes = r.Bytes(nameLen);
    item.Name = DecodeName(nameBytes);
    item.IsDirectory = !item.IsService && (fileFlags & 1) != 0;
    item.Size = (fileFlags & 8) != 0 ? -1 : unpacked;
    if (item.IsService) {
      item.ServiceName = item.Name;
      item.Owner = items.FindLastIndex(i => !i.IsService);
    }

    item.AlgorithmVersion = (int)(comp & 0x3F);
    item.Solid = (comp & 0x40) != 0;
    item.Method = (int)((comp >> 7) & 7);
    var dictBits = (int)((comp >> 10) & (item.AlgorithmVersion == 0 ? 0xFu : 0x1Fu));
    var frac = item.AlgorithmVersion == 1 ? (int)((comp >> 15) & 0x1F) : 0;
    item.DictionarySize = (32L + frac) << (12 + dictBits);
    if (item.AlgorithmVersion == 1 && (comp & 0x100000) != 0)
      item.AlgorithmVersion = 0; // version-1 flags over a version-0 stream

    if (unixMtime is { } mt)
      item.Modified = DateTimeOffset.FromUnixTimeSeconds(mt);

    // Extra area.
    // A malformed record ends the extra area, as unrar skips what it cannot parse; a broken
    // encryption record still marks the file encrypted so it is refused, not read as plain.
    var extraEnd = Math.Min(r.Pos + (int)h.ExtraSize, r.End);
    while (r.Pos < extraEnd) {
      var savedEnd = r.End;
      ulong type = 0;
      int recEnd;
      try {
        var size = (int)r.Vint();
        recEnd = r.Pos + size;
        if (size <= 0 || recEnd > extraEnd) break;
        r.End = recEnd;
        type = r.Vint();
        ReadExtraRecord(r, item, type, recEnd);
      } catch (InvalidDataException) {
        if (type == 1) item.IsEncrypted = true;
        r.End = savedEnd;
        break;
      }
      r.End = savedEnd;
      r.Pos = recEnd;
    }
    r.Pos = Math.Max(r.Pos, extraEnd);
    FinishFile(s, h, volumeIndex, items, item);
  }

  private static void ReadExtraRecord(HeaderReader r, RarItem item, ulong type, int recEnd) {
      switch (type) {
        case 1: {
          var version = r.Vint();
          var cf = r.Vint();
          var kdf = r.U8();
          var salt = r.Bytes(16);
          var iv = r.Bytes(16);
          var check = (cf & 1) != 0 ? r.Bytes(12) : null;
          item.IsEncrypted = true;
          if (version == 0)
            item.Crypt = new Rar5CryptInfo(kdf, salt, iv, check, (cf & 2) != 0);
          break;
        }
        case 2: {
          var hashType = r.Vint();
          if (hashType == 0 && recEnd - r.Pos >= 32) item.Blake2 = r.Bytes(32);
          break;
        }
        case 3:
          ReadTimes(r, item);
          break;
        case 4: {
          _ = r.Vint();
          item.FileVersion = (long)r.Vint();
          break;
        }
        case 5: {
          var linkType = (int)r.Vint();
          var lf = r.Vint();
          var len = (int)r.Vint();
          var target = Encoding.UTF8.GetString(r.Bytes(len));
          item.Link = linkType is >= 1 and <= 5 ? (RarLinkKind)linkType : RarLinkKind.None;
          item.LinkTarget = linkType is 2 or 3 ? target.Replace('\\', '/') : target;
          item.LinkTargetIsDirectory = (lf & 1) != 0;
          break;
        }
        case 7:
          if (item.IsService)
            item.StreamName = DecodeName(r.Bytes(recEnd - r.Pos));
          break;
      }
  }

  private static void FinishFile(Stream s, Header h, int volumeIndex, List<RarItem> items, RarItem item) {
    // RAR applies its version suffix to the name of a -ver file ("name;2").
    if (item.FileVersion is { } fv && !item.IsService)
      item.Name += ";" + fv;

    var part = new RarPart(volumeIndex, h.DataOffset, h.DataSize);
    var splitBefore = (h.Flags & 0x08) != 0;
    var splitAfter = (h.Flags & 0x10) != 0;
    SkipData(s, h);

    if (splitBefore) {
      // Continuation of the last item of its kind in the previous volume (service records such
      // as the quick-open data may sit between the two parts).
      var at = items.FindLastIndex(i => i.IsService == item.IsService);
      var prev = at >= 0 ? items[at] : null;
      if (prev != null && prev.SplitAfter && prev.Name == item.Name) {
        prev.Parts.Add(part);
        prev.SplitAfter = splitAfter;
        // The last part carries the checksums of the whole file.
        prev.Crc = item.Crc;
        prev.Blake2 = item.Blake2;
        // Only the last part's encryption record says whether those checksums are tweaked
        // (rar 7.12 writes flag 0x0002 there and leaves it off the earlier parts).
        if (prev.Crypt != null && item.Crypt != null)
          prev.Crypt = prev.Crypt with { UseMac = item.Crypt.UseMac };
        if (prev.Size < 0 || item.Size >= 0) prev.Size = item.Size;
        return;
      }
      // The first part is in a volume we do not have.
      item.SplitBefore = true;
    }
    item.Parts.Add(part);
    item.SplitAfter = splitAfter;
    if (splitAfter) {
      // Part CRCs of non-final parts cover packed data only; the final part brings the real ones.
      item.Crc = null;
      item.Blake2 = null;
    }
    items.Add(item);
  }

  private static void ReadTimes(HeaderReader r, RarItem item) {
    var flags = r.Vint();
    var unix = (flags & 1) != 0;
    var present = new[] { (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0 };
    var values = new DateTimeOffset?[3];
    var raw = new long[3];
    for (var i = 0; i < 3; ++i)
      if (present[i])
        raw[i] = unix ? r.U32() : (long)r.U64();
    for (var i = 0; i < 3; ++i) {
      if (!present[i]) continue;
      if (unix) {
        var t = DateTimeOffset.FromUnixTimeSeconds((uint)raw[i]);
        values[i] = t;
      } else if (raw[i] > 0 && raw[i] < DateTime.MaxValue.ToFileTimeUtc())
        values[i] = new DateTimeOffset(DateTime.FromFileTimeUtc(raw[i]));
    }
    if (unix && (flags & 0x10) != 0)
      for (var i = 0; i < 3; ++i)
        if (present[i] && values[i] is { } t) {
          var ns = r.U32() & 0x3FFFFFFF;
          if (ns < 1_000_000_000)
            values[i] = t.AddTicks(ns / 100);
        }
    if (values[0] != null) item.Modified = values[0];
    item.Created = values[1];
    item.Accessed = values[2];
  }

  /// <summary>
  /// A RAR 5 name: UTF-8, slash-separated. Unix names that held bytes UTF-8 cannot carry
  /// were mapped by RAR to U+E080-U+E0FF with a U+FFFE marker; those characters are kept
  /// as mapped, minus the marker.
  /// </summary>
  private static string DecodeName(byte[] bytes) {
    var name = Encoding.UTF8.GetString(bytes);
    var zero = name.IndexOf('\0');
    if (zero >= 0) name = name[..zero];
    return name.Replace("\uFFFE", "", StringComparison.Ordinal);
  }

  private static byte[] ReadExact(Stream s, int n) {
    var b = new byte[n];
    ReadExact(s, b);
    return b;
  }

  private static void ReadExact(Stream s, Span<byte> b) {
    var done = 0;
    while (done < b.Length) {
      var n = s.Read(b[done..]);
      if (n <= 0) throw new EndOfStreamException();
      done += n;
    }
  }

  private static int ReadSome(Stream s, byte[] b) {
    var done = 0;
    while (done < b.Length) {
      var n = s.Read(b, done, b.Length - done);
      if (n <= 0) break;
      done += n;
    }
    return done;
  }
}

