using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Aplib;

namespace FileFormat.EnigmaVirtualBox;

/// <summary>A file or folder of an Enigma Virtual Box filesystem.</summary>
/// <param name="FileName">Its path, "/"-separated; the "%DEFAULT FOLDER%" root (the folder of the boxed exe) is the empty prefix.</param>
/// <param name="IsDirectory">True for a folder.</param>
/// <param name="Offset">Where its stored data starts in the file.</param>
/// <param name="StoredSize">Its size as stored (the aPLib chunk table and chunks when compressed).</param>
/// <param name="Size">Its original size.</param>
public sealed record EnigmaVirtualBoxEntry(string FileName, bool IsDirectory, long Offset, long StoredSize, long Size) {
  /// <summary>True when the packer compressed it (stored and original sizes differ).</summary>
  public bool IsCompressed => !this.IsDirectory && this.StoredSize != this.Size;
}

/// <summary>
/// Reads the virtual filesystem Enigma Virtual Box embeds in the executables it boxes: the
/// file table after the "EVB\0" header in the ".enigma1" section, and each file's data, stored
/// or in aPLib-compressed chunks.
/// </summary>
/// <remarks>
/// <para>Converted from evbunpack (mos9527, Apache-2.0, revision
/// ee7b647acf2a46617b0c448dfa83d63132ea917b): its two table walkers — the current layout,
/// where the table comes first and the data after it, and the legacy one of EVB 7.80 and
/// earlier, where each file's data follows its node — and its chunked-aPLib decoding. Where
/// evbunpack has to be told the layout, it is recognised here from the root node, and the
/// table is walked by its folder counts instead of being cut off by a running total.</para>
/// <para>Checked against evbunpack's own extraction of boxes made by EVB 7.80, 9.70, 11.00 and
/// 11.30. Registry virtualisation and external ".dat" packages are not read.</para>
/// </remarks>
public sealed class EnigmaVirtualBoxReader {
  private const byte NodeFolder = 3;
  private const byte NodeFile = 2;
  private const int HeaderSize = 64;
  private const int FolderTail = 25;     // a byte, then three timestamps
  private const int FileTail = 53;       // current layout: sizes, timestamps, attributes, offset
  private const int LegacyFileTail = 49; // legacy layout: the last 49 bytes of a file node
  private const int ChunkSize = 0x10000;

  private readonly Stream _stream;
  private readonly List<EnigmaVirtualBoxEntry> _entries = [];

  /// <summary>The section the filesystem lives in.</summary>
  public const string SectionName = ".enigma1";

  /// <summary>Gets the file offset of the "EVB\0" header.</summary>
  public long HeaderOffset { get; }

  /// <summary>Gets whether the box uses the legacy layout of EVB 7.80 and earlier.</summary>
  public bool IsLegacyLayout { get; }

  /// <summary>Gets the files and folders, parents before their children.</summary>
  public IReadOnlyList<EnigmaVirtualBoxEntry> Entries => this._entries;

  /// <summary>Opens a boxed executable.</summary>
  /// <exception cref="InvalidDataException">No ".enigma1" section with an "EVB\0" header, or a table that does not parse.</exception>
  public EnigmaVirtualBoxReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._stream = stream;
    this.HeaderOffset = FindHeader(stream) ?? throw new InvalidDataException("No Enigma Virtual Box header in a \".enigma1\" section.");

