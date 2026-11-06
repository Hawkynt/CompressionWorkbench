using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Bkf;

/// <summary>
/// Lays a medium out the way MTF 1.00a §3.3–3.5 prescribes for a disk file: the TAPE DBLK,
/// a filemark, then each Data Set — SSET, VOLB, DIRB and FILE DBLKs, a filemark, the ESET
/// and a filemark. Filemarks are soft filemarks (MTF_SFMB, §5.2.10), one physical block of
/// the Format Logical Block size each. Every DBLK starts on a Format Logical Block, its
/// streams on 4-byte boundaries, and a SPAD stream closes it; the Format Logical Address,
/// Control Block ID, directory and file IDs and the header checksum of every DBLK are
/// written from its position, so blocks kept from an existing medium only need their
/// own bytes and streams.
/// </summary>
internal static class MtfComposer {
  public sealed record DataSet(MtfBlock Sset, IReadOnlyList<MtfBlock> Blocks, MtfBlock Eset);

  public static void Write(Stream output, MtfBlock tape, IReadOnlyList<DataSet> sets, int flb, Func<MtfStream, byte[]> streamData) {
    var softFilemarkSize = flb; // one physical block; TAPE's Soft Filemark Block Size says the same
    var filemarks = new List<long>();
    uint sfmbControlId = 0;
    var start = output.Position;
    long Pba() => (output.Position - start) / flb;

    void Filemark() {
      var block = new byte[softFilemarkSize];
      Encoding.ASCII.GetBytes("SFMB", block);
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(Mtf.OffFirstEvent), (ushort)softFilemarkSize);
      block[Mtf.OffOsId] = Mtf.OsWindowsNt;
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(Mtf.OffDisplayableSize), (ulong)softFilemarkSize);
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(Mtf.OffFormatLogicalAddress), (ulong)Pba());
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(Mtf.OffControlBlockId), ++sfmbControlId);
      var entries = (softFilemarkSize - 60) / 4;
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(52), (uint)entries);
      var previous = filemarks.AsEnumerable().Reverse().Take(entries).ToList();
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(56), (uint)previous.Count);
      for (var i = 0; i < previous.Count; ++i)
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(60 + 4 * i), (uint)previous[i]);
      Mtf.SealHeader(block);
      filemarks.Add(Pba());
      output.Write(block);
    }

    void Emit(MtfBlock block, Action<byte[]> patch) {
      var dblk = (byte[])block.Dblk.Clone();
      var firstEvent = Mtf.Align(dblk.Length, 4);
      BinaryPrimitives.WriteUInt16LittleEndian(dblk.AsSpan(Mtf.OffFirstEvent), (ushort)firstEvent);
      patch(dblk);
      Mtf.SealHeader(dblk);
      output.Write(dblk);
      Pad(firstEvent - dblk.Length);
      foreach (var s in block.Streams) {
        var data = streamData(s);
        output.Write(s.Header);
        output.Write(data);
        Pad(Mtf.AlignLong(output.Position - start, 4) - (output.Position - start)); // next stream header on a 4-byte boundary
      }
      // SPAD to the next Format Logical Block; a header that does not fit pushes it one block on.
      var end = output.Position + Mtf.StreamHeaderSize;
      var boundary = Mtf.AlignLong(end - start, flb) + start;
      output.Write(Mtf.StreamHeader("SPAD", boundary - end));
      Pad(boundary - end);
    }

    void Pad(long count) {
      if (count <= 0) return;
      Span<byte> zeros = stackalloc byte[256];
      zeros.Clear();
      while (count > 0) {
        var n = (int)Math.Min(zeros.Length, count);
        output.Write(zeros[..n]);
        count -= n;
      }
    }

    Emit(tape, d => {
      BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(Mtf.OffFormatLogicalAddress), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(Mtf.OffControlBlockId), 0);
    });
    Filemark();

    foreach (var set in sets) {
      var setStart = output.Position;
      uint controlId = 0, directoryId = 0, fileId = 0;
      ulong Fla() => (ulong)((output.Position - setStart) / flb);
      var ssetPba = (ulong)Pba();
      Emit(set.Sset, d => {
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(Mtf.OffFormatLogicalAddress), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(Mtf.OffControlBlockId), controlId++);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(80), ssetPba);
      });
      foreach (var block in set.Blocks) {
        var fla = Fla();
        var type = block.Type;
        Emit(block, d => {
          BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(Mtf.OffFormatLogicalAddress), fla);
          BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(Mtf.OffControlBlockId), controlId++);
          if (type == "DIRB" && d.Length >= 80) {
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(76), ++directoryId);
          } else if (type == "FILE" && d.Length >= 84) {
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(76), directoryId);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(80), ++fileId);
          }
        });
      }
      Filemark();
      Emit(set.Eset, d => {
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(Mtf.OffFormatLogicalAddress), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(Mtf.OffControlBlockId), controlId);
      });
      Filemark();
    }
  }

  // ── Fresh DBLKs (§5.2) ───────────────────────────────────────────────────

  /// <summary>A DBLK with its Common Block Header, fixed fields and a string storage area.</summary>
  public static MtfBlock Block(string type, int fixedLength, uint blockAttributes, ulong displayableSize,
      byte stringType, Action<byte[], Func<string, (ushort Size, ushort Offset)>> fill) {
    var strings = new List<byte[]>();
    var fixedPart = new byte[fixedLength];
    var cursor = fixedLength;
    (ushort, ushort) Add(string s) {
      if (string.IsNullOrEmpty(s)) return (0, 0);
      var bytes = Mtf.EncodeString(s, stringType);
      strings.Add(bytes);
      var at = cursor;
      cursor += bytes.Length;
      return ((ushort)bytes.Length, (ushort)at);
    }
    fill(fixedPart, Add);
    var dblk = new byte[cursor];
    fixedPart.CopyTo(dblk, 0);
    var p = fixedLength;
    foreach (var s in strings) {
      s.CopyTo(dblk, p);
      p += s.Length;
    }
    Encoding.ASCII.GetBytes(type, dblk);
    BinaryPrimitives.WriteUInt32LittleEndian(dblk.AsSpan(Mtf.OffAttributes), blockAttributes);
    dblk[Mtf.OffOsId] = Mtf.OsWindowsNt;
    dblk[Mtf.OffOsVersion] = 0;
    BinaryPrimitives.WriteUInt64LittleEndian(dblk.AsSpan(Mtf.OffDisplayableSize), displayableSize);
    dblk[Mtf.OffStringType] = stringType;
    return new MtfBlock { Dblk = dblk };
  }

  public static void SetAddress(byte[] dblk, int at, (ushort Size, ushort Offset) address) {
    BinaryPrimitives.WriteUInt16LittleEndian(dblk.AsSpan(at), address.Size);
    BinaryPrimitives.WriteUInt16LittleEndian(dblk.AsSpan(at + 2), address.Offset);
  }

  public static MtfBlock Tape(DateTime when, string mediaName, int flb) {
    var familyId = (uint)(when.Ticks / TimeSpan.TicksPerSecond);
    return Block("TAPE", 94, 0, 0, Mtf.UnicodeStrings, (d, add) => {
      BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), familyId);
      BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(56), Mtf.TapeSoftFilemarkBit);
      BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(60), 1);              // media sequence number
      BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(64), (ushort)(flb / 512)); // soft filemark block size
      BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(66), 0);              // no Media Based Catalog
      SetAddress(d, 68, add(mediaName));
      SetAddress(d, 80, add("CompressionWorkbench"));
      BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(84), (ushort)flb);
      Mtf.WriteDate(d.AsSpan(88, 5), when);
      d[93] = 1;                                                              // MTF major version
    });
  }

  public static MtfBlock Sset(DateTime when, string name) => Block("SSET", 98, 0, 0, Mtf.UnicodeStrings, (d, add) => {
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), Mtf.SsetNormalBit);
    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(62), 1);                // Data Set number
    SetAddress(d, 64, add(name));
    Mtf.WriteDate(d.AsSpan(88, 5), when);
    d[93] = 1;                                                                // software major version
    d[95] = 127;                                                              // LOCAL_TZ
    d[96] = 0;                                                                // MTF minor version
  });

  public static MtfBlock Volb(DateTime when, string device) => Block("VOLB", 73, 0, 0, Mtf.UnicodeStrings, (d, add) => {
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), Mtf.VolbDevDriveBit);
    SetAddress(d, 56, add(device));
    Mtf.WriteDate(d.AsSpan(68, 5), when);
  });

  public static MtfBlock Eset(DateTime when, uint attributes, ushort setNumber) => Block("ESET", 85, 0, 0, Mtf.NoStrings, (d, _) => {
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), attributes);
    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(78), setNumber);
    Mtf.WriteDate(d.AsSpan(80, 5), when);
  });

  /// <summary>
  /// A DIRB for the folder path (segments joined by NUL, each with a trailing NUL; the root is
  /// a single NUL). A name that would make the DBLK larger than a Format Logical Block goes into
  /// a PNAM stream (§6.2.1.2).
  /// </summary>
  public static MtfBlock Dirb(string path, DateTime? modified, DateTime backup, bool empty, int flb) {
    var name = path.Length == 0 ? "\0" : string.Join('\0', path.Split('/')) + "\0";
    var inStream = 84 + Encoding.Unicode.GetByteCount(name) > flb - Mtf.StreamHeaderSize * 2;
    var attributes = (empty ? Mtf.DirbEmptyBit : 0) | (inStream ? Mtf.DirbPathInStreamBit : 0);
    var block = Block("DIRB", 84, 0, 0, Mtf.UnicodeStrings, (d, add) => {
      BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), attributes);
      Mtf.WriteDate(d.AsSpan(56, 5), modified);
      Mtf.WriteDate(d.AsSpan(61, 5), modified);
      Mtf.WriteDate(d.AsSpan(66, 5), backup);
      if (!inStream) SetAddress(d, 80, add(name));
    });
    if (inStream) {
      var bytes = Encoding.Unicode.GetBytes(name);
      block.Streams.Add(new MtfStream { Header = Mtf.StreamHeader("PNAM", bytes.Length), Data = bytes });
    }
    return block;
  }

  /// <summary>
  /// A FILE with its data in a STAN stream followed by the CSUM stream of that data (§6.2.1.1,
  /// §6.2.1.4), as NTBackup writes them; a name too long for the DBLK goes into an FNAM stream.
  /// </summary>
  public static MtfBlock File(string name, byte[] data, DateTime? modified, DateTime backup, int flb) {
    var inStream = 88 + Encoding.Unicode.GetByteCount(name) > flb - Mtf.StreamHeaderSize * 2;
    var block = Block("FILE", 88, 0, (ulong)data.Length, Mtf.UnicodeStrings, (d, add) => {
      BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), inStream ? Mtf.FileNameInStreamBit : 0);
      Mtf.WriteDate(d.AsSpan(56, 5), modified);
      Mtf.WriteDate(d.AsSpan(61, 5), modified);
      Mtf.WriteDate(d.AsSpan(66, 5), backup);
      if (!inStream) SetAddress(d, 84, add(name));
    });
    if (inStream) {
      var bytes = Encoding.Unicode.GetBytes(name);
      block.Streams.Add(new MtfStream { Header = Mtf.StreamHeader("FNAM", bytes.Length), Data = bytes });
    }
    block.Streams.Add(new MtfStream { Header = Mtf.StreamHeader("STAN", data.Length, mediaAttributes: Mtf.StreamChecksummed), Data = data });
    var sum = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sum, Mtf.Xor32(data));
    block.Streams.Add(new MtfStream { Header = Mtf.StreamHeader("CSUM", 4), Data = sum });
    return block;
  }
}
