using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Rar;

namespace FileFormat.Rar;

/// <summary>
/// Creates archives in the RAR 1.5-4.x block format: RAR 4 targets store or compress (RAR 2.9
/// format, <see cref="Rar3Encoder"/>), solid or not, optionally AES-128 encrypted, as one file
/// or as a volume set; the RAR 1.5 target writes stored, unencrypted members with
/// <c>UNP_VER=15</c>.
/// </summary>
/// <remarks>
/// Block layout follows the RAR 1.5-4.x structures as 7-Zip's
/// <c>CPP/7zip/Archive/Rar/RarHandler.cpp</c> reads them (Igor Pavlov, LGPL-2.1-or-later):
/// non-ASCII names go out as UTF-8 under <c>LHD_UNICODE</c> without the OEM part, which RAR
/// 3.x+ and 7-Zip accept; times use the extended-time field at 100 ns precision; volume parts
/// carry <c>LHD_SPLIT_BEFORE</c>/<c>LHD_SPLIT_AFTER</c>, and the end block of every volume but
/// the last sets <c>EARC_NEXT_VOLUME</c>.
/// </remarks>
public sealed class Rar4Writer : IDisposable {
  private const long SpillInMemory = 64L << 20;
  private static readonly Encoding Oem;

  static Rar4Writer() {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    Oem = Encoding.GetEncoding(437, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
  }

  private readonly Stream _first;
  private readonly bool _leaveOpen;
  private readonly int _method;
  private readonly int _windowBits;
  private readonly bool _solid;
  private readonly string? _password;
  private readonly RarCompatibility _targetCompatibility;
  private readonly long _volumeSize;
  private readonly Func<int, Stream>? _openVolume;
  private readonly bool _newVolumeNaming;
  private readonly List<Stream> _opened = [];
  private Stream _out;
  private long _written;
  private int _volumeIndex;
  private Rar3Encoder? _solidEncoder;
  private bool _anyCompressed;
  private bool _headerWritten;
  private bool _finished;
  private bool _disposed;

  /// <summary>Initializes a writer for the RAR 1.5-4.x container family.</summary>
  /// <param name="stream">The stream to write the archive (its first volume) to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="method">RAR4 compression method (0x30=Store, 0x31-0x35=compressed). Default Normal.</param>
  /// <param name="windowBits">Window size as log2 (16-22). Default 20 (1MB).</param>
  /// <param name="solid">Whether to create a solid archive.</param>
  /// <param name="password">Optional password for RAR3/4 AES-128 encryption.</param>
  /// <param name="targetCompatibility">RAR generation that must be able to read the emitted archive.</param>
  /// <param name="volumeSize">Split into volumes of at most this many bytes (0: one file).</param>
  /// <param name="openVolume">Opens volume <c>n</c> (1-based after the first).</param>
  /// <param name="newVolumeNaming">The set is named <c>.partN.rar</c> (sets <c>MHD_NEWNUMBERING</c>), not <c>.rar/.r00</c>.</param>
  public Rar4Writer(Stream stream, bool leaveOpen = false,
      byte method = RarConstants.Rar4MethodNormal,
      int windowBits = 20, bool solid = false, string? password = null,
      RarCompatibility targetCompatibility = RarCompatibility.Rar4,
      long volumeSize = 0, Func<int, Stream>? openVolume = null, bool newVolumeNaming = false) {
    this._first = stream ?? throw new ArgumentNullException(nameof(stream));
    this._out = stream;
    this._leaveOpen = leaveOpen;
    this._targetCompatibility = targetCompatibility;
    if (targetCompatibility is not (RarCompatibility.Rar4 or RarCompatibility.Rar1_5))
      throw new ArgumentOutOfRangeException(nameof(targetCompatibility), targetCompatibility,
        "Rar4Writer can emit only the shared RAR 1.5-4.x container family.");
    if (volumeSize > 0) {
      if (volumeSize < 1024) throw new ArgumentOutOfRangeException(nameof(volumeSize), "RAR volumes must hold at least 1 KB.");
      this._volumeSize = volumeSize;
      this._openVolume = openVolume ?? throw new ArgumentNullException(nameof(openVolume));
      this._newVolumeNaming = newVolumeNaming;
    }

    if (targetCompatibility == RarCompatibility.Rar1_5) {
      if (method != RarConstants.Rar4MethodStore)
        throw new NotSupportedException("RAR 1.5 target currently supports stored members only; Unpack15 compression is not implemented yet.");
      if (solid)
        throw new NotSupportedException("RAR 1.5 target does not use the later solid-archive semantics.");
      if (password != null)
        throw new NotSupportedException("RAR 1.5 target cannot use RAR3/4 AES encryption.");
      this._method = RarConstants.Rar4MethodStore;
      this._windowBits = 16;
      return;
    }
    this._method = method;
    this._windowBits = Math.Clamp(windowBits, 16, 22);
    this._solid = solid;
    this._password = string.IsNullOrEmpty(password) ? null : password;
  }

  /// <summary>How many volumes were written.</summary>
  public int VolumeCount => this._volumeIndex + 1;

  /// <summary>Adds a file held in memory.</summary>
  public void AddFile(string fileName, ReadOnlySpan<byte> data, DateTimeOffset? modifiedTime = null) {
    using var ms = new MemoryStream(data.ToArray(), writable: false);
    this.AddFile(fileName, ms, data.Length, new RarWriter.ArchiveTimes(modifiedTime, null, null));
  }

  /// <summary>Adds <paramref name="length"/> bytes of <paramref name="data"/> as a file.</summary>
  public void AddFile(string fileName, Stream data, long length, RarWriter.ArchiveTimes times = default, uint attributes = 0x20) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);
    this.EnsureHeader();
    var name = this.EncodeName(fileName, out var unicode);

