using System.IO.Compression;
using System.Text;

namespace FileFormat.Ypf;

/// <summary>
/// Writes a YPF v480 archive. Per spec, the per-entry CRC is computed over the on-disk
/// COMPRESSED bytes (a frequent foot-gun if mistakenly applied to the uncompressed payload).
/// Name hashes are produced via <see cref="YpfHash"/> on raw (non-obfuscated) names so the
/// archive round-trips identically through <see cref="YpfReader"/>.
/// </summary>
public sealed class YpfWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<PendingEntry> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>Initializes a new <see cref="YpfWriter"/> bound to <paramref name="stream"/>.</summary>
  public YpfWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file to the archive. Compresses with zlib unless that would inflate the payload.</summary>
  public void AddEntry(string name, byte[] data, byte type = YpfConstants.TypeUnspecified) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var nameBytes = Encoding.ASCII.GetBytes(name);
    if (nameBytes.Length > byte.MaxValue)
      throw new ArgumentException($"YPF entry name length must fit in one byte (got {nameBytes.Length}).", nameof(name));

    var compressed = Deflate(data);
    byte compressionFlag;
    byte[] payload;
    if (compressed.Length < data.Length) {
      compressionFlag = YpfConstants.CompressionZlib;
      payload = compressed;
    } else {
      // Fallback to stored when zlib doesn't shrink — saves space and avoids redundant CPU on extract.
      compressionFlag = YpfConstants.CompressionStored;
      payload = data;
    }

    this._entries.Add(new PendingEntry {
      Name = name,
      NameBytes = nameBytes,
      NameHash = YpfHash.Hash(name),
      Type = type,
      Compression = compressionFlag,
      RawSize = (uint)data.Length,
      Payload = payload,
      Crc32 = YpfCrc32.Compute(payload),
    });
  }

  /// <summary>Serializes the header, entry table, and all payloads to the output stream.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    // Records are variable-length: 4(hash) + 1(nameLen) + N(name) + 1(type) + 1(comp) + 4(raw) + 4(comp) + 4(off) + 4(crc).
    var tableSize = 0u;
    foreach (var e in this._entries)
      tableSize += (uint)(4 + 1 + e.NameBytes.Length + 1 + 1 + 4 + 4 + 4 + 4);

    // 1) Write header.
    Span<byte> header = stackalloc byte[YpfConstants.HeaderSize];
    YpfConstants.Magic.CopyTo(header);
    BitConverter.TryWriteBytes(header[4..8],   YpfConstants.SupportedVersion);
    BitConverter.TryWriteBytes(header[8..12],  (uint)this._entries.Count);
    BitConverter.TryWriteBytes(header[12..16], tableSize);
    // header[16..32] reserved — already zero from stackalloc semantics not guaranteed; explicitly clear.
    header[16..32].Clear();
    this._stream.Write(header);

    // 2) Reserve entry table space (we'll backpatch with real offsets after writing payloads).
    var tableStart = this._stream.Position;
    if (tableSize > 0)
      this._stream.Write(new byte[tableSize]);

    // 3) Write payloads, recording offsets.
    var dataOffsets = new uint[this._entries.Count];
    for (var i = 0; i < this._entries.Count; ++i) {
      dataOffsets[i] = (uint)this._stream.Position;
      var p = this._entries[i].Payload;
      if (p.Length > 0)
        this._stream.Write(p);
    }

    // 4) Backpatch entry table.
    this._stream.Position = tableStart;
    // Hoisted out of the loop: avoids CA2014 (stackalloc-in-loop). 5 + 18 = 23 bytes total.
    Span<byte> headPart = stackalloc byte[5];
    Span<byte> tailPart = stackalloc byte[1 + 1 + 4 + 4 + 4 + 4];
    for (var i = 0; i < this._entries.Count; ++i) {
      var e = this._entries[i];
      BitConverter.TryWriteBytes(headPart[0..4], e.NameHash);
      headPart[4] = (byte)e.NameBytes.Length;
      this._stream.Write(headPart);
      this._stream.Write(e.NameBytes);

      tailPart[0] = e.Type;
      tailPart[1] = e.Compression;
      BitConverter.TryWriteBytes(tailPart[2..6],   e.RawSize);
      BitConverter.TryWriteBytes(tailPart[6..10],  (uint)e.Payload.Length);
      BitConverter.TryWriteBytes(tailPart[10..14], dataOffsets[i]);
      BitConverter.TryWriteBytes(tailPart[14..18], e.Crc32);
      this._stream.Write(tailPart);
    }

    this._stream.Position = this._stream.Length;
  }

  private static byte[] Deflate(byte[] data) {
    if (data.Length == 0)
      return [];
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      z.Write(data, 0, data.Length);
    return ms.ToArray();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private sealed class PendingEntry {
    public string Name { get; init; } = "";
    public byte[] NameBytes { get; init; } = [];
    public uint NameHash { get; init; }
    public byte Type { get; init; }
    public byte Compression { get; init; }
    public uint RawSize { get; init; }
    public byte[] Payload { get; init; } = [];
    public uint Crc32 { get; init; }
  }
}
