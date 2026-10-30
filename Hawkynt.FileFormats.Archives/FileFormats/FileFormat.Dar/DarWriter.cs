using System.Buffers;
using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Streams;
using FileFormat.Bzip2;
using FileFormat.Xz;
using FileFormat.Zlib;
using FileFormat.Zstd;

namespace FileFormat.Dar;

/// <summary>
/// Writes a DAR archive in format 11.3 (<c>docs/DAR-ON-DISK.md</c>, section 10): one slice or a
/// slice set cut at <c>-s</c>/<c>-S</c> sizes, with or without tape marks, stored or compressed per
/// file. File data streams from its source to the slices, so memory stays bounded whatever the
/// input size; only the catalogue, which grows with the number of entries, is held in memory.
/// </summary>
internal static class DarWriter {
  /// <summary>What an <see cref="Item"/> is.</summary>
  public enum ItemKind { File, Directory, Symlink }

  /// <summary>One item to store. <see cref="Open"/> and <see cref="Length"/> describe a file's data;
  /// <see cref="LinkTarget"/> a symlink's target.</summary>
  public sealed record Item(string Path, ItemKind Kind, ushort Permissions, DateTime Modified, DateTime Accessed) {
    public Func<Stream>? Open { get; init; }
    public long Length { get; init; }
    public string? LinkTarget { get; init; }
    public ulong Uid { get; init; }
    public ulong Gid { get; init; }
    public DateTime? Changed { get; init; }

    /// <summary>A regular file whose content is <paramref name="data"/>.</summary>
    public static Item FromBytes(string path, byte[] data, ushort permissions, DateTime modified, DateTime accessed)
      => new(path, ItemKind.File, permissions, modified, accessed) { Open = () => new MemoryStream(data, writable: false), Length = data.LongLength };
  }

  /// <summary>How to write.</summary>
  /// <param name="Algorithm">Compression letter (<see cref="AlgorithmFor"/>).</param>
  /// <param name="Level">Compression level, or <see langword="null"/> for the codec's default.</param>
  /// <param name="TapeMarks">Whether to write the escape layer dar's sequential reading relies on.</param>
  /// <param name="SliceSize">Size of every slice file, 0 for one slice of any size.</param>
  /// <param name="FirstSliceSize">Size of the first slice when it differs, 0 otherwise.</param>
  /// <param name="BlockSize">Compress in dar's block mode, this many clear bytes per independently
  /// compressed block (dar's <c>-z algo:level:size</c>); 0 for streaming mode.</param>
  public sealed record Settings(char Algorithm = 'n', int? Level = null, bool TapeMarks = false, long SliceSize = 0, long FirstSliceSize = 0, int BlockSize = 0) {
    /// <summary>Files up to this size are compressed in memory first, so they can be stored as-is
    /// when compression would not shrink them; larger ones are compressed on the fly.</summary>
    public int InMemoryLimit { get; init; } = 4 << 20;
  }

  /// <summary>Compression letters this writer produces, by method name.</summary>
  public static char AlgorithmFor(string? method) => (method ?? "stored").Trim().ToLowerInvariant() switch {
    "" or "stored" or "store" or "none" => 'n',
    "gzip" or "zlib" or "deflate" => 'z',
    "bzip2" or "bz2" => 'y',
    "xz" or "lzma2" => 'x',
    "zstd" or "zstandard" => 'd',
    "lz4" => 'q',
    var other => throw new NotSupportedException($"DAR creation does not support the method '{other}'."),
  };

  private const int CopyBuffer = 1 << 20;

  /// <summary>Writes the archive into <paramref name="output"/>; fails if the slicing in
  /// <paramref name="settings"/> needs a second slice.</summary>
  public static void Write(Stream output, IReadOnlyList<Item> items, Settings settings)
    => Write(n => n == 1 ? output : throw new InvalidOperationException("This DAR archive needs more than one slice; give the writer somewhere to put them."),
      items, settings);

