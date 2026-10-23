using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.AlZip;

/// <summary>
/// Writes single-volume ALZip archives as ALZip 8 lays them out: the "ALZ\x01" header with version
/// 0x0A, one "BLZ\x01" local header per member (stored or deflated, size fields as narrow as the
/// sizes allow, CP949 names, optional ZipCrypto), then the "CLZ\x01" and "CLZ\x02" end records.
/// </summary>
public sealed class AlZipWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string? _password;
  private readonly bool _store;
  private bool _finished;

  public AlZipWriter(Stream stream, bool leaveOpen = false, string? password = null, bool store = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._password = string.IsNullOrEmpty(password) ? null : password;
    this._store = store;
    this._stream.Write("ALZ\x01\x0A\x00\x00\x00"u8);
  }

  private static int Width(long a, long b) {
    var max = Math.Max(a, b);
    return max <= byte.MaxValue ? 1 : max <= ushort.MaxValue ? 2 : max <= uint.MaxValue ? 4 : 8;
  }

  public void AddFile(string fileName, byte[] data, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);
    var crc = Crc32.Compute(data);
    byte method = 0;
    var payload = data;
    if (!this._store && data.Length > 0) {
      var deflated = DeflateCompressor.Compress(data);
      if (deflated.Length < data.Length) {
        method = 2;
        payload = deflated;
      }
    }
    var dosTime = AlZipReader.DateTimeToDosTime(modified ?? DateTime.Now);
    byte[]? header = null;
    if (this._password != null) {
      var crypto = new AlZipCrypto(AlZipReader.Cp949.GetBytes(this._password));
      header = crypto.MakeHeader((byte)(crc >> 24));
      payload = (byte[])payload.Clone();
      crypto.Encrypt(payload);
    }
    this.WriteHeader(fileName, 0x20, dosTime, method, crc, payload.Length, data.Length, header);
    this._stream.Write(payload);
  }

  public void AddDirectory(string dirName, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(dirName);
    this.WriteHeader(dirName.TrimEnd('/', '\\'), 0x10, AlZipReader.DateTimeToDosTime(modified ?? DateTime.Now), null, 0, 0, 0, null);
  }

  private void WriteHeader(string name, byte attributes, uint dosTime, byte? method, uint crc, long packed, long size, byte[]? encryptionHeader) {
    var nameBytes = AlZipReader.Cp949.GetBytes(name.Replace('/', '\\'));
    if (nameBytes.Length is 0 or > 4096)
      throw new ArgumentException($"Invalid ALZip member name '{name}'.");
    var width = method == null ? 0 : Width(packed, size);
    var widthBits = width switch { 0 => 0x00, 1 => 0x10, 2 => 0x20, 4 => 0x40, _ => 0x80 };
    var descriptor = (byte)(widthBits | (encryptionHeader != null ? 0x01 : 0));
    var buf = new byte[4 + 9 + (width > 0 ? 6 + 2 * width : 0)];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, AlZipReader.LocalSig);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)nameBytes.Length);
    buf[6] = attributes;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(7), dosTime);
    buf[11] = descriptor;
    buf[12] = 0;
    if (width > 0) {
      buf[13] = method!.Value;
      buf[14] = 0;
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(15), crc);
      Span<byte> v = stackalloc byte[8];
      BinaryPrimitives.WriteUInt64LittleEndian(v, (ulong)packed);
      v[..width].CopyTo(buf.AsSpan(19));
      BinaryPrimitives.WriteUInt64LittleEndian(v, (ulong)size);
      v[..width].CopyTo(buf.AsSpan(19 + width));
    }
    this._stream.Write(buf);
    this._stream.Write(nameBytes);
    if (encryptionHeader != null)
      this._stream.Write(encryptionHeader);
  }

  /// <summary>Writes the end records; called by <see cref="Dispose"/>.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;
    Span<byte> tail = stackalloc byte[16];
    tail.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(tail, AlZipReader.CentralSig);
    BinaryPrimitives.WriteUInt32LittleEndian(tail[12..], AlZipReader.EndSig);
    this._stream.Write(tail);
  }

  public void Dispose() {
    this.Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
