#pragma warning disable CS1591

namespace FileFormat.Asf;

/// <summary>
/// Describes one complete ASF media object after packet reassembly.
/// </summary>
internal sealed record AsfMediaObjectInfo(
  int Length,
  uint PresentationTimeMs,
  bool KeyFrame
);