  /// <summary>Writes the archive, opening slice <c>n</c> (1-based) through <paramref name="openSlice"/>.</summary>
  public static void Write(Func<int, Stream> openSlice, IReadOnlyList<Item> items, Settings settings) {
    if (settings.BlockSize < 0 || settings.BlockSize > MaxBlockSize)
      throw new ArgumentOutOfRangeException(nameof(settings), $"DAR compression block size must be between 1 and {MaxBlockSize} bytes.");
    if (settings.Algorithm == 'n')
      settings = settings with { BlockSize = 0 };
    else if (settings.BlockSize == 0 && settings.Algorithm is 'x' or 'd'
             && items.Any(i => i.Kind == ItemKind.File && i.Length > SingleUnitLimit))
      // The xz and zstd encoders end their stream or frame every 64 MiB and start another, and
      // dar stops reading a unit at the first end: a file that large goes into blocks instead.
      settings = settings with { BlockSize = AutoBlockSize };
    var root = BuildTree(items);
    var name = NewLabel();
    using var sink = new DarArchiveSink(openSlice, name, settings.SliceSize, settings.FirstSliceSize, settings.TapeMarks);

    var headerFlags = (settings.TapeMarks ? DarArchive.FlagTapeMarks : 0) | (settings.BlockSize > 0 ? DarArchive.FlagCompressionBlockSize : 0);
    var header = new DarVersionHeader(11, 3, settings.Algorithm, "N/A", headerFlags, 0, (ulong)settings.BlockSize);
    sink.Data(Bytes(header.Write));
    var initialOffset = (ulong)sink.Position;
    if (settings.TapeMarks) {
      // Section 6: the data name and the in-place path open the sequential stream.
      sink.Mark((byte)'D');
      sink.Data(name);
      sink.Mark((byte)'P');
      sink.Data(Bytes(s => DarPrimitives.WriteString(s, InPlace)));
    }

    WriteChildren(sink, root, settings);

    var catalogue = new MemoryStream();
    catalogue.Write(name);
    DarPrimitives.WriteString(catalogue, InPlace);
    WriteCatalogueDirectory(catalogue, root, "root");
    DarPrimitives.WriteChecksum(catalogue, DarPrimitives.Checksum(catalogue.GetBuffer().AsSpan(0, (int)catalogue.Length), 4));

    if (settings.TapeMarks)
      sink.Mark((byte)'C');
    var catalogueAt = (ulong)sink.Position;
    var catalogueBytes = catalogue.ToArray();
    sink.Data(settings.Algorithm == 'n' ? catalogueBytes : Compress(settings.Algorithm, catalogueBytes, settings.Level, settings.BlockSize));
    sink.Raw(Bytes(s => DarPrimitives.WriteTerminator(s, catalogueAt)));
    var trailerAt = (ulong)sink.Position;
    var trailerFlags = DarArchive.FlagInitialOffset | headerFlags;
    sink.Raw(Bytes((header with { Flags = trailerFlags, InitialOffset = initialOffset }).Write));
    sink.Raw(Bytes(s => DarPrimitives.WriteTerminator(s, trailerAt)));
    sink.Finish();
  }

  // The -R root dar records; "." is accepted wherever an absolute path is.
  private const string InPlace = ".";

  private static byte[] Bytes(Action<Stream> write) {
    using var ms = new MemoryStream();
    write(ms);
    return ms.ToArray();
  }

