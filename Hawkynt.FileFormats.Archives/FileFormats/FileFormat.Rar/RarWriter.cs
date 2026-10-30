using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Rar;

namespace FileFormat.Rar;

/// <summary>
/// Writes RAR 5 archives (technote <c>https://www.rarlab.com/technote.htm</c>): stored or
/// compressed files, directories, solid mode, AES-256 encryption of data (<c>-p</c>) or of
/// data and headers (<c>-hp</c>), and volume sets.
/// </summary>
/// <remarks>
/// <para>
/// Files are compressed (and encrypted) into a spill buffer first — memory up to 64 MB, a temp
/// file beyond — so the header can carry the packed size and CRC before the data, and a file
/// can be cut across volumes at any byte. Large files therefore stream with bounded memory.
/// </para>
/// <para>
/// Encrypted files carry the password check value and use RAR's key-tweaked CRC (encryption
/// flags 0x0001 and 0x0002), as RAR 5.x writes them; the key schedule is
/// <see cref="KeyDerivation.Rar5DeriveKeys"/>. Every capability is checked against unrar and
/// 7-Zip by the conformance tests.
/// </para>
/// </remarks>
public sealed class RarWriter : IDisposable {
  private const int KdfLog = 15;
  private const long SpillInMemory = 64L << 20;

  private readonly Stream _first;
  private readonly bool _leaveOpen;
  private readonly int _method;
  private readonly int _dictionarySize;
  private readonly bool _solid;
  private readonly string? _password;
  private readonly bool _encryptHeaders;
  private readonly long _volumeSize;
  private readonly Func<int, Stream>? _openVolume;
  private readonly bool _locked;
  private readonly List<Stream> _openedVolumes = [];

  private Stream _out;
  private int _volumeIndex;
  private long _volumeStart;
  private bool _headerWritten;
  private bool _finished;
  private bool _disposed;
  private bool _anyCompressed;
  private Rar5Encoder? _solidEncoder;

  private readonly byte[]? _salt;
  private readonly byte[]? _key;
  private readonly byte[]? _hashKey;
  private readonly byte[]? _checkField;

  /// <summary>Creates a writer.</summary>
  /// <param name="stream">Receives the archive (the first volume, for a volume set).</param>
  /// <param name="leaveOpen">Leave <paramref name="stream"/> open on dispose.</param>
  /// <param name="method">0 store, 1-5 compression level.</param>
  /// <param name="dictionarySizeLog">log2 of the dictionary, 17 (128 KB) to 26 (64 MB).</param>
  /// <param name="solid">Compress all files as one solid stream.</param>
  /// <param name="password">Encrypt file data with AES-256 under this password.</param>
  /// <param name="encryptHeaders">With a password, encrypt the headers (file names) too.</param>
  /// <param name="volumeSize">Split into volumes of at most this many bytes (0: one file).</param>
  /// <param name="openVolume">Opens volume <c>n</c> (1-based after the first, which is
  /// <paramref name="stream"/>); required when <paramref name="volumeSize"/> is set.</param>
  /// <param name="locked">Set the "locked archive" flag.</param>
  public RarWriter(Stream stream, bool leaveOpen = false, int method = RarConstants.MethodNormal,
      int dictionarySizeLog = 17, bool solid = false, string? password = null,
      bool encryptHeaders = false, long volumeSize = 0, Func<int, Stream>? openVolume = null,
      bool locked = false) {
    this._first = stream ?? throw new ArgumentNullException(nameof(stream));
    this._out = stream;
    this._leaveOpen = leaveOpen;
    this._method = Math.Clamp(method, 0, 5);
    this._dictionarySize = 1 << Math.Clamp(dictionarySizeLog, 17, 26);
    this._solid = solid;
    this._password = string.IsNullOrEmpty(password) ? null : password;
    this._encryptHeaders = encryptHeaders && this._password != null;
    this._locked = locked;
    if (volumeSize > 0) {
      if (volumeSize < 1024)
        throw new ArgumentOutOfRangeException(nameof(volumeSize), "RAR volumes must hold at least 1 KB.");
      this._volumeSize = volumeSize;
      this._openVolume = openVolume ?? throw new ArgumentNullException(nameof(openVolume), "Volumes need a way to open the next volume.");
    }
    if (this._password != null) {
      this._salt = RandomNumberGenerator.GetBytes(16);
      var keys = KeyDerivation.Rar5DeriveKeys(this._password, this._salt, KdfLog);
      this._key = keys.Key;
      this._hashKey = keys.HashKey;
      this._checkField = Rar5Crypto.CheckField(keys.PasswordCheck);
    }
  }

