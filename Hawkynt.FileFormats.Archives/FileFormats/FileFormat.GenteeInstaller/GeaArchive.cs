using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Entropy.Ppmd;

namespace FileFormat.GenteeInstaller;

/// <summary>One file of a GEA archive.</summary>
/// <param name="Path">Sub-folder and name, '/'-separated.</param>
/// <param name="Size">Unpacked size.</param>
/// <param name="CompressedSize">Packed size.</param>
/// <param name="Crc">CRC-32 of the unpacked file, 0 when none is recorded.</param>
/// <param name="Modified">File time.</param>
/// <param name="IsSolid">Its blocks continue the previous file's LZGE window.</param>
/// <param name="IsProtected">Encrypted with an archive password.</param>
public sealed record GeaEntry(string Path, long Size, long CompressedSize, uint Crc, DateTime? Modified, bool IsSolid, bool IsProtected) {
  internal long Offset { get; init; }
}

/// <summary>
/// GEA, the Gentee archive: a <c>GEA\0</c> volume header, the archive header, a file table (LZGE
/// packed or not), the data moved from the end to the head, and the files as blocks, each stored,
/// LZGE or PPMd (var. H) coded.
/// </summary>
/// <remarks>
/// Converted from the <c>gentee</c> module of Observer (lazyhamster, LGPL-3.0-or-later),
/// <c>GeaFile.cpp</c> and <c>gea.h</c>, revision <c>ddbe6b6d</c>; see THIRD-PARTY-NOTICE.Observer.txt.
/// Deviations: a PPMd block's model order is the low nibble of its order byte plus one (Observer
/// computes it as <c>order &amp; 0x10</c> by operator precedence, which passes 0 or 16); multi-volume
/// archives and password-protected files are refused; every file's CRC-32 is checked.
/// </remarks>
public sealed class GeaArchive {
  private const ushort FlagSolid = 0x0100, FlagAttribute = 0x0001, FlagFolder = 0x0010, FlagVersion = 0x0020,
    FlagGroup = 0x0040, FlagProtect = 0x0080;
  private const uint HeadPassword = 0x0001, HeadCompress = 0x0002;
  private const int Store = 0, LzgeMethod = 1, PpmdMethod = 2;
  private const int VolumeHeaderSize = 10, HeadSize = 63, BlockHeaderSize = 5, WideBlockHeaderSize = 9;

  private static readonly Encoding Ansi = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;

  private readonly byte[] _data;
  private readonly long _dataStart;
  private readonly long _volumeSize;
  private readonly byte[] _moved;
  private readonly int _blockSize, _solidSize, _memoryMb;

  private GeaArchive(byte[] data, long dataStart, long volumeSize, byte[] moved, int blockSize, int solidSize, int memoryMb, List<GeaEntry> entries) {
    this._data = data;
    this._dataStart = dataStart;
    this._volumeSize = volumeSize;
    this._moved = moved;
    this._blockSize = blockSize;
    this._solidSize = solidSize;
    this._memoryMb = memoryMb;
    this.Entries = entries;
  }

  /// <summary>The archive's files.</summary>
  public IReadOnlyList<GeaEntry> Entries { get; }

  /// <summary>Whether a GEA volume header starts at <paramref name="offset"/>.</summary>
  public static bool StartsAt(ReadOnlySpan<byte> data, long offset) =>
    offset >= 0 && offset + VolumeHeaderSize + HeadSize <= data.Length && data.Slice((int)offset, 4).SequenceEqual("GEA\0"u8) &&
    data[(int)offset + VolumeHeaderSize] is 1 or 2 && data[(int)offset + VolumeHeaderSize + 1] == 0;

