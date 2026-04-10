#pragma warning disable CS1591

namespace FileFormat.GodotPck;

/// <summary>Represents a single file entry in a Godot PCK archive.</summary>
public sealed class PckEntry {
  /// <summary>The virtual path of the file (e.g. "res://scenes/main.tscn").</summary>
  public string Path { get; init; } = "";

  /// <summary>The uncompressed size of the file data in bytes.</summary>
  public long Size { get; init; }

  /// <summary>The absolute byte offset within the PCK stream where the file data begins.</summary>
  public long Offset { get; init; }

  /// <summary>The 16-byte MD5 hash of the file data.</summary>
  public byte[] Md5 { get; init; } = new byte[16];
}
