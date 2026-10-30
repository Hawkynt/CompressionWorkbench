// B1 archive writing, converted from b1-pack (https://github.com/b1-pack/b1-pack, Apache-2.0,
// Copyright 2011 b1.org; changed: C# conversion, one-pass single-volume layout). See
// THIRD-PARTY-NOTICE.b1-pack.txt.

using System.Security.Cryptography;
using System.Text;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.B1;

/// <summary>
/// Writes a single-volume B1 archive. The archive stream holds every complete record (folders
/// before their contents) followed by one catalog group pointing back at them and the null record.
/// It is stored as one LZMA chunk (with end marker) split into blocks of at most 1 MiB, or as plain
/// blocks. Every record pointer addresses the first block, with the record's offset in the archive
/// stream; b1-pack's reader skips across block boundaries to reach it. With a password, every block
/// is AES-256-GCM encrypted as b1-pack does.
/// </summary>
public sealed class B1Writer {
  private const int MaxChunk = 0xFFFFF;
  private const int DefaultIterations = 100_000;

  private readonly List<(string Path, bool Folder, byte[] Data, DateTime? Modified)> _items = [];

  /// <summary>Adds a file; missing parent folders are created.</summary>
  public void AddFile(string path, byte[] data, DateTime? modifiedUtc = null) => this._items.Add((Normalise(path), false, data, modifiedUtc));

  /// <summary>Adds a folder.</summary>
  public void AddFolder(string path, DateTime? modifiedUtc = null) => this._items.Add((Normalise(path), true, [], modifiedUtc));

  private static string Normalise(string path) {
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
      throw new ArgumentException($"'{path}' is not a valid B1 member path.", nameof(path));
    return string.Join('/', parts);
  }

  private sealed record Node(long Id, long? Parent, string Name, bool Folder, byte[] Data, DateTime? Modified);

  private List<Node> _Tree() {
    var nodes = new List<Node>();
    var folders = new Dictionary<string, long>(StringComparer.Ordinal);
    var nextId = 1L;
    long? Folder(string path, DateTime? modified) {
      if (path.Length == 0)
        return null;
      if (folders.TryGetValue(path, out var existing))
        return existing;
      var slash = path.LastIndexOf('/');
      var parent = Folder(slash < 0 ? "" : path[..slash], null);
      var id = nextId++;
      folders[path] = id;
      nodes.Add(new(id, parent, path[(slash + 1)..], true, [], modified));
      return id;
    }

    foreach (var (path, folder, data, modified) in this._items) {
      if (folder) {
        Folder(path, modified);
        continue;
      }

      var slash = path.LastIndexOf('/');
      var parent = Folder(slash < 0 ? "" : path[..slash], null);
      nodes.Add(new(nextId++, parent, path[(slash + 1)..], false, data, modified));
    }

    return nodes;
  }

  private static void Header(Stream s, Node node) {
    B1Int.Write(s, node.Id);
    B1Int.Write(s, node.Parent);
    B1Int.WriteText(s, node.Name);
    if (node.Modified is { } modified) {
      B1Int.Write(s, 0);
      B1Int.Write(s, (long)Math.Floor((modified.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds));
    }

    B1Int.Write(s, null);
  }

  /// <summary>Returns the archive.</summary>
  /// <param name="compress">LZMA (true) or plain blocks (false).</param>
  /// <param name="password">AES-256-GCM encryption when not null.</param>
  public byte[] ToArray(bool compress = true, string? password = null) {
    var nodes = this._Tree();

    var salt = RandomNumberGenerator.GetBytes(password == null ? 16 : 32);
    var archiveId = Convert.ToBase64String(salt);
    var cipher = password == null ? null : new B1VolumeCipher(password, salt, DefaultIterations, 1);
    var privateHead = $"b1:as v:{(cipher == null ? "0.2" : "0.3")} a:{archiveId} n:1 t:{nodes.Count}";
    var head = cipher == null
      ? privateHead
      : $"b1:as v:0.3 a:{archiveId} n:1 e:1/{DefaultIterations} x:{Convert.ToBase64String(cipher.Head(true, Encoding.UTF8.GetBytes(privateHead)))}";
    var headBytes = Encoding.UTF8.GetBytes(head);
    long firstBlock = headBytes.Length + 1;

    // Archive stream: complete records, then the catalog group, then the null record.
    using var stream = new MemoryStream();
    var completeAt = new Dictionary<long, long>();
    foreach (var node in nodes) {
      completeAt[node.Id] = stream.Position;
      B1Int.Write(stream, node.Folder ? B1Reader.CompleteFolderType : B1Reader.CompleteFileType);
      Header(stream, node);
      if (!node.Folder)
        B1Int.WriteBinary(stream, node.Data);
    }

    var catalogAt = stream.Position;
    foreach (var node in nodes) {
      B1Int.Write(stream, node.Folder ? B1Reader.CatalogFolderType : B1Reader.CatalogFileType);
      B1Int.Write(stream, 1);
      B1Int.Write(stream, firstBlock);
      B1Int.Write(stream, completeAt[node.Id]);
      Header(stream, node);
      if (!node.Folder)
        B1Int.Write(stream, node.Data.LongLength);
    }

    B1Int.Write(stream, null);
    var archiveStream = stream.ToArray();

    using var volume = new MemoryStream();
    volume.Write(headBytes);
    volume.WriteByte(B1Reader.Separator);
    if (compress) {
      var encoder = new LzmaEncoder(dictionarySize: 1 << 22);
      using var packed = new MemoryStream();
      packed.Write(encoder.Properties);
      encoder.Encode(packed, archiveStream, writeEndMarker: true);
      var payload = packed.ToArray();
      for (var at = 0; at < payload.Length; at += MaxChunk)
        WriteBlock(volume, at == 0 ? B1Reader.FirstLzmaBlockType : B1Reader.NextLzmaBlockType,
          payload.AsSpan(at, Math.Min(MaxChunk, payload.Length - at)), cipher);
    } else {
      for (var at = 0; at < archiveStream.Length; at += MaxChunk)
        WriteBlock(volume, B1Reader.PlainBlockType, archiveStream.AsSpan(at, Math.Min(MaxChunk, archiveStream.Length - at)), cipher);
    }

    B1Int.Write(volume, null);
    var tail = $"c:1/{firstBlock}/{catalogAt} b1:ae";
    if (cipher != null)
      tail = $"x:{Convert.ToBase64String(cipher.Tail(true, Encoding.UTF8.GetBytes(tail)))} b1:ae";
    volume.WriteByte(B1Reader.Separator);
    volume.Write(Encoding.UTF8.GetBytes(tail));
    return volume.ToArray();
  }

  private static void WriteBlock(Stream volume, int type, ReadOnlySpan<byte> data, B1VolumeCipher? cipher) {
    using var block = new MemoryStream();
    B1Int.Write(block, type);
    B1Int.WriteBinary(block, data);
    Span<byte> check = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(check, B1Int.Adler32(data));
    block.Write(check);
    if (cipher == null) {
      volume.Write(block.ToArray());
      return;
    }

    var offset = volume.Position;
    B1Int.Write(volume, B1Reader.AesBlockType);
    B1Int.WriteBinary(volume, cipher.Block(true, offset, block.ToArray()));
  }
}