  /// <summary>Opens the GEA archive at <paramref name="offset"/> of <paramref name="data"/>.</summary>
  /// <exception cref="InvalidDataException">The header or file table is damaged.</exception>
  /// <exception cref="NotSupportedException">A multi-volume archive.</exception>
  public static GeaArchive Open(byte[] data, long offset) {
    if (!StartsAt(data, offset))
      throw new InvalidDataException("no GEA 1.0 or 2.0 archive header");
    var h = data.AsSpan((int)offset + VolumeHeaderSize, HeadSize);
    var fileTime = BinaryPrimitives.ReadInt64LittleEndian(h[2..]);
    _ = fileTime;
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(h[10..]);
    var volumes = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
    var infoSize = BinaryPrimitives.ReadUInt32LittleEndian(h[28..]);
    var geaSize = BinaryPrimitives.ReadInt64LittleEndian(h[32..]);
    var movedSize = BinaryPrimitives.ReadUInt32LittleEndian(h[56..]);
    var summary = BinaryPrimitives.ReadInt64LittleEndian(h[20..]);
    var memoryMb = h[60];
    var blockSize = h[61] * 0x40000;
    var solidSize = h[62] * 0x40000;
    if (volumes != 1)
      throw new NotSupportedException($"a GEA archive in {volumes} volumes");
    if (blockSize == 0 || offset + headerSize + movedSize > data.Length || infoSize > 64 << 20)
      throw new InvalidDataException("GEA header out of range");

    var p = offset + VolumeHeaderSize + HeadSize;
    CString(data, ref p); // the file name pattern
    if ((flags & HeadPassword) != 0) {
      int passwords = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)p));
      p += 2 + 4L * passwords;
    }

    byte[] info;
    if ((flags & HeadCompress) != 0) {
      info = new byte[infoSize];
      Lzge.Decode(data.AsSpan((int)p, (int)Math.Min(data.Length - p, offset + headerSize - p + 16)), info, 0, (int)infoSize);
    } else {
      if (p + infoSize > data.Length)
        throw new InvalidDataException("GEA file table out of range");
      info = data.AsSpan((int)p, (int)infoSize).ToArray();
    }

    // Two layouts share version 1.0: 32-bit or 64-bit sizes, in the file table and in the block
    // headers alike. The header's total packed size tells them apart.
    List<GeaEntry> entries;
    bool wideSizes;
    if (ReadTable(info, wide: false) is { } narrow && narrow.Packed == summary)
      (entries, wideSizes) = (narrow.Entries, false);
    else if (ReadTable(info, wide: true) is { } wide && wide.Packed == summary)
      (entries, wideSizes) = (wide.Entries, true);
    else
      throw new InvalidDataException("the GEA file table matches neither layout's packed total");

    var moved = data.AsSpan((int)(offset + headerSize), (int)movedSize).ToArray();
    var dataStart = offset + headerSize + movedSize;
    // The data runs from after the moved block to the archive's end, which the header gives as an
    // offset in the file the archive sits in; the moved block holds the logical tail.
    var volumeSize = geaSize - movedSize - headerSize - offset;
    if (volumeSize < 0 || dataStart + volumeSize > data.Length)
      throw new InvalidDataException("GEA data size out of range");
    return new GeaArchive(data, dataStart, volumeSize, moved, blockSize, solidSize, memoryMb, entries) { _wideSizes = wideSizes };
  }

  private bool _wideSizes;

  private static (List<GeaEntry> Entries, long Packed)? ReadTable(byte[] info, bool wide) {
    var record = wide ? 30 : 22;
    var entries = new List<GeaEntry>();
    var folder = "";
    long logical = 0;
    long q = 0;
    try {
      while (q < info.Length) {
        if (q + record > info.Length)
          return null;
        var r = info.AsSpan((int)q);
        var fileFlags = BinaryPrimitives.ReadUInt16LittleEndian(r);
        var time = BinaryPrimitives.ReadInt64LittleEndian(r[2..]);
        long size = wide ? BinaryPrimitives.ReadInt64LittleEndian(r[10..]) : BinaryPrimitives.ReadUInt32LittleEndian(r[10..]);
        long compressed = wide ? BinaryPrimitives.ReadInt64LittleEndian(r[18..]) : BinaryPrimitives.ReadUInt32LittleEndian(r[14..]);
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(r[(wide ? 26 : 18)..]);
        if (size < 0 || compressed < 0 || size > int.MaxValue)
          return null;
        q += record;
        if ((fileFlags & FlagAttribute) != 0)
          q += 4;
        if ((fileFlags & FlagVersion) != 0)
          q += 8;
        if ((fileFlags & FlagGroup) != 0)
          q += 4;
        var protect = false;
        if ((fileFlags & FlagProtect) != 0)
          protect = U32(info, ref q) != 0;
        var name = CString(info, ref q);
        if ((fileFlags & FlagFolder) != 0)
          folder = CString(info, ref q);
        var path = string.Join('/', (folder.Length == 0 ? name : folder + "\\" + name).Split('\\', '/').Where(s => s.Length > 0 && s != "." && s != ".."));
        DateTime? modified = null;
        if (time > 0 && time < DateTime.MaxValue.ToFileTimeUtc())
          modified = DateTime.FromFileTimeUtc(time);
        entries.Add(new GeaEntry(path, size, compressed, crc, modified, (fileFlags & FlagSolid) != 0, protect) { Offset = logical });
        logical += compressed;
      }
    } catch (InvalidDataException) {
      return null;
    }
    return (entries, logical);
  }

  /// <summary>Unpacks every file in order (solid files need the ones before them).</summary>
  /// <exception cref="InvalidDataException">A block does not decode, or a file misses its CRC-32.</exception>
  /// <exception cref="NotSupportedException">A password-protected file.</exception>
  public IEnumerable<(GeaEntry Entry, byte[] Data)> ExtractAll() {
    var window = new Window(this._blockSize, this._solidSize);
    foreach (var entry in this.Entries)
      yield return (entry, this.Decode(entry, window));
  }

  /// <summary>Unpacks one file, decoding the solid files before it as needed.</summary>
  public byte[] Extract(GeaEntry entry) {
    var index = this.Entries.ToList().IndexOf(entry);
    if (index < 0)
      throw new ArgumentException("not a file of this archive", nameof(entry));
    var first = index;
    while (first > 0 && this.Entries[first].IsSolid)
      --first;
    var window = new Window(this._blockSize, this._solidSize);
    for (var i = first; i < index; ++i)
      this.Decode(this.Entries[i], window);
    return this.Decode(entry, window);
  }

  /// <summary>The LZGE output buffer that solid blocks continue.</summary>
  private sealed class Window(int blockSize, int solidSize) {
    public readonly byte[] Buffer = new byte[blockSize + solidSize + 64];
    public int Length;
  }

  private byte[] Decode(GeaEntry entry, Window window) {
    if (entry.IsProtected)
      throw new NotSupportedException($"{entry.Path} is password-protected");
    var output = new byte[entry.Size];
    var written = 0;
    var next = entry.Offset;
    int order = 0;
    long blockLeft = 0;
    var crc = new Crc32();
    while (written < output.Length) {
      var solidOffset = 0;
      if (blockLeft == 0) {
        var headerSize = this._wideSizes ? WideBlockHeaderSize : BlockHeaderSize;
        var head = this.Read(next, headerSize);
        order = head[0] & 0x7F;
        blockLeft = this._wideSizes ? BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(1)) : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
        next += headerSize;
        if (blockLeft <= 0 || blockLeft > this._data.Length)
          throw new InvalidDataException($"{entry.Path}: GEA block size out of range");
        var method = order >> 4;
        var level = (order & 0x0F) + 1;
        if (method != LzgeMethod || level > 1)
          window.Length = 0;
        else {
          if (window.Length + Math.Min(output.Length - written, this._blockSize) > this._blockSize + this._solidSize) {
            var drop = window.Length - this._solidSize;
            System.Buffer.BlockCopy(window.Buffer, drop, window.Buffer, 0, this._solidSize);
            window.Length = this._solidSize;
          }
          solidOffset = window.Length;
        }
      }
      var inSize = (int)Math.Min(this._blockSize, blockLeft);
      var outSize = Math.Min(this._blockSize, output.Length - written);
      var input = this.Read(next, inSize);
      next += inSize;
      ReadOnlySpan<byte> produced;
      switch (order >> 4) {
        case Store:
          if (inSize < outSize)
            throw new InvalidDataException($"{entry.Path}: stored GEA block shorter than its data");
          produced = input.AsSpan(0, outSize);
          break;
        case LzgeMethod:
          Lzge.Decode(input, window.Buffer, solidOffset, solidOffset + outSize);
          window.Length = solidOffset + outSize;
          produced = window.Buffer.AsSpan(solidOffset, outSize);
          break;
        case PpmdMethod:
          produced = DecodePpmd(input, outSize, (order & 0x0F) + 1, this._memoryMb);
          break;
        default:
          throw new InvalidDataException($"{entry.Path}: unknown GEA block method {order >> 4}");
      }
      crc.Update(produced);
      produced.CopyTo(output.AsSpan(written));
      written += outSize;
      blockLeft -= inSize;
    }
    if (entry.Crc != 0 && ~crc.Value != entry.Crc)
      throw new InvalidDataException($"{entry.Path} does not match its CRC-32");
    return output;
  }

  private static byte[] DecodePpmd(byte[] input, int size, int order, int memoryMb) {
    var position = 0;
    var rc = new RarPpmdRangeDecoder(() => position < input.Length ? input[position++] : (byte)0);
    var model = new Ppmd7();
    model.Allocate((uint)Math.Max(1, memoryMb) << 20);
    model.Initialize(order);
    var output = new byte[size];
    for (var i = 0; i < size; ++i) {
      var symbol = model.DecodeSymbol(rc);
      if (symbol < 0)
        throw new InvalidDataException("GEA PPMd block ends before its data");
      output[i] = (byte)symbol;
    }
    return output;
  }

  /// <summary>Reads logical archive bytes: the volume's data, then the data moved to the head.</summary>
  private byte[] Read(long logical, int count) {
    var result = new byte[count];
    for (var done = 0; done < count;) {
      var at = logical + done;
      if (at >= this._volumeSize) {
        var movedAt = at - this._volumeSize;
        if (movedAt + (count - done) > this._moved.Length)
          throw new InvalidDataException("GEA data runs past the archive");
        this._moved.AsSpan((int)movedAt, count - done).CopyTo(result.AsSpan(done));
        break;
      }
      var take = (int)Math.Min(count - done, this._volumeSize - at);
      this._data.AsSpan((int)(this._dataStart + at), take).CopyTo(result.AsSpan(done));
      done += take;
    }
    return result;
  }

  private static uint U32(byte[] data, ref long p) {
    if (p + 4 > data.Length)
      throw new InvalidDataException("GEA file table ends inside a record");
    var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)p));
    p += 4;
    return v;
  }

  private static string CString(byte[] data, ref long p) {
    var end = Array.IndexOf(data, (byte)0, (int)p);
    if (end < 0)
      throw new InvalidDataException("GEA string runs past its table");
    var s = Ansi.GetString(data, (int)p, end - (int)p);
    p = end + 1;
    return s;
  }
}