    var main = this.Read(this.HeaderOffset + HeaderSize, 16);
    var tableSize = BinaryPrimitives.ReadUInt32LittleEndian(main);
    var topLevel = BinaryPrimitives.ReadInt32LittleEndian(main.AsSpan(12));
    // The legacy root is itself a node — header, empty name, type 0 — sized 15 past its size word;
    // the current root's size word is the size of the whole table.
    this.IsLegacyLayout = tableSize == 15;
    if (this.IsLegacyLayout) {
      var cursor = this.HeaderOffset + HeaderSize + 4 + tableSize;
      for (var i = 0; i < topLevel; ++i)
        cursor = this.ReadLegacyNode(cursor, "");
    } else {
      var cursor = this.HeaderOffset + HeaderSize + 15;
      var data = this.HeaderOffset + HeaderSize + 4 + tableSize;
      for (var i = 0; i < topLevel; ++i)
        cursor = this.ReadNode(cursor, "", ref data);
    }
  }

  /// <summary>True when the stream is a PE whose ".enigma1" section holds an "EVB\0" header.</summary>
  public static bool IsEnigmaVirtualBox(Stream stream) {
    var pos = stream.Position;
    try {
      return FindHeader(stream) != null;
    } catch (Exception ex) when (ex is IOException or ArgumentException) {
      return false;
    } finally {
      stream.Position = pos;
    }
  }

  /// <summary>Returns a file's contents.</summary>
  /// <exception cref="InvalidDataException">The stored data does not decode to the recorded size.</exception>
  public byte[] Extract(EnigmaVirtualBoxEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      return [];
    if (!entry.IsCompressed)
      return this.Read(entry.Offset, checked((int)entry.Size));

    // A table of 12-byte rows (compressed size, running total, 0), its last row only the size,
    // then the chunks; each chunk inflates to 64 KB but the last.
    var stored = this.Read(entry.Offset, checked((int)entry.StoredSize));
    var tableSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(stored);
    if (tableSize < 12 || tableSize > stored.Length || (tableSize - 8) % 4 != 0)
      throw new InvalidDataException($"{entry.FileName}: bad chunk table.");
    var output = new byte[entry.Size];
    var written = 0;
    var at = tableSize;
    for (var row = 8; row < tableSize; row += 12) {
      var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(row));
      if (length <= 0 || length > stored.Length - at || written >= output.Length)
        throw new InvalidDataException($"{entry.FileName}: chunk {(row - 8) / 12} runs past the stored data.");
      var expected = Math.Min(ChunkSize, output.Length - written);
      var chunk = AplibBuildingBlock.DecompressRaw(stored.AsSpan(at, length), expected);
      if (chunk.Length != expected)
        throw new InvalidDataException($"{entry.FileName}: chunk {(row - 8) / 12} inflates to {chunk.Length} bytes, not {expected}.");
      chunk.CopyTo(output, written);
      written += chunk.Length;
      at += length;
    }
    if (written != output.Length)
      throw new InvalidDataException($"{entry.FileName}: {written} of {output.Length} bytes in the chunks.");
    return output;
  }

  // Current layout: header (size 0, id, depth, child count), UTF-16 name, type, then the tail;
  // a file's data is the next stretch of the data area that follows the table.
  private long ReadNode(long at, string parent, ref long data) {
    var (count, name, type, tail) = this.NodeHead(at, parent);
    switch (type) {
      case NodeFolder: {
        var path = Join(parent, name);
        if (path.Length > 0)
          this._entries.Add(new(path, true, 0, 0, 0));
        var cursor = tail + FolderTail;
        for (var i = 0; i < count; ++i)
          cursor = this.ReadNode(cursor, path, ref data);
        return cursor;
      }
      case NodeFile: {
        var t = this.Read(tail, FileTail);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(2));
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(49));
        this.AddFile(Join(parent, name), data, stored, size);
        data += stored;
        return tail + FileTail;
      }
      default:
        throw new InvalidDataException($"Node '{name}' has unknown type {type}.");
    }
  }

  // Legacy layout: the size word counts the node from the byte after it; a file's data follows it.
  private long ReadLegacyNode(long at, string parent) {
    var size = BinaryPrimitives.ReadUInt32LittleEndian(this.Read(at, 4));
    var (count, name, type, _) = this.NodeHead(at, parent);
    var end = at + 4 + size;
    switch (type) {
      case NodeFolder: {
        var path = Join(parent, name);
        if (path.Length > 0)
          this._entries.Add(new(path, true, 0, 0, 0));
        var cursor = end;
        for (var i = 0; i < count; ++i)
          cursor = this.ReadLegacyNode(cursor, path);
        return cursor;
      }
      case NodeFile: {
        var t = this.Read(end - LegacyFileTail, LegacyFileTail);
        var original = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(2));
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(41));
        this.AddFile(Join(parent, name), end, stored, original);
        return end + stored;
      }
      default:
        throw new InvalidDataException($"Node '{name}' has unknown type {type}.");
    }
  }

  private void AddFile(string path, long offset, long stored, long size) {
    if (offset + stored > this._stream.Length)
      throw new InvalidDataException($"File '{path}' runs past the end of the executable.");
    this._entries.Add(new(path, false, offset, stored, size));
  }

  private (int Count, string Name, byte Type, long Tail) NodeHead(long at, string parent) {
    var head = this.Read(at, 16);
    var count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(12));
    var name = new StringBuilder();
    var cursor = at + 16;
    while (true) {
      var unit = BinaryPrimitives.ReadUInt16LittleEndian(this.Read(cursor, 2));
      cursor += 2;
      if (unit == 0)
        break;
      if (name.Length > 1024)
        throw new InvalidDataException("Node name is not terminated.");
      name.Append((char)unit);
    }
    var type = this.Read(cursor, 1)[0];
    var text = name.ToString();
    if (text is "." or ".." || text.AsSpan().IndexOfAny('\\', '/', ':') >= 0)
      throw new InvalidDataException($"Node name '{text}' under '{parent}' is not a plain name.");
    if (count < 0 || count > 1_000_000)
      throw new InvalidDataException($"Node '{text}' claims {count} children.");
    return (count, text, type, cursor + 1);
  }

  // The root folder of the box is the folder of the boxed executable.
  private static string Join(string parent, string name)
    => name == "%DEFAULT FOLDER%" && parent.Length == 0 ? "" : parent.Length == 0 ? name : parent + "/" + name;

  private byte[] Read(long offset, int count) {
    if (offset < 0 || offset + count > this._stream.Length)
      throw new InvalidDataException("Enigma Virtual Box table runs past the end of the executable.");
    this._stream.Position = offset;
    var buffer = new byte[count];
    this._stream.ReadExactly(buffer);
    return buffer;
  }

  /// <summary>Finds the "EVB\0" header inside the PE's ".enigma1" section.</summary>
  private static long? FindHeader(Stream stream) {
    if (stream.Length < 0x40)
      return null;
    Span<byte> dos = stackalloc byte[0x40];
    stream.Position = 0;
    stream.ReadExactly(dos);
    if (dos[0] != 'M' || dos[1] != 'Z')
      return null;
    var pe = BinaryPrimitives.ReadUInt32LittleEndian(dos[0x3C..]);
    if (pe > stream.Length - 24)
      return null;
    Span<byte> coff = stackalloc byte[24];
    stream.Position = pe;
    stream.ReadExactly(coff);
    if (BinaryPrimitives.ReadUInt32LittleEndian(coff) != 0x00004550)
      return null;
    var sections = BinaryPrimitives.ReadUInt16LittleEndian(coff[6..]);
    var optional = BinaryPrimitives.ReadUInt16LittleEndian(coff[20..]);
    var table = pe + 24L + optional;
    if (table + 40L * sections > stream.Length)
      return null;
    Span<byte> header = stackalloc byte[40];
    for (var i = 0; i < sections; ++i) {
      stream.Position = table + 40L * i;
      stream.ReadExactly(header);
      if (!header[..8].TrimEnd((byte)0).SequenceEqual(".enigma1"u8))
        continue;
      var raw = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
      var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
      var length = (int)Math.Min(Math.Min(rawSize, 0x10000), Math.Max(0, stream.Length - raw));
      if (length < HeaderSize)
        return null;
      var bytes = new byte[length];
      stream.Position = raw;
      stream.ReadExactly(bytes);
      var hit = bytes.AsSpan().IndexOf("EVB\0"u8);
      return hit >= 0 && hit + HeaderSize + 16 <= length ? raw + hit : null;
    }
    return null;
  }
}
