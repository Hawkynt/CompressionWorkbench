namespace Compression.Registry;

/// <summary>
/// Self-describing metadata for a file format. Each FileFormat.* project provides
/// one implementation of this interface to register itself with the format registry.
/// </summary>
public interface IFormatDescriptor {
  /// <summary>Unique format identifier (e.g. "Zip", "Gzip"). Must match the Format enum name for backward compat.</summary>
  string Id { get; }

  /// <summary>Human-readable display name (e.g. "ZIP", "GZIP").</summary>
  string DisplayName { get; }

  /// <summary>Primary category of this format.</summary>
  FormatCategory Category { get; }

  /// <summary>Capabilities flags for this format.</summary>
  FormatCapabilities Capabilities { get; }

  /// <summary>Default file extension including the dot (e.g. ".gz").</summary>
  string DefaultExtension { get; }

  /// <summary>All recognized single extensions (e.g. [".gz", ".gzip"]).</summary>
  IReadOnlyList<string> Extensions { get; }

  /// <summary>Compound (multi-dot) extensions this format owns (e.g. [".tar.gz", ".tgz"]). Checked before single extensions.</summary>
  IReadOnlyList<string> CompoundExtensions { get; }

  /// <summary>Magic byte signatures for detection.</summary>
  IReadOnlyList<MagicSignature> MagicSignatures { get; }

  /// <summary>Compression methods available for this format.</summary>
  IReadOnlyList<FormatMethodInfo> Methods { get; }

  /// <summary>For compound tar formats: the ID of the outer stream compression format. Null for non-compound formats.</summary>
  string? TarCompressionFormatId { get; }
}
