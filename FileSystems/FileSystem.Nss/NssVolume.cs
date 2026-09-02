#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Nss;

/// <summary>
/// Walks a container written by <see cref="NssWriter" /> and says where each
/// file's blocks are.
/// </summary>
/// <remarks>
/// A real NSS pool is not walked here and never was: its object tree has no
/// public spec, so what this recognises is the container magic behind the pool
/// anchor. An image without that magic is still an NSS image as far as
/// <see cref="NssHeaders" /> is concerned — it simply has no files this can
/// name, which is the state every NSS image was in before.
/// </remarks>
public sealed class NssVolume {

  /// <summary>A file, and the directory field that says where it is.</summary>
  /// <param name="Name">Its name.</param>
  /// <param name="Offset">Where its bytes are.</param>
  /// <param name="Size">How many of them there are.</param>
  /// <param name="OffsetField">Where the directory records its position.</param>
  public readonly record struct VolumeFile(string Name, long Offset, long Size, long OffsetField);

  private readonly byte[] _image;

  /// <summary>
  /// Gets a value indicating whether valid.
  /// </summary>
public bool Valid { get; }
  /// <summary>
  /// Gets the status.
  /// </summary>
public string Status { get; } = "unparsed";
  /// <summary>
  /// Gets the block size.
  /// </summary>
public int BlockSize => NssLayout.BlockSize;
  /// <summary>
  /// Gets the image length.
  /// </summary>
public long ImageLength => this._image.LongLength;

  /// <summary>
  /// Gets the files.
  /// </summary>
public IReadOnlyList<VolumeFile> Files => this._files;
  private readonly List<VolumeFile> _files = [];

  /// <summary>
  /// Initializes a new instance of <see cref="NssVolume"/>.
  /// </summary>
public NssVolume(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    using var ms = new MemoryStream();
    image.Position = 0;
    image.CopyTo(ms);
    this._image = ms.ToArray();

    if (this._image.Length < NssLayout.FirstDataBlock * NssLayout.BlockSize) {
      this.Status = "too small to be one of ours";
      return;
    }

    if (!this._image.AsSpan((int)NssLayout.ContainerMagicOffset, NssLayout.ContainerMagic.Length)
             .SequenceEqual(NssLayout.ContainerMagic)) {
      this.Status = "an NSS pool this did not write, whose object tree has no public spec";
      return;
    }

    var count = BinaryPrimitives.ReadInt64LittleEndian(
      this._image.AsSpan((int)NssLayout.FileCountOffset));
    if (count is < 0 or > 65536) { this.Status = $"implausible file count {count}"; return; }

    var cursor = NssLayout.DirectoryOffset;
    var end = NssLayout.DirectoryOffset + NssLayout.BlockSize;
    for (var i = 0L; i < count; ++i) {
      if (cursor + NssLayout.EntryHeaderBytes + 2 > end) { this.Status = "the directory is short"; return; }

      var offsetField = cursor + NssLayout.EntryOffsetField;
      var offset = BinaryPrimitives.ReadInt64LittleEndian(this._image.AsSpan((int)offsetField));
      var size = BinaryPrimitives.ReadInt64LittleEndian(
        this._image.AsSpan((int)(cursor + NssLayout.EntrySizeField)));
      var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
        this._image.AsSpan((int)(cursor + NssLayout.EntryHeaderBytes)));

      var nameAt = cursor + NssLayout.EntryHeaderBytes + 2;
      if (nameLength == 0 || nameAt + nameLength > end) { this.Status = "a name runs past the directory"; return; }
      if (offset < 0 || size < 0 || offset + size > this._image.LongLength) {
        this.Status = "a file's bytes are not inside the container";
        return;
      }

      this._files.Add(new VolumeFile(
        Encoding.UTF8.GetString(this._image, (int)nameAt, nameLength), offset, size, offsetField));
      cursor = nameAt + nameLength;
    }

    this.Valid = true;
    this.Status = "ok";
  }

  /// <summary>Returns a file's bytes.</summary>
  public byte[] Read(VolumeFile file) {
    var data = new byte[file.Size];
    if (file.Offset < 0 || file.Offset + file.Size > this._image.LongLength) return data;
    Array.Copy(this._image, file.Offset, data, 0, file.Size);
    return data;
  }

  /// <summary>How many whole blocks a file occupies.</summary>
  public long BlocksOf(VolumeFile file)
    => Math.Max(1, (file.Size + NssLayout.BlockSize - 1) / NssLayout.BlockSize);

  /// <summary>The layout a pass plans against.</summary>
  public IEnumerable<DefragBlockInfo> Enumerate() {
    if (!this.Valid) yield break;

    yield return new DefragBlockInfo(
      0, NssLayout.FirstDataBlock * NssLayout.BlockSize, DefragBlockKind.MetadataReserved,
      "NSS anchors and directory");

    var claimed = this._files
      .Select(f => (f.Offset, Length: this.BlocksOf(f) * NssLayout.BlockSize, f.Name))
      .OrderBy(x => x.Offset)
      .ToList();

    var cursor = NssLayout.FirstDataBlock * NssLayout.BlockSize;
    foreach (var (offset, length, name) in claimed) {
      if (offset > cursor)
        yield return new DefragBlockInfo(cursor, offset - cursor, DefragBlockKind.Free, null);
      if (offset < cursor) continue;

      yield return new DefragBlockInfo(offset, length, DefragBlockKind.Used, name);
      cursor = offset + length;
    }

    if (cursor < this.ImageLength)
      yield return new DefragBlockInfo(cursor, this.ImageLength - cursor, DefragBlockKind.Free, null);
  }
}
