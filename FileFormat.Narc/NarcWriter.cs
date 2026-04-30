using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Narc;

/// <summary>
/// Writes a Nintendo NARC archive using the flat-tree BTNF variant
/// (one root directory containing all files at the top level).
/// </summary>
public sealed class NarcWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Creates a new NARC writer.
  /// </summary>
  /// <param name="stream">The destination stream; must support writing and seeking.</param>
  /// <param name="leaveOpen">If true, the stream is not disposed when this writer is disposed.</param>
  public NarcWriter(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file to the archive. Names must be 1..127 ASCII bytes (BTNF length byte is 7-bit).</summary>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after the writer has been finished.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    if (name.Length == 0)
      throw new ArgumentException("Entry name must not be empty.", nameof(name));

    var nameBytes = Encoding.ASCII.GetByteCount(name);
    if (nameBytes > NarcConstants.MaxNameLength)
      throw new ArgumentException(
        $"Entry name '{name}' is {nameBytes} bytes; NARC BTNF length byte is 7-bit (max {NarcConstants.MaxNameLength}).",
        nameof(name));

    this._entries.Add((name, data));
  }

  /// <summary>Flushes all sections to the stream. Idempotent.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    // Layout up-front so we can fill in NITRO file_size on the first pass.
    var btafSize = NarcConstants.SectionHeaderSize + 4 + this._entries.Count * NarcConstants.BtafEntrySize;

    // Flat BTNF body: 8-byte root entry + length-prefixed name list + 0x00 terminator. The
    // canonical NARC pads the section size up to a 4-byte boundary so the GMIF header lands aligned.
    var btnfBodySize = 8;
    foreach (var (name, _) in this._entries)
      btnfBodySize += 1 + Encoding.ASCII.GetByteCount(name);
    btnfBodySize += 1; // list terminator
    var btnfSizeUnpadded = NarcConstants.SectionHeaderSize + btnfBodySize;
    var btnfSize = (btnfSizeUnpadded + 3) & ~3;
    var btnfPad  = btnfSize - btnfSizeUnpadded;

    long gmifDataSize = 0;
    foreach (var (_, data) in this._entries)
      gmifDataSize += data.Length;
    var gmifSize = NarcConstants.SectionHeaderSize + gmifDataSize;

    var totalSize = NarcConstants.NitroHeaderSize + btafSize + btnfSize + gmifSize;

    // NITRO header.
    Span<byte> nitro = stackalloc byte[NarcConstants.NitroHeaderSize];
    NarcConstants.MagicNarc.CopyTo(nitro);
    BinaryPrimitives.WriteUInt16LittleEndian(nitro[4..6],   NarcConstants.BomLittleEndian);
    BinaryPrimitives.WriteUInt16LittleEndian(nitro[6..8],   NarcConstants.DefaultVersion);
    BinaryPrimitives.WriteUInt32LittleEndian(nitro[8..12],  (uint)totalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(nitro[12..14], NarcConstants.NitroHeaderSize);
    BinaryPrimitives.WriteUInt16LittleEndian(nitro[14..16], NarcConstants.SectionCount);
    this._stream.Write(nitro);

    // BTAF: header + count + (start,end) pairs relative to start of GMIF data region.
    Span<byte> btafHdr = stackalloc byte[NarcConstants.SectionHeaderSize + 4];
    NarcConstants.MagicBtaf.CopyTo(btafHdr);
    BinaryPrimitives.WriteUInt32LittleEndian(btafHdr[4..8],  (uint)btafSize);
    BinaryPrimitives.WriteUInt32LittleEndian(btafHdr[8..12], (uint)this._entries.Count);
    this._stream.Write(btafHdr);

    Span<byte> btafEntry = stackalloc byte[NarcConstants.BtafEntrySize];
    uint cursor = 0;
    foreach (var (_, data) in this._entries) {
      var end = cursor + (uint)data.Length;
      BinaryPrimitives.WriteUInt32LittleEndian(btafEntry[..4], cursor);
      BinaryPrimitives.WriteUInt32LittleEndian(btafEntry[4..8], end);
      this._stream.Write(btafEntry);
      cursor = end;
    }

    // BTNF: flat tree. Root sub-table offset = 8 (immediately after the root entry).
    Span<byte> btnfHdr = stackalloc byte[NarcConstants.SectionHeaderSize + 8];
    NarcConstants.MagicBtnf.CopyTo(btnfHdr);
    BinaryPrimitives.WriteUInt32LittleEndian(btnfHdr[4..8],   (uint)btnfSize);
    BinaryPrimitives.WriteUInt32LittleEndian(btnfHdr[8..12],  0x00000008u);            // sub-table offset
    BinaryPrimitives.WriteUInt16LittleEndian(btnfHdr[12..14], 0x0000);                 // first file ID
    BinaryPrimitives.WriteUInt16LittleEndian(btnfHdr[14..16], 0x0001);                 // dir count (root only)
    this._stream.Write(btnfHdr);

    foreach (var (name, _) in this._entries) {
      var nameBytes = Encoding.ASCII.GetBytes(name);
      this._stream.WriteByte((byte)nameBytes.Length);
      this._stream.Write(nameBytes);
    }
    this._stream.WriteByte(0x00); // terminator
    for (var i = 0; i < btnfPad; ++i)
      this._stream.WriteByte(0x00);

    // GMIF: header + concatenated payloads at the offsets BTAF promised.
    Span<byte> gmifHdr = stackalloc byte[NarcConstants.SectionHeaderSize];
    NarcConstants.MagicGmif.CopyTo(gmifHdr);
    BinaryPrimitives.WriteUInt32LittleEndian(gmifHdr[4..8], (uint)gmifSize);
    this._stream.Write(gmifHdr);

    foreach (var (_, data) in this._entries)
      if (data.Length > 0)
        this._stream.Write(data);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      this.Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
