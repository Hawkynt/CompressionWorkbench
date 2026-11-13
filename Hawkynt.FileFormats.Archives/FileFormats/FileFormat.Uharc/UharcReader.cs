using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Uharc;

/// <summary>
/// Reads UHARC 0.6 archives (magic <c>UHA\x06</c>) as written by UHARC 0.6/0.6a/0.6b, UHARC 0.4
/// archives (<c>UHA\x04</c>) and UHARC 0.2 archives (<c>UHA\x02</c>). Layout and codecs are
/// described in docs/UHARC-ON-DISK.md.
/// </summary>
/// <remarks>
/// The directory is always readable; file data decodes for every 0.6 method (store, RLE, LZ78,
/// LZP, PPM and ALZ), every 0.4 mode but PPM and every 0.2 mode, and every extracted file is checked
/// against its stored CRC-32.
/// </remarks>
public sealed class UharcReader {

  private readonly byte[] _archive;
  private readonly int _base;
  private readonly UharcHeader _header = null!;
  private readonly Uharc02Header? _legacy;
  private readonly Uharc04Header? _v04;
  private readonly byte[]? _password;
  private readonly List<UharcEntry> _entries = [];
  private readonly List<long> _fileEnds = [];
  private byte[]? _stream;

  /// <summary>Key in <see cref="Exception.Data"/> that marks an <see cref="InvalidDataException"/> as "wrong password".</summary>
  public const string WrongPasswordKey = "Uharc.WrongPassword";

  private static InvalidDataException WrongPassword() {
    var e = new InvalidDataException("UHARC: wrong password or corrupt directory.");
    e.Data[WrongPasswordKey] = true;
    return e;
  }

  /// <summary>Gets the entries of the archive in stored order.</summary>
  public IReadOnlyList<UharcEntry> Entries => this._entries;

  /// <summary>Gets the format version: 6 for UHARC 0.6, 4 for 0.4, 3 for 0.3, 2 for 0.2.</summary>
  public int Version => this._legacy != null ? Uharc02Header.Version : this._v04?.Version ?? UharcHeader.Version;

  /// <summary>Gets the stream method byte (see <see cref="MethodName"/>).</summary>
  public int Method => this._legacy?.Method ?? this._v04?.Method ?? this._header.Method;

  /// <summary>Gets the name of the compression mode, as <c>uharc l</c> prints it (0.2: the mode switch).</summary>
  public string MethodName => this._legacy != null ? Uharc02Header.MethodName(this._legacy.Method)
    : this._v04 != null && this.Method is >= UharcMethods.Lz78First and <= UharcMethods.Lz78Last ? "LZW"
    : UharcMethods.Name(this.Method);

  /// <summary>Gets the dictionary size in bytes (0.2 and 0.4 LZW: codes), 0 for the store method.</summary>
  public int DictionarySize => this._legacy != null ? Uharc02Header.DictionarySize(this._legacy.Method)
    : this._v04 != null && this.Method is >= UharcMethods.Lz78First and <= UharcMethods.Lz78Last ? Uharc02Header.DictionarySize(this.Method)
    : UharcMethods.DictionarySize(this.Method);

  /// <summary>Gets whether the archive was created with <c>-pw</c>.</summary>
  public bool IsEncrypted => this._legacy == null && (this._v04 != null
    ? (this._v04.Flags & Uharc04Header.UsesPassword) != 0
    : (this._header.Flags & (UharcHeader.TablesUsePassword | UharcHeader.StreamUsesPassword)) != 0);

  /// <summary>Gets the size of the packed solid stream.</summary>
  public long PackedSize => this._legacy?.PackedSize ?? this._v04?.PackedSize ?? this._header.PackedSize;

