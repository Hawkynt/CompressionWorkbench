#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Nsis;

/// <summary>
/// Writes a minimal NSIS-formatted file. No PE stub is emitted — the reader's
/// <c>ScanForSignature</c> fallback finds the NSIS overlay via linear scan when
/// PE parsing fails. Files are stored uncompressed as individual data blocks
/// (non-solid). Roundtrips through <see cref="NsisReader"/>.
///
/// Note: the produced file has no executable code and cannot be "run" to
/// install — the NSIS format is primarily an installer stub + overlay data,
/// and emitting a functional installer would require shipping a signed PE stub
/// which is out of scope for a compression toolkit.
/// </summary>
public sealed class NsisWriter {
  private readonly List<byte[]> _blocks = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    // NSIS data blocks have no embedded file names (the reader names them
    // "block_N"); we simply treat each input as one opaque block.
    _blocks.Add(data);
  }

  public void WriteTo(Stream output) {
    // ── NSIS first-header (28 bytes) ──
    Span<byte> hdr = stackalloc byte[NsisConstants.FirstHeaderSize];
    BinaryPrimitives.WriteInt32LittleEndian(hdr[..4], NsisConstants.CompNone); // flags: stored, non-solid
    NsisConstants.Signature.CopyTo(hdr[NsisConstants.SignatureOffset..]);
    BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(20, 4), 0); // header_size = 0 (no script header)
    // archive_size (int32 LE) = total size of data blocks area — informational; reader doesn't enforce
    var blocksSize = 0;
    foreach (var b in _blocks) blocksSize += 4 + b.Length;
    BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(24, 4), blocksSize);
    output.Write(hdr);

    // ── Data blocks: each = 4-byte length (with UncompressedFlag) + raw data ──
    Span<byte> lenBuf = stackalloc byte[4];
    foreach (var data in _blocks) {
      var word = (uint)data.Length | NsisConstants.UncompressedFlag; // stored
      BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, word);
      output.Write(lenBuf);
      output.Write(data);
    }
  }
}