    using var spill = new Spill();
    var crc = new Crc32();
    var method = length == 0 ? RarConstants.Rar4MethodStore : (byte)this._method;
    var solidFlag = false;
    if (method == RarConstants.Rar4MethodStore) {
      Copy(data, spill.Stream, length, crc);
    } else {
      // The RAR 2.9 encoder works on whole files.
      if (length > int.MaxValue) throw new NotSupportedException("RAR 4 compression of files over 2 GiB is not supported; store them (method 0).");
      var bytes = new byte[length];
      ReadExactly(data, bytes);
      crc.Update(bytes);
      Rar3Encoder encoder;
      if (this._solid) {
        solidFlag = this._anyCompressed;
        encoder = this._solidEncoder ??= new Rar3Encoder(this._windowBits);
      } else
        encoder = new Rar3Encoder(this._windowBits);
      var packed = encoder.Compress(bytes);
      this._anyCompressed = true;
      if (!this._solid && packed.Length >= bytes.Length) {
        // Did not shrink: store (never in a solid stream, whose window must see every file).
        spill.Stream.Write(bytes);
        method = RarConstants.Rar4MethodStore;
      } else
        spill.Stream.Write(packed);
    }

    byte[]? salt = null;
    var plainLength = spill.Stream.Length;
    if (this._password != null && length > 0) {
      salt = RandomNumberGenerator.GetBytes(8);
      var (key, iv) = KeyDerivation.Rar3DeriveKey(this._password, salt);
      spill.Encrypt(key, iv);
    }

    // AES-encrypted members must declare 2.9: readers pick the cipher by unpack version, and
    // version 20 means the RAR 2.0 cipher.
    var unpackVer = this._targetCompatibility == RarCompatibility.Rar1_5
      ? (byte)15
      : method == RarConstants.Rar4MethodStore && salt == null ? (byte)20 : (byte)29;