  /// <summary>Gets whether the stream's method can be decoded by this reader.</summary>
  public bool CanDecodeMethod => this._legacy != null
    ? this._legacy.Method is 0 or >= 2 and <= 43
    : this._v04 != null
    ? this._v04.Method is UharcMethods.Store or UharcMethods.Rle or >= UharcMethods.Lz78First and <= UharcMethods.LzpLast
      or >= UharcMethods.AlzFirst and <= UharcMethods.AlzLast
    : this._header.Method is UharcMethods.Store or UharcMethods.Rle
    or >= UharcMethods.Lz78First and <= UharcMethods.Lz78Last
    or >= UharcMethods.LzpFirst and <= UharcMethods.LzpLast
    or >= UharcMethods.PpmFirst and <= UharcMethods.PpmLast
    or >= UharcMethods.AlzFirst and <= UharcMethods.AlzLast;

  /// <summary>Opens a UHARC 0.6 archive (optionally behind a self-extractor stub) or a UHARC 0.2 archive.</summary>
  /// <param name="stream">The archive; read completely into memory.</param>
  /// <param name="password">The <c>-pw</c> password, if the archive has one.</param>
  /// <exception cref="InvalidDataException">The data is not a well-formed UHARC 0.6 archive.</exception>
  /// <exception cref="InvalidOperationException">The directory is password protected and no password was given.</exception>
  /// <exception cref="InvalidDataException">Also thrown for a wrong password.</exception>
  public UharcReader(Stream stream, string? password = null) {
    ArgumentNullException.ThrowIfNull(stream);
    using var copy = new MemoryStream();
    if (stream.CanSeek)
      stream.Position = 0;
    stream.CopyTo(copy);
    this._archive = copy.ToArray();
    this._base = FindArchiveStart(this._archive);
    if (this._archive.Length - this._base > 3 && this._archive[this._base + 3] == Uharc02Header.Version) {
      this._legacy = Uharc02Header.Parse(this._archive.AsSpan(this._base));
      this.ReadDirectory02();
      return;
    }

    this._password = string.IsNullOrEmpty(password) ? null : Encoding.Latin1.GetBytes(password);
    if (Uharc04Header.HasMagic(this._archive.AsSpan(this._base))) {
      if (this._archive[this._base + 3] != 4)
        throw new InvalidDataException($"UHARC: archive version {this._archive[this._base + 3]} is not supported.");

      this._v04 = Uharc04Header.Parse(this._archive.AsSpan(this._base));
      if ((this._v04.Flags & Uharc04Header.UsesPassword) != 0 && this._password == null)
        throw new InvalidOperationException("UHARC: the archive is password protected; a password is required.");

      this.ReadDirectory04();
      return;
    }

    this._header = UharcHeader.Parse(this._archive.AsSpan(this._base));
    if ((this._header.Flags & UharcHeader.TablesUsePassword) != 0 && this._password == null)
      throw new InvalidOperationException("UHARC: the archive is password protected; a password is required.");

    this.ReadDirectory();
  }

