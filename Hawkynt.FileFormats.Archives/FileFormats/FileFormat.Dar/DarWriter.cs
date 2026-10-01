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
/// Writes a single-slice DAR archive in format 11.3, the layout dar 2.7 and 2.8 produce with
/// <c>-at</c> (no tape marks): slice header, version header, file data, catalogue, terminator,
/// version trailer, terminator, slice trailer (<c>docs/DAR-ON-DISK.md</c>, section 10).
/// </summary>
internal static class DarWriter {
  /// <summary>One item to store.</summary>
  public sealed record Item(string Path, bool IsDirectory, byte[] Data, ushort Permissions, DateTime Modified, DateTime Accessed);

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

  public static void Write(Stream output, IReadOnlyList<Item> items, char algorithm, int? level) {
    var root = BuildTree(items);
    var name = NewLabel();
    var archive = new MemoryStream();

    var header = new DarVersionHeader(11, 3, algorithm, "N/A", 0, 0, 0);
    header.Write(archive);
    var initialOffset = (ulong)archive.Length;

    // File data, in catalogue order.
    foreach (var node in root.Walk().Where(n => !n.IsDirectory)) {
      var data = node.Data;
      var stored = data;
      var used = 'n';
      if (algorithm != 'n' && data.Length > 0) {
        var compressed = Compress(algorithm, data, level);
        if (compressed.Length < data.Length) {
          stored = compressed;
          used = algorithm;
        }
      }
      node.Offset = (ulong)archive.Length;
      node.StorageSize = (ulong)stored.Length;
      node.Algorithm = used;
      archive.Write(stored);
    }

    var catalogue = new MemoryStream();
    catalogue.Write(name);
    DarPrimitives.WriteString(catalogue, ".");
    WriteDirectory(catalogue, root, "root");
    var catalogueBody = catalogue.ToArray();
    DarPrimitives.WriteChecksum(catalogue, DarPrimitives.Checksum(catalogueBody, 4));
    var catalogueBytes = catalogue.ToArray();

    var catalogueAt = (ulong)archive.Length;
    archive.Write(algorithm == 'n' ? catalogueBytes : Compress(algorithm, catalogueBytes, level));
    DarPrimitives.WriteTerminator(archive, catalogueAt);
    var trailerAt = (ulong)archive.Length;
    (header with { Flags = DarArchive.FlagInitialOffset, InitialOffset = initialOffset }).Write(archive);
    DarPrimitives.WriteTerminator(archive, trailerAt);

    // Slice header: magic, internal name, flag 'T' (last), extension 'T' and a one-entry TLV list
    // holding the data name; the slice trailer repeats 'T'.
    Span<byte> magic = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(magic, DarSliceHeader.SliceMagic);
    output.Write(magic);
    output.Write(name);
    output.WriteByte((byte)'T');
    output.WriteByte((byte)'T');
    DarPrimitives.WriteInfinint(output, 1);
    Span<byte> type = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(type, DarSliceHeader.TlvDataName);
    output.Write(type);
    DarPrimitives.WriteInfinint(output, DarSliceHeader.NameLength);
    output.Write(name);
    archive.Position = 0;
    archive.CopyTo(output);
    output.WriteByte((byte)'T');
    output.Flush();
  }

