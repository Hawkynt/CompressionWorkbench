using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzx;
using Compression.Core.Dictionary.Quantum;

namespace FileFormat.Cab;

/// <summary>
/// Creates Microsoft Cabinet (CAB) archives.
/// </summary>
/// <remarks>
/// All files are placed in a single folder. Supported compression types are
/// <see cref="CabCompressionType.None"/> (Store), <see cref="CabCompressionType.MsZip"/>,
/// <see cref="CabCompressionType.Lzx"/>, and <see cref="CabCompressionType.Quantum"/>.
/// Call <see cref="AddFile"/> for each file, then call <see cref="WriteTo"/> to produce
/// the archive.
/// </remarks>
public sealed class CabWriter {
  private readonly List<FileEntry> _files = [];
  private readonly CabCompressionType _compressionType;
  private readonly DeflateCompressionLevel _deflateLevel;
  private readonly int _lzxWindowBits;
  private readonly int _quantumWindowLevel;

  /// <summary>
  /// Gets or sets the cabinet set identifier. Defaults to 0.
  /// </summary>
  public ushort SetId { get; set; }

  /// <summary>
  /// Gets or sets the zero-based cabinet index within its set. Defaults to 0.
  /// </summary>
  public ushort CabinetIndex { get; set; }

  // Pending file data.
  private sealed class FileEntry {
    public string Name = "";
    public byte[] Data = [];
    public ushort Date;
    public ushort Time;
    public ushort Attributes;
  }

  /// <summary>
  /// Initializes a new <see cref="CabWriter"/>.
  /// </summary>
  /// <param name="compressionType">
  /// The compression type to apply to the folder.
  /// Supported: <see cref="CabCompressionType.None"/>,
  /// <see cref="CabCompressionType.MsZip"/>, <see cref="CabCompressionType.Lzx"/>,
  /// and <see cref="CabCompressionType.Quantum"/>.
  /// </param>
  /// <param name="deflateLevel">
  /// The Deflate compression level used when
  /// <paramref name="compressionType"/> is <see cref="CabCompressionType.MsZip"/>.
  /// </param>
  /// <param name="lzxWindowBits">
  /// The LZX window size exponent (15–21) used when
  /// <paramref name="compressionType"/> is <see cref="CabCompressionType.Lzx"/>.
  /// </param>
  /// <param name="quantumWindowLevel">
  /// The Quantum window level (1–7) used when
  /// <paramref name="compressionType"/> is <see cref="CabCompressionType.Quantum"/>.
  /// </param>
  public CabWriter(
    CabCompressionType compressionType = CabCompressionType.MsZip,
    DeflateCompressionLevel deflateLevel = DeflateCompressionLevel.Default,
    int lzxWindowBits = 15,
    int quantumWindowLevel = 4) {
    if (compressionType is not CabCompressionType.None
        and not CabCompressionType.MsZip
        and not CabCompressionType.Lzx
        and not CabCompressionType.Quantum)
      throw new ArgumentException(
        $"CabWriter only supports None, MsZip, Lzx, and Quantum compression; got {compressionType}.",
        nameof(compressionType));

    this._compressionType    = compressionType;
    this._deflateLevel       = deflateLevel;
    this._lzxWindowBits      = lzxWindowBits;
    this._quantumWindowLevel = quantumWindowLevel;
  }

  /// <summary>
  /// Adds a file to the cabinet.
  /// </summary>
  /// <param name="name">The file name as it will appear in the archive.</param>
  /// <param name="data">The uncompressed file data.</param>
  /// <param name="lastModified">
  /// The last-modified timestamp. Defaults to <see cref="DateTime.UtcNow"/> when
  /// <c>null</c>.
  /// </param>
  /// <param name="attributes">MS-DOS file attribute flags. Defaults to 0x20 (archive).</param>
  public void AddFile(
    string name,
    byte[] data,
    DateTime? lastModified = null,
    ushort attributes = CabConstants.AttribArchive) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var dt = lastModified ?? DateTime.UtcNow;
    var date = EncodeDosDate(dt);
    var time = EncodeDosTime(dt);

