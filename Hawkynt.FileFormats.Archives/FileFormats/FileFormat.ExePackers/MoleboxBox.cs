#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>One file of a MoleBox 2.x file box.</summary>
/// <param name="Name">Path inside the box, <c>/</c>-separated.</param>
/// <param name="DataOffset">File offset of the first block (the block-length table sits right before it).</param>
/// <param name="Size">Original size.</param>
/// <param name="Flags">Item flags: bit 0 zlib blocks, bit 1 IDEA-encrypted blocks.</param>
/// <param name="IsMainExecutable">The copy of the boxed application's own executable the box header points at.</param>
public sealed record MoleboxBoxEntry(string Name, long DataOffset, long Size, uint Flags, bool IsMainExecutable);

/// <summary>
/// The file box MoleBox 2.x appends to a packed executable (layout in
/// <c>docs/MOLEBOX-ON-DISK.md</c>). The box key is the section key of the loader chain
/// <see cref="MoleboxExecutablePackerHandler"/> replays; every file is read in 64 KiB blocks,
/// each a zlib stream (Adler-32 checked) or stored, and IDEA-ECB encrypted over its whole
/// 8-byte blocks. The directory is checked against the MD5 the trailer stores.
/// </summary>
public sealed class MoleboxBox {
  private const int TrailerSize = 24;
  private const int HeaderSize = 32;
  private const int SlotSize = 12;
  private const int BlockSize = 0x10000;
  private const uint StoredBlock = 0x80000000;
  private const uint FlagCompressed = 1;
  private const uint FlagEncrypted = 2;
  private const uint KnownFlags = 0x3F;

  private readonly byte[] _file;
  private readonly ushort[] _schedule;

  /// <summary>Name the loader gives the box (e.g. <c>Game.exe.BOX</c>).</summary>
  public string BoxName { get; }

  /// <summary>The files, the boxed main executable (if any) first.</summary>
  public IReadOnlyList<MoleboxBoxEntry> Entries { get; }

  private MoleboxBox(byte[] file, ushort[] schedule, string boxName, IReadOnlyList<MoleboxBoxEntry> entries) {
    this._file = file;
    this._schedule = schedule;
    this.BoxName = boxName;
    this.Entries = entries;
  }

  /// <summary>
  /// Cheap structural test of the trailer and box header, without any key: the trailer's length
  /// field equals the file length and the directory lies inside the file.
  /// </summary>
  public static bool HasBoxTrailer(Stream stream) {
    var length = stream.Length;
    if (length < TrailerSize + HeaderSize || length > uint.MaxValue)
      return false;
    Span<byte> tail = stackalloc byte[TrailerSize + HeaderSize];
    stream.Position = length - tail.Length;
    stream.ReadExactly(tail);
    return TryReadHeader(tail, length, out _);
  }

  private readonly record struct Header(uint MainOffset, uint MainSize, uint MainFlags, uint DirectoryLength, uint DirectoryOffset, uint NamesLength, uint Count);

  private static bool TryReadHeader(ReadOnlySpan<byte> tail, long fileLength, out Header header) {
    header = default;
    var h = tail[^(TrailerSize + HeaderSize)..];
    if (BinaryPrimitives.ReadUInt32LittleEndian(h[HeaderSize..]) != fileLength)
      return false;
    var u = new uint[8];
    for (var i = 0; i < u.Length; ++i)
      u[i] = BinaryPrimitives.ReadUInt32LittleEndian(h[(4 * i)..]);
    var storedLength = u[3];
    header = new(u[0], u[1], u[2], u[4], u[5], u[6], u[7]);
    // The directory ends right before the header: before the program-copy slot in builds that
    // have one, or before the directory fields themselves in builds without it.
    var directoryEnd = (long)header.DirectoryOffset + header.DirectoryLength;
    var withSlot = directoryEnd == fileLength - TrailerSize - HeaderSize;
    if (!withSlot && directoryEnd != fileLength - TrailerSize - (HeaderSize - SlotSize))
      return false;
    if (!withSlot)
      header = header with { MainOffset = 0, MainSize = 0, MainFlags = 0 };
    return storedLength == header.DirectoryLength
      && header.DirectoryLength > 0 && header.DirectoryLength % 8 == 0
      && header.Count <= 1_000_000
      && (long)header.NamesLength + 16L * header.Count <= header.DirectoryLength;
  }

