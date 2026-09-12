#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vhdx;

/// <summary>
/// Descriptor for standalone Hyper-V VHDX virtual hard-disk images. Guest-file
/// operations delegate through <see cref="VhdxStream"/>; container maintenance
/// rebuilds the raw guest disk and verifies byte identity before committing.
///
/// References:
/// <list type="bullet">
///   <item><description>[MS-VHDX]: Virtual Hard Disk v2 (VHDX) File Format (Microsoft Open Specifications, learn.microsoft.com)</description></item>
///   <item><description><c>https://github.com/libyal/libvhdi</c> — libvhdi — open VHD/VHDX implementation with format documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/VHD_(file_format)</c> — Wikipedia overview (covers VHDX)</description></item>
/// </list>
/// </summary>
public sealed class VhdxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable, IArchiveLayoutMap, IFilesystemExtentMap, IPartitionEditable {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Vhdx";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "VHDX";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".vhdx";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".vhdx"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("vhdxfile"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Microsoft Hyper-V VHDX virtual hard disk (MS-VHDX)";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (VhdxStream.TryOpen(stream) is { } vhdxStream) {
      using (vhdxStream) {
        vhdxStream.Position = 0;
        if (Compression.Core.DiskImage.PartitionedDiskLister.List(vhdxStream, password) is { } partitioned)
          return partitioned;

        var inner = InnerFsDetector.Detect(vhdxStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vhdxStream.Position = 0;
            return ops.List(vhdxStream, password);
          } catch {
            // fall through to structural listing
          }
        }
      }
    }

    return BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (VhdxStream.TryOpen(stream) is { } vhdxStream) {
      using (vhdxStream) {
        vhdxStream.Position = 0;
        if (Compression.Core.DiskImage.PartitionedDiskLister.Extract(vhdxStream, outputDir, password, files))
          return;

        var inner = InnerFsDetector.Detect(vhdxStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vhdxStream.Position = 0;
            ops.Extract(vhdxStream, outputDir, password, files);
            return;
          } catch {
            // fall through to structural extraction
          }
        }
      }
    }

    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Wraps the supplied input files into a sparse standalone VHDX container.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fat = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    var w = new VhdxWriter();
    w.SetDiskData(fat);
    output.Write(w.Build());
  }

  // ── IArchiveLayoutMap ───────────────────────────────────────────────

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    yield return new DefragBlockInfo(0, Math.Min(0x100000, archive.Length),
      DefragBlockKind.MetadataReserved, FileName: "VHDX Headers + Region Tables");
    if (archive.Length > 0x100000)
      yield return new DefragBlockInfo(0x100000, archive.Length - 0x100000,
        DefragBlockKind.Used, FileName: "Metadata + BAT + Payload");
  }

  // ── IFilesystemExtentMap ────────────────────────────────────────────

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    if (VhdxStream.TryOpen(image) is { } vhdxStream) {
      using (vhdxStream) {
        var inner = InnerFsDetector.Detect(vhdxStream);
        if (inner is IFilesystemExtentMap extentMap) {
          vhdxStream.Position = 0;
          return extentMap.EnumerateExtents(vhdxStream).ToList();
        }
      }
    }

    return EnumerateLayout(image);
  }

  // ── IArchiveModifiable (inner-FS-aware) ────────────────────────────

  /// <inheritdoc />
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (VhdxStream.TryOpen(archive) is { } guestForPart) {
      using (guestForPart) {
        try {
          guestForPart.Position = 0;
          if (Compression.Core.DiskImage.PartitionedDiskLister.TryAdd(guestForPart, inputs)) {
            guestForPart.Flush();
            return;
          }
        } catch (InvalidOperationException) { throw; }
        catch { /* fall through */ }
      }
    }

    if (TryDelegateModifiable(archive, out var vhdxStream, out var modifiable) && vhdxStream is not null && modifiable is not null) {
      using (vhdxStream) {
        try {
          vhdxStream.Position = 0;
          modifiable.Add(vhdxStream, inputs);
          vhdxStream.Flush();
          return;
        } catch {
          // fall through to rebuild
        }
      }
    }

    ModifyRebuilder.Add(archive, inputs, ReadDiskEntries, BuildImage);
  }

  /// <inheritdoc />
  public void Remove(Stream archive, string[] entryNames) {
    if (VhdxStream.TryOpen(archive) is { } guestForPart) {
      using (guestForPart) {
        try {
          guestForPart.Position = 0;
          if (Compression.Core.DiskImage.PartitionedDiskLister.TryRemove(guestForPart, entryNames)) {
            guestForPart.Flush();
            return;
          }
        } catch (InvalidOperationException) { throw; }
        catch { /* fall through */ }
      }
    }

    if (TryDelegateModifiable(archive, out var vhdxStream, out var modifiable) && vhdxStream is not null && modifiable is not null) {
      using (vhdxStream) {
        try {
          vhdxStream.Position = 0;
          modifiable.Remove(vhdxStream, entryNames);
          vhdxStream.Flush();
          return;
        } catch {
          // fall through to rebuild
        }
      }
    }

    ModifyRebuilder.Remove(archive, entryNames, ReadDiskEntries, BuildImage);
  }

  // ── Maintenance ────────────────────────────────────────────────────

  /// <inheritdoc />
  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <inheritdoc />
  public void Defragment(Stream archive, DefragOptions options) {
    if (VhdxStream.TryOpen(archive) is { } vhdxStream) {
      using (vhdxStream) {
        var inner = InnerFsDetector.Detect(vhdxStream);
        if (inner is IArchiveDefragmentable defrag) {
          try {
            vhdxStream.Position = 0;
            defrag.Defragment(vhdxStream, options);
            vhdxStream.Flush();
            return;
          } catch {
            // fall through to raw-disk rebuild
          }
        }
      }
    }

    DefragRebuilder.Rebuild(archive, options, ReadDiskEntries, BuildImage);
  }

  /// <inheritdoc />
  public void Shrink(Stream input, Stream output)
    => RawDiskShrinkRebuilder.Shrink(
      input,
      output,
      ReadGuestDisk,
      static disk => {
        var writer = new VhdxWriter();
        writer.SetDiskData(disk);
        return writer.Build();
      },
      CanRebuildStandaloneVhdx);

  // ── Private helpers ────────────────────────────────────────────────

  private static bool TryDelegateModifiable(Stream archive, out VhdxStream? vhdxStream, out IArchiveModifiable? modifiable) {
    vhdxStream = null;
    modifiable = null;
    var vs = VhdxStream.TryOpen(archive);
    if (vs == null) return false;

    var inner = InnerFsDetector.Detect(vs);
    if (inner is IArchiveModifiable mod) {
      vhdxStream = vs;
      modifiable = mod;
      return true;
    }

    vs.Dispose();
    return false;
  }

  private static bool CanRebuildStandaloneVhdx(Stream stream) {
    using var guest = VhdxStream.TryOpen(stream);
    if (guest is null || guest.HasAmbiguousPayloadBlocks) return false;

    stream.Position = 0;
    var headerLength = checked((int)Math.Min(stream.Length, 0x50000));
    var headerBytes = new byte[headerLength];
    stream.ReadExactly(headerBytes);
    var image = VhdxReader.Read(headerBytes, stream.Length);
    if (image.PrimaryHeaderInfo?.LogGuid != Guid.Empty || image.BackupHeaderInfo?.LogGuid != Guid.Empty)
      return false;

    var region = image.RegionTablePrimary;
    if (region.Length < 80 || !region.AsSpan(0, 4).SequenceEqual("regi"u8)) return false;
    var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(8, 4));
    if (count != 2) return false; // do not discard optional/unknown regions during canonical rebuild

    var batGuid = new Guid("2DC27766-F623-4200-9D64-115E9BFD4A08");
    var metadataGuid = new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    var first = new Guid(region.AsSpan(16, 16));
    var second = new Guid(region.AsSpan(48, 16));
    return (first == batGuid && second == metadataGuid) || (first == metadataGuid && second == batGuid);
  }

  // ── Rebuild-path delegates ─────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadDiskEntries(Stream stream) {
    yield return ("disk.img", ReadGuestDisk(stream));
  }

  private static byte[] ReadGuestDisk(Stream stream) {
    using var guest = VhdxStream.TryOpen(stream)
      ?? throw new InvalidDataException("Stream is not a supported standalone VHDX image.");
    if (guest.HasAmbiguousPayloadBlocks)
      throw new InvalidDataException("VHDX contains payload BAT states without canonical standalone bytes; refusing raw-disk rebuild.");
    if (guest.Length > int.MaxValue)
      throw new NotSupportedException("Buffered VHDX maintenance currently supports guest disks up to 2 GiB.");
    var disk = new byte[checked((int)guest.Length)];
    guest.Position = 0;
    guest.ReadExactly(disk);
    return disk;
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var disk = files.Count > 0 ? files[0].Data : [];
    var writer = new VhdxWriter();
    writer.SetDiskData(disk);
    return writer.Build();
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var cache = new SectorCache(stream);
    var totalLen = stream.Length;
    var headerLen = (int)Math.Min(totalLen, 0x100000);
    var headerBuf = cache.Read(0, headerLen);
    var img = VhdxReader.Read(headerBuf, totalLen);

    var entries = new List<(string, byte[])> {
      ("metadata.ini", BuildMetadata(img)),
      ("file_type_identifier.bin", img.FileTypeIdentifier),
    };
    if (img.HeaderPrimary.Length > 0) entries.Add(("header_primary.bin", img.HeaderPrimary));
    if (img.HeaderBackup.Length > 0) entries.Add(("header_backup.bin", img.HeaderBackup));
    if (img.RegionTablePrimary.Length > 0) entries.Add(("region_table_primary.bin", img.RegionTablePrimary));
    if (img.RegionTableBackup.Length > 0) entries.Add(("region_table_backup.bin", img.RegionTableBackup));
    return entries;
  }

  private static byte[] BuildMetadata(VhdxReader.VhdxImage img) {
    var sb = new StringBuilder();
    sb.AppendLine("[vhdx]");
    sb.Append(CultureInfo.InvariantCulture, $"file_size = {img.TotalFileSize}\n");
    sb.Append("signature = vhdxfile\n");
    sb.Append(CultureInfo.InvariantCulture, $"creator = {img.Creator}\n");
    AppendHeader(sb, "header_primary", img.PrimaryHeaderInfo);
    AppendHeader(sb, "header_backup", img.BackupHeaderInfo);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  // ── IPartitionEditable ─────────────────────────────────────────────

  /// <inheritdoc />
  public Stream OpenGuestDiskStream(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite)
      throw new NotSupportedException("Partition editing requires a writable VHDX stream.");
    return VhdxStream.TryOpen(image)
      ?? throw new InvalidDataException("Stream is not a valid standalone VHDX image.");
  }

  private static void AppendHeader(StringBuilder sb, string prefix, VhdxReader.HeaderInfo? info) {
    if (info is null) {
      sb.Append(CultureInfo.InvariantCulture, $"{prefix}_valid = false\n");
      return;
    }
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_valid = true\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_checksum = 0x{info.Checksum:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_sequence_number = {info.SequenceNumber}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_file_write_guid = {info.FileWriteGuid:D}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_data_write_guid = {info.DataWriteGuid:D}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_log_guid = {info.LogGuid:D}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_log_version = {info.LogVersion}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_version = {info.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_log_length = {info.LogLength}\n");
    sb.Append(CultureInfo.InvariantCulture, $"{prefix}_log_offset = 0x{info.LogOffset:X16}\n");
  }
}