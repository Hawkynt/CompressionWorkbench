using Compression.Core.Crypto;
using Compression.Core.Dictionary.Rar;
using System.Text;

namespace FileFormat.Rar;

/// <summary>
/// Reads RAR archives: RAR 5 (RAR 5.0-7.x, compression algorithm version 0) and RAR 1.5-4.x,
/// single files or volume sets in either naming scheme, behind an SFX module or not, with
/// encrypted data or headers.
/// </summary>
/// <remarks>
/// <para>
/// Opening a <see cref="FileStream"/> of a volume brings the rest of its set from the same
/// directory (<c>name.partN.rar</c>, or <c>name.rar</c>/<c>name.r00</c>/…); a volume that is
/// missing leaves the entries that reach into it listed but <see cref="RarEntry.IsIncomplete"/>,
/// and extracting one of those throws.
/// </para>
/// <para>
/// Every extracted file is checked against its CRC-32 or BLAKE2sp (with RAR 5's key-dependent
/// checksum tweak for encrypted files); a mismatch throws rather than returning wrong bytes.
/// Solid archives decode in order and replay the start of a solid run when an entry is asked
/// for out of order.
/// </para>
/// </remarks>
public sealed class RarReader : IDisposable {
  private const int SfxSearchLimit = 4 * 1024 * 1024;

  private readonly List<Stream> _volumes = [];
  private readonly List<Stream> _owned = [];
  private readonly bool _leaveOpen;
  private readonly Stream _first;
  private readonly string? _password;
  private readonly Rar5Crypto? _rar5Crypto;
  private readonly List<RarItem> _items = [];
  private readonly List<int> _entryItems = [];
  private readonly List<RarEntry> _entries = [];
  private readonly RarArchiveInfo _info = new();
  private bool _disposed;

  // Solid state: the decoder of the solid run in progress and the item it expects next.
  private object? _solidDecoder;
  private int _lastDecoded = -1;

  static RarReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

  /// <summary>Opens a RAR archive (and, for a <see cref="FileStream"/>, the other volumes of its set).</summary>
  /// <param name="stream">A seekable stream holding the archive or its first volume.</param>
  /// <param name="password">The password for encrypted entries or headers, or <see langword="null"/>.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open when this reader is disposed.</param>
  /// <exception cref="InvalidDataException">The stream holds no RAR archive.</exception>
  /// <exception cref="InvalidOperationException">The headers are encrypted and no password was given.</exception>
  public RarReader(Stream stream, string? password, bool leaveOpen = false)
    : this([stream ?? throw new ArgumentNullException(nameof(stream))], password, leaveOpen, discover: true) { }

  /// <summary>Opens a RAR archive from a seekable stream.</summary>
  public RarReader(Stream stream, bool leaveOpen = false) : this(stream, null, leaveOpen) { }

  /// <summary>Opens the volumes of a RAR set given as streams, first volume first.</summary>
  public RarReader(IReadOnlyList<Stream> volumes, string? password, bool leaveOpen = false)
    : this(volumes, password, leaveOpen, discover: false) { }

  private RarReader(IReadOnlyList<Stream> volumes, string? password, bool leaveOpen, bool discover) {
    if (volumes.Count == 0) throw new ArgumentException("No volume given.", nameof(volumes));
    foreach (var v in volumes)
      if (!v.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(volumes));
    this._first = volumes[0];
    this._leaveOpen = leaveOpen;
    this._password = password;
    if (password != null) this._rar5Crypto = new Rar5Crypto(password);

    if (discover && volumes.Count == 1 && volumes[0] is FileStream { Name: { Length: > 0 } path })
      this.OpenSet(volumes[0], path);
    else
      for (var i = 0; i < volumes.Count; ++i)
        this.ReadVolume(volumes[i], i == 0);

    this.BuildEntries();
  }

  /// <summary>Opens the RAR archive or volume set at <paramref name="path"/>.</summary>
  public static RarReader Open(string path, string? password = null)
    => new(File.OpenRead(path), password, leaveOpen: false);

