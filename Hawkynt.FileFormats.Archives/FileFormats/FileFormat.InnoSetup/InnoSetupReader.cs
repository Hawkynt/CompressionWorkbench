using System.Text;

namespace FileFormat.InnoSetup;

/// <summary>
/// Reads the files an Inno Setup installer carries.
/// </summary>
/// <remarks>
/// <para>Layout: the setup loader exe holds an offset table (at 0x30 before 5.1.5, otherwise in
/// RCDATA resource 11111) pointing at setup-0 (a 64-byte version tag, 12 bytes for 1.2.x, and two compressed header
/// blocks) and setup-1 (the file data as "zlb\x1a" chunks, embedded or in setup-N.bin slices).
/// Header block one holds the setup header and every script section; block two holds the file
/// locations: chunk, offset inside the decompressed chunk, size and checksum.</para>
/// <para>Converted from innoextract (https://github.com/dscharrer/innoextract, zlib licence).
/// Every extracted file is checked against the checksum the compiler stored for it, so a file
/// either comes out exactly as the compiler stored it or the extraction fails.</para>
/// <para>Encrypted files (Encryption=yes) open with the installer's password: ARC4 keyed with MD5
/// (4.2.2 – 5.3.8) or SHA-1 (5.3.9 – 6.3.x) of a per-chunk salt and the password, XChaCha20 with
/// a PBKDF2-SHA256 key from 6.4; Encryption=full (6.5+) encrypts the setup headers too. The
/// password is checked against the one the installer stores before anything is decrypted.</para>
/// </remarks>
public sealed class InnoSetupReader {

  private readonly Stream _stream;
  private readonly InnoOffsets _offsets;
  private readonly InnoSetupInfo _info;
  private readonly List<InnoSetupEntry> _entries = [];
  private readonly string? _sliceDirectory;
  private readonly string? _sliceBase;
  private readonly string? _password;
  private byte[]? _key;

  /// <summary>Gets the setup-data version.</summary>
  public InnoVersion Version => this._info.Version;

  /// <summary>Gets the version as text, e.g. "5.5.7 (unicode)".</summary>
  public string VersionText => this._info.Version.ToString();

  /// <summary>Gets the application name from the setup header.</summary>
  public string AppName => this._info.AppName;

  /// <summary>Gets the application version from the setup header.</summary>
  public string AppVersion => this._info.AppVersion;

  /// <summary>Gets the compressor the files were stored with.</summary>
  public InnoCompression Compression => this._info.Compression;

  /// <summary>Gets whether some files are encrypted.</summary>
  public bool IsEncrypted => this._info.EncryptionUsed;

  /// <summary>Gets the ciphers the installer's files use (empty when none is encrypted).</summary>
  public IReadOnlyList<InnoEncryption> EncryptionMethods =>
    [.. this._info.DataEntries.Select(d => d.Encryption).Where(e => e != InnoEncryption.None).Distinct()];

  /// <summary>Gets the files, named and filtered the way innoextract extracts them.</summary>
  public IReadOnlyList<InnoSetupEntry> Entries => this._entries;

  /// <summary>
  /// Opens an Inno Setup installer (setup.exe) or a bare setup-0 file.
  /// </summary>
  /// <param name="stream">A seekable stream over the whole file.</param>
  /// <exception cref="InvalidDataException">No Inno Setup data, or headers that do not parse.</exception>
  public InnoSetupReader(Stream stream) : this(stream, null) { }

  /// <summary>
  /// Opens an Inno Setup installer with the password its encrypted files (or, with
  /// Encryption=full, its headers) need.
  /// </summary>
  /// <param name="stream">A seekable stream over the whole file.</param>
  /// <param name="password">The installer's password, or <see langword="null"/>.</param>
  /// <exception cref="InvalidDataException">No Inno Setup data, or headers that do not parse.</exception>
  /// <exception cref="Compression.Registry.ArchivePasswordException">Encrypted headers without the right password.</exception>
  public InnoSetupReader(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    this._password = password;
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._stream = stream;
    this._offsets = InnoLoader.Locate(stream);
    if (stream is FileStream fs) {
      this._sliceDirectory = Path.GetDirectoryName(Path.GetFullPath(fs.Name));
      this._sliceBase = Path.GetFileNameWithoutExtension(fs.Name);
    }

    if (this._offsets.HeaderOffset < 0 || this._offsets.HeaderOffset + 64 > stream.Length)
      throw new InvalidDataException("Inno Setup headers lie outside the file.");
    var tag = new byte[64];
    stream.Position = this._offsets.HeaderOffset;
    stream.ReadExactly(tag);
    var version = InnoVersion.Parse(tag);
    this._info = this.LoadInfo(version);
    this.BuildEntries();
  }

