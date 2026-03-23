using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzx;
using Compression.Core.Dictionary.Quantum;

namespace FileFormat.Cab;

/// <summary>
/// Reads and extracts files from a Microsoft Cabinet (CAB) archive.
/// </summary>
/// <remarks>
/// Supports <see cref="CabCompressionType.None"/> (Store),
/// <see cref="CabCompressionType.MsZip"/>, <see cref="CabCompressionType.Quantum"/>,
/// and <see cref="CabCompressionType.Lzx"/> folders.
/// </remarks>
public sealed class CabReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _quantumRescaleThreshold;
  private readonly List<CabEntry> _entries = [];
  private readonly List<FolderInfo> _folders = [];
  private bool _disposed;

  /// <summary>
  /// Gets the list of file entries in the cabinet.
  /// </summary>
  public IReadOnlyList<CabEntry> Entries => this._entries;

  // Internal folder metadata.
  private sealed class FolderInfo {
    public uint DataOffset;
    public ushort DataCount;
    public CabCompressionType CompressionType;
    public ushort RawCompressionType;
  }

  /// <summary>
  /// Opens a CAB archive from a stream.
  /// </summary>
  /// <param name="stream">The stream to read from. Must support seeking.</param>
  /// <param name="leaveOpen">
  /// When <c>true</c>, the stream is not closed on <see cref="Dispose"/>.
  /// </param>
  /// <param name="quantumRescaleThreshold">
  /// The rescale threshold for Quantum adaptive models. Use 3800 (default) for
  /// standard Quantum data from Microsoft tools, or 256 for data produced by
  /// <see cref="QuantumCompressor"/>.
  /// </param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the stream does not contain a valid CAB archive.
  /// </exception>
  public CabReader(Stream stream, bool leaveOpen = false,
    int quantumRescaleThreshold = 3800) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._quantumRescaleThreshold = quantumRescaleThreshold;
    this.ReadHeader();
  }

  /// <summary>
  /// Extracts the content of <paramref name="entry"/> and returns it as a byte array.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The uncompressed file data.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the folder uses an unsupported compression type.
  /// </exception>
  public byte[] ExtractEntry(CabEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    var folder = this._folders[entry.FolderIndex];
    var folderData = this.DecompressFolder(folder);

    // Slice the file's portion from the folder data.
    var start = (int)entry.FolderOffset;
    var length = (int)entry.UncompressedSize;
    return folderData[start..(start + length)];
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  private void ReadHeader() {
    using var reader = new BinaryReader(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);

    // Validate signature "MSCF".
    var sig = reader.ReadBytes(4);
    if (sig.Length < 4 || sig[0] != 0x4D || sig[1] != 0x53 || sig[2] != 0x43 || sig[3] != 0x46)
      throw new InvalidDataException("CAB: invalid signature (expected 'MSCF').");

    var reserved1  = reader.ReadUInt32(); // must be 0
    var cbCabinet  = reader.ReadUInt32(); // total size
    var reserved2  = reader.ReadUInt32(); // must be 0
    var coffFiles  = reader.ReadUInt32(); // offset to first CFFILE
    var reserved3  = reader.ReadUInt32(); // must be 0
    var verMinor   = reader.ReadByte();
    var verMajor   = reader.ReadByte();
    var cFolders   = reader.ReadUInt16();
    var cFiles     = reader.ReadUInt16();
    var flags      = reader.ReadUInt16();
    var setId      = reader.ReadUInt16();
    var iCabinet   = reader.ReadUInt16();

    // Skip reserve fields if present.
    if ((flags & CabConstants.FlagReserveFields) != 0) {
      var cbCabinetReserve = reader.ReadUInt16();
      var cbFolderReserve  = reader.ReadByte();
      var cbDataReserve    = reader.ReadByte();
      reader.ReadBytes(cbCabinetReserve);
    }

    // Read CFFOLDER entries.
    for (var i = 0; i < cFolders; ++i) {
      var coffCabStart = reader.ReadUInt32();
      var cCFData      = reader.ReadUInt16();
      var typeCompress = reader.ReadUInt16();

      this._folders.Add(new FolderInfo {
        DataOffset          = coffCabStart,
        DataCount           = cCFData,
        CompressionType     = (CabCompressionType)(typeCompress & 0x000F),
        RawCompressionType  = typeCompress,
      });
    }

    // Seek to the first CFFILE.
    this._stream.Seek(coffFiles, SeekOrigin.Begin);

    // Re-wrap reader (position changed).
    using var fileReader = new BinaryReader(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);

    for (var i = 0; i < cFiles; ++i) {
      var cbFile          = fileReader.ReadUInt32();
      var uoffFolderStart = fileReader.ReadUInt32();
      var iFolderIdx      = fileReader.ReadUInt16();
      var date            = fileReader.ReadUInt16();
      var time            = fileReader.ReadUInt16();
      var attribs         = fileReader.ReadUInt16();

      // Read null-terminated file name.
      var nameBytes = new List<byte>();
      byte b;
      while ((b = fileReader.ReadByte()) != 0)
        nameBytes.Add(b);

      var isUtf8 = (attribs & CabConstants.AttribUtf8) != 0;
      var name = isUtf8
        ? System.Text.Encoding.UTF8.GetString([.. nameBytes])
        : System.Text.Encoding.ASCII.GetString([.. nameBytes]);

      this._entries.Add(new CabEntry(name, cbFile, uoffFolderStart, iFolderIdx, date, time, attribs));
    }
  }

  private byte[] DecompressFolder(FolderInfo folder) {
    var output = new List<byte>();

    this._stream.Seek(folder.DataOffset, SeekOrigin.Begin);
    using var reader = new BinaryReader(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);

    for (var i = 0; i < folder.DataCount; ++i) {
      var csum     = reader.ReadUInt32(); // checksum (ignored)
      var cbData   = reader.ReadUInt16(); // compressed size
      var cbUncomp = reader.ReadUInt16(); // uncompressed size
      var abData   = reader.ReadBytes(cbData);

      switch (folder.CompressionType) {
        case CabCompressionType.None:
          output.AddRange(abData);
          break;

        case CabCompressionType.MsZip: {
          var decompressed = MsZipDecompressor.DecompressBlock(abData);
          output.AddRange(decompressed);
          break;
        }

        case CabCompressionType.Quantum: {
          var windowLevel = (folder.RawCompressionType >> 8) & 0x1F;
          if (windowLevel < 1) windowLevel = 4;
          var decompressed = QuantumDecompressor.Decompress(abData, cbUncomp, windowLevel,
            this._quantumRescaleThreshold);
          output.AddRange(decompressed);
          break;
        }

        case CabCompressionType.Lzx: {
          var windowBits = (folder.RawCompressionType >> 8) & 0x1F;
          if (windowBits < 15) windowBits = 15;
          using var lzxStream = new MemoryStream(abData);
          var lzxDecomp = new LzxDecompressor(lzxStream, windowBits);
          var decompressed = lzxDecomp.Decompress(cbUncomp);
          output.AddRange(decompressed);
          break;
        }

        default:
          throw new InvalidDataException(
            $"CAB: unsupported compression type {folder.CompressionType}.");
      }
    }

    return [.. output];
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (this._disposed)
      return;

    this._disposed = true;

    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