  /// <summary>Gets the entries (files and directories; service records are not listed).</summary>
  public IReadOnlyList<RarEntry> Entries => this._entries;

  /// <summary>Gets a value indicating whether this is a RAR 5 archive.</summary>
  public bool IsRar5 => this._info.Format == 5;

  /// <summary>Gets a value indicating whether this is a RAR 1.5-4.x archive.</summary>
  public bool IsRar4 => this._info.Format == 4;

  /// <summary>Gets the archive format generation: 5, or 4 for RAR 1.5-4.x.</summary>
  public int Version => this._info.Format;

  /// <summary>Gets whether the main header announces a recovery record.</summary>
  public bool HasRecoveryRecord => this._info.HasRecoveryRecord;

  /// <summary>Gets whether the archive is part of a volume set.</summary>
  public bool IsVolume => this._info.IsVolume;

  /// <summary>Gets how many volumes were read.</summary>
  public int VolumeCount => this._volumes.Count;

  /// <summary>Gets the name of the first missing volume, or null when the set is complete.</summary>
  public string? MissingVolume => this._info.MissingVolume;

  /// <summary>Gets whether the archive is solid.</summary>
  public bool IsSolid => this._info.IsSolid;

  /// <summary>Gets whether the archive is locked against changes.</summary>
  public bool IsLocked => this._info.IsLocked;

  /// <summary>Gets whether the headers (file names) are encrypted.</summary>
  public bool HeadersEncrypted => this._info.HeadersEncrypted;

  /// <summary>Gets the bytes of SFX module before the archive in the first volume.</summary>
  public long SfxSize => this._info.SfxSize;

  /// <summary>Gets the archive comment, when there is one.</summary>
  public string? Comment => this._info.Comment ??= this.ReadComment();

