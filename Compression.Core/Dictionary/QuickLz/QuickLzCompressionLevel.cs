namespace Compression.Core.Dictionary.QuickLz;

/// <summary>QuickLZ 1.5.0 compression levels supported by the managed codec.</summary>
public enum QuickLzCompressionLevel : byte {
  /// <summary>Fastest-compression mode using one hash candidate.</summary>
  Level1 = 1,

  /// <summary>Best-ratio mode using relative-offset references and a deeper match search.</summary>
  Level3 = 3,
}
