using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace FileSystem.Vdfs;

/// <summary>
/// Reads a ZenGin VDF volume: the 296-byte header, the catalog tree behind it, and each
/// file's payload, stored or as a Union ZippedStream.
/// </summary>
/// <remarks>
/// The catalog is walked the way ZenKit walks it — a directory's entries run until one
/// carries <see cref="VdfsLayout.TypeLast" />, and a directory entry's offset is the index of
/// its first child. A damaged catalog is read as far as it holds together and reported
/// through <see cref="IsPartial" />; only a missing signature makes the volume unreadable.
/// </remarks>
public sealed class VdfsReader : IDisposable {

  /// <summary>Most directory levels followed before the walk calls the catalog damaged.</summary>
  private const int MaxDepth = 256;

  /// <summary>
  /// Random-access view over the volume. Copying it into a byte[] capped the reader at
  /// the array limit, which the 32-bit payload offsets do not.
  /// </summary>
  private readonly ImageAccessor _data;
  private readonly List<VdfsEntry> _entries = [];

  /// <summary>Directory and file entries, parents before their children.</summary>
  public IReadOnlyList<VdfsEntry> Entries => this._entries;

  /// <summary>The volume comment, up to its first padding byte.</summary>
  public string Comment { get; private set; } = "";

  /// <summary>The 16 signature bytes as written.</summary>
  public byte[] Signature { get; private set; } = [];

  /// <summary>When the header says the volume was written, if it names a valid date.</summary>
  public DateTime? Timestamp { get; private set; }

  /// <summary>Whether the volume flags mark it as a zipped (Union) volume.</summary>
  public bool IsZippedVolume { get; private set; }

  /// <summary>Raw volume flags.</summary>
  public uint VolumeFlags { get; private set; }

  /// <summary>Byte offset the catalog starts at.</summary>
  public long EntryTableOffset { get; private set; }

  /// <summary>Bytes the catalog occupies, as far as the volume holds it.</summary>
  public long EntryTableLength { get; private set; }

  /// <summary>Catalog entries the header declares.</summary>
  public int DeclaredEntryCount { get; private set; }

  /// <summary>Whether part of the catalog could not be followed or pointed outside the volume.</summary>
  public bool IsPartial { get; private set; }