  /// <summary>
  /// Locates the archive: offset 0 for a plain archive, or the <c>UHA\x06</c> header that
  /// follows the last PE section of a self-extractor stub.
  /// </summary>
  private static int FindArchiveStart(byte[] data) {
    if (UharcHeader.HasMagic(data) || data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z')
      return 0;

    var pe = BitConverter.ToInt32(data, 0x3C);
    if (pe <= 0 || pe > data.Length - 24 || data[pe] != 'P' || data[pe + 1] != 'E')
      return 0;

    var sections = BitConverter.ToUInt16(data, pe + 6);
    var optional = BitConverter.ToUInt16(data, pe + 20);
    var table = pe + 24 + optional;
    var end = 0L;
    for (var i = 0; i < sections; ++i) {
      var entry = table + i * 40;
      if (entry + 40 > data.Length)
        return 0;
      end = Math.Max(end, (long)BitConverter.ToUInt32(data, entry + 20) + BitConverter.ToUInt32(data, entry + 16));
    }

    return end > 0 && end < data.Length && UharcHeader.HasMagic(data.AsSpan((int)end)) ? (int)end : 0;
  }

  private ReadOnlySpan<byte> TablePassword
    => (this._header.Flags & UharcHeader.TablesUsePassword) != 0 ? this._password : UharcCipher.DefaultPassword;

  private byte[] ReadTable(ref int offset, int length, int unpackedSize, bool compressed) {
    if (length < 0 || offset > this._archive.Length - length)
      throw new InvalidDataException("UHARC: archive directory is truncated.");

    var table = this._archive.AsSpan(offset, length).ToArray();
    offset += length;
    UharcCipher.Apply(table, this.TablePassword);
    if (!compressed)
      return table;

    try {
      return UharcLzpDecoder.Decode(table, 0, table.Length, UharcMethods.Table, unpackedSize, []);
    } catch (InvalidDataException) when ((this._header.Flags & UharcHeader.TablesUsePassword) != 0) {
      // A wrong password turns the table into noise long before its check can be read.
      throw WrongPassword();
    }
  }

  private void ReadDirectory() {
    var h = this._header;
    var n = h.FileCount;
    var offset = this._base + UharcHeader.Size;
    if (h.PackedSize > this._archive.Length - offset)
      throw new InvalidDataException("UHARC: packed stream is truncated.");

    offset += (int)h.PackedSize;
    var crcs = this.ReadTable(ref offset, 4 * n, 4 * n, compressed: false);
    var names = this.ReadTable(ref offset, h.NamesPackedSize, h.NamesSize, compressed: true);
    var tableCrc = new Crc32();
    tableCrc.Update(crcs);
    tableCrc.Update(names);
    if ((ushort)tableCrc.Value != h.CrcTable)
      throw (h.Flags & UharcHeader.TablesUsePassword) != 0
        ? WrongPassword()
        : new InvalidDataException("UHARC: name table check failed.");

    var meta = this.ReadTable(ref offset, h.MetadataPackedSize, 11 * n, compressed: true);
    if ((ushort)Crc32.Compute(meta) != h.MetadataCrc)
      throw new InvalidDataException("UHARC: file table check failed.");

    var position = 0;
    var directories = new string[h.DirectoryCount];
    for (var i = 0; i < directories.Length; ++i)
      directories[i] = ReadName(names, ref position);

    var emitted = new HashSet<string>(StringComparer.Ordinal);
    foreach (var directory in directories)
      this.AddDirectory(directory, emitted);

    long size = 0, streamOffset = 0;
    for (var i = 0; i < n; ++i) {
      var name = ReadName(names, ref position);
      var date = (ushort)(meta[2 * i] | meta[2 * i + 1] << 8);
      var time = (ushort)(meta[2 * n + 2 * i] | meta[2 * n + 2 * i + 1] << 8);
      var directory = meta[4 * n + i] | meta[5 * n + i] << 8;
      var delta = (uint)(meta[6 * n + i] | meta[7 * n + i] << 8 | meta[8 * n + i] << 16 | meta[9 * n + i] << 24);
      var attributes = meta[10 * n + i];
      size = (uint)(size - delta);
      if (directory >= directories.Length)
        throw new InvalidDataException("UHARC: file refers to a missing directory.");

      var prefix = directories[directory];
      this._entries.Add(new UharcEntry {
        FileName = prefix.Length == 0 ? name : prefix + "/" + name,
        Size = size,
        Crc32 = BitConverter.ToUInt32(crcs, 4 * i),
        LastModified = UharcEntry.FromDos(date, time),
        Attributes = attributes,
        StreamOffset = streamOffset,
      });
      streamOffset += size;
      this._fileEnds.Add(streamOffset);
    }

    if (streamOffset != h.UnpackedSize)
      throw new InvalidDataException("UHARC: file sizes do not add up to the stream size.");
  }

  private void AddDirectory(string directory, HashSet<string> emitted) {
    if (directory.Length == 0)
      return;

    var slash = directory.LastIndexOf('/');
    if (slash > 0)
      this.AddDirectory(directory[..slash], emitted);
    if (emitted.Add(directory))
      this._entries.Add(new UharcEntry { FileName = directory, IsDirectory = true });
  }

  private static string ReadName(byte[] table, ref int position) {
    if (position >= table.Length)
      throw new InvalidDataException("UHARC: name table is truncated.");

    var length = table[position++];
    if (length > table.Length - position)
      throw new InvalidDataException("UHARC: name table is truncated.");

    var name = Encoding.Latin1.GetString(table, position, length).Replace('\\', '/').Trim('/');
    position += length;
    return name;
  }

  /// <summary>Extracts and CRC-verifies one file.</summary>
  /// <exception cref="NotSupportedException">The archive's compression mode is not supported.</exception>
  /// <exception cref="InvalidDataException">The data is corrupt or fails its CRC.</exception>
  public byte[] Extract(UharcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0)
      return [];

    var stream = this.DecodeStream();
    var data = stream.AsSpan((int)entry.StreamOffset, (int)entry.Size).ToArray();
    if (this._legacy != null) {
      if (this._legacy.Method != 0)
        UharcExecutableFilter.RestoreLegacy(data);
    } else if (this._v04 != null)
      RestoreFilters(data, this._v04.Filters, this._v04.Method, swapOperandBytes: false);
    else
      RestoreFilters(data, this._header.Filters, this._header.Method, swapOperandBytes: this._header.Method is >= UharcMethods.PpmFirst and <= UharcMethods.PpmLast);

    if (Crc32.Compute(data) != entry.Crc32)
      throw new InvalidDataException($"UHARC: CRC mismatch in '{entry.FileName}'.");

    return data;
  }

