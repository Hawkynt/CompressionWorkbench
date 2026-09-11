namespace Compression.Registry;

/// <summary>
/// Per-operation options for mutating an existing archive or filesystem image.
/// </summary>
/// <remarks>
/// Mutation credentials belong to the operation, not to the input entries. Keeping
/// them here avoids format-specific ambient state and lets encrypted containers
/// expose the same add/replace/remove contract as unencrypted formats.
/// </remarks>
public sealed class ArchiveMutationOptions {
  /// <summary>Password or passphrase required to unlock the existing container.</summary>
  public string? Password { get; init; }
}