  private static byte[] NewLabel() {
    // dar fills the label with its clock and process id; any value unique to the archive works.
    var label = new byte[DarSliceHeader.NameLength];
    BinaryPrimitives.WriteInt64LittleEndian(label, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    Random.Shared.NextBytes(label.AsSpan(8));
    return label;
  }

  private static void WriteDirectory(Stream cat, Node dir, string name) {
    cat.WriteByte(Signature('d'));
    DarPrimitives.WriteString(cat, name);
    WriteInode(cat, dir.Permissions, dir.Accessed, dir.Modified);
    foreach (var child in dir.Children.Values) {
      if (child.IsDirectory) {
        WriteDirectory(cat, child, child.Name);
        continue;
      }
      cat.WriteByte(Signature('f'));
      DarPrimitives.WriteString(cat, child.Name);
      WriteInode(cat, child.Permissions, child.Accessed, child.Modified);
      DarPrimitives.WriteInfinint(cat, (ulong)child.Data.LongLength);
      DarPrimitives.WriteInfinint(cat, child.Offset);
      DarPrimitives.WriteInfinint(cat, child.StorageSize);
      cat.WriteByte(0); // data flags: no holes, not dirty, no delta signature
      cat.WriteByte((byte)child.Algorithm);
      DarPrimitives.WriteChecksum(cat, DarPrimitives.Checksum(child.Data, DarPrimitives.DataChecksumWidth((ulong)child.Data.LongLength)));
    }
    cat.WriteByte(Signature('z'));
  }

  // Saved status 3 ("saved") in the top three bits, the type letter's low five bits below.
  private static byte Signature(char type) => (byte)((3 << 5) | (type & 0x1F));

  private static void WriteInode(Stream cat, ushort permissions, DateTime accessed, DateTime modified) {
    cat.WriteByte(0x03); // EA none, FSA none
    DarPrimitives.WriteInfinint(cat, 0); // uid
    DarPrimitives.WriteInfinint(cat, 0); // gid
    Span<byte> perm = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(perm, permissions);
    cat.Write(perm);
    WriteDate(cat, accessed);
    WriteDate(cat, modified);
    WriteDate(cat, modified); // inode change time: the closest value the input carries
  }

  private static void WriteDate(Stream cat, DateTime value) {
    var ticks = Math.Max(0, (value.ToUniversalTime() - DateTime.UnixEpoch).Ticks);
    cat.WriteByte((byte)'n');
    DarPrimitives.WriteInfinint(cat, (ulong)(ticks / TimeSpan.TicksPerSecond));
    DarPrimitives.WriteInfinint(cat, (ulong)(ticks % TimeSpan.TicksPerSecond) * 100);
  }

  internal static byte[] Compress(char algorithm, byte[] data, int? level) {
    switch (algorithm) {
      case 'z':
        return ZlibStream.Compress(data, (level ?? 6) switch {
          <= 0 => DeflateCompressionLevel.None,
          <= 3 => DeflateCompressionLevel.Fast,
          <= 7 => DeflateCompressionLevel.Default,
          _ => DeflateCompressionLevel.Best,
        });
      case 'q': {
        using var framed = new MemoryStream();
        for (var at = 0; at < data.Length; at += DarArchive.StreamingBlockSize) {
          var block = Lz4BlockCompressor.Compress(data.AsSpan(at, Math.Min(DarArchive.StreamingBlockSize, data.Length - at)));
          framed.WriteByte(1);
          DarPrimitives.WriteInfinint(framed, (ulong)block.Length);
          framed.Write(block);
        }
        framed.WriteByte(2);
        DarPrimitives.WriteInfinint(framed, 0);
        return framed.ToArray();
      }
    }
    using var output = new MemoryStream();
    using (Stream encoder = algorithm switch {
      'y' => new Bzip2Stream(output, CompressionStreamMode.Compress, Math.Clamp(level ?? 9, 1, 9), leaveOpen: true),
      'x' => new XzStream(output, CompressionStreamMode.Compress, leaveOpen: true),
      'd' => new ZstdStream(output, CompressionStreamMode.Compress, Math.Clamp(level ?? 3, 1, 19), leaveOpen: true),
      _ => throw new NotSupportedException($"DAR compression '{algorithm}' cannot be written."),
    })
      encoder.Write(data);
    return output.ToArray();
  }

  private static Node BuildTree(IReadOnlyList<Item> items) {
    // dar records the root with permissions 0 and never restores them onto the target directory.
    var root = new Node("", true, [], 0, DateTime.UtcNow, DateTime.UtcNow);
    foreach (var item in items) {
      var parts = item.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0)
        continue;
      foreach (var part in parts)
        if (part is "." or ".." || part.Contains('\0'))
          throw new ArgumentException($"DAR entry name '{item.Path}' is not a relative path.");
      var dir = root;
      for (var i = 0; i < parts.Length - 1; ++i) {
        if (!dir.Children.TryGetValue(parts[i], out var next)) {
          next = new Node(parts[i], true, [], 0x1ED, item.Modified, item.Accessed);
          dir.Children.Add(parts[i], next);
        } else if (!next.IsDirectory)
          throw new ArgumentException($"'{string.Join('/', parts[..(i + 1)])}' is both a file and a directory.");
        dir = next;
      }
      var leaf = parts[^1];
      if (dir.Children.TryGetValue(leaf, out var existing)) {
        if (existing.IsDirectory != item.IsDirectory)
          throw new ArgumentException($"'{item.Path}' is both a file and a directory.");
        if (item.IsDirectory) {
          existing.Permissions = item.Permissions;
          existing.Modified = item.Modified;
          existing.Accessed = item.Accessed;
        } else
          dir.Children[leaf] = new Node(leaf, false, item.Data, item.Permissions, item.Modified, item.Accessed);
        continue;
      }
      dir.Children.Add(leaf, new Node(leaf, item.IsDirectory, item.Data, item.Permissions, item.Modified, item.Accessed));
    }
    return root;
  }

  private sealed class Node(string name, bool isDirectory, byte[] data, ushort permissions, DateTime modified, DateTime accessed) {
    public string Name { get; } = name;
    public bool IsDirectory { get; } = isDirectory;
    public byte[] Data { get; } = data;
    public ushort Permissions { get; set; } = permissions;
    public DateTime Modified { get; set; } = modified;
    public DateTime Accessed { get; set; } = accessed;
    public SortedDictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    public ulong Offset { get; set; }
    public ulong StorageSize { get; set; }
    public char Algorithm { get; set; } = 'n';

    public IEnumerable<Node> Walk() {
      foreach (var child in this.Children.Values) {
        yield return child;
        if (child.IsDirectory)
          foreach (var grandchild in child.Walk())
            yield return grandchild;
      }
    }
  }
}
