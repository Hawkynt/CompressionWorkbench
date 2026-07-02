#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vhdx;

/// <summary>
/// Descriptor for Hyper-V VHDX virtual hard-disk images (MS-VHDX v1).
/// For fixed-payload VHDX images the descriptor delegates List, Extract, Add,
/// Remove, and Defragment operations to the detected inner filesystem via
/// <see cref="VhdxStream"/>. Falls back to structural metadata listing when
/// the inner FS is not detected or the image uses dynamic/differencing layout.
///
/// References:
/// <list type="bullet">
///   <item><description>[MS-VHDX]: Virtual Hard Disk v2 (VHDX) File Format (Microsoft Open Specifications, learn.microsoft.com)</description></item>
///   <item><description><c>https://github.com/libyal/libvhdi</c> — libvhdi — open VHD/VHDX implementation with format documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/VHD_(file_format)</c> — Wikipedia overview (covers VHDX)</description></item>
/// </list>
/// </summary>
public sealed class VhdxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IFilesystemExtentMap, IPartitionEditable {
  public string Id => "Vhdx";
  public string DisplayName => "VHDX";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vhdx";
  public IReadOnlyList<string> Extensions => [".vhdx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("vhdxfile"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft Hyper-V VHDX virtual hard disk (MS-VHDX v1)";

  // ── IArchiveFormatOperations ──────────────────────────────────────

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

    // Fallback: structural metadata listing
    return BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null
    )).ToList();
  }

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
  /// Wraps the supplied input files into a fixed-payload VHDX container.
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
    // Simple: emit the entire file as metadata + payload
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

  // ── IArchiveDefragmentable (inner-FS-aware) ────────────────────────

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
            // fall through to rebuild
          }
        }
      }
    }

    DefragRebuilder.Rebuild(archive, options, ReadDiskEntries, BuildImage);
  }

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

  // ── Rebuild-path delegates (fallback) ──────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadDiskEntries(Stream stream) {
    // Try inner FS first
    if (VhdxStream.TryOpen(stream) is { } vhdxStream) {
      using (vhdxStream) {
        var inner = InnerFsDetector.Detect(vhdxStream);
        if (inner is IArchiveFormatOperations ops) {
          vhdxStream.Position = 0;
          var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
          try {
            Directory.CreateDirectory(tmpDir);
            ops.Extract(vhdxStream, tmpDir, null, null);
            foreach (var f in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
              var rel = Path.GetRelativePath(tmpDir, f);
              yield return (rel, File.ReadAllBytes(f));
            }
            yield break;
          } finally {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
          }
        }
      }
    }

    // Raw fallback
    var entries = BuildEntries(stream);
    foreach (var e in entries)
      yield return (e.Name, e.Data);
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var fat = FileSystem.Fat.FatWriter.BuildFromFiles(files);
    var w = new VhdxWriter();
    w.SetDiskData(fat);
    return w.Build();
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    // VHDX header region is the first 1 MiB. Only fetch that — never load the
    // payload (which can be multi-TB).
    using var cache = new SectorCache(stream);
    var totalLen = stream.Length;
    var headerLen = (int)Math.Min(totalLen, 0x100000); // 1 MiB
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
  /// <remarks>
  /// Returns a writable <see cref="VhdxStream"/> over the guest payload.
  /// VHDX dynamic layouts are supported but block allocation happens on
  /// first write, so callers should ensure the host stream has enough room
  /// for any new partitions before adding them.
  /// </remarks>
  public Stream OpenGuestDiskStream(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite)
      throw new NotSupportedException("Partition editing requires a writable VHDX stream.");
    return VhdxStream.TryOpen(image)
      ?? throw new InvalidDataException("Stream is not a valid VHDX image.");
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
