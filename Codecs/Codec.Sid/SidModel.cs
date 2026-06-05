#pragma warning disable CS1591
namespace Codec.Sid;

/// <summary>The SID revisions, which differ chiefly in their filter cutoff curve.</summary>
public enum SidModel {
  /// <summary>The original MOS 6581: nonlinear, S-shaped filter cutoff curve and a DC distortion offset.</summary>
  Mos6581,

  /// <summary>The later MOS 8580: near-linear filter cutoff curve.</summary>
  Mos8580,

  /// <summary>
  /// The MOS 6582. Electrically and sonically it is an 8580 (it shares the 8580 die and filter
  /// behaviour); it is named here so a real-world chip set can be reported faithfully, but it is
  /// treated identically to <see cref="Mos8580"/> for synthesis. See <see cref="SidModelExtensions.Resolve"/>.
  /// </summary>
  Mos6582,
}

/// <summary>Helpers for collapsing SID model aliases onto the two electrically distinct behaviours.</summary>
public static class SidModelExtensions {
  /// <summary>
  /// Resolves a model to the electrically distinct behaviour the emulator implements: every alias of
  /// the 8580 (including the <see cref="SidModel.Mos6582"/>) maps to <see cref="SidModel.Mos8580"/>;
  /// otherwise <see cref="SidModel.Mos6581"/>.
  /// </summary>
  public static SidModel Resolve(this SidModel model)
    => model is SidModel.Mos8580 or SidModel.Mos6582 ? SidModel.Mos8580 : SidModel.Mos6581;
}
