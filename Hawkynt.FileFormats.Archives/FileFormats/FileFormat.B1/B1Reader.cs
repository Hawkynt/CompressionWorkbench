// B1 archive reading, converted from b1-pack (https://github.com/b1-pack/b1-pack, Apache-2.0,
// Copyright 2011 b1.org; changed: C# conversion, whole-volume decoding instead of streaming
// cursors). See THIRD-PARTY-NOTICE.b1-pack.txt.

using System.Text;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.B1;

/// <summary>A file or folder of a B1 archive.</summary>
public sealed class B1Entry {
  /// <summary>Gets the full path, '/'-separated.</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>Gets whether this is a folder.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Gets the file size (0 for folders).</summary>
  public long Size { get; init; }

  /// <summary>Gets the modification time (UTC), when recorded.</summary>
  public DateTime? LastModified { get; init; }

  internal long Id { get; init; }

  internal long? ParentId { get; init; }

  internal (long Volume, long Block, long Record) Pointer { get; init; }
}

/// <summary>
/// Reads a single-volume B1 archive (b1-pack's format, schema up to 0.4): plain and LZMA blocks,
/// AES-256-GCM encryption. The whole archive stream is decoded once; records are then read through
/// the record pointers exactly as b1-pack follows them.
/// </summary>
public sealed class B1Reader {
  internal const int RecordPointerType = 0;
  internal const int CatalogFileType = 1;
  internal const int CompleteFileType = 2;
  internal const int CatalogFolderType = 3;
  internal const int CompleteFolderType = 4;
  internal const int EmptyRecordType = 5;
  internal const int AesBlockType = 0;
  internal const int PlainBlockType = 1;
  internal const int FirstLzmaBlockType = 2;
  internal const int NextLzmaBlockType = 3;
  internal const int EmptyBlockType = 5;
  internal const byte Separator = 0xFC;
  internal const double MaxSchemaVersion = 0.4;

  private readonly byte[] _stream;
  private readonly Dictionary<long, long> _chunkStart = [];
  private readonly List<B1Entry> _entries = [];

  /// <summary>Gets the files and folders, in catalog order.</summary>
  public IReadOnlyList<B1Entry> Entries => this._entries;

  /// <summary>Gets whether the archive is encrypted.</summary>
  public bool IsEncrypted { get; }

  /// <summary>Gets whether any block is LZMA-compressed.</summary>
  public bool IsCompressed { get; private set; }

  /// <summary>Gets the head's compression method label ("m:"), when present.</summary>
  public string? Method { get; }

  /// <summary>Opens a B1 volume.</summary>
  /// <exception cref="InvalidDataException">The data is not a B1 archive, or it is corrupt or the password is wrong.</exception>
  /// <exception cref="NotSupportedException">The archive spans several volumes, or uses a newer schema.</exception>
  /// <exception cref="InvalidOperationException">The archive is encrypted and no password was given.</exception>
  public B1Reader(Stream input, string? password = null) {
    input.Position = 0;
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var volume = ms.ToArray();

    var headEnd = Array.IndexOf(volume, Separator);
    if (headEnd < 5 || !volume.AsSpan(0, 5).SequenceEqual("b1:as"u8))
      throw new InvalidDataException(volume.Length >= 5 && volume.AsSpan(0, 5).SequenceEqual("b1:vs"u8)
        ? "This is a later volume of a multi-volume B1 archive; open the first."
        : "Not a B1 archive (no b1:as head).");
    var head = Items(Encoding.UTF8.GetString(volume, 0, headEnd));

    var tailStart = Array.LastIndexOf(volume, Separator);
    if (tailStart <= headEnd)
      throw new InvalidDataException("B1 volume has no tail.");
    var tail = Items(Encoding.UTF8.GetString(volume, tailStart + 1, volume.Length - tailStart - 1));

    B1VolumeCipher? cipher = null;
    if (head.TryGetValue("e", out var encryption)) {
      this.IsEncrypted = true;
      if (!encryption.StartsWith("1/", StringComparison.Ordinal) || !int.TryParse(encryption[2..], out var iterations))
        throw new NotSupportedException($"B1 encryption method '{encryption}' is not supported.");
      if (password == null)
        throw new InvalidOperationException("The B1 archive is encrypted but no password was provided.");
      cipher = new B1VolumeCipher(password, Convert.FromBase64String(head["a"]), iterations, 1);
      head = Items(Encoding.UTF8.GetString(cipher.Head(false, Convert.FromBase64String(head["x"]))));
      tail = Items(Encoding.UTF8.GetString(cipher.Tail(false, Convert.FromBase64String(tail["x"]))));
    }

    if (!head.TryGetValue("v", out var version) || !double.TryParse(version, System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out var schema))
      throw new InvalidDataException("B1 head has no schema version.");
    if (schema > MaxSchemaVersion)
      throw new NotSupportedException($"B1 schema {version} is newer than {MaxSchemaVersion}.");
    if (head.GetValueOrDefault("n") != "1" || !tail.ContainsKey("b1:ae"))
      throw new NotSupportedException("Multi-volume B1 archives are not supported; only single-volume archives are read.");
    this.Method = head.GetValueOrDefault("m");

    this._stream = this._DecodeBody(volume, headEnd + 1, tailStart, cipher);

    var catalog = tail.GetValueOrDefault("c") ?? head.GetValueOrDefault("c")
      ?? throw new InvalidDataException("B1 archive has no catalog pointer.");
    var parts = catalog.Split('/');
    if (parts.Length != 3)
      throw new InvalidDataException($"Malformed B1 catalog pointer '{catalog}'.");
    long? total = head.TryGetValue("t", out var t) ? long.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
    this._ReadCatalog((long.Parse(parts[0]), long.Parse(parts[1]), long.Parse(parts[2])), total);
  }

