using System.IO.Compression;
using System.Text;

namespace FileFormat.Psf;

/// <summary>
/// Writes a Portable Sound Format (PSF) container. The CRC stored in the header is over
/// the COMPRESSED program bytes (per spec) — common bug source if mistakenly computed
/// over the uncompressed payload, which the round-trip test guards against.
/// </summary>
public sealed class PsfWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _finished;
  private bool _disposed;

  /// <summary>The platform/version byte (default 0x01 = PS1).</summary>
  public byte VersionByte { get; set; } = PsfConstants.VersionPs1;

  /// <summary>Reserved-area blob written verbatim between header and compressed program.</summary>
  public byte[] ReservedData { get; set; } = [];

  /// <summary>Uncompressed program payload. Will be zlib-compressed at <c>CompressionLevel.Optimal</c>.</summary>
  public byte[] ProgramData { get; set; } = [];

  /// <summary>Tag key/value pairs serialized as a UTF-8 <c>[TAG]</c> block. Empty -> no tag block.</summary>
  public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Initializes a new <see cref="PsfWriter"/> bound to <paramref name="stream"/>.</summary>
  public PsfWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Serializes all fields to the underlying stream. Idempotent.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var compressed = Deflate(this.ProgramData);
    var crc = PsfCrc32.Compute(compressed);

    Span<byte> header = stackalloc byte[PsfConstants.HeaderSize];
    header[0] = PsfConstants.Magic[0];
    header[1] = PsfConstants.Magic[1];
    header[2] = PsfConstants.Magic[2];
    header[3] = this.VersionByte;
    BitConverter.TryWriteBytes(header[4..8],   (uint)this.ReservedData.Length);
    BitConverter.TryWriteBytes(header[8..12],  (uint)compressed.Length);
    BitConverter.TryWriteBytes(header[12..16], crc);

    this._stream.Write(header);
    if (this.ReservedData.Length > 0)
      this._stream.Write(this.ReservedData);
    if (compressed.Length > 0)
      this._stream.Write(compressed);

    if (this.Tags.Count > 0)
      WriteTagBlock();
  }

  private static byte[] Deflate(byte[] data) {
    if (data.Length == 0) {
      // ZLibStream still emits a valid zlib frame for empty input; preserve that so the
      // CRC and reader path are exercised the same way as for non-empty programs.
      using var emptyMs = new MemoryStream();
      using (var z = new ZLibStream(emptyMs, CompressionLevel.Optimal, leaveOpen: true)) { }
      return emptyMs.ToArray();
    }
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      z.Write(data, 0, data.Length);
    return ms.ToArray();
  }

  private void WriteTagBlock() {
    var sb = new StringBuilder(PsfConstants.TagPrefix);
    foreach (var kvp in this.Tags) {
      // Newlines in values are split into multiple key=value records so the reader's
      // line-oriented parser reconstructs identical Tags content.
      foreach (var line in kvp.Value.Split('\n'))
        sb.Append(kvp.Key).Append('=').Append(line).Append('\n');
    }
    this._stream.Write(Encoding.UTF8.GetBytes(sb.ToString()));
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
}