  /// <summary>How many volumes were written (1 without volumes).</summary>
  public int VolumeCount => this._volumeIndex + 1;

  /// <summary>Adds a file held in memory.</summary>
  public void AddFile(string fileName, ReadOnlySpan<byte> data, DateTimeOffset? modifiedTime = null) {
    using var ms = new MemoryStream(data.ToArray(), writable: false);
    this.AddFile(fileName, ms, data.Length, new ArchiveTimes(modifiedTime, null, null));
  }

  /// <summary>Adds <paramref name="length"/> bytes of <paramref name="data"/> as a file.</summary>
  public void AddFile(string fileName, Stream data, long length, ArchiveTimes times = default, uint? attributes = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);
    ArgumentOutOfRangeException.ThrowIfNegative(length);
    this.EnsureHeader();

    // Pack into the spill: compressed (or stored), then encrypted.
    using var spill = new SpillStream();
    var crc = new Crc32();
    var method = length == 0 ? 0 : this._method;
    var solidBit = false;
    var hashing = new CrcReadStream(data, crc);
    Stream sink = spill;
    RarCbcEncryptStream? enc = null;
    byte[]? iv = null;
    if (this._key != null && length > 0) {
      iv = RandomNumberGenerator.GetBytes(16);
      enc = new RarCbcEncryptStream(spill, this._key, iv);
      sink = enc;
    }
    if (method == 0) {
      CopyExactly(hashing, sink, length);
    } else {
      Rar5Encoder encoder;
      if (this._solid) {
        solidBit = this._anyCompressed;
        encoder = this._solidEncoder ??= new Rar5Encoder(this._dictionarySize, this._method);
      } else
        encoder = new Rar5Encoder(DictionaryFor(this._dictionarySize, length), this._method);
      encoder.Compress(hashing, length, sink);
      this._anyCompressed = true;
      if (!this._solid && enc == null && spill.Length >= length && data.CanSeek) {
        // Did not shrink: store instead (never in a solid stream, whose window must see it).
        data.Position -= length;
        spill.SetLength(0);
        crc.Reset();
        hashing = new CrcReadStream(data, crc);
        CopyExactly(hashing, spill, length);
        method = 0;
      }
    }
    enc?.FinishBlock();
    if (this._solid && length == 0) {
      // In a solid stream every entry after the first compressed one is marked solid, empty
      // files included, as rar marks them: 7-Zip resets its window at a non-solid entry.
      method = this._method;
      solidBit = this._anyCompressed;
    }
    var dict = method == 0 ? 0 : this._solid ? this._dictionarySize : DictionaryFor(this._dictionarySize, length);