  // The head and tail are space-separated items; apostrophes quote an item and double inside it.
  // Items of the form key:value are keyed by key; others (the b1:* signatures) by themselves.
  private static Dictionary<string, string> Items(string text) {
    var items = new Dictionary<string, string>(StringComparer.Ordinal);
    var current = new StringBuilder();
    var quoted = false;
    var any = false;
    void Save() {
      var item = current.ToString();
      var colon = item.IndexOf(':');
      if (item.StartsWith("b1:", StringComparison.Ordinal))
        items[item] = item;
      else if (colon >= 0)
        items[item[..colon]] = item[(colon + 1)..];
      current.Clear();
      any = false;
    }

    for (var i = 0; i < text.Length; ++i) {
      var c = text[i];
      if (quoted) {
        if (c != '\'')
          current.Append(c);
        else if (i + 1 < text.Length && text[i + 1] == '\'') {
          current.Append('\'');
          ++i;
        } else
          quoted = false;
      } else if (c == '\'') {
        quoted = true;
        any = true;
      } else if (c == ' ') {
        if (any)
          Save();
      } else {
        current.Append(c);
        any = true;
      }
    }

    if (quoted)
      throw new InvalidDataException("B1 head/tail item is not closed.");
    if (any)
      Save();
    return items;
  }

  // Decodes every block into one archive stream, remembering where each chunk-starting block's
  // data begins in it (record pointers address chunk-start block + offset).
  private byte[] _DecodeBody(byte[] volume, int start, int end, B1VolumeCipher? cipher) {
    using var body = new MemoryStream(volume, start, end - start, writable: false);
    using var stream = new MemoryStream();
    MemoryStream? lzma = null;
    long lzmaStart = 0;

    void FlushLzma() {
      if (lzma == null)
        return;
      lzma.Position = 0;
      var properties = new byte[5];
      lzma.ReadExactly(properties);
      while (lzma.Position < lzma.Length) {
        var decoder = new LzmaDecoder(lzma, properties);
        decoder.Decode(stream);
      }

      lzma = null;
    }

    var check = new byte[4];
    while (true) {
      var offset = start + body.Position;
      var type = B1Int.Read(body);
      if (type == null)
        break;
      if (type == EmptyBlockType)
        continue;

      Stream blockStream = body;
      if (cipher != null) {
        if (type != AesBlockType)
          throw new InvalidDataException("An encrypted B1 archive may only hold AES blocks.");
        var encrypted = B1Int.ReadBinary(body) ?? throw new InvalidDataException("Null B1 AES block.");
        blockStream = new MemoryStream(cipher.Block(false, offset, encrypted));
        type = B1Int.ReadRequired(blockStream);
      }

      if (type is not (PlainBlockType or FirstLzmaBlockType or NextLzmaBlockType))
        throw new InvalidDataException($"Unknown B1 block type {type}.");
      var data = B1Int.ReadBinary(blockStream) ?? throw new InvalidDataException("Null B1 block content.");
      if (data.Length == 0)
        throw new InvalidDataException("Empty B1 block.");
      blockStream.ReadExactly(check);
      if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(check) != B1Int.Adler32(data))
        throw new InvalidDataException($"B1 block at offset {offset} fails its Adler-32 check.");

      if (type == NextLzmaBlockType) {
        if (lzma == null)
          throw new InvalidDataException("B1 LZMA continuation block without a first block.");
        lzma.Write(data);
        continue;
      }

      FlushLzma();
      if (type == PlainBlockType) {
        this._chunkStart[offset] = stream.Position;
        stream.Write(data);
      } else {
        this.IsCompressed = true;
        lzma = new MemoryStream();
        lzma.Write(data);
        lzmaStart = stream.Position;
        this._chunkStart[offset] = lzmaStart;
      }
    }

