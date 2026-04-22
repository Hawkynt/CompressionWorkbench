namespace Compression.Registry;

/// <summary>
/// Classifies a format by its primary behavior.
/// </summary>
public enum FormatCategory {
  /// <summary>Multi-file container (ZIP, TAR, 7z, etc.).</summary>
  Archive,
  /// <summary>Single-stream compressor (Gzip, Bzip2, Xz, etc.).</summary>
  Stream,
  /// <summary>Encoding wrapper (MacBinary, BinHex).</summary>
  Wrapper,
  /// <summary>Auto-generated tar + stream combination (tar.gz, tar.bz2, etc.).</summary>
  CompoundTar,
  /// <summary>Recognized by signature only, no operations (ISO, UDF).</summary>
  DetectionOnly,
  /// <summary>Audio container surfaced as an archive of tracks/channels/tags (FLAC, WAV, MP3, OGG).</summary>
  Audio,
  /// <summary>Video container surfaced as an archive of demuxed tracks + attachments (MKV, MP4).</summary>
  Video,
  /// <summary>Image container surfaced as an archive of the full image + per-plane pixel data (PNG, JPEG).</summary>
  Image,
}