    var entry = new FileRecord(Normalize(fileName), IsDirectory: false, length, crc.Value, method, solidBit, dict, iv,
      attributes ?? 0x20, times);
    this.WriteParts(entry, spill);
  }

  /// <summary>Adds a directory entry.</summary>
  public void AddDirectory(string name, ArchiveTimes times = default) {
    ArgumentNullException.ThrowIfNull(name);
    this.EnsureHeader();
    var entry = new FileRecord(Normalize(name).TrimEnd('/'), IsDirectory: true, 0, 0, 0, this._solid && this._anyCompressed, 0, null, 0x10, times);
    using var empty = new SpillStream();
    this.WriteParts(entry, empty);
  }

  /// <summary>Writes the end of archive header and flushes.</summary>
  public void Finish() {
    if (this._finished) return;
    this._finished = true;
    this.EnsureHeader();
    this.WriteHeader(this.EndHeader(moreVolumes: false));
    this._out.Flush();
  }

  /// <summary>Creates a RAR 5 volume set in memory: one byte array per volume.</summary>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      int method = RarConstants.MethodNormal,
      string? password = null) {
    var volumes = new List<MemoryStream> { new() };
    using (var w = new RarWriter(volumes[0], leaveOpen: true, method: method, password: password,
             volumeSize: maxVolumeSize, openVolume: _ => { var ms = new MemoryStream(); volumes.Add(ms); return ms; })) {
      foreach (var (name, data) in entries)
        w.AddFile(name, data);
      w.Finish();
    }
    return volumes.Select(v => v.ToArray()).ToArray();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._finished) this.Finish();
    foreach (var v in this._openedVolumes) v.Dispose();
    if (!this._leaveOpen) this._first.Dispose();
  }

  // ---------------------------------------------------------------- records

  /// <summary>Creation, access and modification time of an entry, any of which may be absent.</summary>
  public readonly record struct ArchiveTimes(DateTimeOffset? Modified, DateTimeOffset? Created, DateTimeOffset? Accessed);

  private sealed record FileRecord(string Name, bool IsDirectory, long Size, uint Crc, int Method, bool Solid, int Dictionary,
    byte[]? Iv, uint Attributes, ArchiveTimes Times);

  private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');

  private static int DictionaryFor(int dictionary, long size) {
    // rar declares the smaller of the dictionary and the file, at least 128 KB.
    var d = Rar5Constants.MinDictionarySize;
    while (d < dictionary && d < size) d <<= 1;
    return d;
  }

  private void EnsureHeader() {
    if (this._headerWritten) return;
    this._headerWritten = true;
    this.StartVolume();
  }

  private void StartVolume() {
    this._volumeStart = this._out.CanSeek ? this._out.Position : 0;
    this._written = 0;
    this.Raw(RarConstants.Rar5Signature);
    if (this._encryptHeaders) {
      // Archive encryption header (plain): version 0, flags (check present), KDF count, salt, check.
      var body = new MemoryStream();
      RarVint.Write(body, 4);
      RarVint.Write(body, 0);
      RarVint.Write(body, 0);
      RarVint.Write(body, 1);
      body.WriteByte(KdfLog);
      body.Write(this._salt);
      body.Write(this._checkField);
      this.Raw(Frame(body.ToArray()));
    }
    var main = new MemoryStream();
    RarVint.Write(main, 1);
    RarVint.Write(main, 0);
    ulong flags = 0;
    if (this._volumeSize > 0) flags |= 1;
    if (this._volumeIndex > 0) flags |= 2;
    if (this._solid) flags |= 4;
    if (this._locked) flags |= 0x10;
    RarVint.Write(main, flags);
    if (this._volumeIndex > 0) RarVint.Write(main, (ulong)this._volumeIndex);
    this.WriteHeader(main.ToArray());
  }

  private byte[] EndHeader(bool moreVolumes) {
    var body = new MemoryStream();
    RarVint.Write(body, 5);
    RarVint.Write(body, 0);
    RarVint.Write(body, moreVolumes ? 1UL : 0UL);
    return body.ToArray();
  }

  private long _written;

  private void Raw(ReadOnlySpan<byte> bytes) {
    this._out.Write(bytes);
    this._written += bytes.Length;
  }

  /// <summary>CRC32 + size vint + body.</summary>
  private static byte[] Frame(byte[] body) {
    var size = new MemoryStream();
    RarVint.Write(size, (ulong)body.Length);
    var sizeBytes = size.ToArray();
    var frame = new byte[4 + sizeBytes.Length + body.Length];
    sizeBytes.CopyTo(frame, 4);
    body.CopyTo(frame, 4 + sizeBytes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(frame, Crc32.Compute(frame.AsSpan(4)));
    return frame;
  }

  /// <summary>Bytes a header occupies on disk: framed, and with -hp an IV plus padding.</summary>
  private int OnDiskSize(byte[] body) {
    var framed = Frame(body).Length;
    return this._encryptHeaders ? 16 + ((framed + 15) & ~15) : framed;
  }

  private void WriteHeader(byte[] body) {
    var frame = Frame(body);
    if (!this._encryptHeaders) {
      this.Raw(frame);
      return;
    }
    var iv = RandomNumberGenerator.GetBytes(16);
    var padded = new byte[(frame.Length + 15) & ~15];
    frame.CopyTo(padded, 0);
    using var aes = Aes.Create();
    aes.Key = this._key!;
    this.Raw(iv);
    this.Raw(aes.EncryptCbc(padded, iv, PaddingMode.None));
  }

  private byte[] FileHeaderBody(FileRecord f, long partSize, uint crc, bool splitBefore, bool splitAfter) {
    var extra = new MemoryStream();
    if (f.Iv != null) {
      var rec = new MemoryStream();
      RarVint.Write(rec, 1);       // type: encryption
      RarVint.Write(rec, 0);       // version: AES-256
      // Flags: check value present; tweaked checksums only where the CRC is the file's (the
      // last part) - earlier parts carry the plain CRC of their packed bytes, as rar writes them.
      RarVint.Write(rec, splitAfter ? 1UL : 3UL);
      rec.WriteByte(KdfLog);
      rec.Write(this._salt);
      rec.Write(f.Iv);
      rec.Write(this._checkField);
      WriteRecord(extra, rec);
    }
    if (f.Times.Modified != null || f.Times.Created != null || f.Times.Accessed != null) {
      var rec = new MemoryStream();
      RarVint.Write(rec, 3);       // type: file time
      ulong tf = 0;
      if (f.Times.Modified != null) tf |= 2;
      if (f.Times.Created != null) tf |= 4;
      if (f.Times.Accessed != null) tf |= 8;
      RarVint.Write(rec, tf);
      Span<byte> b8 = stackalloc byte[8];
      foreach (var t in new[] { f.Times.Modified, f.Times.Created, f.Times.Accessed })
        if (t is { } v) {
          BinaryPrimitives.WriteInt64LittleEndian(b8, v.UtcDateTime.ToFileTimeUtc());
          rec.Write(b8);
        }
      WriteRecord(extra, rec);
    }
    var extraBytes = extra.ToArray();

    var body = new MemoryStream();
    RarVint.Write(body, 2);
    ulong hflags = 0;
    if (extraBytes.Length > 0) hflags |= 1;
    if (partSize > 0 || splitBefore || splitAfter) hflags |= 2;
    if (splitBefore) hflags |= 0x08;
    if (splitAfter) hflags |= 0x10;
    RarVint.Write(body, hflags);
    if (extraBytes.Length > 0) RarVint.Write(body, (ulong)extraBytes.Length);
    if ((hflags & 2) != 0) RarVint.Write(body, (ulong)partSize);
    ulong fileFlags = 0;
    if (f.IsDirectory) fileFlags |= 1;
    else fileFlags |= 4;
    RarVint.Write(body, fileFlags);
    RarVint.Write(body, (ulong)f.Size);
    RarVint.Write(body, f.Attributes);
    if (!f.IsDirectory) {
      Span<byte> c = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(c, crc);
      body.Write(c);
    }
    ulong comp = f.Solid ? 0x40UL : 0;
    if (!f.IsDirectory) {
      var dictBits = 0;
      while ((Rar5Constants.MinDictionarySize << dictBits) < f.Dictionary) ++dictBits;
      comp = (ulong)((f.Solid ? 0x40 : 0) | (f.Method << 7) | (f.Method == 0 ? 0 : dictBits << 10));
    }
    RarVint.Write(body, comp);
    RarVint.Write(body, 0);         // host OS: Windows
    var name = Encoding.UTF8.GetBytes(f.Name);
    RarVint.Write(body, (ulong)name.Length);
    body.Write(name);
    body.Write(extraBytes);
    return body.ToArray();
  }

  private static void WriteRecord(Stream extra, MemoryStream rec) {
    var r = rec.ToArray();
    RarVint.Write(extra, (ulong)r.Length);
    extra.Write(r);
  }

  private void WriteParts(FileRecord f, SpillStream spill) {
    var total = spill.Length;
    // The CRC of the whole file, tweaked by the key when encrypted.
    var finalCrc = f.Iv != null ? Rar5Crypto.TweakCrc(this._hashKey!, f.Crc) : f.Crc;
    spill.Position = 0;
    var offset = 0L;
    var splitBefore = false;
    var buf = new byte[1 << 16];
    while (true) {
      var left = total - offset;
      var part = left;
      var splitAfter = false;
      if (this._volumeSize > 0) {
        var endSize = this.OnDiskSize(this.EndHeader(true));
        var headerSize = this.OnDiskSize(this.FileHeaderBody(f, left, finalCrc, splitBefore, false));
        var room = this._volumeSize - this._written - endSize;
        if (headerSize + left > room) {
          var dataRoom = room - this.OnDiskSize(this.FileHeaderBody(f, Math.Max(room, 0), 0, splitBefore, true));
          if (dataRoom < 16 || left == 0) {
            if (this._written <= RarConstants.Rar5Signature.Length + 64 && dataRoom < 16)
              throw new InvalidOperationException("RAR volume size too small for a file header.");
            this.NextVolume();
            continue;
          }
          part = Math.Min(left, dataRoom);
          if (f.Iv != null) part &= ~15L;   // keep encrypted parts on AES block boundaries
          splitAfter = part < left;
        }
      }

      uint partCrc;
      if (splitAfter) {
        // A part's CRC covers its packed bytes, untweaked.
        var c = new Crc32();
        spill.Position = offset;
        CopyCounted(spill, Stream.Null, part, buf, c);
        partCrc = c.Value;
      } else
        partCrc = finalCrc;

      this.WriteHeader(this.FileHeaderBody(f, part, partCrc, splitBefore, splitAfter));
      spill.Position = offset;
      CopyCounted(spill, this._out, part, buf, null);
      this._written += part;
      offset += part;
      if (!splitAfter) break;
      splitBefore = true;
      this.NextVolume();
    }
  }

  private void NextVolume() {
    this.WriteHeader(this.EndHeader(moreVolumes: true));
    this._out.Flush();
    ++this._volumeIndex;
    var next = this._openVolume!(this._volumeIndex);
    this._openedVolumes.Add(next);
    this._out = next;
    this.StartVolume();
  }

  private static void CopyCounted(Stream from, Stream to, long count, byte[] buf, Crc32? crc) {
    while (count > 0) {
      var n = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
      if (n <= 0) throw new EndOfStreamException();
      crc?.Update(buf.AsSpan(0, n));
      to.Write(buf, 0, n);
      count -= n;
    }
  }

  private static void CopyExactly(Stream from, Stream to, long count) {
    var buf = new byte[1 << 16];
    while (count > 0) {
      var n = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
      if (n <= 0) throw new EndOfStreamException("Input ended before the declared length.");
      to.Write(buf, 0, n);
      count -= n;
    }
  }

  /// <summary>Hashes what is read through it.</summary>
  private sealed class CrcReadStream(Stream inner, Crc32 crc) : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) {
      var n = inner.Read(buffer, offset, count);
      crc.Update(buffer.AsSpan(offset, n));
      return n;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>A seekable buffer that moves from memory to a temp file past 64 MB.</summary>
  private sealed class SpillStream : Stream {
    private Stream _inner = new MemoryStream();
    private string? _tempPath;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => this._inner.Length;
    public override long Position { get => this._inner.Position; set => this._inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => this._inner.Seek(offset, origin);
    public override void SetLength(long value) => this._inner.SetLength(value);
    public override void Flush() => this._inner.Flush();
    public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) {
      if (this._tempPath == null && this._inner.Length + buffer.Length > SpillInMemory) {
        this._tempPath = Path.GetTempFileName();
        var file = new FileStream(this._tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        var pos = this._inner.Position;
        this._inner.Position = 0;
        this._inner.CopyTo(file);
        file.Position = pos;
        this._inner.Dispose();
        this._inner = file;
      }
      this._inner.Write(buffer);
    }
    protected override void Dispose(bool disposing) {
      if (disposing) this._inner.Dispose();
      base.Dispose(disposing);
    }
  }
}

/// <summary>AES-CBC encryption into a stream, zero-padding the last block.</summary>
internal sealed class RarCbcEncryptStream : Stream {
  private readonly Stream _sink;
  private readonly ICryptoTransform _enc;
  private readonly Aes _aes;
  private readonly byte[] _pending = new byte[16];
  private int _pendingLen;
  private readonly byte[] _outBuf = new byte[1 << 16];

  public RarCbcEncryptStream(Stream sink, byte[] key, byte[] iv) {
    this._sink = sink;
    this._aes = Aes.Create();
    this._aes.Mode = CipherMode.CBC;
    this._aes.Padding = PaddingMode.None;
    this._enc = this._aes.CreateEncryptor(key, iv);
  }

  public override bool CanRead => false;
  public override bool CanSeek => false;
  public override bool CanWrite => true;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

  public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

  public override void Write(ReadOnlySpan<byte> data) {
    if (this._pendingLen > 0) {
      var take = Math.Min(16 - this._pendingLen, data.Length);
      data[..take].CopyTo(this._pending.AsSpan(this._pendingLen));
      this._pendingLen += take;
      data = data[take..];
      if (this._pendingLen < 16) return;
      var n = this._enc.TransformBlock(this._pending, 0, 16, this._outBuf, 0);
      this._sink.Write(this._outBuf, 0, n);
      this._pendingLen = 0;
    }
    while (data.Length >= 16) {
      var chunk = Math.Min(data.Length & ~15, this._outBuf.Length);
      var input = data[..chunk].ToArray();
      var n = this._enc.TransformBlock(input, 0, chunk, this._outBuf, 0);
      this._sink.Write(this._outBuf, 0, n);
      data = data[chunk..];
    }
    data.CopyTo(this._pending);
    this._pendingLen = data.Length;
  }

  /// <summary>Pads the open block with zeros and encrypts it.</summary>
  public void FinishBlock() {
    if (this._pendingLen == 0) return;
    this._pending.AsSpan(this._pendingLen).Clear();
    var n = this._enc.TransformBlock(this._pending, 0, 16, this._outBuf, 0);
    this._sink.Write(this._outBuf, 0, n);
    this._pendingLen = 0;
  }

  protected override void Dispose(bool disposing) {
    if (disposing) {
      this._enc.Dispose();
      this._aes.Dispose();
    }
    base.Dispose(disposing);
  }

  public override void Flush() { }
  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
}