  /// <summary>
  /// Undoes the 0.4/0.6 preprocessing of one file: a fax page alone (stored archives too), else,
  /// for compressed streams, executable then multimedia.
  /// </summary>
  private static void RestoreFilters(Span<byte> data, byte filters, int method, bool swapOperandBytes) {
    if ((filters & UharcHeader.FaxFilter) != 0 && data.Length == UharcFaxFilter.PageSize) {
      UharcFaxFilter.Restore(data);
      return;
    }

    if (method == UharcMethods.Store)
      return;
    if ((filters & UharcHeader.ExecutableFilter) != 0)
      UharcExecutableFilter.Restore(data, swapOperandBytes);
    if ((filters & UharcHeader.MultimediaFilter) != 0)
      UharcMultimediaFilter.Restore(data);
  }

  private byte[] DecodeStream() {
    if (this._stream != null)
      return this._stream;
    if (this._legacy != null)
      return this._stream = this.DecodeStream02();
    if (this._v04 != null)
      return this._stream = this.DecodeStream04();

    var h = this._header;
    if (!this.CanDecodeMethod)
      throw new NotSupportedException($"UHARC: {UharcMethods.Name(h.Method)} streams (method {h.Method}) are not supported.");
    if (h.UnpackedSize > Array.MaxLength)
      throw new NotSupportedException("UHARC: archive is too large to unpack in memory.");

    var offset = this._base + UharcHeader.Size;
    var length = (int)h.PackedSize;
    var packed = this._archive;
    if ((h.Flags & UharcHeader.StreamUsesPassword) != 0) {
      if (this._password == null)
        throw new InvalidOperationException("UHARC: the archive data is password protected; a password is required.");

      packed = this._archive.AsSpan(offset, length).ToArray();
      UharcCipher.Apply(packed, this._password);
      offset = 0;
    }

    var size = (int)h.UnpackedSize;
    try {
      this._stream = h.Method switch {
        UharcMethods.Store when length == size => packed.AsSpan(offset, size).ToArray(),
        UharcMethods.Store => throw new InvalidDataException("UHARC: stored stream size does not match."),
        UharcMethods.Rle => UharcRleDecoder.Decode(packed, offset, length, size),
        >= UharcMethods.Lz78First and <= UharcMethods.Lz78Last => UharcLz78Decoder.Decode(packed, offset, length, h.Method, size),
        >= UharcMethods.LzpFirst and <= UharcMethods.LzpLast => UharcLzpDecoder.Decode(packed, offset, length, h.Method, size, this._fileEnds),
        >= UharcMethods.PpmFirst and <= UharcMethods.PpmLast => UharcPpmDecoder.Decode(packed, offset, length, h.Method, size, this._fileEnds,
          recordModel: (h.Filters & UharcHeader.RecordFileModel) != 0),
        _ => UharcAlzDecoder.Decode(packed, offset, length, h.Method, size, this._fileEnds),
      };
    } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException) {
      // Only a damaged stream can steer the models outside their tables.
      throw new InvalidDataException("UHARC: packed stream is corrupt.", e);
    }

