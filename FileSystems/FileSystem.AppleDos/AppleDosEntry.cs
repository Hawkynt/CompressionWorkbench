#pragma warning disable CS1591
namespace FileSystem.AppleDos;

/// <summary>
/// Directory entry in an Apple DOS 3.3 disk image.
/// </summary>
public sealed class AppleDosEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory => false;
  /// <summary>DOS 3.3 file type nibble. Low 7 bits: 0=T(ext), 1=I(nteger BASIC),
  /// 2=A(pplesoft BASIC), 4=B(inary), 8=S, 0x10=R, 0x20=AA, 0x40=BB. High bit = locked.</summary>
  public byte FileType { get; init; }
  /// <summary>Sector count stored in the catalog (total sectors including T/S list).</summary>
  public int SectorCount { get; init; }
  internal int TrackSectorListTrack { get; init; }
  internal int TrackSectorListSector { get; init; }
}