  private static byte[] NewLabel() {
    // dar fills the label with its clock and process id; any value unique to the archive works.
    var label = new byte[DarSliceHeader.NameLength];
    BinaryPrimitives.WriteInt64LittleEndian(label, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    Random.Shared.NextBytes(label.AsSpan(8));
    return label;
  }

  // ── Data and tape marks ────────────────────────────────────────────────────

  private static void WriteChildren(DarArchiveSink sink, Node dir, Settings settings) {
    foreach (var child in dir.Children.Values) {
      switch (child.Kind) {
        case ItemKind.File:
          WriteFile(sink, child, settings);
          break;
        case ItemKind.Directory:
          if (settings.TapeMarks)
            InlineEntry(sink, child);
          WriteChildren(sink, child, settings);
          if (settings.TapeMarks) {
            // End of directory, as its own inline entry.
            sink.Mark((byte)'F');
            sink.Data(WithInlineChecksum([Signature('z')]));
          }
          break;
        case ItemKind.Symlink:
          if (settings.TapeMarks)
            InlineEntry(sink, child);
          break;
      }
    }
  }

  /// <summary>
  /// A tape mark <c>F</c> and the entry's inline copy (section 6): the catalogue entry without the
  /// fields only known once the data is written — offset, stored size and data checksum — and a
  /// directory with an empty child list, then a 2-byte checksum of those bytes.
  /// </summary>
  private static void InlineEntry(DarArchiveSink sink, Node node) {
    using var ms = new MemoryStream();
    WriteEntryHead(ms, node, node.Name);
    switch (node.Kind) {
      case ItemKind.File:
        DarPrimitives.WriteInfinint(ms, (ulong)node.Size);
        ms.WriteByte(0); // data flags
        ms.WriteByte((byte)node.Algorithm);
        break;
      case ItemKind.Directory:
        ms.WriteByte(Signature('z'));
        break;
      case ItemKind.Symlink:
        DarPrimitives.WriteString(ms, node.LinkTarget!);
        break;
    }
    sink.Mark((byte)'F');
    sink.Data(WithInlineChecksum(ms.ToArray()));
  }

  private static byte[] WithInlineChecksum(byte[] entry) {
    using var ms = new MemoryStream(entry.Length + 7);
    ms.Write(entry);
    DarPrimitives.WriteChecksum(ms, DarPrimitives.Checksum(entry, 2));
    return ms.ToArray();
  }

  private static void WriteFile(DarArchiveSink sink, Node node, Settings settings) {
    var algorithm = settings.Algorithm;
    var declared = node.Item!.Length;
    using var source = node.Item.Open?.Invoke() ?? new MemoryStream([], writable: false);
    var checksum = new DarChecksum(DarPrimitives.DataChecksumWidth((ulong)declared));

    if (algorithm != 'n' && declared <= settings.InMemoryLimit) {
      // Small enough to try compressing first and keep whichever is smaller, as dar does.
      var data = ReadExactly(source, declared, node.Path);
      checksum.Update(data);
      var stored = data;
      var used = 'n';
      if (data.Length > 0) {
        var compressed = Compress(algorithm, data, settings.Level, settings.BlockSize);
        if (compressed.Length < data.Length) {
          stored = compressed;
          used = algorithm;
        }
      }
      node.Size = data.LongLength;
      node.Algorithm = used;
      if (settings.TapeMarks)
        InlineEntry(sink, node);
      node.Offset = (ulong)sink.Position;
      sink.Data(stored);
      node.StorageSize = (ulong)stored.LongLength;
    } else {
      // Stored, or too large to hold: stream it, compressed on the fly whatever the outcome.
      node.Size = declared;
      node.Algorithm = declared == 0 ? 'n' : algorithm;
      if (settings.TapeMarks)
        InlineEntry(sink, node);
      node.Offset = (ulong)sink.Position;
      var counter = new SinkStream(sink);
      // Disposing the counter itself is harmless; disposing an encoder ends its unit.
      using (var encoder = node.Algorithm == 'n' ? counter : OpenEncoder(algorithm, counter, settings.Level, settings.BlockSize)) {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBuffer);
        try {
          int read;
          while ((read = source.Read(buffer, 0, buffer.Length)) > 0) {
            checksum.Update(buffer.AsSpan(0, read));
            if (checksum.Length > declared)
              break;
            encoder.Write(buffer, 0, read);
          }
        } finally {
          ArrayPool<byte>.Shared.Return(buffer);
        }
      }
      if (checksum.Length != declared)
        throw new IOException($"'{node.Path}' changed size while it was being archived ({checksum.Length} bytes read, {declared} expected).");
      node.StorageSize = (ulong)counter.Count;
    }
    node.Checksum = checksum.Value;
    if (settings.TapeMarks) {
      sink.Mark((byte)'R');
      sink.Data(DarPrimitives.EncodeChecksum(node.Checksum));
    }
  }

  private static byte[] ReadExactly(Stream source, long length, string path) {
    if (length > Array.MaxLength)
      throw new NotSupportedException($"'{path}' is too large to hold in memory.");
    var data = new byte[length];
    var at = 0;
    while (at < data.Length) {
      var read = source.Read(data, at, data.Length - at);
      if (read <= 0)
        throw new IOException($"'{path}' ended after {at} of {length} bytes while it was being archived.");
      at += read;
    }
    if (source.ReadByte() >= 0)
      throw new IOException($"'{path}' grew while it was being archived.");
    return data;
  }

  /// <summary>A write-only view of the sink that counts what passes through it, for the codecs.</summary>
  private sealed class SinkStream(DarArchiveSink sink) : Stream {
    public long Count { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => this.Count;
    public override long Position { get => this.Count; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) {
      sink.Data(buffer);
      this.Count += buffer.Length;
    }

    public override void WriteByte(byte value) => this.Write([value]);
  }

  // ── Compression ────────────────────────────────────────────────────────────

  /// <summary>Compresses one unit (a file or the catalogue) in memory.</summary>
  internal static byte[] Compress(char algorithm, byte[] data, int? level, int blockSize = 0) {
    using var output = new MemoryStream();
    using (var encoder = OpenEncoder(algorithm, output, level, blockSize))
      encoder.Write(data);
    return output.ToArray();
  }