    var rec = new FileRecord(name, unicode, IsDirectory: false, length, crc.Value, method, unpackVer, solidFlag, salt, attributes, times);
    this.WriteParts(rec, spill.Stream);
  }

  /// <summary>Adds a directory.</summary>
  public void AddDirectory(string name, RarWriter.ArchiveTimes times = default) {
    this.EnsureHeader();
    var bytes = this.EncodeName(name.TrimEnd('/', '\\'), out var unicode);
    using var spill = new Spill();
    this.WriteParts(new FileRecord(bytes, unicode, IsDirectory: true, 0, 0, RarConstants.Rar4MethodStore, 20, false, null, 0x10, times), spill.Stream);
  }

  /// <summary>Writes the end-of-archive block and flushes.</summary>
  public void Finish() {
    if (this._finished) return;
    this._finished = true;
    this.EnsureHeader();
    this.WriteEnd(moreVolumes: false);
    this._out.Flush();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._finished) this.Finish();
    foreach (var v in this._opened) v.Dispose();
    if (!this._leaveOpen) this._first.Dispose();
  }

  /// <summary>Creates a RAR 4 volume set in memory, one array per volume (old <c>.r00</c> naming).</summary>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      byte method = RarConstants.Rar4MethodNormal, string? password = null) {
    var vols = new List<MemoryStream> { new() };
    using (var w = new Rar4Writer(vols[0], leaveOpen: true, method: method, password: password,
             volumeSize: maxVolumeSize, openVolume: _ => { var m = new MemoryStream(); vols.Add(m); return m; })) {
      foreach (var (name, data) in entries) w.AddFile(name, data);
      w.Finish();
    }
    return vols.Select(v => v.ToArray()).ToArray();
  }

  // ---------------------------------------------------------------- internals

  private sealed record FileRecord(byte[] Name, bool Unicode, bool IsDirectory, long Size, uint Crc, byte Method, byte UnpackVer,
    bool Solid, byte[]? Salt, uint Attributes, RarWriter.ArchiveTimes Times);

  private byte[] EncodeName(string fileName, out bool unicode) {
    var name = fileName.Replace('/', '\\').TrimStart('\\');
    unicode = false;
    if (this._targetCompatibility == RarCompatibility.Rar1_5) {
      try {
        return Oem.GetBytes(name);
      } catch (EncoderFallbackException e) {
        throw new NotSupportedException($"Filename '{fileName}' cannot be represented by the RAR 1.5 OEM code page.", e);
      }
    }
    if (name.All(c => c is >= ' ' and < (char)0x7F))
      return Encoding.ASCII.GetBytes(name);
    // LHD_UNICODE: the OEM name, a zero, then RAR's compressed UTF-16. unrar reads only this
    // form (a bare UTF-8 name it shows as OEM text); 7-Zip reads both.
    unicode = true;
    var oem = Encoding.GetEncoding(437, new EncoderReplacementFallback("_"), DecoderFallback.ReplacementFallback).GetBytes(name);
    var field = new List<byte>(oem.Length + 1 + name.Length * 3);
    field.AddRange(oem);
    field.Add(0);
    field.AddRange(EncodeUnicodeName(name));
    return [.. field];
  }

  /// <summary>
  /// RAR's Unicode name encoding, inverse of the decoder in <see cref="Rar4Parser"/>: a high-byte
  /// seed, then per four characters a flag byte of 2-bit modes; mode 0 is one byte (high byte 0),
  /// mode 2 the low and the high byte.
  /// </summary>
  internal static byte[] EncodeUnicodeName(string name) {
    var output = new List<byte> { 0 };
    for (var i = 0; i < name.Length; i += 4) {
      var flagAt = output.Count;
      output.Add(0);
      var flags = 0;
      for (var k = 0; k < 4 && i + k < name.Length; ++k) {
        var c = name[i + k];
        if (c < 0x100) {
          output.Add((byte)c);
        } else {
          flags |= 2 << (6 - 2 * k);
          output.Add((byte)c);
          output.Add((byte)(c >> 8));
        }
      }
      output[flagAt] = (byte)flags;
    }
    return [.. output];
  }

  private void EnsureHeader() {
    if (this._headerWritten) return;
    this._headerWritten = true;
    this.StartVolume();
  }

  private void StartVolume() {
    this._written = 0;
    this.Raw(RarConstants.Rar4Signature);
    ushort flags = 0;
    if (this._volumeSize > 0) {
      flags |= 0x0001;                       // MHD_VOLUME
      if (this._newVolumeNaming) flags |= 0x0010; // MHD_NEWNUMBERING
      if (this._volumeIndex == 0) flags |= 0x0100; // MHD_FIRSTVOLUME
    }
    if (this._solid) flags |= 0x0008;
    var h = new byte[13];
    h[2] = RarConstants.Rar4TypeMain;
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(3), flags);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(5), 13);
    this.Raw(Seal(h));
  }

  private void WriteEnd(bool moreVolumes) {
    var h = new byte[7];
    h[2] = RarConstants.Rar4TypeEnd;
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(3), (ushort)(moreVolumes ? 0x0001 : 0));
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(5), 7);
    this.Raw(Seal(h));
  }

  private static byte[] Seal(byte[] h) {
    BinaryPrimitives.WriteUInt16LittleEndian(h, (ushort)(Crc32.Compute(h.AsSpan(2)) & 0xFFFF));
    return h;
  }

  private void Raw(ReadOnlySpan<byte> b) {
    this._out.Write(b);
    this._written += b.Length;
  }

  private byte[] FileHeader(FileRecord f, long partSize, uint crc, bool splitBefore, bool splitAfter) {
    var large = f.Size > uint.MaxValue || partSize > uint.MaxValue;
    var times = TimeField(f.Times, out var hasExt);
    var size = 32 + (large ? 8 : 0) + f.Name.Length + (f.Salt != null ? 8 : 0) + times.Length;
    if (size > ushort.MaxValue) throw new NotSupportedException("RAR 4 file header too large (name too long).");
    var h = new byte[size];
    ushort flags = 0x8000;                    // LONG_BLOCK: PACK_SIZE is the data size
    if (splitBefore) flags |= 0x0001;
    if (splitAfter) flags |= 0x0002;
    if (f.Salt != null) flags |= 0x0004 | 0x0400;
    if (f.Solid) flags |= 0x0010;
    if (f.IsDirectory) flags |= 0x00E0;
    else if (this._targetCompatibility != RarCompatibility.Rar1_5) flags |= (ushort)(((this._windowBits - 16) & 7) << 5);
    if (large) flags |= 0x0100;
    if (f.Unicode) flags |= 0x0200;
    if (hasExt) flags |= 0x1000;
    h[2] = RarConstants.Rar4TypeFile;
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(3), flags);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(5), (ushort)size);
    var p = h.AsSpan(7);
    BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)partSize);
    BinaryPrimitives.WriteUInt32LittleEndian(p[4..], (uint)f.Size);
    p[8] = (byte)(this._targetCompatibility == RarCompatibility.Rar1_5 ? 0 : 2); // host OS: MS-DOS / Win32
    BinaryPrimitives.WriteUInt32LittleEndian(p[9..], crc);
    BinaryPrimitives.WriteUInt32LittleEndian(p[13..], DosTime(f.Times.Modified ?? DateTimeOffset.Now));
    p[17] = f.UnpackVer;
    p[18] = f.Method;
    BinaryPrimitives.WriteUInt16LittleEndian(p[19..], (ushort)f.Name.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(p[21..], f.Attributes);
    var pos = 25;
    if (large) {
      BinaryPrimitives.WriteUInt32LittleEndian(p[pos..], (uint)(partSize >> 32));
      BinaryPrimitives.WriteUInt32LittleEndian(p[(pos + 4)..], (uint)(f.Size >> 32));
      pos += 8;
    }
    f.Name.CopyTo(p[pos..]);
    pos += f.Name.Length;
    if (f.Salt != null) {
      f.Salt.CopyTo(p[pos..]);
      pos += 8;
    }
    times.CopyTo(p[pos..]);
    return Seal(h);
  }

  /// <summary>
  /// The extended time field: flags (4 bits per time: present, +1 s, count of sub-second
  /// bytes), then for mtime its sub-second bytes and for ctime/atime a DOS time plus theirs.
  /// </summary>
  private byte[] TimeField(RarWriter.ArchiveTimes t, out bool present) {
    present = false;
    if (this._targetCompatibility == RarCompatibility.Rar1_5 || t.Modified is null && t.Created is null && t.Accessed is null)
      return [];
    present = true;
    var body = new List<byte>();
    var flags = 0;
    void Add(DateTimeOffset? v, int shift, bool withDos) {
      if (v is not { } value) return;
      var local = value.LocalDateTime;
      var dosSecond = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second / 2 * 2, DateTimeKind.Local);
      var rem = local.Ticks - dosSecond.Ticks;      // 0 .. 2 s in 100 ns units
      var extraSecond = rem >= TimeSpan.TicksPerSecond;
      if (extraSecond) rem -= TimeSpan.TicksPerSecond;
      flags |= (8 | (extraSecond ? 4 : 0) | 3) << shift;
      if (withDos) {
        var dos = DosTime(value);
        body.AddRange([(byte)dos, (byte)(dos >> 8), (byte)(dos >> 16), (byte)(dos >> 24)]);
      }
      body.AddRange([(byte)rem, (byte)(rem >> 8), (byte)(rem >> 16)]);
    }
    Add(t.Modified, 12, withDos: false);
    Add(t.Created, 8, withDos: true);
    Add(t.Accessed, 4, withDos: true);
    var field = new byte[2 + body.Count];
    BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)flags);
    body.CopyTo(field, 2);
    return field;
  }

  private void WriteParts(FileRecord f, Stream spill) {
    var total = spill.Length;
    spill.Position = 0;
    var offset = 0L;
    var splitBefore = false;
    var buf = new byte[1 << 16];
    while (true) {
      var left = total - offset;
      var part = left;
      var splitAfter = false;
      if (this._volumeSize > 0) {
        var header = this.FileHeader(f, left, f.Crc, splitBefore, false).Length;
        var room = this._volumeSize - this._written - 7;
        if (header + left > room) {
          var dataRoom = room - this.FileHeader(f, Math.Max(room, 0), 0, splitBefore, true).Length;
          if (dataRoom < 16 || left == 0) {
            if (this._written <= 7 + 13 && dataRoom < 16)
              throw new InvalidOperationException("RAR volume size too small for a file header.");
            this.NextVolume();
            continue;
          }
          part = Math.Min(left, dataRoom);
          if (f.Salt != null) part &= ~15L;
          splitAfter = part < left;
        }
      }
      uint crc;
      if (splitAfter) {
        var c = new Crc32();
        spill.Position = offset;
        CopyTo(spill, Stream.Null, part, buf, c);
        crc = c.Value;
      } else
        crc = f.Crc;
      this.Raw(this.FileHeader(f, part, crc, splitBefore, splitAfter));
      spill.Position = offset;
      CopyTo(spill, this._out, part, buf, null);
      this._written += part;
      offset += part;
      if (!splitAfter) return;
      splitBefore = true;
      this.NextVolume();
    }
  }

  private void NextVolume() {
    this.WriteEnd(moreVolumes: true);
    this._out.Flush();
    ++this._volumeIndex;
    this._out = this._openVolume!(this._volumeIndex);
    this._opened.Add(this._out);
    this.StartVolume();
  }

  private static void CopyTo(Stream from, Stream to, long count, byte[] buf, Crc32? crc) {
    while (count > 0) {
      var n = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
      if (n <= 0) throw new EndOfStreamException();
      crc?.Update(buf.AsSpan(0, n));
      to.Write(buf, 0, n);
      count -= n;
    }
  }

  private static void Copy(Stream from, Stream to, long count, Crc32 crc) {
    var buf = new byte[1 << 16];
    while (count > 0) {
      var n = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
      if (n <= 0) throw new EndOfStreamException("Input ended before the declared length.");
      crc.Update(buf.AsSpan(0, n));
      to.Write(buf, 0, n);
      count -= n;
    }
  }

  private static void ReadExactly(Stream s, byte[] b) {
    var got = 0;
    while (got < b.Length) {
      var n = s.Read(b, got, b.Length - got);
      if (n <= 0) throw new EndOfStreamException("Input ended before the declared length.");
      got += n;
    }
  }

  private static uint DosTime(DateTimeOffset dto) {
    var dt = dto.LocalDateTime;
    if (dt.Year < 1980) dt = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
    var time = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
    var date = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
    return (uint)((date << 16) | time);
  }

  /// <summary>Packed bytes of one file: memory, or a temp file past 64 MB; encrypted in place.</summary>
  private sealed class Spill : IDisposable {
    public Stream Stream { get; private set; } = new GrowingSpill();

    public void Encrypt(byte[] key, byte[] iv) {
      var plain = this.Stream;
      plain.Position = 0;
      var target = new GrowingSpill();
      using (var enc = new RarCbcEncryptStream(target, key, iv)) {
        plain.CopyTo(enc);
        enc.FinishBlock();
      }
      plain.Dispose();
      this.Stream = target;
    }

    public void Dispose() => this.Stream.Dispose();
  }

  private sealed class GrowingSpill : Stream {
    private Stream _inner = new MemoryStream();
    private bool _onDisk;
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
      if (!this._onDisk && this._inner.Length + buffer.Length > SpillInMemory) {
        var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        var pos = this._inner.Position;
        this._inner.Position = 0;
        this._inner.CopyTo(file);
        file.Position = pos;
        this._inner.Dispose();
        this._inner = file;
        this._onDisk = true;
      }
      this._inner.Write(buffer);
    }
    protected override void Dispose(bool disposing) {
      if (disposing) this._inner.Dispose();
      base.Dispose(disposing);
    }
  }
}