  /// <summary>
  /// 6.5.0+: a CRC32 and TSetupEncryptionHeader follow the version tag: EncryptionUse (0 none,
  /// 1 files, 2 full: the header blocks too), KDF salt (16), iterations, base nonce (24) and the
  /// password test. Returns the mode and the password material (null before 6.5).
  /// </summary>
  private static (int Mode, InnoPasswordData? Password) ReadEncryptionHeader(Stream stream, InnoVersion v) {
    if (v.Below(6, 5, 0))
      return (0, null);
    var h = new byte[4 + 49];
    stream.ReadExactly(h);
    if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(h) != global::Compression.Core.Checksums.Crc32.Compute(h.AsSpan(4)))
      throw new InvalidDataException("Inno Setup encryption header CRC mismatch.");
    if (h[4] > 2)
      throw new InvalidDataException($"Unknown Inno Setup encryption mode {h[4]}.");
    return (h[4], new InnoPasswordData {
      Check = InnoPasswordCheck.XChaCha20, Salt = h[5..21],
      Iterations = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(21)),
      BaseNonce = h[25..49], Expected = h[49..53],
    });
  }

  /// <summary>
  /// Cheap detection: a setup loader offset table whose setup-0 starts with an Inno Setup
  /// version tag.
  /// </summary>
  public static bool IsInnoSetup(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    try {
      var offsets = InnoLoader.Locate(stream);
      if (!offsets.TableFound || offsets.HeaderOffset < 0 || offsets.HeaderOffset + 64 > stream.Length)
        return false;
      var tag = new byte[64];
      stream.Position = offsets.HeaderOffset;
      stream.ReadExactly(tag);
      return tag.AsSpan().IndexOf("Inno Setup"u8) >= 0 || InnoVersion.StartsWithLegacyTag(tag);
    } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) {
      return false;
    }
  }

  // Ambiguous or unknown tags: try the listed layout, then each later one, and keep the first
  // that parses cleanly (no warnings, no trailing bytes) -- innoextract's rule.
  private InnoSetupInfo LoadInfo(InnoVersion version) {
    InnoSetupInfo? fallback = null;
    Exception? firstError = null;
    var candidate = (InnoVersion?)version;
    var tryOthers = !version.IsKnown || version.IsAmbiguous;
    while (candidate is { } v) {
      try {
        var info = new InnoSetupInfo { Version = v };
        this._stream.Position = this._offsets.HeaderOffset + version.TagLength;
        var (mode, password) = ReadEncryptionHeader(this._stream, v);
        Action<Span<byte>>? block1 = null, block2 = null;
        if (mode == 2) {
          // Encryption=full: each header block is decrypted sub-block by sub-block, after its CRC
          var key = this._key ??= password!.Key(this._password, unicode: true, Encoding.Latin1);
          var c1 = password!.Cipher(key, 0, InnoPasswordData.HeaderBlock1Slice);
          var c2 = password.Cipher(key, 0, InnoPasswordData.HeaderBlock2Slice);
          (block1, block2) = (c1.Transform, c2.Transform);
        }
        info.LoadPrimary(InnoCodec.ReadHeaderBlock(this._stream, v, block1), out var w1, out var t1);
        if (password != null) {
          info.Password = password;
          info.EncryptionUsed |= mode != 0;
        }
        info.LoadData(InnoCodec.ReadHeaderBlock(this._stream, v, block2), out var w2, out var t2);
        if (w1 + w2 == 0 && !t1 && !t2)
          return info;
        fallback ??= info;
      } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException) {
        firstError ??= ex;
      }
      if (!tryOthers)
        break;
      candidate = v.Next();
      tryOthers = candidate is { IsAmbiguous: true };
    }
    return fallback ?? throw new InvalidDataException($"Inno Setup {version} headers could not be parsed: {firstError?.Message}", firstError);
  }

  // ── naming and selection (innoextract cli/extract.cpp, setup/filename.cpp) ──────────

  private void BuildEntries() {
    var chosen = new Dictionary<string, (InnoFileEntry File, string Path)>(StringComparer.Ordinal);
    var order = new List<string>();
    foreach (var file in this._info.Files) {
      if (file.Location >= this._info.DataEntries.Count)
        continue; // external file, copied from the target system
      var path = ConvertPath(file.Destination);
      if (path.Length == 0)
        continue;
      var key = path.ToLowerInvariant();
      if (!chosen.TryGetValue(key, out var existing)) {
        chosen[key] = (file, path);
        order.Add(key);
        continue;
      }
      var skip = HandleCollision(existing.File, this._info.DataEntries[(int)existing.File.Location], file, this._info.DataEntries[(int)file.Location]);
      if (!skip)
        chosen[key] = (file, file.IsUninstallerExe ? existing.Path : path);
    }

    foreach (var key in order) {
      var (file, path) = chosen[key];
      var data = this._info.DataEntries[(int)file.Location];
      this._entries.Add(new InnoSetupEntry(path, (long)data.FileSize, data.Timestamp, (int)file.Location));
    }
  }

  /// <summary>innoextract's handle_collision: true when the new file loses to the one already chosen.</summary>
  private static bool HandleCollision(InnoFileEntry oldFile, InnoDataEntry oldData, InnoFileEntry newFile, InnoDataEntry newData) {
    var allowTimestamp = true;
    if (!newFile.IgnoreVersion) {
      if (oldData.VersionInfoValid) {
        allowTimestamp = false;
        if (!newData.VersionInfoValid || oldData.FileVersion > newData.FileVersion) {
          if (!newFile.PromptIfOlder)
            return true;
        } else if (newData.FileVersion == oldData.FileVersion && !newFile.OverwriteSameVersion) {
          if (newFile.ReplaceSameVersionIfContentsDiffer && oldData.Checksum.AsSpan().SequenceEqual(newData.Checksum))
            return true;
          if (!newFile.CompareTimeStamp)
            return true;
          allowTimestamp = true;
        }
      } else if (newData.VersionInfoValid)
        allowTimestamp = false;
    }
    if (allowTimestamp && newFile.CompareTimeStamp) {
      if (newData.Timestamp == oldData.Timestamp)
        return true;
      if (newData.Timestamp < oldData.Timestamp && !newFile.PromptIfOlder)
        return true;
    }
    return false;
  }

  /// <summary>
  /// Expands Inno constants to directory names ({app} → app, {{ → {), replaces characters that
  /// are unsafe in paths with '$', and normalises the separators and . / .. segments.
  /// </summary>
  internal static string ConvertPath(string path) {
    var i = 0;
    var expanded = Expand(path, ref i, close: false);
    var parts = new List<string>();
    foreach (var segment in expanded.Split('\\', '/')) {
      if (segment.Length == 0 || segment == ".")
        continue;
      if (segment == "..") {
        if (parts.Count > 0)
          parts.RemoveAt(parts.Count - 1);
        continue;
      }
      parts.Add(segment);
    }
    return string.Join('/', parts);
  }

  private static string Expand(string s, ref int i, bool close) {
    var sb = new StringBuilder();
    while (i < s.Length) {
      var c = s[i];
      if (c != '{' && c != '}') {
        sb.Append(IsUnsafe(c) ? '$' : c);
        ++i;
        continue;
      }
      ++i;
      if (c == '}') {
        if (close)
          break;
        sb.Append('}');
        continue;
      }
      if (i < s.Length && s[i] == '{') {
        sb.Append('{');
        ++i;
        continue;
      }
      var name = Expand(s, ref i, close: true);
      foreach (var ch in name)
        sb.Append(IsUnsafe(ch) ? '$' : ch);
    }
    return sb.ToString();
  }

  private static bool IsUnsafe(char c) => c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*';

  // ── extraction ──────────────────────────────────────────────────────

  /// <summary>The checksum the compiler stored for an entry's bytes.</summary>
  internal (InnoChecksumKind Kind, byte[] Checksum) StoredChecksum(InnoSetupEntry entry) {
    var d = this._info.DataEntries[entry.DataIndex];
    return (d.ChecksumKind, d.Checksum);
  }

  /// <summary>Returns the bytes of one entry, verified against the stored checksum.</summary>
  public byte[] Extract(InnoSetupEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    byte[]? result = null;
    this.ExtractMany([entry], (_, data) => result = data);
    return result!;
  }

  /// <summary>
  /// Decodes the given entries; each chunk is decompressed once for all the files it holds.
  /// </summary>
  public void ExtractMany(IEnumerable<InnoSetupEntry> entries, Action<InnoSetupEntry, byte[]> sink) {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(sink);
    var byChunk = entries
      .GroupBy(e => {
        var d = this._info.DataEntries[e.DataIndex];
        return (d.FirstSlice, d.ChunkOffset, d.ChunkSize, d.Compression, d.Encryption);
      })
      .OrderBy(g => g.Key.FirstSlice).ThenBy(g => g.Key.ChunkOffset);

    foreach (var chunk in byChunk) {
      var needed = chunk.Max(e => this._info.DataEntries[e.DataIndex].FileOffset + this._info.DataEntries[e.DataIndex].FileSize);
      if (needed > int.MaxValue)
        throw new NotSupportedException("Inno Setup chunks larger than 2 GiB are not supported.");
      var decoded = this.ReadChunk(chunk.Key.FirstSlice, chunk.Key.ChunkOffset, chunk.Key.ChunkSize, chunk.Key.Compression, chunk.Key.Encryption, (long)needed);
      foreach (var e in chunk) {
        var d = this._info.DataEntries[e.DataIndex];
        var bytes = decoded.AsSpan((int)d.FileOffset, (int)d.FileSize).ToArray();
        InnoCodec.Unfilter(d.Filter, bytes);
        if (!InnoCodec.Verify(d.ChecksumKind, d.Checksum, bytes))
          throw new InvalidDataException($"Inno Setup {d.ChecksumKind} checksum mismatch for '{e.FileName}'.");
        sink(e, bytes);
      }
    }
  }

  private static ReadOnlySpan<byte> ChunkMagic => "zlb\x1a"u8;

  /// <summary>The key for encrypted chunks, from the password (checked once).</summary>
  private byte[] Key() => this._key ??= this._info.Password.Key(this._password, this._info.Version.IsUnicode, this._info.TextEncoding);

  private byte[] ReadChunk(uint slice, ulong offset, ulong size, InnoCompression compression, InnoEncryption encryption, long needed) {
    var key = encryption == InnoEncryption.None ? null : this.Key();
    using var slices = new SliceReader(this, slice, (long)offset);
    Span<byte> magic = stackalloc byte[4];
    slices.ReadExactly(magic);
    if (!magic.SequenceEqual(ChunkMagic))
      throw new InvalidDataException("Inno Setup chunk has no \"zlb\\x1a\" magic.");
    Stream data = new BoundedReader(slices, (long)size);
    switch (encryption) {
      case InnoEncryption.Arc4Md5 or InnoEncryption.Arc4Sha1: {
        // an 8-byte salt between the magic and the encrypted data
        var salt = new byte[8];
        slices.ReadExactly(salt);
        data = new InnoDecryptingStream(data, InnoPasswordData.Arc4(encryption, salt, key!).Transform);
        break;
      }
      case InnoEncryption.XChaCha20:
        data = new InnoDecryptingStream(data, this._info.Password.Cipher(key!, (long)offset, (int)slice).Transform);
        break;
    }
    using var _ = data;
    try {
      return InnoCodec.Decompress(compression, data, needed);
    } catch (InvalidDataException ex) {
      throw new InvalidDataException($"Inno Setup chunk at slice {slice} offset {offset} ({compression}, {size} bytes): {ex.Message}", ex);
    }
  }

  /// <summary>Opens setup-1 slice <paramref name="index"/>; returns the stream and its data start/end.</summary>
  private (Stream Stream, long Start, long End, bool Owned) OpenSlice(uint index) {
    if (this._offsets.DataOffset != 0) {
      if (index != 0)
        throw new InvalidDataException("An Inno Setup installer with embedded data has only one slice.");
      return (this._stream, this._offsets.DataOffset, this._stream.Length, false);
    }
    if (this._sliceDirectory == null)
      throw new NotSupportedException("This installer keeps its files in external setup-N.bin slices, which need a file path to locate.");

    var perDisk = Math.Max(1u, this._info.SlicesPerDisk);
    var suffix = perDisk == 1 ? $"{index + 1}" : $"{index / perDisk + 1}{(char)('a' + index % perDisk)}";
    // Up to 7.1.0 the header names the slices (OutputBaseFilename); 7.1.1 names them after the
    // setup EXE itself (Setup.FileExtractor.pas FindSliceFilename), so the EXE's name is tried too.
    var bases = new[] { this._info.BaseFilename, this._sliceBase ?? "", "setup" }.Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    foreach (var b in bases) {
      var name = b + "-" + suffix + ".bin";
      var path = Directory.EnumerateFiles(this._sliceDirectory)
        .FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
      if (path == null)
        continue;
      var fs = File.OpenRead(path);
      Span<byte> head = stackalloc byte[16];
      fs.ReadExactly(head[..12]);
      long total;
      if (head[..8].SequenceEqual("idska32\x1a"u8) || head[..8].SequenceEqual("idska16\x1a"u8))
        total = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
      else if (head[..8].SequenceEqual("idskb32\x1a"u8)) {
        fs.ReadExactly(head[12..16]);
        total = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(head[8..]);
      } else {
        fs.Dispose();
        throw new InvalidDataException($"'{name}' is not an Inno Setup slice.");
      }
      if (total > fs.Length)
        total = fs.Length;
      return (fs, 0, total, true);
    }
    throw new InvalidDataException($"Inno Setup slice {index + 1} ({string.Join(" or ", bases.Select(b => b + "-" + suffix + ".bin"))}) was not found.");
  }

  /// <summary>Reads setup-1 data sequentially across slice boundaries.</summary>
  private sealed class SliceReader : Stream {
    private readonly InnoSetupReader _owner;
    private uint _slice;
    private (Stream Stream, long Start, long End, bool Owned) _current;
    private long _pos;

    public SliceReader(InnoSetupReader owner, uint slice, long offset) {
      this._owner = owner;
      this._slice = slice;
      this._current = owner.OpenSlice(slice);
      this._pos = this._current.Start + offset;
      if (this._pos > this._current.End)
        throw new InvalidDataException("Inno Setup chunk offset lies outside its slice.");
    }

    public override int Read(byte[] buffer, int offset, int count) {
      while (this._pos >= this._current.End) {
        if (this._owner._offsets.DataOffset != 0)
          return 0;
        if (this._current.Owned)
          this._current.Stream.Dispose();
        ++this._slice;
        this._current = this._owner.OpenSlice(this._slice);
        this._pos = this._current.Stream.Position; // just past the slice header
      }
      this._current.Stream.Position = this._pos;
      var n = this._current.Stream.Read(buffer, offset, (int)Math.Min(count, this._current.End - this._pos));
      this._pos += n;
      return n;
    }

    protected override void Dispose(bool disposing) {
      if (disposing && this._current.Owned)
        this._current.Stream.Dispose();
      base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>Limits a stream to a number of bytes.</summary>
  private sealed class BoundedReader(Stream inner, long length) : Stream {
    private long _left = length;
    public override int Read(byte[] buffer, int offset, int count) {
      if (this._left <= 0)
        return 0;
      var n = inner.Read(buffer, offset, (int)Math.Min(count, this._left));
      this._left -= n;
      return n;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