    return this._stream;
  }

  // ── UHARC 0.2 ──────────────────────────────────────────────────────────

  private byte[] ReadTable02(ref int offset, int length, int method, int unpackedSize, Crc32 tablesCrc) {
    if (length < 0 || offset > this._archive.Length - length)
      throw new InvalidDataException("UHARC: archive directory is truncated.");

    var table = this._archive.AsSpan(offset, length).ToArray();
    offset += length;
    Uharc02Cipher.Apply(table);
    var plain = method == 0 ? table : Uharc02LzwDecoder.Decode(table, 0, table.Length, method, unpackedSize);
    if (plain.Length != unpackedSize)
      throw new InvalidDataException("UHARC: archive directory is truncated.");
    tablesCrc.Update(plain);
    return plain;
  }

  private void ReadDirectory02() {
    var h = this._legacy!;
    var n = h.FileCount;
    var offset = this._base + Uharc02Header.Size;
    if (n < 0 || h.PackedSize > this._archive.Length - offset)
      throw new InvalidDataException("UHARC: packed stream is truncated.");

    offset += (int)h.PackedSize;
    var tablesCrc = new Crc32();
    byte[] crcs, names, times, sizes;
    try {
      crcs = this.ReadTable02(ref offset, 4 * n, 0, 4 * n, tablesCrc);
      names = this.ReadTable02(ref offset, h.NamesPackedSize, 7, h.NamesSize, tablesCrc);
      times = this.ReadTable02(ref offset, h.TimesPackedSize, 2, 4 * n, tablesCrc);
      sizes = this.ReadTable02(ref offset, h.SizesPackedSize, 7, 7 * n, tablesCrc);
    } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException) {
      throw new InvalidDataException("UHARC: archive directory is corrupt.", e);
    }

    if (tablesCrc.Value != h.TablesCrc)
      throw new InvalidDataException("UHARC: archive directory check failed.");

    var position = 0;
    var directories = new string[h.DirectoryCount];
    for (var i = 0; i < directories.Length; ++i)
      directories[i] = ReadName(names, ref position);

    var emitted = new HashSet<string>(StringComparer.Ordinal);
    foreach (var directory in directories)
      this.AddDirectory(directory, emitted);

    long size = 0, streamOffset = 0;
    for (var i = 0; i < n; ++i) {
      var name = ReadName(names, ref position);
      var date = (ushort)(times[2 * i] | times[2 * i + 1] << 8);
      var time = (ushort)(times[2 * n + 2 * i] | times[2 * n + 2 * i + 1] << 8);
      var delta = BitConverter.ToUInt32(sizes, 4 * i);
      var directory = sizes[4 * n + 2 * i] | sizes[4 * n + 2 * i + 1] << 8;
      size = (uint)(size - delta);
      if (directory >= directories.Length)
        throw new InvalidDataException("UHARC: file refers to a missing directory.");

      var prefix = directories[directory];
      this._entries.Add(new UharcEntry {
        FileName = prefix.Length == 0 ? name : prefix + "/" + name,
        Size = size,
        Crc32 = BitConverter.ToUInt32(crcs, 4 * i),
        LastModified = UharcEntry.FromDos(date, time),
        Attributes = sizes[6 * n + i],
        StreamOffset = streamOffset,
      });
      streamOffset += size;
      this._fileEnds.Add(streamOffset);
    }

    if (streamOffset != h.UnpackedSize)
      throw new InvalidDataException("UHARC: file sizes do not add up to the stream size.");
  }

  private byte[] DecodeStream02() {
    var h = this._legacy!;
    if (!this.CanDecodeMethod)
      throw new NotSupportedException($"UHARC: 0.2 {Uharc02Header.MethodName(h.Method)} streams (method {h.Method}) are not supported.");
    if (h.UnpackedSize > Array.MaxLength)
      throw new NotSupportedException("UHARC: archive is too large to unpack in memory.");

    var offset = this._base + Uharc02Header.Size;
    var length = (int)h.PackedSize;
    var size = (int)h.UnpackedSize;
    try {
      return h.Method switch {
        0 when length == size => this._archive.AsSpan(offset, size).ToArray(),
        0 => throw new InvalidDataException("UHARC: stored stream size does not match."),
        <= 7 => Uharc02LzwDecoder.Decode(this._archive, offset, length, h.Method, size),
        _ => Uharc02Decoder.Decode(this._archive, offset, length, h.Method, size),
      };
    } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException) {
      throw new InvalidDataException("UHARC: packed stream is corrupt.", e);
    }
  }

  // ── UHARC 0.3 / 0.4 ────────────────────────────────────────────────────

  private ReadOnlySpan<byte> Password04
    => (this._v04!.Flags & Uharc04Header.UsesPassword) != 0 ? this._password : UharcCipher.DefaultPassword;

  /// <summary>Reads one directory table: deciphered, then unpacked with <paramref name="method"/> (0: stored).</summary>
  private byte[] ReadTable04(ref int offset, int length, int method, int unpackedSize) {
    if (method == UharcMethods.Store)
      length = unpackedSize;
    if (length < 0 || offset > this._archive.Length - length)
      throw new InvalidDataException("UHARC: archive directory is truncated.");

    var table = this._archive.AsSpan(offset, length).ToArray();
    offset += length;
    if (unpackedSize == 0)
      return [];

    UharcCipher.Apply(table, this.Password04);
    var plain = method switch {
      UharcMethods.Store => table,
      UharcMethods.Table => Uharc04LzpDecoder.Decode(table, 0, table.Length, method, unpackedSize, []),
      _ => Uharc02LzwDecoder.Decode(table, 0, table.Length, method, unpackedSize),
    };
    if (plain.Length != unpackedSize)
      throw new InvalidDataException("UHARC: archive directory is truncated.");
    return plain;
  }

  private void ReadDirectory04() {
    var h = this._v04!;
    var n = h.FileCount;
    var offset = this._base + Uharc04Header.Size;
    if (h.PackedSize > this._archive.Length - offset)
      throw new InvalidDataException("UHARC: packed stream is truncated.");

    offset += (int)h.PackedSize;
    byte[] crcs, names, dates, sizes;
    try {
      crcs = this.ReadTable04(ref offset, 4 * n, UharcMethods.Store, 4 * n);
      names = this.ReadTable04(ref offset, h.NamesPackedSize, UharcMethods.Table, h.NamesSize);
      var namesCrc = new Crc32();
      namesCrc.Update(crcs);
      namesCrc.Update(names);
      if ((ushort)namesCrc.Value != h.NamesCrc)
        throw new InvalidDataException("UHARC: name table check failed.");

      dates = this.ReadTable04(ref offset, h.DatesPackedSize, 2, 4 * n);
      sizes = this.ReadTable04(ref offset, h.SizesPackedSize, 7, 7 * n);
    } catch (InvalidDataException) when (this.IsEncrypted) {
      // A wrong password turns every table into noise long before its check can be read.
      throw WrongPassword();
    } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException) {
      throw this.IsEncrypted ? WrongPassword() : new InvalidDataException("UHARC: archive directory is corrupt.", e);
    }

    if ((ushort)Crc32.Compute(dates) != h.DatesCrc || (ushort)Crc32.Compute(sizes) != h.SizesCrc)
      throw new InvalidDataException("UHARC: file table check failed.");

    var position = 0;
    var directories = new string[h.DirectoryCount];
    for (var i = 0; i < directories.Length; ++i)
      directories[i] = ReadName(names, ref position);

    var emitted = new HashSet<string>(StringComparer.Ordinal);
    foreach (var directory in directories)
      this.AddDirectory(directory, emitted);

    long size = 0, streamOffset = 0;
    for (var i = 0; i < n; ++i) {
      var name = ReadName(names, ref position);
      var date = (ushort)(dates[2 * i] | dates[2 * i + 1] << 8);
      var time = (ushort)(dates[2 * n + 2 * i] | dates[2 * n + 2 * i + 1] << 8);
      var directory = sizes[i] | sizes[n + i] << 8;
      var delta = (uint)(sizes[2 * n + i] | sizes[3 * n + i] << 8 | sizes[4 * n + i] << 16 | sizes[5 * n + i] << 24);
      size = (uint)(size - delta);
      if (directory >= directories.Length)
        throw new InvalidDataException("UHARC: file refers to a missing directory.");

      var prefix = directories[directory];
      this._entries.Add(new UharcEntry {
        FileName = prefix.Length == 0 ? name : prefix + "/" + name,
        Size = size,
        Crc32 = BitConverter.ToUInt32(crcs, 4 * i),
        LastModified = UharcEntry.FromDos(date, time),
        Attributes = sizes[6 * n + i],
        StreamOffset = streamOffset,
      });
      streamOffset += size;
      this._fileEnds.Add(streamOffset);
    }

    if (streamOffset != h.UnpackedSize)
      throw new InvalidDataException("UHARC: file sizes do not add up to the stream size.");
  }

  private byte[] DecodeStream04() {
    var h = this._v04!;
    if (!this.CanDecodeMethod)
      throw new NotSupportedException($"UHARC: 0.{h.Version} {UharcMethods.Name(h.Method)} streams (method {h.Method}) are not supported.");
    if (h.UnpackedSize > Array.MaxLength)
      throw new NotSupportedException("UHARC: archive is too large to unpack in memory.");

    var offset = this._base + Uharc04Header.Size;
    var length = (int)h.PackedSize;
    var packed = this._archive;
    if ((h.Flags & Uharc04Header.UsesPassword) != 0) {
      packed = this._archive.AsSpan(offset, length).ToArray();
      UharcCipher.Apply(packed, this._password);
      offset = 0;
    }

    var size = (int)h.UnpackedSize;
    try {
      return h.Method switch {
        UharcMethods.Store when length == size => packed.AsSpan(offset, size).ToArray(),
        UharcMethods.Store => throw new InvalidDataException("UHARC: stored stream size does not match."),
        UharcMethods.Rle => UharcRleDecoder.Decode(packed, offset, length, size),
        <= UharcMethods.Lz78Last => Uharc02LzwDecoder.Decode(packed, offset, length, h.Method, size),
        <= UharcMethods.LzpLast => Uharc04LzpDecoder.Decode(packed, offset, length, h.Method, size, this._fileEnds),
        _ => Uharc04AlzDecoder.Decode(packed, offset, length, h.Method, size, this._fileEnds),
      };
    } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException) {
      throw new InvalidDataException("UHARC: packed stream is corrupt.", e);
    }
  }
}