  /// <summary>
  /// A streaming encoder for one unit, writing into <paramref name="target"/> (left open). gzip is
  /// a zlib stream, bzip2 a .bz2 stream, xz an .xz stream, zstd a zstd frame sequence, and lz4
  /// dar's block framing with 246 660 clear bytes per block (section 7).
  /// </summary>
  internal static Stream OpenEncoder(char algorithm, Stream target, int? level, int blockSize = 0) => blockSize > 0
    ? new BlockFramingStream(target, blockSize, block => CompressBlock(algorithm, block, level))
    : algorithm switch {
    'z' => new ZlibStream(target, CompressionStreamMode.Compress, (level ?? 6) switch {
      <= 0 => DeflateCompressionLevel.None,
      <= 3 => DeflateCompressionLevel.Fast,
      <= 7 => DeflateCompressionLevel.Default,
      _ => DeflateCompressionLevel.Best,
    }, leaveOpen: true),
    'y' => new Bzip2Stream(target, CompressionStreamMode.Compress, Math.Clamp(level ?? 9, 1, 9), leaveOpen: true),
    'x' => new XzStream(target, CompressionStreamMode.Compress, leaveOpen: true),
    'd' => new ZstdStream(target, CompressionStreamMode.Compress, Math.Clamp(level ?? 3, 1, 19), leaveOpen: true),
    'q' => new BlockFramingStream(target, DarArchive.StreamingBlockSize, block => Lz4BlockCompressor.Compress(block)),
    _ => throw new NotSupportedException($"DAR compression '{algorithm}' cannot be written."),
  };

  /// <summary>One block of block mode (section 7): a whole zlib stream, .bz2, .xz or zstd frame, or a raw LZ4 block.</summary>
  private static byte[] CompressBlock(char algorithm, ReadOnlySpan<byte> block, int? level) {
    if (algorithm == 'q')
      return Lz4BlockCompressor.Compress(block);
    using var output = new MemoryStream();
    using (var encoder = OpenEncoder(algorithm, output, level))
      encoder.Write(block);
    return output.ToArray();
  }

  // The xz and zstd encoders' stream / frame length (XzStream, ZstdStream).
  private const long SingleUnitLimit = 64L << 20;
  private const int AutoBlockSize = 8 << 20;
  private const int MaxBlockSize = 64 << 20;

  /// <summary>dar's block framing: <c>01</c>, an infinint size and the compressed block, per
  /// <paramref name="blockSize"/> clear bytes; <c>02 0</c> at the end.</summary>
  private sealed class BlockFramingStream(Stream target, int blockSize, Func<ReadOnlySpan<byte>, byte[]> compress) : Stream {
    private readonly byte[] _block = new byte[blockSize];
    private int _filled;
    private bool _closed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) {
      while (!buffer.IsEmpty) {
        var n = Math.Min(buffer.Length, this._block.Length - this._filled);
        buffer[..n].CopyTo(this._block.AsSpan(this._filled));
        this._filled += n;
        buffer = buffer[n..];
        if (this._filled == this._block.Length)
          this.EmitBlock();
      }
    }

    private void EmitBlock() {
      var block = compress(this._block.AsSpan(0, this._filled));
      target.WriteByte(1);
      DarPrimitives.WriteInfinint(target, (ulong)block.Length);
      target.Write(block);
      this._filled = 0;
    }

    protected override void Dispose(bool disposing) {
      if (disposing && !this._closed) {
        this._closed = true;
        if (this._filled > 0)
          this.EmitBlock();
        target.WriteByte(2);
        DarPrimitives.WriteInfinint(target, 0);
      }
      base.Dispose(disposing);
    }
  }

  // ── Catalogue ──────────────────────────────────────────────────────────────

  private static void WriteCatalogueDirectory(Stream cat, Node dir, string name) {
    WriteEntryHead(cat, dir, name);
    foreach (var child in dir.Children.Values) {
      switch (child.Kind) {
        case ItemKind.Directory:
          WriteCatalogueDirectory(cat, child, child.Name);
          continue;
        case ItemKind.Symlink:
          WriteEntryHead(cat, child, child.Name);
          DarPrimitives.WriteString(cat, child.LinkTarget!);
          continue;
        default:
          WriteEntryHead(cat, child, child.Name);
          DarPrimitives.WriteInfinint(cat, (ulong)child.Size);
          DarPrimitives.WriteInfinint(cat, child.Offset);
          DarPrimitives.WriteInfinint(cat, child.StorageSize);
          cat.WriteByte(0); // data flags: no holes, not dirty, no delta signature
          cat.WriteByte((byte)child.Algorithm);
          DarPrimitives.WriteChecksum(cat, child.Checksum!);
          continue;
      }
    }
    cat.WriteByte(Signature('z'));
  }

