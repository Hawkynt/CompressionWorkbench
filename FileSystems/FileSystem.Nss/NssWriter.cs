#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nss;

/// <summary>
/// Writes the NSS container described in <see cref="NssLayout" />.
/// </summary>
/// <remarks>
/// The anchors go where a real pool carries them, so an image this writes is
/// detected as NSS by the same scan that detects a real one. Everything behind
/// them is this project's own, because Novell never published what a real image
/// puts there.
/// </remarks>
public sealed class NssWriter {

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>The volume name written next to the volume anchor.</summary>
  public string VolumeName { get; init; } = "POOL1";

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var clean = Path.GetFileName(name);
    if (clean.Length is 0 or > 255)
      throw new ArgumentException($"NSS: '{name}' is not a name this can write.", nameof(name));
    this._files.Add((clean, data));
  }

  /// <summary>Lays the container out and returns its bytes.</summary>
  public byte[] Build() {
    const int bs = NssLayout.BlockSize;

    var directory = this.BuildDirectory(out var starts, out var counts);
    if (directory.Length > bs)
      throw new InvalidOperationException(
        "NSS: the directory does not fit its block; this writes only one.");

    var total = NssLayout.FirstDataBlock + counts.Sum();
    var image = new byte[total * bs];

    NssHeaders.NssPoolMagic.CopyTo(image, NssLayout.PoolAnchor);
    NssLayout.ContainerMagic.CopyTo(image, NssLayout.ContainerMagicOffset);
    BinaryPrimitives.WriteInt64LittleEndian(
      image.AsSpan((int)NssLayout.FileCountOffset), this._files.Count);

    NssHeaders.NssVolumeMagic.CopyTo(image, NssLayout.VolumeAnchor);
    var name = Encoding.ASCII.GetBytes(this.VolumeName);
    name.CopyTo(image, NssLayout.VolumeAnchor + NssHeaders.NssVolumeMagic.Length + 1);

    NssHeaders.NssSuperblockMagic.CopyTo(image, NssLayout.SuperblockAnchor);
    NssHeaders.NovellMagic.CopyTo(image, NssLayout.SuperblockAnchor + 16);
    NssHeaders.NetWareMagic.CopyTo(image, NssLayout.SuperblockAnchor + 32);

    directory.CopyTo(image, NssLayout.DirectoryOffset);

    for (var i = 0; i < this._files.Count; ++i)
      this._files[i].Data.CopyTo(image, starts[i] * bs);

    return image;
  }

  /// <summary>
  /// Lays out the directory and places each file, one contiguous run apiece.
  /// </summary>
  private byte[] BuildDirectory(out long[] starts, out long[] counts) {
    const int bs = NssLayout.BlockSize;

    starts = new long[this._files.Count];
    counts = new long[this._files.Count];

    var entries = new List<byte>();
    var cursor = NssLayout.FirstDataBlock;

    for (var i = 0; i < this._files.Count; ++i) {
      var data = this._files[i].Data;
      counts[i] = Math.Max(1, (data.LongLength + bs - 1) / bs);
      starts[i] = cursor;
      cursor += counts[i];

      var name = Encoding.UTF8.GetBytes(this._files[i].Name);
      var entry = new byte[NssLayout.EntryHeaderBytes + 2 + name.Length];
      BinaryPrimitives.WriteInt64LittleEndian(
        entry.AsSpan(NssLayout.EntryOffsetField), starts[i] * bs);
      BinaryPrimitives.WriteInt64LittleEndian(
        entry.AsSpan(NssLayout.EntrySizeField), data.LongLength);
      BinaryPrimitives.WriteUInt16LittleEndian(
        entry.AsSpan(NssLayout.EntryHeaderBytes), (ushort)name.Length);
      name.CopyTo(entry, NssLayout.EntryHeaderBytes + 2);
      entries.AddRange(entry);
    }

    return entries.ToArray();
  }
}
