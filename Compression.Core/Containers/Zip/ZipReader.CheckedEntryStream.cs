using Compression.Core.Checksums;

namespace FileFormat.Zip;

public sealed partial class ZipReader {
  /// <summary>
  /// Forward-only view of an entry's decoded bytes that checks the CRC-32 and the size from the
  /// central directory once the data ends.
  /// </summary>
  private sealed class CheckedEntryStream(Stream inner, ZipEntry entry) : Stream {
    private readonly Crc32 _crc = new();
    private long _produced;
    private bool _verified;

    public override int Read(Span<byte> buffer) {
      if (buffer.IsEmpty)
        return 0;

      var n = this._produced < entry.UncompressedSize
        ? inner.Read(buffer[..(int)Math.Min(buffer.Length, entry.UncompressedSize - this._produced)])
        : 0;

      if (n > 0) {
        this._crc.Update(buffer[..n]);
        this._produced += n;
        return n;
      }

      this.Verify();
      return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    private void Verify() {
      if (this._verified)
        return;

      this._verified = true;
      if (this._produced != entry.UncompressedSize)
        throw new InvalidDataException($"ZIP entry '{entry.FileName}' ended after {this._produced} of {entry.UncompressedSize} bytes.");
      if (this._crc.Value != entry.Crc32)
        throw new InvalidDataException($"CRC-32 mismatch for '{entry.FileName}': expected 0x{entry.Crc32:X8}, computed 0x{this._crc.Value:X8}.");
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => entry.UncompressedSize;

    public override long Position {
      get => this._produced;
      set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
      if (disposing)
        inner.Dispose();
      base.Dispose(disposing);
    }
  }
}
