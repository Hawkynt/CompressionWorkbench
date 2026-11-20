using System.Buffers.Binary;

namespace FileFormat.NowCompress;

/// <summary>One file or folder to put into a Now Compress archive.</summary>
/// <param name="Path">The path inside the archive, '/'-separated; missing parent folders are created.</param>
/// <param name="IsDirectory">Whether this is a folder.</param>
/// <param name="Data">The data fork (ignored for a folder).</param>
/// <param name="Resource">The resource fork (ignored for a folder).</param>
/// <param name="LastModified">The modification time; stored as the creation and modification time.</param>
public sealed record NowCompressInput(string Path, bool IsDirectory = false, byte[]? Data = null, byte[]? Resource = null, DateTime? LastModified = null);

/// <summary>
/// Writes Now Compress archives (docs/NOW-COMPRESS-ON-DISK.md): the directory of 110-byte records,
/// then one stream per file holding its resource fork and its data fork in 32 KiB blocks, each block
/// stored or packed with the original LZSS, whichever is shorter.
/// </summary>
public static class NowCompressWriter {
  internal const int RecordSize = 110;
  internal const int HeaderSize = 24;
  internal const int BlockSize = 0x8000;
  internal const ushort FlagFolder = 0x10;
  internal const ushort BlockStored = 0x00;
  internal const ushort BlockLzss = 0x10;
  internal const int MaxNameBytes = 31;
  private const int LzssWindow = 0x1FFF;   // 13-bit offsets
  private const int LzssMinMatch = 3;
  private const int LzssMaxShort = 9;      // 3 bits, length 1..7 + 2
  private const int LzssMaxMatch = 257;    // extra byte + 2

  private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  private sealed class Node(string name, bool isDirectory) {
    public string Name { get; } = name;
    public bool IsDirectory { get; } = isDirectory;
    public List<Node> Children { get; } = [];
    public byte[] Data { get; set; } = [];
    public byte[] Resource { get; set; } = [];
    public DateTime? LastModified { get; set; }
  }

  /// <summary>Whether a path component can be stored: 1 to 31 Mac OS Roman bytes, none of them a control character or ':'.</summary>
  public static bool IsRepresentableName(string name) => TryEncodeName(name, out _);

  /// <summary>Writes an archive holding <paramref name="items"/>, in the given order within each folder.</summary>
  /// <param name="output">The stream to write to.</param>
  /// <param name="items">The files and folders.</param>
  /// <param name="compress">Whether to try LZSS on each block; when false every block is stored.</param>
  /// <exception cref="ArgumentException">A name cannot be stored, a path is used twice, or a file is used as a folder.</exception>
  /// <exception cref="NotSupportedException">The archive would exceed what the format addresses.</exception>
  public static void Write(Stream output, IEnumerable<NowCompressInput> items, bool compress = true) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(items);
    var root = new Node("", true);
    foreach (var item in items)
      Add(root, item);

    var records = new List<(Node Node, int Children)>();
    Flatten(root, records);
    if (records.Count == 0)
      throw new ArgumentException("A Now Compress archive needs at least one entry.", nameof(items));
    if (records.Count > 0xFFFF)
      throw new NotSupportedException("A Now Compress archive holds at most 65535 entries.");
    CheckEmptyFiles(records);