  /// <summary>Extracts the entry at <paramref name="entryIndex"/> into memory.</summary>
  public byte[] Extract(int entryIndex) {
    using var ms = new MemoryStream();
    this.Extract(entryIndex, ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Extracts the entry at <paramref name="entryIndex"/> into <paramref name="output"/>,
  /// streaming; the data is checked against the stored CRC-32/BLAKE2sp at the end.
  /// </summary>
  /// <exception cref="InvalidDataException">The data is damaged, the password is wrong, or a volume is missing.</exception>
  /// <exception cref="InvalidOperationException">The entry is encrypted and no password was given.</exception>
  /// <exception cref="NotSupportedException">The entry uses a method this reader refuses.</exception>
  public void Extract(int entryIndex, Stream output) {
    if ((uint)entryIndex >= (uint)this._entries.Count)
      throw new ArgumentOutOfRangeException(nameof(entryIndex));
    ArgumentNullException.ThrowIfNull(output);
    var item = this._items[this._entryItems[entryIndex]];
    if (item.IsDirectory || item.IsSymlinkLike())
      return;
    this.ExtractItem(this._entryItems[entryIndex], output, depth: 0);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    foreach (var s in this._owned) s.Dispose();
    if (!this._leaveOpen) this._first.Dispose();
  }

  // ---------------------------------------------------------------- opening

  private void OpenSet(Stream given, string path) {
    var full = Path.GetFullPath(path);
    var first = this.ReadVolume(given, true);
    if (!this._info.IsVolume) return;

    var seq = RarVolumeNames.SequenceOf(full);
    if (seq == null) return;
    var own = -1;
    for (var i = 0; i < 100_000; ++i) {
      var candidate = Path.GetFullPath(seq(i));
      if (string.Equals(candidate, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { own = i; break; }
      if (i > 0 && !File.Exists(candidate)) break;
    }
    if (own < 0) return;

    if (own > 0 && File.Exists(seq(0))) {
      // Started from a later volume: read the set from its first volume instead.
      this._volumes.Clear();
      this._items.Clear();
      var info = this._info;
      info.Comment = null;
      var start = File.OpenRead(seq(0));
      this._owned.Add(start);
      first = this.ReadVolume(start, true);
      for (var i = 1; ; ++i) {
        if (first == false) return;
        Stream next;
        if (i == own) next = given;
        else {
          var name = seq(i);
          if (!File.Exists(name)) { this.NoteMissing(name, first); return; }
          next = File.OpenRead(name);
          this._owned.Add(next);
        }
        first = this.ReadVolume(next, false);
      }
    }

    for (var i = own + 1; ; ++i) {
      if (first == false) return;
      var name = seq(i);
      if (!File.Exists(name)) { this.NoteMissing(name, first); return; }
      var next = File.OpenRead(name);
      this._owned.Add(next);
      first = this.ReadVolume(next, false);
    }
  }

  private void NoteMissing(string name, bool? moreExpected) {
    if (moreExpected == true || this._items.Any(i => i.SplitAfter))
      this._info.MissingVolume = name;
  }

  /// <summary>Reads one volume; returns whether more volumes follow (null when unknown).</summary>
  private bool? ReadVolume(Stream s, bool first) {
    var (sigPos, format) = FindSignature(s, first);
    if (first) this._info.SfxSize = sigPos;
    var index = this._volumes.Count;
    this._volumes.Add(s);
    if (format == 14)
      return Rar14Parser.ReadVolume(s, sigPos, index, this._items, this._info, first);
    if (format == 5)
      return Rar5Parser.ReadVolume(s, sigPos + 8, index, this._rar5Crypto, this._items, this._info, first);
    return Rar4Parser.ReadVolume(s, sigPos + 7, index, this._password, this._items, this._info, first);
  }

  /// <summary>
  /// Locates the archive signature: at offset 0, or after an SFX module within the first
  /// megabytes (the technote's limit is 1 MB; RAR's own modules stay below it). A candidate
  /// counts only when the header after it checks out.
  /// </summary>
  internal static (long Position, int Format) FindSignature(Stream s, bool allowSfx) {
    var limit = (int)Math.Min(s.Length, allowSfx ? SfxSearchLimit : 64 * 1024);
    var buf = new byte[limit];
    s.Position = 0;
    var got = 0;
    while (got < limit) {
      var n = s.Read(buf, got, limit - got);
      if (n <= 0) break;
      got += n;
    }
    ReadOnlySpan<byte> sig = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];
    ReadOnlySpan<byte> sig14 = [0x52, 0x45, 0x7E, 0x5E];
    // RAR 1.3/1.4: its technote guarantees the SFX module never holds the signature.
    var at14 = buf.AsSpan(0, got).IndexOf(sig14);
    if (at14 >= 0 && (allowSfx || at14 == 0) && PlausibleRar14(buf, at14, got) && buf.AsSpan(0, at14).IndexOf(sig) < 0)
      return (at14, 14);
    var from = 0;
    while (true) {
      var at = buf.AsSpan(from, got - from).IndexOf(sig);
      if (at < 0) break;
      var pos = from + at;
      if (pos + 7 <= got) {
        if (buf[pos + 6] == 0x01 && pos + 8 <= got && buf[pos + 7] == 0x00 && PlausibleRar5(buf, pos + 8, got))
          return (pos, 5);
        if (buf[pos + 6] == 0x00 && PlausibleRar4(buf, pos + 7, got))
          return (pos, 4);
      }
      if (!allowSfx) break;
      from = pos + 1;
    }
    throw new InvalidDataException("Not a valid RAR archive.");
  }

  private static bool PlausibleRar5(byte[] b, int p, int end) {
    if (p + 7 > end) return false;
    var crc = BitConverter.ToUInt32(b, p);
    ulong size = 0;
    int i;
    for (i = 0; i < 3; ++i) {
      var v = b[p + 4 + i];
      size |= (ulong)(v & 0x7F) << (7 * i);
      if ((v & 0x80) == 0) break;
    }
    if (i == 3) return false;
    var len = 4 + i + 1 + (long)size;
    if (size < 2 || p + len > end) return p + len > end && size >= 2; // header extends beyond the probe: accept
    return Compression.Core.Checksums.Crc32.Compute(b.AsSpan(p + 4, (int)len - 4)) == crc;
  }

  private static bool PlausibleRar14(byte[] b, int p, int end) {
    if (p + 7 > end) return false;
    var len = BitConverter.ToUInt16(b, p + 4);
    return len >= 7 && p + len <= end;
  }

  private static bool PlausibleRar4(byte[] b, int p, int end) {
    if (p + 7 > end || b[p + 2] != 0x73) return false;
    var size = BitConverter.ToUInt16(b, p + 5);
    if (size < 7 || p + size > end) return false;
    static ushort Crc(ReadOnlySpan<byte> h) => (ushort)(Compression.Core.Checksums.Crc32.Compute(h) & 0xFFFF);
    var stored = BitConverter.ToUInt16(b, p);
    return stored == Crc(b.AsSpan(p + 2, size - 2)) || (size > 13 && stored == Crc(b.AsSpan(p + 2, 11)));
  }

  private void BuildEntries() {
    for (var i = 0; i < this._items.Count; ++i) {
      var it = this._items[i];
      if (it.IsService) {
        if (it.ServiceName != "STM" || it.Owner < 0 || it.StreamName is not { Length: > 0 } stream)
          continue;
        // An NTFS alternate data stream of the file before it: listed as "file:stream".
        var owner = this._items[it.Owner];
        var streamName = stream.TrimStart(':');
        this.AddEntry(i, owner.Name + ":" + streamName, it, streamName);
        continue;
      }
      this.AddEntry(i, it.Name, it, null);
    }
  }

  private void AddEntry(int itemIndex, string name, RarItem it, string? streamName) {
    this._entryItems.Add(itemIndex);
    this._entries.Add(new RarEntry {
      Name = name,
      Size = it.Size,
      CompressedSize = it.PackedSize,
      IsDirectory = it.IsDirectory,
      ModifiedTime = it.Modified,
      CreatedTime = it.Created,
      AccessedTime = it.Accessed,
      Crc = it.Crc,
      Blake2sp = it.Blake2,
      CompressionMethod = it.Method,
      IsSolid = it.Solid,
      IsEncrypted = it.IsEncrypted,
      Attributes = it.Attributes,
      HostOs = it.HostOs,
      LinkKind = it.Link,
      LinkTarget = it.LinkTarget,
      FileVersion = it.FileVersion,
      StreamName = streamName,
      IsIncomplete = it.SplitBefore || it.SplitAfter,
      VolumeCount = it.Parts.Select(p => p.Volume).Distinct().Count(),
    });
  }

  private string? ReadComment() {
    var cmt = this._items.FindIndex(i => i.IsService && i.ServiceName == "CMT");
    if (cmt < 0) return null;
    try {
      using var ms = new MemoryStream();
      this.ExtractItem(cmt, ms, 0);
      var bytes = ms.ToArray();
      return this._items[cmt].IsRar5
        ? Encoding.UTF8.GetString(bytes).TrimEnd('\0')
        : DecodeRar4Comment(bytes);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or InvalidOperationException) {
      return null;
    }
  }

  private static string DecodeRar4Comment(byte[] bytes) {
    // RAR 3.x/4.x writes the comment as OEM text or, when it does not fit, as UTF-16LE with a BOM.
    if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
      return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).TrimEnd('\0');
    return Encoding.GetEncoding(437).GetString(bytes).TrimEnd('\0');
  }

  // ---------------------------------------------------------------- extraction

  private void ExtractItem(int itemIndex, Stream output, int depth) {
    var item = this._items[itemIndex];
    if (item.Link is RarLinkKind.HardLink or RarLinkKind.FileCopy && item.PackedSize == 0) {
      if (depth > 16) throw new InvalidDataException("RAR link chain too long.");
      var target = item.LinkTarget?.Replace('\\', '/');
      var source = this._items.FindLastIndex(itemIndex - 1 < 0 ? 0 : itemIndex - 1, i => !i.IsService && i.Name == target);
      if (source < 0)
        throw new InvalidDataException($"RAR entry '{item.Name}' refers to '{target}', which the archive does not hold.");
      this.ExtractItem(source, output, depth + 1);
      return;
    }
    if (item.SplitBefore)
      throw new InvalidDataException($"RAR entry '{item.Name}' starts in a volume that is not present.");
    if (item.SplitAfter)
      throw new InvalidDataException($"RAR entry '{item.Name}' continues in a volume that is not present ({this._info.MissingVolume ?? "next volume"}).");
    if (item.Size == 0 && item.PackedSize == 0)
      return;

    if (UsesDecoder(item) && !item.IsService && item.Solid) {
      // The decoder must hold the state after the compressed file before this one; replay
      // the solid run from its start when it does not.
      var previous = this.PreviousCompressed(itemIndex);
      if (previous >= 0 && (this._solidDecoder == null || this._lastDecoded != previous)) {
        var start = previous;
        while (this._items[start].Solid && this.PreviousCompressed(start) is var p && p >= 0)
          start = p;
        this._solidDecoder = null;
        this._lastDecoded = -1;
        for (var i = start; i <= previous; ++i)
          if (UsesDecoder(this._items[i]) && !this._items[i].IsService)
            this.Decode(i, Stream.Null, verify: false);
      }
    }
    this.Decode(itemIndex, output, verify: true);
  }

  /// <summary>Whether an item's data runs through the LZ decoder (and so through a solid window).</summary>
  // An empty file can still carry packed data (RAR 2.0 solid archives write new tables for it),
  // and that data moves the solid state on.
  private static bool UsesDecoder(RarItem item) => item.Method != 0 && !item.IsDirectory && (item.Size != 0 || item.PackedSize > 0);

  private int PreviousCompressed(int itemIndex) {
    for (var i = itemIndex - 1; i >= 0; --i)
      if (UsesDecoder(this._items[i]) && !this._items[i].IsService)
        return i;
    return -1;
  }

  /// <summary>Whether <paramref name="itemIndex"/> continues the decoder state now held.</summary>
  private bool ContinuesSolid(int itemIndex, RarItem item)
    => item.Solid && !item.IsService && this._solidDecoder != null && this._lastDecoded >= 0
       && this._lastDecoded == this.PreviousCompressed(itemIndex);

  private void Decode(int itemIndex, Stream output, bool verify) {
    var item = this._items[itemIndex];
    if (item.IsRar5) this.DecodeRar5(itemIndex, item, output, verify);
    else this.DecodeRar4(itemIndex, item, output, verify);
    if (UsesDecoder(item) && !item.IsService)
      this._lastDecoded = itemIndex;
  }

  private Stream PackedStream(RarItem item) => new RarPartsStream(this._volumes, item.Parts);

  private void DecodeRar5(int itemIndex, RarItem item, Stream output, bool verify) {
    var packed = this.PackedStream(item);
    var packedSize = packed.Length;
    byte[]? hashKey = null;
    if (item.IsEncrypted && item.Crypt == null)
      throw new NotSupportedException($"RAR entry '{item.Name}' uses an encryption record this reader does not know.");
    if (item.Crypt is { } crypt) {
      if (this._rar5Crypto == null)
        throw new InvalidOperationException($"RAR entry '{item.Name}' is encrypted; a password is required.");
      var keys = this._rar5Crypto.Keys(crypt.Salt, crypt.KdfLog);
      if (Rar5Crypto.PasswordMatches(crypt.Check, keys.Check) == false)
        throw new InvalidDataException($"The password is wrong for RAR entry '{item.Name}'.");
      if (crypt.UseMac) hashKey = keys.HashKey;
      packed = new RarCbcDecryptStream(packed, packedSize, keys.Key, crypt.Iv);
      packedSize &= ~15L;
    }

    var hasher = new RarHashingStream(output, crc: verify && item.Crc != null, blake: verify && item.Blake2 != null);
    if (item.Method == 0) {
      CopyBounded(packed, hasher, item.Size < 0 ? long.MaxValue : item.Size);
    } else {
      if (item.AlgorithmVersion > 0)
        throw new NotSupportedException($"RAR entry '{item.Name}' uses compression algorithm version {item.AlgorithmVersion} (RAR 7 dictionaries over 4 GB); no reference stream exists to prove a decoder for it, so it is refused.");
      if (item.Method > 5)
        throw new NotSupportedException($"RAR entry '{item.Name}' uses unknown method {item.Method}.");
      var solid = this.ContinuesSolid(itemIndex, item) && this._solidDecoder is Rar5Unpacker;
      Rar5Unpacker unpacker;
      if (solid)
        unpacker = (Rar5Unpacker)this._solidDecoder!;
      else {
        unpacker = new Rar5Unpacker(Rar5Unpacker.WindowFor(item.DictionarySize, this.SolidStreamSize(itemIndex)));
        if (!item.IsService) this._solidDecoder = unpacker;
      }
      unpacker.Unpack(packed, packedSize, item.Size, hasher, solid);
    }

    if (!verify) return;
    if (item.Size >= 0 && hasher.Written != item.Size)
      throw new InvalidDataException($"RAR entry '{item.Name}' unpacked to {hasher.Written} bytes, not {item.Size}.");
    if (item.Crc is { } crc) {
      var got = hasher.Crc;
      if (hashKey != null) got = Rar5Crypto.TweakCrc(hashKey, got);
      if (got != crc)
        throw this.ChecksumError(item, "CRC-32");
    }
    if (item.Blake2 is { } want) {
      var got = hasher.Blake2sp!;
      if (hashKey != null) got = Rar5Crypto.TweakDigest(hashKey, got);
      if (!got.AsSpan().SequenceEqual(want))
        throw this.ChecksumError(item, "BLAKE2sp");
    }
  }

  private Exception ChecksumError(RarItem item, string kind)
    => item.IsEncrypted
      ? new InvalidDataException($"{kind} mismatch for encrypted RAR entry '{item.Name}': wrong password or damaged data.")
      : new InvalidDataException($"{kind} mismatch for RAR entry '{item.Name}'.");

  /// <summary>
  /// Unpacked bytes of the solid run <paramref name="itemIndex"/> starts: it and the compressed
  /// files after it that continue its window.
  /// </summary>
  private long SolidStreamSize(int itemIndex) {
    var item = this._items[itemIndex];
    if (item.Size < 0) return long.MaxValue / 4;
    var total = item.Size;
    if (item.IsService) return total;
    for (var i = itemIndex + 1; i < this._items.Count; ++i) {
      var it = this._items[i];
      if (!UsesDecoder(it) || it.IsService) continue;
      if (!it.Solid) break;
      if (it.Size < 0) return long.MaxValue / 4;
      total += it.Size;
    }
    return total;
  }

  private void DecodeRar4(int itemIndex, RarItem item, Stream output, bool verify) {
    Stream packed = this.PackedStream(item);
    var packedSize = packed.Length;
    if (item.IsEncrypted) {
      if (this._password == null)
        throw new InvalidOperationException($"RAR entry '{item.Name}' is encrypted; a password is required.");
      if (item.UnpackVersion >= 29) {
        var (key, iv) = KeyDerivation.Rar3DeriveKey(this._password, item.Salt ?? []);
        packed = new RarCbcDecryptStream(packed, (packedSize + 15) & ~15L, key, iv);
      } else if (item.UnpackVersion >= 20) {
        packed = new Rar20DecryptStream(packed, new Rar20Cipher(Rar20Password(this._password)));
      } else
        packed = new Rar15DecryptStream(packed, new Rar15Cipher(Rar20Password(this._password), item.CryptVersion != 0 ? item.CryptVersion : item.UnpackVersion));
    }

    var hasher = new RarHashingStream(output, crc: verify && item.Crc != null, blake: false, rar14: verify && item.Checksum14 != null);
    if (item.Method == 0) {
      CopyBounded(packed, hasher, item.Size);
    } else {
      if (item.Size > int.MaxValue || packedSize > int.MaxValue)
        throw new NotSupportedException($"RAR entry '{item.Name}': compressed RAR 4 files over 2 GiB are not supported by the in-memory RAR 1.5-4.x decoders.");
      var data = new byte[packedSize];
      var got = 0;
      while (got < data.Length) {
        var n = packed.Read(data, got, data.Length - got);
        if (n <= 0) break;
        got += n;
      }
      var size = (int)item.Size;
      byte[] unpacked;
      var continueSolid = this.ContinuesSolid(itemIndex, item);
      var ver = item.UnpackVersion;
      if (ver <= 15) {
        var d = continueSolid && this._solidDecoder is Rar1Decoder r1 ? r1 : new Rar1Decoder();
        unpacked = d.Decompress(data.AsSpan(0, got), size);
        if (!item.IsService) this._solidDecoder = d;
      } else if (ver is <= 20 or 26) {
        var d = continueSolid && this._solidDecoder is Rar2Decoder r2 ? r2 : new Rar2Decoder();
        unpacked = d.Decompress(data.AsSpan(0, got), size);
        if (!item.IsService) this._solidDecoder = d;
      } else if (ver <= 36) {
        var d = continueSolid && this._solidDecoder is Rar3Decoder r3 ? r3 : new Rar3Decoder();
        unpacked = d.Decompress(data.AsSpan(0, got), size, Math.Min(item.WindowBits, 22));
        if (!item.IsService) this._solidDecoder = d;
      } else
        throw new NotSupportedException($"RAR unpack version {ver} is not supported in RAR 4 archives.");
      hasher.Write(unpacked);
    }

    if (!verify) return;
    if (hasher.Written != item.Size)
      throw new InvalidDataException($"RAR entry '{item.Name}' unpacked to {hasher.Written} bytes, not {item.Size}.");
    if (item.Crc is { } crc && hasher.Crc != crc)
      throw this.ChecksumError(item, "CRC-32");
    if (item.Checksum14 is { } sum && hasher.Checksum14 != sum)
      throw this.ChecksumError(item, "RAR 1.4 checksum");
  }

  private static void CopyBounded(Stream from, Stream to, long count) {
    var buf = new byte[1 << 16];
    while (count > 0) {
      var n = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
      if (n <= 0) {
        if (count == long.MaxValue || count < 0) return;
        throw new InvalidDataException("RAR stored data ends early.");
      }
      to.Write(buf, 0, n);
      count -= n;
    }
  }

  /// <summary>
  /// RAR 2.x keys its cipher with the password in the DOS/OEM code page (437, as for its file
  /// names), at most 127 bytes; characters that page cannot hold become '?', as WinRAR did.
  /// </summary>
  private static byte[] Rar20Password(string password) {
    var bytes = Encoding.GetEncoding(437, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback).GetBytes(password);
    return bytes.Length > Rar20Cipher.MaxPasswordLength ? bytes[..Rar20Cipher.MaxPasswordLength] : bytes;
  }

  /// <summary>Decrypts a RAR 1.3/1.5 keystream cipher.</summary>
  private sealed class Rar15DecryptStream(Stream source, Rar15Cipher cipher) : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) {
      var n = source.Read(buffer, offset, count);
      cipher.Decrypt(buffer.AsSpan(offset, n));
      return n;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>Decrypts a RAR 2.0 cipher stream in 16-byte blocks.</summary>
  private sealed class Rar20DecryptStream(Stream source, Rar20Cipher cipher) : Stream {
    private readonly byte[] _buf = new byte[1 << 16];
    private int _pos, _len;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) {
      if (this._pos == this._len) {
        var got = 0;
        while (got < this._buf.Length) {
          var n = source.Read(this._buf, got, this._buf.Length - got);
          if (n <= 0) break;
          got += n;
        }
        if (got == 0) return 0;
        var whole = (got + 15) & ~15;
        if (whole > got) Array.Clear(this._buf, got, whole - got);
        cipher.Decrypt(this._buf.AsSpan(0, whole));
        this._pos = 0;
        this._len = got;
      }
      var take = Math.Min(count, this._len - this._pos);
      Buffer.BlockCopy(this._buf, this._pos, buffer, offset, take);
      this._pos += take;
      return take;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}

internal static class RarItemExtensions {
  public static bool IsSymlinkLike(this RarItem item) => item.Link is RarLinkKind.UnixSymlink or RarLinkKind.WindowsSymlink or RarLinkKind.WindowsJunction;
}
