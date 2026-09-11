#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Gpfs;

/// <summary>
/// Structural reader for IBM Storage Scale (formerly Spectrum Scale / GPFS)
/// Network Shared Disk images.
///
/// <para>
/// Public IBM documentation specifies that NSD v2 disks use GPT with a single
/// GPFS partition. The filesystem metadata inside that partition is proprietary,
/// so this reader deliberately stops at the GPT envelope instead of guessing at
/// inode, directory or allocation-map bytes.
/// </para>
///
/// <para>
/// The historical workbench fixture beginning with <c>43 47 46 5C</c> remains
/// accepted when GPFS is selected explicitly, but that byte sequence is not
/// advertised as a normative IBM on-disk signature: no authoritative source for
/// that claim could be established.
/// </para>
/// </summary>
public sealed class GpfsReader : IDisposable {

  /// <summary>IBM GPFS GPT partition type.</summary>
  public static readonly Guid GpfsPartitionTypeGuid = new("37AFFC90-EF7D-4E96-91C3-2D7AE055B174");

  /// <summary>IBM GPFS GPT partition type in the mixed-endian byte order stored by GPT.</summary>
  public static readonly byte[] GpfsPartitionTypeGuidBytes = [
    0x90, 0xFC, 0xAF, 0x37, 0x7D, 0xEF, 0x96, 0x4E,
    0x91, 0xC3, 0x2D, 0x7A, 0xE0, 0x55, 0xB1, 0x74,
  ];

  /// <summary>
  /// Historical workbench descriptor-fixture signature. Retained for compatibility only;
  /// this is not treated as an authoritative GPFS disk magic.
  /// </summary>
  public static readonly byte[] NsdMagic = [0x43, 0x47, 0x46, 0x5C];

  private const int LegacyHeaderSize = 8;
  private const int SectorSize = 512;
  private const int GptHeaderOffset = SectorSize;

  private readonly byte[] _data;
  private readonly List<GpfsEntry> _entries = [];

  /// <summary>Gets the synthetic entries exposed by this structural reader.</summary>
  public IReadOnlyList<GpfsEntry> Entries => _entries;

  /// <summary>Gets the compatibility-fixture word when the legacy surface was used.</summary>
  public uint MagicWord { get; private set; }

  /// <summary>Gets the trailing compatibility-fixture word when the legacy surface was used.</summary>
  public uint TrailingWord { get; private set; }

  /// <summary>Gets whether a supported GPFS envelope was recognized.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>Gets whether an IBM NSD v2 GPT envelope was recognized.</summary>
  public bool IsNsdV2Gpt { get; private set; }

  /// <summary>Gets whether the historical workbench descriptor fixture was recognized.</summary>
  public bool UsesLegacyDescriptorSignature { get; private set; }

  /// <summary>Gets the GPFS GPT partition byte offset for an NSD v2 image.</summary>
  public long? GpfsPartitionOffset { get; private set; }

  /// <summary>Gets the GPFS GPT partition byte length for an NSD v2 image.</summary>
  public long? GpfsPartitionSize { get; private set; }

  /// <summary>Gets the GPT partition name for an NSD v2 image, when present.</summary>
  public string? GpfsPartitionName { get; private set; }

  /// <summary>Initializes a new structural GPFS reader.</summary>
  public GpfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (_data.Length < LegacyHeaderSize)
      throw new InvalidDataException("GPFS: image is too small for a supported NSD envelope.");

    if (GpfsDetectionSource.TryFindGpfsPartitionType(_data))
      this.ParseNsdV2Gpt();
    else if (_data.AsSpan(0, 4).SequenceEqual(NsdMagic))
      this.ParseLegacyFixture();
    else if (GptParser.IsGpt(_data))
      throw new InvalidDataException(
        $"GPFS: GPT is present but contains no {GpfsPartitionTypeGuid:D} IBM GPFS partition.");
    else
      throw new InvalidDataException(
        "GPFS: no NSD v2 GPT/GPFS partition envelope was found. The legacy workbench descriptor fixture was not present either.");

    var meta = this.BuildMetadata();
    _entries.Add(new GpfsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    _entries.Add(new GpfsEntry { Name = "gpfs-nsd.bin", Size = _data.LongLength, IsDirectory = false, Offset = 0, Data = _data });
  }