  /// <summary>Reads the box of a MoleBox 2.x packed executable; null when it carries none this reader knows.</summary>
  public static MoleboxBox? TryOpen(byte[] file) {
    if (file.Length < TrailerSize + HeaderSize || !TryReadHeader(file, file.Length, out var header))
      return null;
    var info = ExecutableContainerParsers.ParseBestEffort(file);
    if (!MoleboxExecutablePackerHandler.TryReadBoxKeyBlob(file, info, new UnpackOptions(), out var keyBlob))
      return null;
    var schedule = MoleboxIdea.InvertKey(MoleboxIdea.ExpandKey(keyBlob.AsSpan(0x20, 16)));
    var nameField = keyBlob.AsSpan(0x30, 0x40);
    var nameEnd = nameField.IndexOf((byte)0);
    var boxName = Encoding.Latin1.GetString(nameEnd < 0 ? nameField : nameField[..nameEnd]);

    var directory = MoleboxIdea.ProcessEcb(file.AsSpan((int)header.DirectoryOffset, (int)header.DirectoryLength), schedule);
    // The trailer's 16 bytes are the MD5 of the decrypted directory: a wrong key or a damaged
    // directory stops here.
    if (!Md5.Compute(directory).AsSpan().SequenceEqual(file.AsSpan(file.Length - 20, 16)))
      return null;

    var dataStart = OverlayStart(info);
    var entries = new List<MoleboxBoxEntry>();
    var names = directory.AsSpan(0, (int)header.NamesLength);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < header.Count; ++i) {
      var record = directory.AsSpan((int)header.NamesLength + 16 * i, 16);
      var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(record);
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
      if (nameOffset >= names.Length)
        throw new InvalidDataException("MoleBox box: name offset outside the name table.");
      var end = names[(int)nameOffset..].IndexOf((byte)0);
      if (end <= 0)
        throw new InvalidDataException("MoleBox box: empty or unterminated name.");
      var name = Encoding.Latin1.GetString(names.Slice((int)nameOffset, end)).Replace('\\', '/');
      if (!IsSafePath(name) || !seen.Add(name))
        throw new InvalidDataException($"MoleBox box: unsafe or duplicate path '{name}'.");
      if (!IsPlausible(offset, size, flags, dataStart, header.DirectoryOffset, file.Length))
        throw new InvalidDataException($"MoleBox box: '{name}' points outside the box.");
      entries.Add(new(name, offset, size, flags, false));
    }