    this._files.Add(new FileEntry {
      Name       = name,
      Data       = data,
      Date       = date,
      Time       = time,
      Attributes = attributes,
    });
  }

  /// <summary>
  /// Writes the complete cabinet archive to <paramref name="output"/>.
  /// </summary>
  /// <param name="output">The stream to write to.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="output"/> is null.</exception>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

    // -----------------------------------------------------------------------
    // Step 1: Concatenate all file data into a single uncompressed stream and
    //         record each file's folder offset.
    // -----------------------------------------------------------------------
    var folderData = new List<byte>();
    var fileOffsets = new uint[this._files.Count];

    for (var i = 0; i < this._files.Count; ++i) {
      fileOffsets[i] = (uint)folderData.Count;
      folderData.AddRange(this._files[i].Data);
    }

    // -----------------------------------------------------------------------
    // Step 2: Split folder data into CFDATA blocks and compress.
    // -----------------------------------------------------------------------
    var dataBlocks = BuildDataBlocks([.. folderData]);

    // -----------------------------------------------------------------------
    // Step 3: Compute layout offsets.
    //
    // CFHEADER: 36 bytes
    // CFFOLDER: 8 bytes  (1 folder)
    // CFFILE[]:  (16 + name + NUL) per file
    // CFDATA[]:  (8 + cbData) per block
    // -----------------------------------------------------------------------
    var coffFolderSection = CabConstants.HeaderSize;            // CFFOLDER immediately after header
    var coffFilesSection  = coffFolderSection + CabConstants.FolderSize; // CFFILE section

    // Compute total CFFILE section size.
    var cffileSize = this._files.Sum(f => CabConstants.FileFixedSize + f.Name.Length + 1);

    var coffDataSection = coffFilesSection + cffileSize;

    // Total cabinet size.
    var totalDataSize = dataBlocks.Sum(b => CabConstants.DataFixedSize + b.CompressedData.Length);
    var cbCabinet     = (uint)(coffDataSection + totalDataSize);

    // -----------------------------------------------------------------------
    // Step 4: Write CFHEADER.
    // -----------------------------------------------------------------------
    writer.Write((byte)0x4D); // 'M'
    writer.Write((byte)0x53); // 'S'
    writer.Write((byte)0x43); // 'C'
    writer.Write((byte)0x46); // 'F'
    writer.Write((uint)0);               // reserved1
    writer.Write(cbCabinet);             // cbCabinet
    writer.Write((uint)0);               // reserved2
    writer.Write((uint)coffFilesSection); // coffFiles
    writer.Write((uint)0);               // reserved3
    writer.Write(CabConstants.VersionMinor);
    writer.Write(CabConstants.VersionMajor);
    writer.Write((ushort)1);             // cFolders
    writer.Write((ushort)this._files.Count); // cFiles
    writer.Write((ushort)0);             // flags
    writer.Write(this.SetId);
    writer.Write(this.CabinetIndex);

    // -----------------------------------------------------------------------
    // Step 5: Write CFFOLDER.
    // -----------------------------------------------------------------------
    writer.Write((uint)coffDataSection);        // coffCabStart
    writer.Write((ushort)dataBlocks.Count);     // cCFData
    ushort typeCompress = this._compressionType switch {
      CabCompressionType.Lzx     => (ushort)((this._lzxWindowBits << 8) | (ushort)CabCompressionType.Lzx),
      CabCompressionType.Quantum => (ushort)((this._quantumWindowLevel << 8) | (ushort)CabCompressionType.Quantum),
      _                          => (ushort)this._compressionType,
    };
    writer.Write(typeCompress); // typeCompress

    // -----------------------------------------------------------------------
    // Step 6: Write CFFILE entries.
    // -----------------------------------------------------------------------
    for (var i = 0; i < this._files.Count; ++i) {
      var f = this._files[i];
      writer.Write((uint)f.Data.Length);  // cbFile
      writer.Write(fileOffsets[i]);       // uoffFolderStart
      writer.Write((ushort)0);            // iFolder (always folder 0)
      writer.Write(f.Date);
      writer.Write(f.Time);
      writer.Write(f.Attributes);

      // null-terminated ASCII name
      writer.Write(System.Text.Encoding.ASCII.GetBytes(f.Name));
      writer.Write((byte)0);
    }

    // -----------------------------------------------------------------------
    // Step 7: Write CFDATA records.
    // -----------------------------------------------------------------------
    foreach (var block in dataBlocks) {
      writer.Write((uint)0);                           // csum (0 = not computed)
      writer.Write((ushort)block.CompressedData.Length); // cbData
      writer.Write((ushort)block.UncompressedSize);    // cbUncomp
      writer.Write(block.CompressedData);
    }
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  private List<(byte[] CompressedData, int UncompressedSize)> BuildDataBlocks(byte[] data) {
    if (this._compressionType == CabCompressionType.None) {
      // Store: split into 32 KB raw blocks wrapped without "CK" prefix.
      var blocks = new List<(byte[], int)>();
      var offset = 0;

      do {
        var len = Math.Min(MsZipCompressor.BlockSize, data.Length - offset);
        var chunk = new byte[len];
        data.AsSpan(offset, len).CopyTo(chunk);
        blocks.Add((chunk, len));
        offset += len;
      } while (offset < data.Length);

      // Special case: always at least one block (even for empty data).
      if (blocks.Count == 0)
        blocks.Add(([], 0));

      return blocks;
    }

    if (this._compressionType == CabCompressionType.Lzx) {
      var blocks = new List<(byte[], int)>();
      int offset = 0;
      var compressor = new LzxCompressor(this._lzxWindowBits);

      while (offset < data.Length) {
        int len = Math.Min(MsZipCompressor.BlockSize, data.Length - offset);
        var chunk = data.AsSpan(offset, len);
        byte[] compressed = compressor.Compress(chunk);
        blocks.Add((compressed, len));
        offset += len;
      }

      if (blocks.Count == 0)
        blocks.Add(([], 0));

      return blocks;
    }

    if (this._compressionType == CabCompressionType.Quantum) {
      var blocks = new List<(byte[], int)>();
      int offset = 0;

      while (offset < data.Length) {
        int len = Math.Min(MsZipCompressor.BlockSize, data.Length - offset);
        var chunk = data.AsSpan(offset, len);
        byte[] compressed = QuantumCompressor.Compress(chunk, this._quantumWindowLevel);
        blocks.Add((compressed, len));
        offset += len;
      }

      if (blocks.Count == 0)
        blocks.Add(([], 0));

      return blocks;
    }

    // MSZIP: use MsZipCompressor which adds "CK" prefix to each Deflate block.
    var msBlocks = MsZipCompressor.CompressBlocks(data, this._deflateLevel);

    // Special case: empty input — emit one empty block.
    if (msBlocks.Count == 0) {
      var emptyBlock = MsZipCompressor.CompressBlocks([], this._deflateLevel);
      return emptyBlock;
    }

    return msBlocks;
  }

  private static ushort EncodeDosDate(DateTime dt) {
    var year  = dt.Year  - 1980;
    var month = dt.Month;
    var day   = dt.Day;
    return (ushort)((year << 9) | (month << 5) | day);
  }

  /// <summary>
  /// Creates a CAB archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <param name="compressionType">The compression type.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      CabCompressionType compressionType = CabCompressionType.MsZip) {
    var writer = new CabWriter(compressionType);
    foreach (var (name, data) in entries)
      writer.AddFile(name, data);
    using var ms = new MemoryStream();
    writer.WriteTo(ms);
    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }

  private static ushort EncodeDosTime(DateTime dt) {
    var hour = dt.Hour;
    var min  = dt.Minute;
    var sec  = dt.Second / 2;
    return (ushort)((hour << 11) | (min << 5) | sec);
  }
}
