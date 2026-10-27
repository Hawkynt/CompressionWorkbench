#pragma warning disable CS1591

using Compression.Core.DiskImage;
using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Opens a writable stream to the deepest addressable filesystem inside a
/// potentially nested container. Supports descent through disk images
/// (VHD/VHDX/VMDK/QCOW2/VDI), partition tables (MBR/GPT), and nested
/// filesystem images.
///
/// <para>Example descent: disk.vhd -> partition 0 -> NTFS volume -> writable stream.</para>
/// </summary>
public static class NestedStreamResolver {

  /// <summary>Maximum nesting depth to prevent infinite loops.</summary>
  private const int MaxDepth = 8;

  /// <summary>
  /// Format IDs that represent virtual disk images whose inner content is raw
  /// disk data that may contain a partition table.
  /// </summary>
  private static readonly HashSet<string> DiskImageFormatIds = new(StringComparer.OrdinalIgnoreCase) {
    "Vhd", "Vhdx", "Vmdk", "Qcow2", "Vdi"
  };

  /// <summary>
  /// Result of a nested stream resolution, containing the innermost writable
  /// stream, the descriptor that governs it, and the human-readable nesting path.
  /// </summary>
  public sealed class Resolution {
    /// <summary>The innermost writable stream. Caller owns this and must dispose it
    /// (which flushes writes up through the container chain).</summary>
    public required Stream InnerStream { get; init; }

    /// <summary>The format descriptor for the innermost filesystem.</summary>
    public required IFormatDescriptor InnerDescriptor { get; init; }

    /// <summary>Human-readable nesting path, e.g. "VHD -> MBR partition 0 -> FAT".</summary>
    public required string NestingPath { get; init; }

    /// <summary>Streams opened during descent that must be kept alive while
    /// <see cref="InnerStream"/> is in use. Dispose in reverse order when done.</summary>
    public required List<IDisposable> OwnershipChain { get; init; }

    /// <summary>Disposes all streams in the ownership chain (outermost first).</summary>
    public void DisposeAll() {
      for (var i = OwnershipChain.Count - 1; i >= 0; i--) {
        try { OwnershipChain[i].Dispose(); } catch { /* best effort */ }
      }
    }
  }

  /// <summary>
  /// Attempts to resolve the deepest writable filesystem inside the file at
  /// <paramref name="path"/>. Returns <c>null</c> if the file is not a
  /// recognized container or contains no writable filesystem.
  /// </summary>
  public static Resolution? ResolveDeepest(string path) {
    FormatRegistration.EnsureInitialized();

    if (!File.Exists(path)) return null;

    var fileStream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
    var ownership = new List<IDisposable> { fileStream };

    try {
      var result = Descend(fileStream, ownership, [], 0);
      if (result != null) return result;
    } catch {
      // Fall through — dispose and return null
    }

    // No inner FS found — clean up
    for (var i = ownership.Count - 1; i >= 0; i--) {
      try { ownership[i].Dispose(); } catch { /* best effort */ }
    }

    return null;
  }

  /// <summary>
  /// Attempts to resolve the deepest writable filesystem from an already-open
  /// stream. The caller retains ownership of <paramref name="stream"/>.
  /// Returns <c>null</c> if no writable filesystem is found inside.
  /// </summary>
  public static Resolution? ResolveDeepest(Stream stream) {
    FormatRegistration.EnsureInitialized();

    var ownership = new List<IDisposable>();

    try {
      var result = Descend(stream, ownership, [], 0);
      if (result != null) return result;
    } catch {
      // Fall through
    }

    for (var i = ownership.Count - 1; i >= 0; i--) {
      try { ownership[i].Dispose(); } catch { /* best effort */ }
    }

    return null;
  }

  private static Resolution? Descend(
    Stream current, List<IDisposable> ownership,
    List<string> pathSegments, int depth) {

    if (depth >= MaxDepth) return null;
    if (current.Length < 512) return null;

    var desc = DetectFormat(current);
    if (desc == null) return null;

    // If it's a disk image container, try to open the virtual disk stream
    if (DiskImageFormatIds.Contains(desc.Id)) {
      var virtualStream = TryOpenVirtualDisk(current, desc.Id);
      if (virtualStream != null) {
        ownership.Add(virtualStream);
        var innerSegments = new List<string>(pathSegments) { desc.DisplayName };

        // Try partition table first
        var partResult = TryDescendPartitions(virtualStream, ownership, innerSegments, depth + 1);
        if (partResult != null) return partResult;

        // Try direct filesystem detection on the virtual disk
        return Descend(virtualStream, ownership, innerSegments, depth + 1);
      }
    }

    // If it's a modifiable filesystem, this is our target
    if (desc is IArchiveModifiable) {
      var segments = new List<string>(pathSegments) { desc.DisplayName };
      return new Resolution {
        InnerStream = current,
        InnerDescriptor = desc,
        NestingPath = string.Join(" -> ", segments),
        OwnershipChain = ownership,
      };
    }

    return null;
  }