    // The header's first slot names a copy of the boxed program itself; packers that leave it
    // unused fill it with arbitrary bytes, so it counts only when it is consistent.
    if (header.MainSize > 0 && IsPlausible(header.MainOffset, header.MainSize, header.MainFlags, dataStart, header.DirectoryOffset, file.Length)) {
      var mainName = MainName(boxName);
      if (!seen.Contains(mainName))
        entries.Insert(0, new(mainName, header.MainOffset, header.MainSize, header.MainFlags, true));
    }
    return new(file, schedule, boxName, entries);
  }

  /// <summary>Bytes the file occupies in the box (its blocks, without the length table).</summary>
  public long StoredLength(MoleboxBoxEntry entry) {
    var blocks = (int)((entry.Size + BlockSize - 1) / BlockSize);
    var tableOffset = entry.DataOffset - 4L * blocks;
    var total = 0L;
    for (var b = 0; b < blocks; ++b)
      total += BinaryPrimitives.ReadUInt32LittleEndian(this._file.AsSpan((int)(tableOffset + 4 * b))) & ~StoredBlock;
    return total;
  }

  /// <summary>Decodes one file, checking every block's Adler-32 and length.</summary>
  public byte[] Extract(MoleboxBoxEntry entry) {
    var blocks = (int)((entry.Size + BlockSize - 1) / BlockSize);
    var tableOffset = entry.DataOffset - 4L * blocks;
    if (tableOffset < 0)
      throw new InvalidDataException($"MoleBox box: block table of '{entry.Name}' starts before the file.");
    var output = new byte[entry.Size];
    var position = entry.DataOffset;
    for (var b = 0; b < blocks; ++b) {
      var word = BinaryPrimitives.ReadUInt32LittleEndian(this._file.AsSpan((int)(tableOffset + 4 * b)));
      var stored = (word & StoredBlock) != 0;
      var length = word & ~StoredBlock;
      var expected = (int)Math.Min(BlockSize, entry.Size - (long)b * BlockSize);
      if (position + length > this._file.Length)
        throw new InvalidDataException($"MoleBox box: block {b} of '{entry.Name}' runs past the file.");
      var raw = this._file.AsSpan((int)position, (int)length);
      var plain = (entry.Flags & FlagEncrypted) != 0 ? MoleboxIdea.ProcessEcb(raw, this._schedule) : raw.ToArray();
      var data = (entry.Flags & FlagCompressed) != 0 && !stored ? Inflate(plain, expected, entry.Name, b) : plain;
      if (data.Length != expected)
        throw new InvalidDataException($"MoleBox box: block {b} of '{entry.Name}' holds {data.Length} bytes, {expected} expected.");
      data.CopyTo(output, (long)b * BlockSize);
      position += length;
    }
    return output;
  }

  private static byte[] Inflate(byte[] block, int expected, string name, int index) {
    if (block.Length < 6 || (block[0] & 0x0F) != 8 || ((block[0] << 8) | block[1]) % 31 != 0 || (block[1] & 0x20) != 0)
      throw new InvalidDataException($"MoleBox box: block {index} of '{name}' is not a zlib stream.");
    byte[] data;
    try {
      data = DeflateDecompressor.Decompress(block.AsSpan(2, block.Length - 6));
    } catch (Exception ex) when (ex is not InvalidDataException and (IndexOutOfRangeException or ArgumentException or OverflowException or EndOfStreamException)) {
      throw new InvalidDataException($"MoleBox box: block {index} of '{name}' is damaged.", ex);
    }
    if (data.Length != expected || Adler32.Compute(data) != BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(block.Length - 4)))
      throw new InvalidDataException($"MoleBox box: block {index} of '{name}' fails its Adler-32.");
    return data;
  }

  private static bool IsPlausible(uint offset, uint size, uint flags, long dataStart, uint directoryOffset, long fileLength) {
    if ((flags & ~KnownFlags) != 0 || offset >= directoryOffset || offset < dataStart)
      return false;
    var blocks = ((long)size + BlockSize - 1) / BlockSize;
    return offset - 4 * blocks >= dataStart && offset < fileLength;
  }

  private static long OverlayStart(ExecutableImageInfo info) {
    var end = 0UL;
    foreach (var region in info.Regions)
      end = Math.Max(end, region.FileOffset + region.FileSize);
    return (long)end;
  }

  private static string MainName(string boxName) {
    var name = boxName.EndsWith(".BOX", StringComparison.OrdinalIgnoreCase) ? boxName[..^4] : boxName;
    name = Path.GetFileName(name.Replace('\\', '/'));
    if (name.Length == 0)
      name = "main";
    return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
  }

  private static bool IsSafePath(string path) {
    if (path.Length == 0 || path.StartsWith('/') || path.Contains(':'))
      return false;
    foreach (var part in path.Split('/'))
      if (part is "" or "." or "..")
        return false;
    return true;
  }
}
