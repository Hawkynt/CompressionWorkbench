namespace Compression.Registry;

/// <summary>
/// Associates a format descriptor with the concrete physical extent mover it
/// deliberately exposes when the mover is kept as a separate helper object.
/// </summary>
/// <remarks>
/// Descriptor-native movers do not need this attribute: implementing
/// <see cref="IFilesystemBlockMover"/> directly is already an explicit claim.
/// The attribute exists for composition and for formats that intentionally reuse
/// another format family's compatible mover.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FilesystemBlockMoverAttribute(Type moverType) : Attribute {
  /// <summary>The concrete mover type exposed by the descriptor.</summary>
  public Type MoverType { get; } = moverType ?? throw new ArgumentNullException(nameof(moverType));
}