    FlushLzma();
    return stream.ToArray();
  }

  private MemoryStream _Seek((long Volume, long Block, long Record) pointer) {
    if (pointer.Volume != 1 || !this._chunkStart.TryGetValue(pointer.Block, out var chunk))
      throw new InvalidDataException($"B1 record pointer {pointer.Volume}/{pointer.Block}/{pointer.Record} addresses no chunk.");
    var position = chunk + pointer.Record;
    if (pointer.Record < 0 || position > this._stream.Length)
      throw new InvalidDataException("B1 record pointer runs past the archive stream.");
    return new MemoryStream(this._stream, (int)position, this._stream.Length - (int)position, writable: false);
  }

  private static (long, long, long) ReadPointer(Stream s) => (B1Int.ReadRequired(s), B1Int.ReadRequired(s), B1Int.ReadRequired(s));

  private static (long Id, long? Parent, string Name, DateTime? Modified) ReadHeader(Stream s) {
    var id = B1Int.ReadRequired(s);
    var parent = B1Int.Read(s);
    var name = B1Int.ReadText(s) ?? throw new InvalidDataException("B1 record has no name.");
    DateTime? modified = null;
    while (B1Int.Read(s) is { } code) {
      switch (code) {
        case 0:
          var ms = B1Int.Read(s);
          if (ms is > -62135596800000 and < 253402300799000)
            modified = DateTime.UnixEpoch.AddMilliseconds(ms.Value);
          break;
        case 1 or 2:
          _ = B1Int.Read(s);
          _ = B1Int.Read(s);
          break;
        default:
          throw new InvalidDataException($"Unknown B1 attribute {code}.");
      }
    }

    return (id, parent, name, modified);
  }

  private void _ReadCatalog((long, long, long) start, long? total) {
    var paths = new Dictionary<long, string>();
    var s = this._Seek(start);
    long count = 0;
    while (total == null || count < total) {
      var type = B1Int.Read(s);
      if (type == null)
        break;
      switch (type) {
        case RecordPointerType:
          s = this._Seek(ReadPointer(s));
          break;
        case CatalogFileType or CatalogFolderType: {
          ++count;
          var pointer = ReadPointer(s);
          var (id, parent, name, modified) = ReadHeader(s);
          var size = type == CatalogFileType ? B1Int.ReadRequired(s) : 0;
          if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new InvalidDataException($"B1 record name '{name}' is not a plain name.");
          var path = parent == null ? name
            : paths.TryGetValue(parent.Value, out var parentPath) ? parentPath + "/" + name
            : throw new InvalidDataException($"B1 record '{name}' names an unknown parent folder.");
          if (type == CatalogFolderType)
            paths[id] = path;
          this._entries.Add(new B1Entry {
            FileName = path, IsDirectory = type == CatalogFolderType, Size = size, LastModified = modified,
            Id = id, ParentId = parent, Pointer = pointer,
          });
          break;
        }
        case EmptyRecordType:
          break;
        default:
          throw new InvalidDataException($"Unexpected B1 record type {type} in the catalog.");
      }
    }
  }

  /// <summary>Returns a file's content (empty for folders).</summary>
  /// <exception cref="InvalidDataException">The complete record is missing or does not match the catalog.</exception>
  public byte[] Extract(B1Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      return [];
    var s = this._Seek(entry.Pointer);
    if (B1Int.Read(s) != CompleteFileType)
      throw new InvalidDataException($"B1 catalog pointer of '{entry.FileName}' does not lead to its file record.");
    var (id, _, _, _) = ReadHeader(s);
    if (id != entry.Id)
      throw new InvalidDataException($"B1 file record id {id} does not match the catalog's {entry.Id}.");
    var content = B1Int.ReadBinary(s) ?? [];
    if (content.LongLength != entry.Size)
      throw new InvalidDataException($"B1 file '{entry.FileName}' holds {content.Length} bytes, not {entry.Size}.");
    return content;
  }
}