  private static Resolution? TryDescendPartitions(
    Stream diskStream, List<IDisposable> ownership,
    List<string> pathSegments, int depth) {

    if (depth >= MaxDepth) return null;

    var detection = PartitionTableDetector.Detect(diskStream);
    if (detection.Scheme == "None" || detection.Partitions.Count == 0)
      return null;

    // Try each partition, preferring the first that yields a writable FS
    foreach (var part in detection.Partitions) {
      if (part.Size <= 0 || part.StartOffset < 0 || part.StartOffset >= diskStream.Length)
        continue;

      var actualSize = Math.Min(part.Size, diskStream.Length - part.StartOffset);
      var partStream = new WritableSubStream(diskStream, part.StartOffset, actualSize);
      // Don't add to ownership — it doesn't own the underlying stream

      var partSegments = new List<string>(pathSegments) {
        $"{detection.Scheme} partition {part.Index} ({part.TypeName})"
      };

      var result = Descend(partStream, ownership, partSegments, depth + 1);
      if (result != null) return result;
    }

    return null;
  }

  private static IFormatDescriptor? DetectFormat(Stream stream) {
    if (stream.Length < 4) return null;

    var savedPos = stream.Position;
    try {
      stream.Position = 0;
      var headerSize = (int)Math.Min(4096, stream.Length);
      var header = new byte[headerSize];
      var bytesRead = 0;
      while (bytesRead < headerSize) {
        var n = stream.Read(header, bytesRead, headerSize - bytesRead);
        if (n == 0) break;
        bytesRead += n;
      }

      var headerSpan = header.AsSpan(0, bytesRead);
      IFormatDescriptor? best = null;
      var bestConfidence = 0.0;

      foreach (var desc in FormatRegistry.All) {
        foreach (var sig in desc.MagicSignatures) {
          if (MatchesMagic(headerSpan, sig) && sig.Confidence > bestConfidence) {
            bestConfidence = sig.Confidence;
            best = desc;
          }
        }
      }

      if (best != null) return best;

      // Heuristic: VHD fixed-disk detection — the "conectix" footer is at the
      // END of the file (offset = length - 512), not at offset 0. The registry
      // magic signature only checks offset 0 (which works for dynamic VHD).
      if (stream.Length >= 512 && stream.CanSeek) {
        stream.Position = stream.Length - 512;
        Span<byte> footer = stackalloc byte[8];
        if (stream.Read(footer) == 8 && "conectix"u8.SequenceEqual(footer)) {
          var vhdDesc = FormatRegistry.GetById("Vhd");
          if (vhdDesc != null) return vhdDesc;
        }
      }

      // Heuristic: FAT detection (no magic, BPB-based)
      if (bytesRead >= 64 && headerSpan[0] is 0xEB or 0xE9) {
        var bytesPerSector = (int)(headerSpan[11] | (headerSpan[12] << 8));
        var sectorsPerCluster = headerSpan[13];
        if (bytesPerSector is 512 or 1024 or 2048 or 4096
            && sectorsPerCluster is > 0 and <= 128
            && (sectorsPerCluster & (sectorsPerCluster - 1)) == 0) {
          return FormatRegistry.GetById("Fat");
        }
      }

      return null;
    } finally {
      stream.Position = savedPos;
    }
  }

  private static bool MatchesMagic(ReadOnlySpan<byte> header, MagicSignature sig) {
    if (header.Length < sig.Offset + sig.Bytes.Length) return false;
    var slice = header.Slice(sig.Offset, sig.Bytes.Length);
    if (sig.Mask != null) {
      for (var i = 0; i < sig.Bytes.Length; i++)
        if ((slice[i] & sig.Mask[i]) != (sig.Bytes[i] & sig.Mask[i]))
          return false;
      return true;
    }
    return slice.SequenceEqual(sig.Bytes);
  }

  /// <summary>
  /// Opens the raw virtual disk content as a seekable read/write stream.
  /// Uses format-specific stream wrappers (VhdStream, VhdxStream, etc.) that
  /// translate virtual addresses to physical file offsets, rather than the
  /// Extract path which would delegate to the inner FS and return user files.
  /// </summary>
  private static Stream? TryOpenVirtualDisk(Stream container, string formatId) {
    try {
      container.Position = 0;
      return formatId switch {
        "Vhd" => TryOpenVhd(container),
        "Vhdx" => TryOpenVhdx(container),
        "Vmdk" => TryOpenVmdk(container),
        "Qcow2" => TryOpenQcow2(container),
        "Vdi" => TryOpenVdi(container),
        _ => null
      };
    } catch {
      return null;
    }
  }

  private static Stream? TryOpenVhd(Stream container) {
    try {
      container.Position = 0;
      return new FileFormat.Vhd.VhdStream(container, leaveOpen: true);
    } catch { return null; }
  }

  private static Stream? TryOpenVhdx(Stream container) {
    try {
      container.Position = 0;
      return FileFormat.Vhdx.VhdxStream.TryOpen(container);
    } catch { return null; }
  }

  private static Stream? TryOpenVmdk(Stream container) {
    try {
      container.Position = 0;
      return FileFormat.Vmdk.VmdkStream.TryOpen(container);
    } catch { return null; }
  }

  private static Stream? TryOpenQcow2(Stream container) {
    try {
      container.Position = 0;
      return FileFormat.Qcow2.Qcow2Stream.TryOpen(container);
    } catch { return null; }
  }

  private static Stream? TryOpenVdi(Stream container) {
    try {
      container.Position = 0;
      return FileFormat.Vdi.VdiStream.TryOpen(container);
    } catch { return null; }
  }
}
