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
      // First pass: 4 KiB covers boot sectors and the superblocks at 1024, which is where
      // the large majority of filesystems announce themselves.
      var header = ReadPrefix(virtualDisk, (int)Math.Min(FastProbeBytes, virtualDisk.Length));

      // Phase 1: magic-signature-based detection via filesystem descriptors
      // only. This matters for mount composition: e.g. a VHD guest containing
      // ext4 must select our ext driver even when the host OS could mount ext4.
      if (BestFilesystemMatch(header) is { } best)
        return best;

      // Phase 2: heuristic detection for FAT (no magic signature in registry).
      // FAT boot sector: byte 0 is a JMP (0xEB or 0xE9), bytes 11-12 are
      // bytes-per-sector (typically 512), byte 13 is sectors-per-cluster (power of 2).
      if (header.Length >= 64 && header[0] is 0xEB or 0xE9) {
        var bytesPerSector = (int)(header[11] | (header[12] << 8));
        var sectorsPerCluster = header[13];
        if (bytesPerSector is 512 or 1024 or 2048 or 4096
            && sectorsPerCluster is > 0 and <= 128
            && (sectorsPerCluster & (sectorsPerCluster - 1)) == 0) {
          return FormatRegistry.GetById("Fat");
        }
      }

      // Phase 3: the superblocks that live past 4 KiB — ISO 9660 and UDF volume descriptors
      // at 32 KiB, JFS at 32 KiB, btrfs/GFS2/ReiserFS at 64 KiB, ZFS labels at 128 KiB. Read
      // only as far as the deepest registered filesystem signature reaches, never the volume.
      var deep = (int)Math.Min(DeepestFilesystemSignature(), virtualDisk.Length);
      if (deep <= header.Length)
        return null;
      return BestFilesystemMatch(ReadPrefix(virtualDisk, deep));
    } finally {
      virtualDisk.Position = savedPos;
    }
  }

  /// <summary>Bytes read by the first, cheap pass.</summary>
  private const int FastProbeBytes = 4096;

  /// <summary>Upper bound on the deep pass, so a malformed descriptor cannot make us read a volume.</summary>
  private const int MaxProbeBytes = 1 << 20;

  private static int _deepestFilesystemSignature;

  private static int DeepestFilesystemSignature() {
    if (_deepestFilesystemSignature > 0)
      return _deepestFilesystemSignature;

    var deepest = FastProbeBytes;
    foreach (var formatId in FormatRegistry.FilesystemFormatIds)
      if (FormatRegistry.GetById(formatId) is { } desc)
        foreach (var sig in desc.MagicSignatures)
          deepest = Math.Max(deepest, sig.Offset + sig.Bytes.Length);

    return _deepestFilesystemSignature = Math.Min(deepest, MaxProbeBytes);
  }

  private static byte[] ReadPrefix(Stream stream, int length) {
    var buffer = new byte[length];
    stream.Position = 0;
    var read = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
    return read == length ? buffer : buffer[..read];
  }

  private static IFormatDescriptor? BestFilesystemMatch(ReadOnlySpan<byte> header) {
    IFormatDescriptor? best = null;
    var bestConfidence = 0.0;

    foreach (var formatId in FormatRegistry.FilesystemFormatIds) {
      var desc = FormatRegistry.GetById(formatId);
      if (desc is null || desc.Category is not FormatCategory.Archive)
        continue;
      if (desc is not IArchiveFormatOperations)
        continue;

      foreach (var sig in desc.MagicSignatures) {
        if (MatchesMagic(header, sig) && sig.Confidence > bestConfidence) {
          bestConfidence = sig.Confidence;
          best = desc;
        }
      }
    }
    return best;
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
