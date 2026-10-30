using Compression.Core.Checksums;
using Compression.Registry;

namespace FileFormat.SevenZip;

/// <summary>
/// Reads entries from a 7z archive.
/// </summary>
/// <remarks>
/// <para>
/// 7z archives use solid compression: multiple files are packed into a single compressed block
/// (folder), so a file is reached by decoding its folder from the start. The reader decodes
/// folders as streams — memory stays bounded by the coders' windows, not by the folder's size —
/// and keeps the folder it decoded last open, so extracting the entries of a folder in order
/// decodes it once.
/// </para>
/// <para>
/// An archive behind a self-extractor stub is found by its signature. Every file's CRC is
/// checked; a data error in an encrypted folder is reported as a probable wrong password.
/// </para>
/// </remarks>
public sealed class SevenZipReader : IDisposable {
  /// <summary>How far into a file the reader looks for the signature of an archive behind an SFX stub.</summary>
  private const int MaxSfxStubSize = 1 << 22;

  private readonly Stream _stream;
  private readonly Stream _archive;
  private readonly object _gate = new();
  private readonly bool _leaveOpen;
  private readonly SevenZipKeyCache _keys;
  private readonly SevenZipDatabase _database;
  private readonly List<SevenZipEntry> _entries = [];
  private readonly long[] _packOffsets;

  // Per entry: (folder, index of its sub-stream within the folder); -1 when it has no data.
  private readonly (int Folder, int InFolder)[] _entryToStream;
  private readonly int[] _folderFirstSubStream;

  private FolderCursor? _cursor;
  private bool _disposed;

  /// <summary>
  /// Gets the entries in the 7z archive.
  /// </summary>
  public IReadOnlyList<SevenZipEntry> Entries => this._entries;

  /// <summary>Whether the archive's header (file names included) is encrypted.</summary>
  public bool IsHeaderEncrypted => this._database.HeaderEncrypted;

  /// <summary>Where the archive starts in the stream: 0, or the size of a self-extractor stub.</summary>
  public long ArchiveOffset { get; }

  /// <summary>
  /// What this archive's header holds that <see cref="Entries"/> does not carry, so that a
  /// rewrite from the entries would drop it: the header properties the reader skips, and
  /// anti-items. Empty when a rewrite can keep everything.
  /// </summary>
  internal IReadOnlyList<string> UncarriedMetadata {
    get {
      var list = this._database.Files.FirstOrDefault()?.SkippedProperties is { Count: > 0 } skipped
        ? skipped.Order(StringComparer.Ordinal).ToList()
        : [];
      if (this._database.Files.Exists(static f => f.IsAnti))
        list.Add("anti-items");
      return list;
    }
  }

  /// <summary>
  /// Initializes a new <see cref="SevenZipReader"/> from a seekable stream.
  /// </summary>
  /// <param name="stream">A seekable stream containing the 7z archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="password">Optional password for encrypted archives.</param>
  /// <exception cref="ArchivePasswordException">The header is encrypted and no password, or the wrong one, was given.</exception>
  public SevenZipReader(Stream stream, bool leaveOpen = false, string? password = null) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;
    this._keys = new SevenZipKeyCache(string.IsNullOrEmpty(password) ? null : password);

    this.ArchiveOffset = FindSignature(stream);
    this._archive = this.ArchiveOffset == 0 ? stream : new SevenZipOffsetStream(stream, this.ArchiveOffset);

    this._archive.Position = 0;
    var signature = SevenZipHeader.Read(this._archive);
    if (signature.MajorVersion != 0)
      throw new NotSupportedException($"7z: format version {signature.MajorVersion}.{signature.MinorVersion} is not supported.");

    if (signature.NextHeaderSize == 0) {
      // An archive with nothing in it: 7-Zip writes no header at all.
      this._database = new SevenZipDatabase();
    } else {
      var headerStart = SevenZipConstants.SignatureHeaderSize + signature.NextHeaderOffset;
      if (signature.NextHeaderOffset < 0 || signature.NextHeaderSize is < 0 or > 1L << 30)
        throw new InvalidDataException("7z: the start header points outside any plausible archive.");
      if (headerStart + signature.NextHeaderSize > this._archive.Length)
        throw new InvalidDataException(
          $"7z: the archive is truncated or a volume is missing: its header lies at {headerStart}..{headerStart + signature.NextHeaderSize}, the data ends at {this._archive.Length}.");

      var header = new byte[signature.NextHeaderSize];
      this._archive.Position = headerStart;
      this._archive.ReadExactly(header);
      if (Crc32.Compute(header) != signature.NextHeaderCrc)
        throw new InvalidDataException("7z next header CRC mismatch.");

      this._database = SevenZipHeaderCodec.ReadDatabase(new MemoryStream(header, writable: false), this._archive, this._gate, this._keys);
    }