  private void ParseNsdV2Gpt() {
    this.ValidateGptEntryTableBounds();

    try {
      using var stream = new MemoryStream(_data, writable: false);
      var partition = GptParser.Parse(stream)
        .FirstOrDefault(entry =>
          string.Equals(entry.TypeCode, GpfsPartitionTypeGuid.ToString("D"), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException(
          $"GPFS: GPT does not contain the {GpfsPartitionTypeGuid:D} IBM GPFS partition type.");

      if (partition.StartOffset < 0 || partition.Size <= 0 || partition.StartOffset > _data.LongLength - partition.Size)
        throw new InvalidDataException(
          $"GPFS: GPT partition range [{partition.StartOffset}, {partition.StartOffset + partition.Size}) exceeds the image bounds.");

      this.IsNsdV2Gpt = true;
      this.GpfsPartitionOffset = partition.StartOffset;
      this.GpfsPartitionSize = partition.Size;
      this.GpfsPartitionName = partition.Name;
      this.ValidHeader = true;
    } catch (InvalidDataException) {
      throw;
    } catch (Exception e) when (e is EndOfStreamException or IOException or OverflowException or ArgumentOutOfRangeException) {
      throw new InvalidDataException($"GPFS: malformed NSD v2 GPT envelope: {e.Message}", e);
    }
  }

  private void ParseLegacyFixture() {
    this.MagicWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(0, 4));
    this.TrailingWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(4, 4));
    this.UsesLegacyDescriptorSignature = true;
    this.ValidHeader = true;
  }

  private void ValidateGptEntryTableBounds() {
    if (_data.Length < GptHeaderOffset + 92)
      throw new InvalidDataException("GPFS: truncated GPT header.");

    var gpt = _data.AsSpan(GptHeaderOffset);
    var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(gpt[72..]);
    var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(gpt[80..]);
    var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(gpt[84..]);
    if (entryCount == 0 || entryCount > int.MaxValue || entrySize < 128 || entrySize > int.MaxValue)
      throw new InvalidDataException("GPFS: invalid GPT partition-entry geometry.");

    long tableOffset;
    long tableLength;
    try {
      tableOffset = checked((long)entriesLba * SectorSize);
      tableLength = checked((long)entryCount * entrySize);
    } catch (OverflowException e) {
      throw new InvalidDataException("GPFS: GPT partition-entry geometry overflows the supported image range.", e);
    }

    if (tableOffset < 0 || tableLength <= 0 || tableLength > int.MaxValue || tableOffset > _data.LongLength - tableLength)
      throw new InvalidDataException("GPFS: GPT partition-entry array lies outside the image.");
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=structural-inspection-only\n");
    bldr.Append("stage=0\n");
    bldr.Append("format=IBM Storage Scale / GPFS NSD\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.LongLength}\n");

    if (this.IsNsdV2Gpt) {
      bldr.Append("nsd_surface=v2-gpt\n");
      bldr.Append(CultureInfo.InvariantCulture, $"gpfs_partition_type={GpfsPartitionTypeGuid:D}\n");
      bldr.Append(CultureInfo.InvariantCulture, $"gpfs_partition_offset={this.GpfsPartitionOffset}\n");
      bldr.Append(CultureInfo.InvariantCulture, $"gpfs_partition_size={this.GpfsPartitionSize}\n");
      if (!string.IsNullOrEmpty(this.GpfsPartitionName))
        bldr.Append(CultureInfo.InvariantCulture, $"gpfs_partition_name={this.GpfsPartitionName}\n");
    } else {
      bldr.Append("nsd_surface=legacy-workbench-descriptor-fixture\n");
      bldr.Append(CultureInfo.InvariantCulture, $"legacy_signature_word=0x{this.MagicWord:X8}\n");
      bldr.Append(CultureInfo.InvariantCulture, $"legacy_trailing_word=0x{this.TrailingWord:X8}\n");
      bldr.Append("legacy_signature_authoritative=false\n");
    }

    bldr.Append("layout_analysis=available\n");
    bldr.Append("layout_rebuild=false\n");
    bldr.Append("maintenance_support=none\n");
    bldr.Append("promotion_blocked_reason=GPFS inode/directory/allocation metadata byte layout is not publicly specified sufficiently for a safe offline file walk or rewrite; complete filesystems may span multiple NSDs; no independent off-cluster read/write oracle is available.\n");
    bldr.Append("note=NSD envelope inspection only. No inode, directory, allocation-map, free-space or file-data mutation is attempted.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>Extracts one synthetic structural entry.</summary>
  public byte[] Extract(GpfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() { }
}
