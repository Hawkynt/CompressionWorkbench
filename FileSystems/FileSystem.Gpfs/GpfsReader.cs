#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Gpfs;

/// <summary>
/// Stage 0 detection-only reader for IBM Spectrum Scale (formerly GPFS —
/// General Parallel File System) NSD (Network Shared Disk) descriptor
/// images. GPFS is a parallel clustered FS — its single-disk surface is
/// the NSD descriptor block whose first four bytes are the GPFS magic
/// integer <c>0x4347465C</c> (the bytes 0x43 0x47 0x46 0x5C — derived
/// from the cluster signature "GCFS\" used in GPFS internal headers).
///
/// Only the magic word is verified. The real NSD descriptor maps onto
/// a GPFS cluster's failure-group topology and storage pool membership;
/// the file table itself lives in the cluster manager and cannot be
/// walked from a single disk image.
/// </summary>
public sealed class GpfsReader : IDisposable {

  /// <summary>GPFS NSD descriptor magic: bytes 0x43 0x47 0x46 0x5C.</summary>
  public static readonly byte[] NsdMagic = [0x43, 0x47, 0x46, 0x5C];

  private const int HeaderSize = 8;

  private readonly byte[] _data;
  private readonly List<GpfsEntry> _entries = [];

  public IReadOnlyList<GpfsEntry> Entries => _entries;
  public uint MagicWord { get; private set; }
  public uint TrailingWord { get; private set; }
  public bool ValidHeader { get; private set; }

  public GpfsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize)
      throw new InvalidDataException("GPFS: file too small for NSD descriptor header.");

    if (!_data.AsSpan(0, 4).SequenceEqual(NsdMagic))
      throw new InvalidDataException("GPFS: missing 0x4347465C NSD magic at offset 0.");

    this.MagicWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(0, 4));
    this.TrailingWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(4, 4));
    this.ValidHeader = true;

    var meta = BuildMetadata();
    _entries.Add(new GpfsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    _entries.Add(new GpfsEntry { Name = "gpfs-nsd.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("stage=0\n");
    bldr.Append("format=IBM Spectrum Scale / GPFS NSD descriptor\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_word=0x{this.MagicWord:X8}\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("promotion_blocked_reason=proprietary on-disk format (no public spec) + ");
    bldr.Append("cluster-distributed metadata (no single-disk content surface) + ");
    bldr.Append("no off-cluster fsck-equivalent oracle\n");
    bldr.Append("note=Stage 0 — detection only. GPFS / Spectrum Scale is a parallel cluster FS; ");
    bldr.Append("file table lives in cluster manager, no single-disk content surface.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(GpfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
