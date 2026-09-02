#pragma warning disable CS1591

namespace Compression.Registry;

/// <summary>
/// Detects the filesystem contained within a virtual disk stream by scanning
/// the registered CompressionWorkbench filesystem descriptors against the
/// stream header. Falls back to heuristic BPB checks for FAT (which has no
/// magic signature).
/// </summary>
/// <remarks>
/// This detector is deliberately filesystem-only. Mount composition must never
/// hand a guest disk back to the host OS, nor mistake an archive/container
/// descriptor for the filesystem that owns the bytes. Containers are decoded
/// by their own CompressionWorkbench layer first; this class selects only the
/// next CompressionWorkbench filesystem parser.
/// </remarks>
public static class InnerFsDetector {

  /// <summary>
  /// Tries to detect the inner filesystem descriptor from a virtual disk stream.
  /// Returns only descriptors registered as filesystem formats and exposing
  /// <see cref="IArchiveFormatOperations"/>; otherwise <c>null</c>.
  /// </summary>
  public static IFormatDescriptor? Detect(Stream virtualDisk) {
    if (virtualDisk.Length < 512)
      return null;

    var savedPos = virtualDisk.Position;
    try {
      virtualDisk.Position = 0;
      // Read enough header bytes to match any registry magic signature.
      // 4096 covers boot sectors, superblocks at offset 1024, etc.
      var headerSize = (int)Math.Min(4096, virtualDisk.Length);
      var header = new byte[headerSize];
      var bytesRead = 0;
      while (bytesRead < headerSize) {
        var n = virtualDisk.Read(header, bytesRead, headerSize - bytesRead);
        if (n == 0) break;
        bytesRead += n;
      }

      var headerSpan = header.AsSpan(0, bytesRead);

      // Phase 1: magic-signature-based detection via filesystem descriptors
      // only. This matters for mount composition: e.g. a VHD guest containing
      // ext4 must select our ext driver even when the host OS could mount ext4.
      IFormatDescriptor? best = null;
      var bestConfidence = 0.0;

      foreach (var formatId in FormatRegistry.FilesystemFormatIds) {
        var desc = FormatRegistry.GetById(formatId);
        if (desc is null || desc.Category is not FormatCategory.Archive)
          continue;
        if (desc is not IArchiveFormatOperations)
          continue;

        foreach (var sig in desc.MagicSignatures) {
          if (MatchesMagic(headerSpan, sig) && sig.Confidence > bestConfidence) {
            bestConfidence = sig.Confidence;
            best = desc;
          }
        }
      }

      if (best != null)
        return best;

      // Phase 2: heuristic detection for FAT (no magic signature in registry).
      // FAT boot sector: byte 0 is a JMP (0xEB or 0xE9), bytes 11-12 are
      // bytes-per-sector (typically 512), byte 13 is sectors-per-cluster (power of 2).
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
      virtualDisk.Position = savedPos;
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
}