  /// <summary>Signature, name and inode (section 9) — what every saved entry starts with.</summary>
  private static void WriteEntryHead(Stream s, Node node, string name) {
    s.WriteByte(Signature(node.Kind switch { ItemKind.Directory => 'd', ItemKind.Symlink => 'l', _ => 'f' }));
    DarPrimitives.WriteString(s, name);
    s.WriteByte(0x03); // EA none, FSA none
    DarPrimitives.WriteInfinint(s, node.Uid);
    DarPrimitives.WriteInfinint(s, node.Gid);
    Span<byte> perm = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(perm, node.Permissions);
    s.Write(perm);
    WriteDate(s, node.Accessed);
    WriteDate(s, node.Modified);
    WriteDate(s, node.Changed);
  }

  // Saved status 3 ("saved") in the top three bits, the type letter's low five bits below.
  private static byte Signature(char type) => (byte)((3 << 5) | (type & 0x1F));

  private static void WriteDate(Stream cat, DateTime value) {
    var ticks = Math.Max(0, (value.ToUniversalTime() - DateTime.UnixEpoch).Ticks);
    cat.WriteByte((byte)'n');
    DarPrimitives.WriteInfinint(cat, (ulong)(ticks / TimeSpan.TicksPerSecond));
    DarPrimitives.WriteInfinint(cat, (ulong)(ticks % TimeSpan.TicksPerSecond) * 100);
  }

  // ── Tree ───────────────────────────────────────────────────────────────────

  private static Node BuildTree(IReadOnlyList<Item> items) {
    // dar records the root with permissions 0 and never restores them onto the target directory.
    var now = DateTime.UtcNow;
    var root = new Node("", "", ItemKind.Directory, null) { Modified = now, Accessed = now, Changed = now };
    foreach (var item in items) {
      var parts = item.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0)
        continue;
      foreach (var part in parts)
        if (part is "." or ".." || part.Contains('\0'))
          throw new ArgumentException($"DAR entry name '{item.Path}' is not a relative path.");
      if (item.Kind == ItemKind.Symlink && string.IsNullOrEmpty(item.LinkTarget))
        throw new ArgumentException($"DAR symlink '{item.Path}' has no target.");
      var dir = root;
      var skip = false;
      for (var i = 0; i < parts.Length - 1; ++i) {
        if (!dir.Children.TryGetValue(parts[i], out var next)) {
          next = new Node(parts[i], string.Join('/', parts[..(i + 1)]), ItemKind.Directory, null) {
            Permissions = 0x1ED, Modified = item.Modified, Accessed = item.Accessed, Changed = item.Changed ?? item.Modified,
          };
          dir.Children.Add(parts[i], next);
        } else if (next.Kind == ItemKind.Symlink) {
          // Something listed below a symlinked directory: reachable through the link already.
          skip = true;
          break;
        } else if (next.Kind != ItemKind.Directory)
          throw new ArgumentException($"'{string.Join('/', parts[..(i + 1)])}' is both a file and a directory.");
        dir = next;
      }
      if (skip)
        continue;
      var leaf = parts[^1];
      var node = new Node(leaf, string.Join('/', parts), item.Kind, item) {
        Permissions = item.Permissions, Modified = item.Modified, Accessed = item.Accessed, Changed = item.Changed ?? item.Modified,
        Uid = item.Uid, Gid = item.Gid, LinkTarget = item.LinkTarget,
      };
      if (dir.Children.TryGetValue(leaf, out var existing)) {
        if (existing.Kind == ItemKind.Directory && item.Kind == ItemKind.Directory) {
          existing.Permissions = item.Permissions;
          existing.Modified = item.Modified;
          existing.Accessed = item.Accessed;
          existing.Changed = node.Changed;
          existing.Uid = item.Uid;
          existing.Gid = item.Gid;
          continue;
        }
        if ((existing.Kind == ItemKind.Directory) != (item.Kind == ItemKind.Directory))
          throw new ArgumentException($"'{item.Path}' is both a file and a directory.");
      }
      dir.Children[leaf] = node;
    }
    return root;
  }

  private sealed class Node(string name, string path, ItemKind kind, Item? item) {
    public string Name { get; } = name;
    public string Path { get; } = path;
    public ItemKind Kind { get; } = kind;
    public Item? Item { get; } = item;
    public ushort Permissions { get; set; }
    public DateTime Modified { get; set; }
    public DateTime Accessed { get; set; }
    public DateTime Changed { get; set; }
    public ulong Uid { get; set; }
    public ulong Gid { get; set; }
    public string? LinkTarget { get; init; }
    public SortedDictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    public long Size { get; set; }
    public ulong Offset { get; set; }
    public ulong StorageSize { get; set; }
    public char Algorithm { get; set; } = 'n';
    public byte[]? Checksum { get; set; }
  }
}