  /// <summary>Opens <paramref name="stream" />, which must be seekable.</summary>
  /// <exception cref="InvalidDataException">The stream holds no VDF signature.</exception>
  public VdfsReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._data = new ImageAccessor(stream, leaveOpen: true);
    _ = leaveOpen;
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < VdfsLayout.HeaderSize)
      throw new InvalidDataException("VDFS: file too small for the 296-byte header.");

    var signature = this._data.Read(VdfsLayout.SignatureOffset, VdfsLayout.SignatureSize);
    if (!signature.AsSpan().StartsWith(VdfsLayout.SignaturePrefix))
      throw new InvalidDataException("VDFS: no PSVDSC_V2.00 signature at offset 256.");
    this.Signature = signature;

    var comment = this._data.Read(0, VdfsLayout.CommentSize);
    var commentEnd = Array.IndexOf(comment, VdfsLayout.CommentPad);
    this.Comment = VdfsLayout.NameEncoding.GetString(comment, 0, commentEnd < 0 ? comment.Length : commentEnd);

    var entryCount = this._data.ReadUInt32(VdfsLayout.EntryCountOffset);
    this.Timestamp = VdfsLayout.FromDosTime(this._data.ReadUInt32(VdfsLayout.TimestampOffset));
    var catalogOffset = (long)this._data.ReadUInt32(VdfsLayout.CatalogOffsetOffset);
    this.VolumeFlags = this._data.ReadUInt32(VdfsLayout.VolumeFlagsOffset);
    this.IsZippedVolume = this.VolumeFlags == VdfsLayout.VolumeZipped;
    if (catalogOffset == 0) catalogOffset = VdfsLayout.HeaderSize;

    var available = catalogOffset >= this._data.Length ? 0 : (this._data.Length - catalogOffset) / VdfsLayout.EntrySize;
    var usable = (int)Math.Min(Math.Min(entryCount, available), int.MaxValue);
    this.IsPartial = usable < entryCount;
    this.DeclaredEntryCount = (int)Math.Min(entryCount, int.MaxValue);
    this.EntryTableOffset = catalogOffset;
    this.EntryTableLength = (long)usable * VdfsLayout.EntrySize;

    if (usable > 0) {
      var budget = usable;
      this.ReadDirectory(catalogOffset, usable, 0, "", 0, [], ref budget);
    }
  }

  /// <summary>
  /// Reads the entries of one directory starting at <paramref name="first" />, then each of
  /// its subdirectories — the order the reference tools lay a catalog out in.
  /// </summary>
  private void ReadDirectory(long catalogOffset, int entryCount, int first, string prefix, int depth,
      HashSet<int> visited, ref int budget) {
    if (depth > MaxDepth || !visited.Add(first)) {
      this.IsPartial = true;
      return;
    }

    var subdirectories = new List<(int First, string Path)>();
    var record = new byte[VdfsLayout.EntrySize];
    for (var index = first; ; ++index) {
      if (index >= entryCount || budget-- <= 0) {
        // Ran off the catalog without meeting the directory's last entry.
        this.IsPartial = true;
        break;
      }

      this._data.Read(catalogOffset + (long)index * VdfsLayout.EntrySize, record);
      var name = DecodeName(record);
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(VdfsLayout.EntryOffsetField));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(VdfsLayout.EntrySizeField));
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(VdfsLayout.EntryTypeField));
      var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(VdfsLayout.EntryAttributesField));

      if (name.Length == 0) {
        this.IsPartial = true;
      } else if ((type & VdfsLayout.TypeDirectory) != 0) {
        var path = prefix + name;
        this._entries.Add(new VdfsEntry { Name = path, IsDirectory = true, Attributes = attributes });
        subdirectories.Add(((int)Math.Min(offset, int.MaxValue), path));
      } else {
        this.AddFile(prefix + name, offset, size, attributes);
      }

      if ((type & VdfsLayout.TypeLast) != 0) break;
    }

    foreach (var (childFirst, path) in subdirectories)
      this.ReadDirectory(catalogOffset, entryCount, childFirst, path + "/", depth + 1, visited, ref budget);
  }

  private void AddFile(string path, long offset, long size, uint attributes) {
    var zipped = this.IsZippedVolume ? VdfsZippedStream.TryParse(this._data, offset, size) : null;
    var stored = zipped?.StoredLength ?? size;
    // An empty file owns no bytes, so where it points does not matter.
    if (stored > 0 && (offset > this._data.Length || stored > this._data.Length - offset)) {
      // ZenKit drops an entry whose payload runs past the volume; so does this reader.
      this.IsPartial = true;
      return;
    }

    this._entries.Add(new VdfsEntry {
      Name = path,
      Size = size,
      IsZipped = zipped != null,
      StoredSize = stored,
      Attributes = attributes,
      DataOffset = offset,
      Zipped = zipped,
    });
  }

  /// <summary>
  /// The name field is space-padded; some tools end it with a NUL instead. Both are trimmed,
  /// along with whatever follows a NUL.
  /// </summary>
  internal static string DecodeName(ReadOnlySpan<byte> record) {
    var field = record[..VdfsLayout.NameSize];
    var nul = field.IndexOf((byte)0);
    if (nul >= 0) field = field[..nul];
    var end = field.Length;
    while (end > 0 && field[end - 1] is 0x20 or 0x09 or 0x0A or 0x0D) --end;
    return VdfsLayout.NameEncoding.GetString(field[..end]);
  }

  /// <summary>The decoded bytes of <paramref name="entry" />; empty for a directory.</summary>
  public byte[] Extract(VdfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.Size > Array.MaxLength)
      throw new InvalidOperationException($"VDFS: '{entry.Name}' is larger than a byte[] can hold; use ExtractTo.");
    using var output = new MemoryStream((int)entry.Size);
    this.ExtractTo(entry, output);
    return output.ToArray();
  }

  /// <summary>
  /// Writes the decoded bytes of <paramref name="entry" /> to <paramref name="destination" />,
  /// a block at a time, so an entry larger than a byte[] can hold is extracted like any other.
  /// </summary>
  /// <exception cref="InvalidDataException">A ZippedStream block does not inflate as its header says.</exception>
  public void ExtractTo(VdfsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return;
    if (entry.Zipped is { } layout)
      VdfsZippedStream.Decode(this._data, layout, destination);
    else
      this._data.CopyTo(entry.DataOffset, destination, entry.Size);
  }

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() => this._data.Dispose();
}
