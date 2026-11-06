#pragma warning disable CS1591
namespace FileFormat.Bkf;

/// <summary>
/// Reads Microsoft NTBackup (<c>.bkf</c>) media written in Microsoft Tape Format 1.00a, the
/// format NTBackup (Windows NT 4 to Server 2003) writes to tape and to backup files.
/// </summary>
/// <remarks>
/// <para>The medium is walked Format Logical Block by Format Logical Block from the TAPE
/// DBLK (whose Format Logical Block Size, 512 or 1024, sets the step): DBLKs are found by
/// their type and header checksum, soft filemarks and padding are stepped over, and each
/// DBLK's streams are followed to its SPAD. A FILE belongs to the folder of the DIRB before
/// it (implied precedence, §3.3.2.1); folder paths are DIRB names with NUL separators, or a
/// PNAM stream, file names the FILE name or an FNAM stream, in the DBLK's string type.</para>
/// <para>A file's data is its STAN stream; the CSUM stream after a checksummed STAN is
/// verified on extraction. Compressed, encrypted and sparse data streams are listed but not
/// decoded. Entries of a medium with more than one Data Set are listed under
/// <c>set-NN/</c>, and those of a Data Set with more than one volume under the volume's
/// device name.</para>
/// </remarks>
public sealed class BkfReader : IDisposable {
  /// <summary>DBLK type identifiers (MTF 1.00a Table 2) as little-endian UINT32.</summary>
  public static readonly uint TapeType = Mtf.Id("TAPE");
  public static readonly uint SsetType = Mtf.Id("SSET");
  public static readonly uint VolbType = Mtf.Id("VOLB");
  public static readonly uint DirbType = Mtf.Id("DIRB");
  public static readonly uint FileType = Mtf.Id("FILE");
  public static readonly uint EsetType = Mtf.Id("ESET");
  public static readonly uint EotmType = Mtf.Id("EOTM");
  public static readonly uint EspbType = Mtf.Id("ESPB");
  public static readonly uint SfmbType = Mtf.Id("SFMB");

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<BkfEntry> _entries = [];

  /// <summary>Folders and files, in medium order.</summary>
  public IReadOnlyList<BkfEntry> Entries => this._entries;

  /// <summary>The Format Logical Block size from the TAPE DBLK.</summary>
  public int LogicalBlockSize { get; }

  internal MtfMedia Media { get; }

  public BkfReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) {
      this._stream = stream;
      this._leaveOpen = leaveOpen;
    } else {
      var copy = new MemoryStream();
      stream.CopyTo(copy);
      if (!leaveOpen) stream.Dispose();
      this._stream = copy;
    }
    this.Media = MtfMedia.Read(this._stream, keepData: false);
    this.LogicalBlockSize = this.Media.FormatLogicalBlockSize;
    this.Collect();
  }

  /// <summary>The file's data; empty for a folder.</summary>
  /// <exception cref="NotSupportedException">The data stream is compressed, encrypted or sparse.</exception>
  /// <exception cref="InvalidDataException">The data does not match its CSUM stream.</exception>
  public byte[] Extract(BkfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.DataLength <= 0) return [];
    if (entry.IsCompressed)
      throw new NotSupportedException($"BKF: '{entry.Name}' is stored compressed or encrypted; its algorithm is not decoded here.");
    if (entry.IsSparse)
      throw new NotSupportedException($"BKF: '{entry.Name}' is stored as sparse (SPAR) streams, which are not assembled here.");
    var data = MtfMedia.ReadAt(this._stream, entry.DataOffset, checked((int)entry.DataLength));
    if (entry.Checksum is { } expected && Mtf.Xor32(data) != expected)
      throw new InvalidDataException($"BKF: '{entry.Name}' does not match its CSUM stream.");
    return data;
  }

  public void Dispose() {
    if (!this._leaveOpen) this._stream.Dispose();
  }

  private void Collect() {
    var manySets = this.Media.Sets.Count > 1;
    for (var s = 0; s < this.Media.Sets.Count; ++s) {
      var set = this.Media.Sets[s];
      var manyVolumes = set.Blocks.Count(b => b.Type == "VOLB") > 1;
      var setPrefix = manySets ? $"set-{s + 1:D2}/" : "";
      var prefix = setPrefix;
      var folder = "";
      foreach (var block in set.Blocks) {
        switch (block.Type) {
          case "VOLB":
            var device = block.String(56).TrimEnd(':', '\\', '/').Replace(':', '_').Replace('\\', '/');
            prefix = setPrefix + (manyVolumes && device.Length > 0 ? device + "/" : "");
            folder = "";
            break;
          case "DIRB":
            folder = FolderPath(block);
            if (folder.Length > 0)
              this._entries.Add(new BkfEntry {
                Name = prefix + folder,
                IsDirectory = true,
                LastModified = Mtf.ReadDate(block.Dblk.AsSpan(56, 5)),
              });
            break;
          case "FILE":
            this._entries.Add(this.FileEntry(block, prefix + (folder.Length > 0 ? folder + "/" : "")));
            break;
        }
      }
    }
  }

  private string FolderPath(MtfBlock dirb) {
    var name = (dirb.U32(52) & Mtf.DirbPathInStreamBit) != 0
      ? this.StreamString(dirb, "PNAM")
      : dirb.String(80);
    return string.Join('/', name.Split('\0', StringSplitOptions.RemoveEmptyEntries));
  }

  private BkfEntry FileEntry(MtfBlock file, string folder) {
    var name = (file.U32(52) & Mtf.FileNameInStreamBit) != 0 ? this.StreamString(file, "FNAM") : file.String(84);
    name = name.TrimEnd('\0').Replace('\\', '_').Replace('/', '_');
    var stan = file.Streams.FirstOrDefault(s => s.Id == "STAN");
    uint? checksum = null;
    var sparse = file.Streams.Any(s => s.Id == "SPAR");
    if (stan != null) {
      var index = file.Streams.IndexOf(stan);
      if ((stan.MediaAttributes & Mtf.StreamChecksummed) != 0 && index + 1 < file.Streams.Count
          && file.Streams[index + 1] is { Id: "CSUM", Length: 4 } csum)
        checksum = BitConverter.ToUInt32(MtfMedia.ReadAt(this._stream, csum.DataOffset, 4));
      sparse |= (stan.FileSystemAttributes & Mtf.StreamIsSparse) != 0;
    }
    return new BkfEntry {
      Name = folder + name,
      Size = stan?.Length ?? 0,
      LastModified = Mtf.ReadDate(file.Dblk.AsSpan(56, 5)),
      DataOffset = stan?.DataOffset ?? 0,
      DataLength = stan?.Length ?? 0,
      Checksum = checksum,
      IsCompressed = stan != null && (stan.MediaAttributes & (Mtf.StreamCompressed | Mtf.StreamEncrypted)) != 0,
      IsSparse = sparse,
    };
  }

  private string StreamString(MtfBlock block, string id) {
    var s = block.Stream(id);
    return s == null ? "" : Mtf.DecodeString(MtfMedia.ReadAt(this._stream, s.DataOffset, checked((int)s.Length)), block.StringType);
  }
}