    var header = new byte[HeaderSize];
    header[1] = 0x02;
    header[3] = 0x60;
    header[5] = 0x01;
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)records.Count);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), ByteSum(header.AsSpan(0, 20)));

    var directory = new byte[records.Count * RecordSize];
    var streams = new MemoryStream();
    long position = HeaderSize + directory.Length;
    for (var i = 0; i < records.Count; ++i) {
      var (node, children) = records[i];
      var record = directory.AsSpan(i * RecordSize, RecordSize);
      TryEncodeName(node.Name, out var name);
      record[0] = (byte)name.Length;
      name.CopyTo(record[1..]);
      var stamp = MacTime(node.LastModified);
      BinaryPrimitives.WriteUInt32BigEndian(record[54..], stamp); // created
      BinaryPrimitives.WriteUInt32BigEndian(record[58..], stamp); // modified
      if (node.IsDirectory) {
        BinaryPrimitives.WriteUInt16BigEndian(record[36..], FlagFolder);
        BinaryPrimitives.WriteUInt16BigEndian(record[82..], (ushort)children);
      } else {
        var stream = EncodeStream(node.Resource, node.Data, position, compress);
        if (position + stream.Length > uint.MaxValue)
          throw new NotSupportedException("A Now Compress archive is limited to 4 GiB.");
        BinaryPrimitives.WriteUInt32BigEndian(record[86..], (uint)node.Data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(record[90..], (uint)node.Resource.Length);
        BinaryPrimitives.WriteUInt32BigEndian(record[98..], (uint)position);
        BinaryPrimitives.WriteUInt32BigEndian(record[102..], (uint)(position + stream.Length));
        streams.Write(stream);
        position += stream.Length;
      }
      BinaryPrimitives.WriteUInt32BigEndian(record[106..], ByteSum(record[..106]));
    }

    output.Write(header);
    output.Write(directory);
    streams.Position = 0;
    streams.CopyTo(output);
  }

  private static void Add(Node root, NowCompressInput item) {
    var parts = item.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
      throw new ArgumentException($"Empty path '{item.Path}'.", nameof(item));
    var folder = root;
    for (var i = 0; i < parts.Length; ++i) {
      var part = parts[i];
      if (!IsRepresentableName(part))
        throw new ArgumentException($"'{part}' cannot be a Now Compress name: it needs 1 to 31 Mac OS Roman characters, no control characters and no ':'.", nameof(item));
      var last = i == parts.Length - 1;
      var existing = folder.Children.Find(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
      if (!last) {
        if (existing == null)
          folder.Children.Add(existing = new Node(part, true));
        else if (!existing.IsDirectory)
          throw new ArgumentException($"'{part}' in '{item.Path}' is a file, not a folder.", nameof(item));
        folder = existing;
        continue;
      }

      if (existing != null) {
        if (existing.IsDirectory && item.IsDirectory) {
          existing.LastModified ??= item.LastModified;
          return;
        }
        throw new ArgumentException($"'{item.Path}' is added twice.", nameof(item));
      }
      folder.Children.Add(new Node(part, item.IsDirectory) {
        Data = item.IsDirectory ? [] : item.Data ?? [],
        Resource = item.IsDirectory ? [] : item.Resource ?? [],
        LastModified = item.LastModified,
      });
    }
  }

  private static bool IsEmptyFile(Node node) => !node.IsDirectory && node.Data.Length == 0 && node.Resource.Length == 0;

  // A folder's record is followed by its children's records, depth first. Empty files go last in
  // their folder (see CheckEmptyFiles).
  private static void Flatten(Node folder, List<(Node, int)> records) {
    foreach (var child in folder.Children.Where(c => !IsEmptyFile(c)).Concat(folder.Children.Where(IsEmptyFile))) {
      records.Add((child, child.Children.Count));
      if (child.IsDirectory)
        Flatten(child, records);
    }
  }

  /// <summary>
  /// A file without data has a stream of no blocks. Readers take the streams as one sequence of
  /// blocks, and unar, the one reader available as a reference, repeats an earlier block when it
  /// meets a stream without blocks. An empty file is therefore written only where no file with
  /// content follows it.
  /// </summary>
  private static void CheckEmptyFiles(List<(Node Node, int Children)> records) {
    string? empty = null;
    foreach (var (node, _) in records) {
      if (node.IsDirectory)
        continue;
      if (IsEmptyFile(node))
        empty ??= node.Name;
      else if (empty != null)
        throw new NotSupportedException(
          $"The empty file '{empty}' would come before '{node.Name}', which has content; a Now Compress archive can hold empty files only after every file with content (put them in the last folder).");
    }
  }

  private static bool TryEncodeName(string name, out byte[] bytes) {
    bytes = [];
    if (string.IsNullOrEmpty(name) || name.Length > MaxNameBytes)
      return false;
    var result = new byte[name.Length];
    for (var i = 0; i < name.Length; ++i) {
      var c = name[i];
      if (c < 0x20 || c == ':' || c == 0x7F)
        return false;
      if (c < 0x80) {
        result[i] = (byte)c;
        continue;
      }
      var index = NowCompressArchive.MacRomanHigh.IndexOf(c);
      if (index < 0)
        return false;
      result[i] = (byte)(0x80 + index);
    }
    bytes = result;
    return true;
  }

  private static uint MacTime(DateTime? time) {
    if (time is not { } t)
      return 0;
    var seconds = (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t).Subtract(MacEpoch).TotalSeconds;
    return seconds <= 0 ? 0 : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }

  internal static uint ByteSum(ReadOnlySpan<byte> bytes) {
    uint sum = 0;
    foreach (var b in bytes)
      sum += b;
    return sum;
  }

  private static uint Sum32(uint v) => (v >> 24) + ((v >> 16) & 0xFF) + ((v >> 8) & 0xFF) + (v & 0xFF);
  private static uint Sum16(int v) => (uint)((v >> 8) + (v & 0xFF));

  /// <summary>
  /// One file's stream: the block table, then the blocks. The resource fork's blocks come first, then
  /// an entry that repeats the previous end offset and so starts the data fork.
  /// </summary>
  private static byte[] EncodeStream(byte[] resource, byte[] data, long start, bool compress) {
    var blocks = new List<(ushort Flags, byte[] Payload)>();
    var resourceBlocks = 0;
    foreach (var (fork, isResource) in new[] { (resource, true), (data, false) })
      for (var offset = 0; offset < fork.Length; offset += BlockSize) {
        blocks.Add(EncodeBlock(fork.AsSpan(offset, Math.Min(BlockSize, fork.Length - offset)), compress));
        if (isResource)
          ++resourceBlocks;
      }

    var entries = blocks.Count + 1; // plus the entry that starts the data fork
    var tableLength = 4 + entries * 8 + 8;
    var first = (uint)(start + tableLength);
    var table = new List<(ushort Flags, ushort Padding, uint Next)>();
    var body = new MemoryStream();
    var next = first;
    Span<byte> sum = stackalloc byte[4];
    for (var i = 0; i <= blocks.Count; ++i) {
      if (i == resourceBlocks)
        table.Add(table.Count > 0 ? table[^1] with { Next = next } : ((ushort)0, (ushort)0, next));
      if (i == blocks.Count)
        break;
      var (flags, payload) = blocks[i];
      var padding = (ushort)(payload.Length & 1);
      body.Write(payload);
      if (padding != 0)
        body.WriteByte(0);
      BinaryPrimitives.WriteUInt32BigEndian(sum, ByteSum(payload));
      body.Write(sum);
      next += (uint)(payload.Length + padding + 4);
      table.Add((flags, padding, next));
    }

    var stream = new byte[tableLength + body.Length];
    BinaryPrimitives.WriteUInt32BigEndian(stream, first);
    var checksum = Sum32(first);
    var p = 4;
    foreach (var (flags, padding, nextOffset) in table) {
      BinaryPrimitives.WriteUInt16BigEndian(stream.AsSpan(p), flags);
      BinaryPrimitives.WriteUInt16BigEndian(stream.AsSpan(p + 2), padding);
      BinaryPrimitives.WriteUInt32BigEndian(stream.AsSpan(p + 4), nextOffset);
      checksum += Sum16(flags) + Sum16(padding) + Sum32(nextOffset);
      p += 8;
    }
    // The four bytes before the checksum repeat the last entry's flags and padding.
    BinaryPrimitives.WriteUInt16BigEndian(stream.AsSpan(p), table[^1].Flags);
    BinaryPrimitives.WriteUInt16BigEndian(stream.AsSpan(p + 2), table[^1].Padding);
    checksum += Sum16(table[^1].Flags) + Sum16(table[^1].Padding);
    BinaryPrimitives.WriteUInt32BigEndian(stream.AsSpan(p + 4), checksum);
    body.ToArray().CopyTo(stream, tableLength);
    return stream;
  }

  private static (ushort Flags, byte[] Payload) EncodeBlock(ReadOnlySpan<byte> block, bool compress) {
    if (compress) {
      var packed = Lzss(block);
      if (packed != null)
        return (BlockLzss, packed);
    }
    return (BlockStored, block.ToArray());
  }

  /// <summary>
  /// Packs one block with the original LZSS: a 2-byte prefix, then groups of eight items under a flag
  /// byte (MSB first, 1 = literal). A match is two bytes, 13 bits of distance and 3 of length − 2,
  /// with a length field of 0 taking the length − 2 from a third byte. Returns null when not shorter.
  /// </summary>
  internal static byte[]? Lzss(ReadOnlySpan<byte> block) {
    var output = new byte[block.Length + block.Length / 8 + 8];
    output[0] = 0x00;
    output[1] = 0x03; // constant in every measured block; decoders skip both bytes
    var o = 2;
    var flagAt = -1;
    var flagBit = 0;
    const int hashSize = 1 << 14;
    Span<int> head = stackalloc int[hashSize];
    head.Fill(-1);
    var previous = new int[block.Length];

    var i = 0;
    while (i < block.Length) {
      if (flagBit == 0) {
        flagAt = o++;
        output[flagAt] = 0;
        flagBit = 0x80;
      }

      var bestLength = 0;
      var bestDistance = 0;
      if (i + LzssMinMatch <= block.Length) {
        var h = Hash(block, i);
        var limit = Math.Min(LzssMaxMatch, block.Length - i);
        var chain = 256;
        for (var candidate = head[h]; candidate >= 0 && i - candidate <= LzssWindow && chain-- > 0; candidate = previous[candidate]) {
          var length = 0;
          while (length < limit && block[candidate + length] == block[i + length])
            ++length;
          if (length > bestLength) {
            bestLength = length;
            bestDistance = i - candidate;
            if (length == limit)
              break;
          }
        }
      }

      var step = bestLength >= LzssMinMatch ? bestLength : 1;
      if (step == 1) {
        output[flagAt] |= (byte)flagBit;
        output[o++] = block[i];
      } else {
        output[o++] = (byte)(((bestDistance >> 5) & 0xF8) | (bestLength <= LzssMaxShort ? bestLength - 2 : 0));
        output[o++] = (byte)bestDistance;
        if (bestLength > LzssMaxShort)
          output[o++] = (byte)(bestLength - 2);
      }
      flagBit >>= 1;

      for (var k = 0; k < step; ++k, ++i)
        if (i + LzssMinMatch <= block.Length) {
          var h = Hash(block, i);
          previous[i] = head[h];
          head[h] = i;
        }

      if (o + 4 >= block.Length)
        return null;
    }
    return output.AsSpan(0, o).ToArray();
  }

  private static int Hash(ReadOnlySpan<byte> b, int i) => ((b[i] << 6) ^ (b[i + 1] << 3) ^ b[i + 2]) & 0x3FFF;
}