    this._packOffsets = SevenZipHeaderCodec.PackStreamOffsets(this._database.PackInfo);
    (this._entryToStream, this._folderFirstSubStream) = this.BuildEntries();
  }

  /// <summary>
  /// The archive's offset in <paramref name="stream"/>: 0 when it starts with the signature,
  /// else the first signature (with a valid start-header CRC) within the first 4 MiB — the archive
  /// behind a self-extractor stub.
  /// </summary>
  private static long FindSignature(Stream stream) {
    stream.Position = 0;
    var probe = new byte[(int)Math.Min(stream.Length, MaxSfxStubSize + SevenZipConstants.SignatureHeaderSize)];
    stream.ReadExactly(probe);
    var signature = SevenZipConstants.Signature;
    if (probe.AsSpan().StartsWith(signature))
      return 0;

    for (var at = probe.AsSpan().IndexOf(signature); at >= 0;) {
      if (at + SevenZipConstants.SignatureHeaderSize <= probe.Length
          && Crc32.Compute(probe.AsSpan(at + 12, 20)) == BitConverter.ToUInt32(probe, at + 8))
        return at;
      var next = probe.AsSpan(at + 1).IndexOf(signature);
      at = next < 0 ? -1 : at + 1 + next;
    }
    throw new InvalidDataException("Invalid 7z signature.");
  }

  private ((int, int)[] Map, int[] FolderFirst) BuildEntries() {
    var db = this._database;
    var folderFirst = new int[db.Folders.Count];
    for (int f = 0, s = 0; f < db.Folders.Count; s += db.SubStreams.NumUnpackStreams[f], ++f)
      folderFirst[f] = s;

    var map = new (int, int)[db.Files.Count];
    var folder = 0;
    var inFolder = 0;
    var subStream = 0;
    var firstPack = 0;
    for (var i = 0; i < db.Files.Count; ++i) {
      var file = db.Files[i];
      var entry = new SevenZipEntry {
        Name = file.Name,
        IsDirectory = file.IsDirectory,
        IsAnti = file.IsAnti,
        LastWriteTime = file.LastWriteTime,
        CreationTime = file.CreationTime,
        LastAccessTime = file.LastAccessTime,
        Attributes = file.Attributes,
      };

      if (file.IsEmptyStream) {
        map[i] = (-1, -1);
        entry.Size = 0;
        entry.CompressedSize = 0;
        this._entries.Add(entry);
        continue;
      }

      while (folder < db.Folders.Count && inFolder >= db.SubStreams.NumUnpackStreams[folder]) {
        firstPack += db.Folders[folder].NumPackStreams;
        ++folder;
        inFolder = 0;
      }
      if (folder >= db.Folders.Count)
        throw new InvalidDataException("7z: more files with data than the folders hold.");

      map[i] = (folder, inFolder);
      var f = db.Folders[folder];
      entry.Size = db.SubStreams.UnpackSizes[subStream];
      entry.Crc = db.SubStreams.Digests[subStream];
      entry.Method = SevenZipCoderNames.MethodOf(f);
      entry.IsEncrypted = f.IsEncrypted;
      var folderSize = f.MainUnpackSize;
      var packed = 0L;
      for (var k = 0; k < f.NumPackStreams; ++k)
        packed += db.PackInfo.PackSizes[firstPack + k];
      entry.CompressedSize = folderSize > 0 ? (long)(packed * ((double)entry.Size / folderSize)) : 0;
      this._entries.Add(entry);
      ++inFolder;
      ++subStream;
    }
    return (map, folderFirst);
  }

  private SevenZipFolderDecoder DecoderFor(int folderIndex) {
    var db = this._database;
    var firstPack = 0;
    for (var k = 0; k < folderIndex; ++k)
      firstPack += db.Folders[k].NumPackStreams;
    return new SevenZipFolderDecoder(db.Folders[folderIndex], p => {
      var index = firstPack + p;
      var start = SevenZipConstants.SignatureHeaderSize + this._packOffsets[index];
      var length = db.PackInfo.PackSizes[index];
      if (start + length > this._archive.Length)
        throw new InvalidDataException(
          $"7z: the archive is truncated or a volume is missing: packed stream {index} lies at {start}..{start + length}, the data ends at {this._archive.Length}.");
      return new SevenZipWindowStream(this._archive, this._gate, start, length);
    }, this._keys);
  }

  /// <summary>
  /// Extracts the decompressed data for an entry by index.
  /// </summary>
  /// <param name="entryIndex">The zero-based index of the entry to extract.</param>
  /// <returns>The decompressed file data.</returns>
  public byte[] Extract(int entryIndex) {
    using var output = new MemoryStream();
    this.Extract(entryIndex, output);
    return output.ToArray();
  }

  /// <summary>
  /// Extracts the decompressed data for an entry into a stream, checking its CRC. On a data
  /// error the stream may hold a partial file; the caller discards it.
  /// </summary>
  /// <param name="entryIndex">The zero-based index of the entry to extract.</param>
  /// <param name="output">The stream to write the decompressed data to.</param>
  public void Extract(int entryIndex, Stream output) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, this._entries.Count);
    ArgumentNullException.ThrowIfNull(output);

    var (folder, inFolder) = this._entryToStream[entryIndex];
    if (folder < 0)
      return;

    var entry = this._entries[entryIndex];
    var cursor = this._cursor;
    if (cursor == null || cursor.Folder != folder || cursor.NextInFolder > inFolder) {
      cursor?.Dispose();
      this._cursor = null;
      cursor = new FolderCursor(folder, this.Guard(folder, () => this.DecoderFor(folder).OpenMain()));
      this._cursor = cursor;
    }

    this.Guard(folder, () => {
      // Skip the files before this one, then copy it out with its CRC.
      var first = this._folderFirstSubStream[folder];
      while (cursor.NextInFolder < inFolder) {
        CopyExactly(cursor.Stream, Stream.Null, this._database.SubStreams.UnpackSizes[first + cursor.NextInFolder], null);
        ++cursor.NextInFolder;
      }
      var crc = new Crc32();
      ++cursor.NextInFolder;
      CopyExactly(cursor.Stream, output, entry.Size, crc);
      if (entry.Crc is { } expected && crc.Value != expected)
        throw new InvalidDataException(
          $"CRC-32 mismatch for '{entry.Name}': expected 0x{expected:X8}, computed 0x{crc.Value:X8}.");
      return 0;
    }, () => {
      cursor.Dispose();
      this._cursor = null;
    });
  }

  /// <summary>
  /// Extracts several entries in one pass per folder: <paramref name="open"/> returns the stream
  /// for an entry (or <see langword="null"/> to skip it) and <paramref name="close"/> receives it
  /// back once the entry is complete and its CRC checked. Folders without a wanted entry are not
  /// decoded. Entries without data are not offered.
  /// </summary>
  public void ExtractMany(Func<int, Stream?> open, Action<int, Stream> close) {
    ArgumentNullException.ThrowIfNull(open);
    ArgumentNullException.ThrowIfNull(close);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    var byFolder = new SortedDictionary<int, List<int>>();
    for (var i = 0; i < this._entries.Count; ++i)
      if (this._entryToStream[i].Folder is var f and >= 0) {
        if (!byFolder.TryGetValue(f, out var list))
          byFolder[f] = list = [];
        list.Add(i);
      }

    foreach (var (folder, entries) in byFolder) {
      var outputs = new Stream?[this._database.SubStreams.NumUnpackStreams[folder]];
      var owners = new int[outputs.Length];
      var any = false;
      foreach (var entry in entries) {
        var inFolder = this._entryToStream[entry].InFolder;
        owners[inFolder] = entry;
        outputs[inFolder] = open(entry);
        any |= outputs[inFolder] != null;
      }
      if (!any)
        continue;

      var sink = new SplitterSink(this, folder, owners, outputs, close);
      this.Guard(folder, () => {
        this.DecoderFor(folder).DecodeMain(sink);
        sink.Finish();
        return 0;
      });
    }
  }

  /// <summary>
  /// Runs <paramref name="body"/>, translating what a decoder throws on corrupt input into a 7z
  /// data error — or, in an encrypted folder, into a probable wrong password.
  /// </summary>
  private T Guard<T>(int folder, Func<T> body, Action? onFailure = null) {
    try {
      return body();
    } catch (Exception e) when (SevenZipHeaderCodec.IsDataError(e) && e is not NotSupportedException) {
      onFailure?.Invoke();
      if (this._database.Folders[folder].IsEncrypted)
        throw new ArchivePasswordException($"7z: data error in encrypted folder {folder} ({e.Message}). Wrong password?", passwordWasSupplied: true);
      if (e is InvalidDataException)
        throw;
      throw new InvalidDataException($"7z: data error in folder {folder}: {e.Message}", e);
    } catch {
      onFailure?.Invoke();
      throw;
    }
  }

  private static void CopyExactly(Stream source, Stream destination, long count, Crc32? crc) {
    var buffer = new byte[(int)Math.Clamp(count, 1, 1 << 16)];
    while (count > 0) {
      var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
      if (read <= 0)
        throw new InvalidDataException("7z: a folder ended before its files did.");
      crc?.Update(buffer.AsSpan(0, read));
      destination.Write(buffer, 0, read);
      count -= read;
    }
  }

  /// <summary>The folder decoded last, positioned before its <see cref="NextInFolder"/>-th file.</summary>
  private sealed class FolderCursor(int folder, Stream stream) : IDisposable {
    public int Folder { get; } = folder;
    public Stream Stream { get; } = stream;
    public int NextInFolder { get; set; }
    public void Dispose() => this.Stream.Dispose();
  }

  /// <summary>Divides a folder's output among its files, checking the CRC of every file someone wants.</summary>
  private sealed class SplitterSink : Stream {
    private readonly SevenZipReader _reader;
    private readonly int[] _owners;
    private readonly Stream?[] _outputs;
    private readonly Action<int, Stream> _close;
    private readonly long[] _sizes;
    private readonly Crc32 _crc = new();
    private int _subStream = -1;
    private long _remaining;

    public SplitterSink(SevenZipReader reader, int folder, int[] owners, Stream?[] outputs, Action<int, Stream> close) {
      this._reader = reader;
      this._owners = owners;
      this._outputs = outputs;
      this._close = close;
      var first = reader._folderFirstSubStream[folder];
      this._sizes = reader._database.SubStreams.UnpackSizes.AsSpan(first, outputs.Length).ToArray();
      this.Advance();
    }

    private void Advance() {
      for (;;) {
        ++this._subStream;
        this._crc.Reset();
        if (this._subStream >= this._sizes.Length)
          return;
        this._remaining = this._sizes[this._subStream];
        if (this._remaining > 0)
          return;
        this.CompleteFile(); // an empty sub-stream (7-Zip never writes one, but it is legal)
        return;
      }
    }

    private void CompleteFile() {
      if (this._outputs[this._subStream] is { } output) {
        var entry = this._reader._entries[this._owners[this._subStream]];
        if (entry.Crc is { } expected && this._crc.Value != expected)
          throw new InvalidDataException(
            $"CRC-32 mismatch for '{entry.Name}': expected 0x{expected:X8}, computed 0x{this._crc.Value:X8}.");
        this._close(this._owners[this._subStream], output);
      }
      this.Advance();
    }

    public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

    public override void WriteByte(byte value) => this.Write([value]);

    public override void Write(ReadOnlySpan<byte> data) {
      while (!data.IsEmpty) {
        if (this._subStream >= this._sizes.Length)
          throw new InvalidDataException("7z: a folder holds more data than its files.");
        var n = (int)Math.Min(data.Length, this._remaining);
        var slice = data[..n];
        if (this._outputs[this._subStream] is { } output) {
          this._crc.Update(slice);
          output.Write(slice);
        }
        this._remaining -= n;
        data = data[n..];
        if (this._remaining == 0)
          this.CompleteFile();
      }
    }

    /// <summary>Checks that every file of the folder was completed.</summary>
    public void Finish() {
      if (this._subStream < this._sizes.Length)
        throw new InvalidDataException("7z: a folder ended before its files did.");
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    this._cursor?.Dispose();
    this._cursor = null;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}

/// <summary>A seekable read-only view of <paramref name="inner"/> from <paramref name="offset"/> on.</summary>
internal sealed class SevenZipOffsetStream(Stream inner, long offset) : Stream {
  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => inner.Length - offset;
  public override long Position { get => inner.Position - offset; set => inner.Position = value + offset; }
  public override int Read(byte[] buffer, int off, int count) => inner.Read(buffer, off, count);
  public override int Read(Span<byte> buffer) => inner.Read(buffer);
  public override long Seek(long position, SeekOrigin origin) => origin switch {
    SeekOrigin.Begin => inner.Seek(position + offset, SeekOrigin.Begin) - offset,
    SeekOrigin.Current => inner.Seek(position, SeekOrigin.Current) - offset,
    _ => inner.Seek(position, SeekOrigin.End) - offset,
  };
  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int off, int count) => throw new NotSupportedException();
}
