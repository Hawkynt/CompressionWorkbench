using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Iso;

namespace FileFormat.Cdi;

/// <summary>
/// Transactionally rebuilds the ISO filesystem inside a supported CDI data
/// track without changing the surrounding optical layout. Every byte outside
/// index 1 of the selected track — audio, pregaps, other sessions, subchannels,
/// descriptor dialect and undeciphered descriptor fields — is copied verbatim.
/// Raw Mode-1 and Mode-2 Form-1 sectors have their EDC/ECC regenerated after
/// the 2,048-byte filesystem payload is replaced.
/// </summary>
internal static class CdiEmbeddedIsoRebuilder {
  private const int SectorSize = CdiCdSectorIntegrity.UserDataSize;
  private const int IsoWriterTrailingPadSectors = 150;

  internal static bool CanRewrite(CdiReader reader)
    => reader.ActiveDataTrack is { } active && CdiCdSectorIntegrity.Supports(active);

  internal static void Rewrite(
    Stream archive,
    CdiFormatDescriptor format,
    Action<string>? mutate = null,
    DefragOptions? progress = null
  ) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(format);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("CDI mutation requires a readable, writable, seekable stream.", nameof(archive));

    var originalPosition = archive.Position;
    var tempDirectory = Path.Combine(Path.GetTempPath(), "cwb_cdi_edit_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDirectory);
    try {
      archive.Position = 0;
      using var sourceReader = new CdiReader(archive, leaveOpen: true);
      var active = sourceReader.ActiveDataTrack;
      if (active == null || !CdiCdSectorIntegrity.Supports(active))
        throw UnsupportedLayout();

      EnsureTrackSectorsRewritable(archive, active);

      var sourceVersion = sourceReader.CdiVersion;
      var sourceTracks = sourceReader.Tracks.ToArray();
      var sourcePvd = CdiCdSectorIntegrity.GetUserData(active, sourceReader.ReadTrackSector(active, 16));
      var absoluteExtents = UsesAbsoluteIsoExtents(sourcePvd, active);

      progress?.CancellationToken.ThrowIfCancellationRequested();
      progress?.OnProgress?.Invoke(new DefragProgressEvent(
        "scanning", 0, active.DataOffset, -1, archive.Length, null,
        $"Extracting CDI track {active.TrackNumber} filesystem while preserving {sourceTracks.Length} optical track(s)"));

      archive.Position = 0;
      format.Extract(archive, tempDirectory, null, null);
      mutate?.Invoke(tempDirectory);
      progress?.CancellationToken.ThrowIfCancellationRequested();

      var embeddedIso = BuildIso(tempDirectory, sourceReader, active, sourcePvd, absoluteExtents);
      var trackCapacity = checked((long)active.DataSectorCount * SectorSize);
      if (embeddedIso.LongLength > trackCapacity)
        throw new IOException(
          $"CDI: rebuilt ISO needs {(embeddedIso.LongLength + SectorSize - 1) / SectorSize:N0} sectors, " +
          $"but the existing data track has only {active.DataSectorCount:N0}; refusing to alter the optical layout.");

      using var staged = RebuildVerb.CreateScratchStream();
      archive.Position = 0;
      archive.CopyTo(staged);
      staged.Flush();

      progress?.CancellationToken.ThrowIfCancellationRequested();
      progress?.OnProgress?.Invoke(new DefragProgressEvent(
        "writing", 0.55, active.DataOffset, active.DataOffset, archive.Length, null,
        $"Rebuilding ISO inside CDI track {active.TrackNumber}; raw-sector integrity is regenerated where required"));

      RewriteTrackPayload(staged, active, embeddedIso);
      staged.Flush();

      progress?.CancellationToken.ThrowIfCancellationRequested();
      progress?.OnProgress?.Invoke(new DefragProgressEvent(
        "verifying", 0.9, -1, active.DataOffset + (long)active.DataSectorCount * active.StoredSectorSize,
        archive.Length, null,
        "Verifying CDI track map and rebuilt ISO before commit"));

      Verify(staged, tempDirectory, sourceVersion, sourceTracks);

      progress?.CancellationToken.ThrowIfCancellationRequested();
      progress?.OnProgress?.Invoke(new DefragProgressEvent(
        "committing", 0.98, -1, 0, archive.Length, null,
        "Committing verified CDI filesystem edit"));

      archive.Position = 0;
      archive.SetLength(0);
      staged.Position = 0;
      staged.CopyTo(archive);
      archive.Flush();

      progress?.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, null,
        "CDI filesystem rebuilt without changing the optical track layout"));
    } finally {
      try { Directory.Delete(tempDirectory, recursive: true); } catch { /* best effort */ }
      if (archive.CanSeek)
        archive.Position = originalPosition;
    }
  }

  internal static NotSupportedException UnsupportedLayout() => new(
    "CDI mutation can preserve Mode-1 and Mode-2 Form-1 data tracks in cooked or raw storage. " +
    "Audio, Mode-2 Form-2/formless sectors, unsupported sector geometries, and mixed-form data tracks remain read-only.");

  private static void EnsureTrackSectorsRewritable(Stream archive, CdiTrackInfo active) {
    if (active.ReadMode == CdiReadMode.Mode1_2048)
      return;

    var originalPosition = archive.Position;
    var stored = new byte[active.StoredSectorSize];
    try {
      for (var sector = 0; sector < active.DataSectorCount; ++sector) {
        archive.Position = checked(active.DataOffset + (long)sector * active.StoredSectorSize);
        archive.ReadExactly(stored);
        if (!CdiCdSectorIntegrity.IsRewritableSector(active, stored))
          throw UnsupportedLayout();
      }
    } finally {
      archive.Position = originalPosition;
    }
  }

  private static void RewriteTrackPayload(Stream staged, CdiTrackInfo active, ReadOnlySpan<byte> embeddedIso) {
    if (embeddedIso.Length % SectorSize != 0)
      throw new InvalidDataException("CDI: rebuilt ISO payload is not sector aligned.");

    var logicalSectorCount = embeddedIso.Length / SectorSize;
    if (logicalSectorCount > active.DataSectorCount)
      throw new IOException("CDI: rebuilt ISO exceeds the fixed data-track capacity.");

    if (active.ReadMode == CdiReadMode.Mode1_2048) {
      staged.Position = active.DataOffset;
      staged.Write(embeddedIso);
      ZeroRange(staged, ((long)active.DataSectorCount - logicalSectorCount) * SectorSize);
      return;
    }

    var stored = new byte[active.StoredSectorSize];
    var emptyUserData = new byte[SectorSize];
    for (var sector = 0; sector < active.DataSectorCount; ++sector) {
      var physicalOffset = checked(active.DataOffset + (long)sector * active.StoredSectorSize);
      staged.Position = physicalOffset;
      staged.ReadExactly(stored);

      var userData = sector < logicalSectorCount
        ? embeddedIso.Slice(sector * SectorSize, SectorSize)
        : emptyUserData;
      CdiCdSectorIntegrity.RewriteUserData(active, stored, userData);

      staged.Position = physicalOffset;
      staged.Write(stored);
    }
  }

  private static byte[] BuildIso(
    string root,
    CdiReader sourceReader,
    CdiTrackInfo active,
    byte[] sourcePvd,
    bool absoluteExtents
  ) {
    var writer = new IsoWriter {
      VolumeIdentifier = ReadPaddedAscii(sourcePvd, 40, 32, "CDROM"),
      SystemIdentifier = ReadPaddedAscii(sourcePvd, 8, 32, ""),
      PublisherIdentifier = ReadPaddedAscii(sourcePvd, 318, 128, ""),
      ApplicationIdentifier = ReadPaddedAscii(sourcePvd, 574, 128, ""),
      EnableJoliet = HasJoliet(sourceReader, active),
    };

    foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
      writer.AddFile(relative, File.ReadAllBytes(file));
    }

    var padded = writer.Build();
    var padBytes = checked(IsoWriterTrailingPadSectors * SectorSize);
    if (padded.Length < padBytes || !padded.AsSpan(padded.Length - padBytes).IsEmptyOrAllZero())
      throw new InvalidOperationException(
        "CDI: ISO writer no longer has the expected zero post-gap; refusing to guess the embedded-volume boundary.");

    var usedLength = padded.Length - padBytes;
    var iso = padded.AsSpan(0, usedLength).ToArray();
    var capacity = active.DataSectorCount;
    if ((long)iso.Length > (long)capacity * SectorSize)
      return iso;

    RewriteIsoAddressing(iso, capacity, absoluteExtents ? active.StartLba : 0);
    return iso;
  }

  private static bool UsesAbsoluteIsoExtents(ReadOnlySpan<byte> pvd, CdiTrackInfo active) {
    if (active.StartLba <= 0 || pvd.Length < 190)
      return false;
    var rootLba = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pvd[158..162]));
    return rootLba >= active.StartLba && (long)rootLba < active.EndLbaExclusive;
  }

  private static bool HasJoliet(CdiReader reader, CdiTrackInfo active) {
    var max = Math.Min(active.DataSectorCount, 64);
    for (var lba = 17; lba < max; ++lba) {
      var stored = reader.ReadTrackSector(active, lba);
      var sector = CdiCdSectorIntegrity.GetUserData(active, stored);
      if (!HasCd001(sector))
        continue;
      if (sector[0] == 0xFF)
        return false;
      if (sector[0] == 2 && sector.Length > 90 &&
          sector[88] == 0x25 && sector[89] == 0x2F && sector[90] is 0x40 or 0x43 or 0x45)
        return true;
    }
    return false;
  }

  private static bool HasCd001(ReadOnlySpan<byte> sector)
    => sector.Length >= 7 && sector[1] == (byte)'C' && sector[2] == (byte)'D' &&
       sector[3] == (byte)'0' && sector[4] == (byte)'0' && sector[5] == (byte)'1' && sector[6] == 1;

  private static string ReadPaddedAscii(ReadOnlySpan<byte> sector, int offset, int length, string fallback) {
    if (offset < 0 || length < 0 || offset > sector.Length - length)
      return fallback;
    var text = System.Text.Encoding.ASCII.GetString(sector.Slice(offset, length)).TrimEnd(' ', '\0');
    return string.IsNullOrEmpty(text) ? fallback : text;
  }

  /// <summary>
  /// Makes a freshly-built relative ISO suitable for a multisession track whose
  /// ECMA-119 extents use disc-absolute LBAs. Physical bytes stay relative to
  /// the track; all recorded path-table and directory extents receive the bias.
  /// </summary>
  private static void RewriteIsoAddressing(byte[] iso, int capacitySectors, int lbaBias) {
    if (iso.Length < 17 * SectorSize)
      throw new InvalidDataException("CDI: rebuilt ISO is too short to contain a primary volume descriptor.");

    var volumeSpace = checked((uint)((long)capacitySectors + lbaBias));
    for (var descriptorLba = 16; descriptorLba * SectorSize < iso.Length && descriptorLba < 64; ++descriptorLba) {
      var descriptorOffset = descriptorLba * SectorSize;
      var descriptor = iso.AsSpan(descriptorOffset, Math.Min(SectorSize, iso.Length - descriptorOffset));
      if (!HasCd001(descriptor))
        continue;
      if (descriptor[0] == 0xFF)
        break;
      if (descriptor[0] is not (1 or 2))
        continue;

      if (lbaBias != 0)
        RebaseDescriptorTree(iso, descriptorOffset, lbaBias);

      WriteBothEndianUInt32(iso.AsSpan(descriptorOffset + 80, 8), volumeSpace);
    }
  }

  private static void RebaseDescriptorTree(byte[] iso, int descriptorOffset, int bias) {
    var descriptor = iso.AsSpan(descriptorOffset, SectorSize);
    var rootRelativeLba = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[158..162]));
    var rootLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[166..170]));
    var pathTableSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[132..136]));

    var lPath = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[140..144]));
    var optionalLPath = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[144..148]));
    var mPath = checked((int)BinaryPrimitives.ReadUInt32BigEndian(descriptor[148..152]));
    var optionalMPath = checked((int)BinaryPrimitives.ReadUInt32BigEndian(descriptor[152..156]));

    PatchDirectoryTree(iso, rootRelativeLba, rootLength, bias);
    PatchPathTable(iso, lPath, pathTableSize, littleEndian: true, bias);
    PatchPathTable(iso, optionalLPath, pathTableSize, littleEndian: true, bias);
    PatchPathTable(iso, mPath, pathTableSize, littleEndian: false, bias);
    PatchPathTable(iso, optionalMPath, pathTableSize, littleEndian: false, bias);

    AddBiasToDirectoryRecord(descriptor[156..], bias);
    AddBiasToLbaField(descriptor[140..144], littleEndian: true, bias);
    AddBiasToLbaField(descriptor[144..148], littleEndian: true, bias);
    AddBiasToLbaField(descriptor[148..152], littleEndian: false, bias);
    AddBiasToLbaField(descriptor[152..156], littleEndian: false, bias);
  }

  private static void PatchDirectoryTree(byte[] iso, int rootLba, int rootLength, int bias) {
    var pending = new Queue<(int Lba, int Length)>();
    var visited = new HashSet<int>();
    pending.Enqueue((rootLba, rootLength));

    while (pending.Count > 0) {
      var (directoryLba, directoryLength) = pending.Dequeue();
      if (directoryLba < 0 || directoryLength <= 0 || !visited.Add(directoryLba))
        continue;

      var directoryOffset = checked((long)directoryLba * SectorSize);
      if (directoryOffset < 0 || directoryOffset > iso.LongLength - directoryLength)
        throw new InvalidDataException("CDI: rebuilt ISO directory points outside the generated image.");

      var consumed = 0;
      while (consumed < directoryLength) {
        var sectorOffset = checked((int)(directoryOffset + (consumed / SectorSize) * SectorSize));
        var withinSector = 0;
        while (withinSector < SectorSize && consumed < directoryLength) {
          var recordOffset = sectorOffset + withinSector;
          if ((uint)recordOffset >= (uint)iso.Length)
            throw new InvalidDataException("CDI: truncated ISO directory while rebasing multisession LBAs.");
          var recordLength = iso[recordOffset];
          if (recordLength == 0) {
            consumed += SectorSize - withinSector;
            break;
          }
          if (recordLength < 34 || withinSector + recordLength > SectorSize || recordOffset > iso.Length - recordLength)
            throw new InvalidDataException("CDI: malformed ISO directory record while rebasing multisession LBAs.");

          var record = iso.AsSpan(recordOffset, recordLength);
          var childRelativeLba = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record[2..6]));
          var childLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record[10..14]));
          var idLength = record[32];
          var isDot = idLength == 1 && record.Length > 33 && record[33] is 0 or 1;
          var isDirectory = (record[25] & 0x02) != 0;
          if (isDirectory && !isDot)
            pending.Enqueue((childRelativeLba, childLength));

          AddBiasToDirectoryRecord(record, bias);
          withinSector += recordLength;
          consumed += recordLength;
        }
      }
    }
  }

  private static void PatchPathTable(byte[] iso, int relativeLba, int byteLength, bool littleEndian, int bias) {
    if (relativeLba <= 0 || byteLength <= 0)
      return;
    var offset = checked(relativeLba * SectorSize);
    if (offset < 0 || offset > iso.Length - byteLength)
      throw new InvalidDataException("CDI: ISO path table points outside the generated image.");

    var position = offset;
    var end = offset + byteLength;
    while (position < end) {
      if (position > iso.Length - 8)
        throw new InvalidDataException("CDI: truncated ISO path table while rebasing multisession LBAs.");
      var nameLength = iso[position];
      var recordLength = 8 + nameLength + (nameLength & 1);
      if (recordLength < 8 || position > end - recordLength)
        throw new InvalidDataException("CDI: malformed ISO path table while rebasing multisession LBAs.");
      AddBiasToLbaField(iso.AsSpan(position + 2, 4), littleEndian, bias);
      position += recordLength;
    }
  }

  private static void AddBiasToDirectoryRecord(Span<byte> record, int bias) {
    if (record.Length < 10)
      throw new InvalidDataException("CDI: truncated ISO directory record.");
    var relative = BinaryPrimitives.ReadUInt32LittleEndian(record[2..6]);
    var absolute = checked(relative + (uint)bias);
    BinaryPrimitives.WriteUInt32LittleEndian(record[2..6], absolute);
    BinaryPrimitives.WriteUInt32BigEndian(record[6..10], absolute);
  }

  private static void AddBiasToLbaField(Span<byte> field, bool littleEndian, int bias) {
    if (field.Length < 4)
      throw new InvalidDataException("CDI: truncated ISO LBA field.");
    var value = littleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(field)
      : BinaryPrimitives.ReadUInt32BigEndian(field);
    if (value == 0)
      return;
    value = checked(value + (uint)bias);
    if (littleEndian)
      BinaryPrimitives.WriteUInt32LittleEndian(field, value);
    else
      BinaryPrimitives.WriteUInt32BigEndian(field, value);
  }

  private static void WriteBothEndianUInt32(Span<byte> destination, uint value) {
    BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], value);
    BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], value);
  }

  private static void ZeroRange(Stream stream, long count) {
    if (count <= 0)
      return;
    var zeros = new byte[64 * 1024];
    while (count > 0) {
      var chunk = (int)Math.Min(zeros.Length, count);
      stream.Write(zeros, 0, chunk);
      count -= chunk;
    }
  }

  private static void Verify(
    Stream staged,
    string expectedRoot,
    uint sourceVersion,
    IReadOnlyList<CdiTrackInfo> sourceTracks
  ) {
    staged.Position = 0;
    using var reader = new CdiReader(staged, leaveOpen: true);
    if (reader.CdiVersion != sourceVersion)
      throw new InvalidOperationException("CDI rebuild changed the descriptor version; refusing the staged result.");
    if (!reader.Tracks.SequenceEqual(sourceTracks))
      throw new InvalidOperationException("CDI rebuild changed the optical session/track map; refusing the staged result.");

    var expected = Directory.GetFiles(expectedRoot, "*", SearchOption.AllDirectories)
      .ToDictionary(
        file => Path.GetRelativePath(expectedRoot, file).Replace('\\', '/'),
        File.ReadAllBytes,
        StringComparer.OrdinalIgnoreCase
      );
    var actual = reader.Entries.Where(static entry => !entry.IsDirectory).ToArray();
    if (actual.Length != expected.Count)
      throw new InvalidOperationException(
        $"CDI rebuild changed the filesystem entry count ({expected.Count} expected, {actual.Length} read back).");

    foreach (var entry in actual) {
      if (!expected.TryGetValue(entry.FullPath, out var bytes))
        throw new InvalidOperationException($"CDI rebuild produced unexpected entry '{entry.FullPath}'.");
      if (!reader.Extract(entry).AsSpan().SequenceEqual(bytes))
        throw new InvalidOperationException($"CDI rebuild changed the contents of '{entry.FullPath}'.");
    }
  }
}

internal static class CdiSpanExtensions {
  internal static bool IsEmptyOrAllZero(this ReadOnlySpan<byte> bytes) {
    foreach (var value in bytes)
      if (value != 0)
        return false;
    return true;
  }
}
