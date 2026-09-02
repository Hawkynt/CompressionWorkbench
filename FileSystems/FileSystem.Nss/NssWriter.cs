#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nss;

/// <summary>
/// Writes the NSS container described in <see cref="NssLayout" />.
/// </summary>
/// <remarks>
/// <para>What this writes carries its own magic and no NSS anchor. It did carry
/// them once, on the reasoning that an image of ours should be detected by the
/// same scan that detects a real pool — which had it announce itself as an NSS
/// pool while being unable to act as one. Anything that knows NSS would have
/// identified it and then failed to read it, and a format that misleads a
/// reader is worse than one that says nothing.</para>
///
/// <para>So the anchors are gone from what is written here. Reading them is
/// untouched: a real pool is still found by them and still surfaced as one
/// whose object tree has no public spec.</para>
/// </remarks>
public sealed class NssWriter {

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>The volume name written next to the volume anchor.</summary>
  public string VolumeName { get; init; } = "POOL1";

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
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

    NssLayout.ContainerMagic.CopyTo(image, NssLayout.ContainerMagicOffset);
    BinaryPrimitives.WriteInt64LittleEndian(
      image.AsSpan((int)NssLayout.FileCountOffset), this._files.Count);

    var name = Encoding.ASCII.GetBytes(this.VolumeName);
    name.CopyTo(image, NssLayout.VolumeAnchor);

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
