#pragma warning disable CS1591
namespace Codec.Sid;

/// <summary>The two SID revisions, which differ chiefly in their filter cutoff curve.</summary>
public enum SidModel {
  /// <summary>The original MOS 6581: nonlinear, S-shaped filter cutoff curve and a DC distortion offset.</summary>
  Mos6581,

  /// <summary>The later MOS 8580: near-linear filter cutoff curve.</summary>
  Mos8580,
}
