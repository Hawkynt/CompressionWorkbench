#pragma warning disable CS1591
namespace Codec.Tracker;

/// <summary>
/// ProTracker Amiga period tables and the PAL clock constants used to turn a
/// period into a playback frequency.
/// </summary>
/// <remarks>
/// Periods and the finetune table are taken from the ProTracker 2.3 replayer
/// (the canonical 16 finetune rows × 36 notes table reproduced in the FireLight
/// "fmoddoc" MOD documentation). The PAL Paula clock is 7093789.2 Hz; replay
/// frequency = clock / (period × 2).
/// </remarks>
internal static class AmigaPeriods {

  /// <summary>PAL Paula clock in Hz (NTSC is 7159090.5; PAL is the ProTracker default).</summary>
  public const double PalClock = 7093789.2;

  /// <summary>Frequency in Hz for a given Amiga period at the PAL clock.</summary>
  public static double FrequencyForPeriod(double period)
    => period <= 0 ? 0 : PalClock / (period * 2.0);

  /// <summary>
  /// The 16 × 36 ProTracker period table. Row = finetune (0..15, where rows 8..15
  /// are the negative finetunes -8..-1), column = note index 0..35
  /// (C-1..B-3, three octaves).
  /// </summary>
  public static readonly short[][] Table = [
    // Finetune 0
    [856,808,762,720,678,640,604,570,538,508,480,453,
     428,404,381,360,339,320,302,285,269,254,240,226,
     214,202,190,180,170,160,151,143,135,127,120,113],
    // Finetune +1
    [850,802,757,715,674,637,601,567,535,505,477,450,
     425,401,379,357,337,318,300,284,268,253,239,225,
     213,201,189,179,169,159,150,142,134,126,119,113],
    // Finetune +2
    [844,796,752,709,670,632,597,563,532,502,474,447,
     422,398,376,355,335,316,298,282,266,251,237,224,
     211,199,188,177,167,158,149,141,133,125,118,112],
    // Finetune +3
    [838,791,746,704,665,628,592,559,528,498,470,444,
     419,395,373,352,332,314,296,280,264,249,235,222,
     209,198,187,176,166,157,148,140,132,125,118,111],
    // Finetune +4
    [832,785,741,699,660,623,588,555,524,495,467,441,
     416,392,370,350,330,312,294,278,262,247,233,220,
     208,196,185,175,165,156,147,139,131,124,117,110],
    // Finetune +5
    [826,779,736,694,655,619,584,551,520,491,463,437,
     413,390,368,347,328,309,292,276,260,245,232,219,
     206,195,184,174,164,155,146,138,130,123,116,109],
    // Finetune +6
    [820,774,730,689,651,614,580,547,516,487,460,434,
     410,387,365,345,325,307,290,274,258,244,230,217,
     205,193,183,172,163,154,145,137,129,122,115,109],
    // Finetune +7
    [814,768,725,684,646,610,575,543,513,484,457,431,
     407,384,363,342,323,305,288,272,256,242,228,216,
     204,192,181,171,161,152,144,136,128,121,114,108],
    // Finetune -8
    [907,856,808,762,720,678,640,604,570,538,508,480,
     453,428,404,381,360,339,320,302,285,269,254,240,
     226,214,202,190,180,170,160,151,143,135,127,120],
    // Finetune -7
    [900,850,802,757,715,675,636,601,567,535,505,477,
     450,425,401,379,357,337,318,300,284,268,253,238,
     225,212,200,189,179,169,159,150,142,134,126,119],
    // Finetune -6
    [894,844,796,752,709,670,632,597,563,532,502,474,
     447,422,398,376,355,335,316,298,282,266,251,237,
     223,211,199,188,177,167,158,149,141,133,125,118],
    // Finetune -5
    [887,838,791,746,704,665,628,592,559,528,498,470,
     444,419,395,373,352,332,314,296,280,264,249,235,
     222,209,198,187,176,166,157,148,140,132,125,118],
    // Finetune -4
    [881,832,785,741,699,660,623,588,555,524,494,467,
     441,416,392,370,350,330,312,294,278,262,247,233,
     220,208,196,185,175,165,156,147,139,131,123,117],
    // Finetune -3
    [875,826,779,736,694,655,619,584,551,520,491,463,
     437,413,390,368,347,328,309,292,276,260,245,232,
     219,206,195,184,174,164,155,146,138,130,123,116],
    // Finetune -2
    [868,820,774,730,689,651,614,580,547,516,487,460,
     434,410,387,365,345,325,307,290,274,258,244,230,
     217,205,193,183,172,163,154,145,137,129,122,115],
    // Finetune -1
    [862,814,768,725,684,646,610,575,543,513,484,457,
     431,407,384,363,342,323,305,288,272,256,242,228,
     216,203,192,181,171,161,152,144,136,128,121,114],
  ];

  /// <summary>
  /// Maps a ProTracker finetune nibble (0..15) to its table row. Finetune is
  /// stored as a signed 4-bit value: 0..7 are +0..+7, 8..15 are -8..-1.
  /// </summary>
  public static int FineTuneToRow(int fineTuneNibble) => fineTuneNibble & 0x0F;

  /// <summary>The base period for a note index (0..35) at the given finetune row.</summary>
  public static int PeriodFor(int noteIndex, int fineTuneRow) {
    if (noteIndex < 0 || noteIndex > 35)
      return 0;
    var row = Table[fineTuneRow & 0x0F];
    return row[noteIndex];
  }

  /// <summary>
  /// Finds the nearest note index (0..35) for a raw period at finetune 0, used to
  /// re-quantise periods to notes for tone-portamento targets and finetune lookups.
  /// </summary>
  public static int NearestNoteIndex(int period) {
    var row = Table[0];
    var best = 0;
    var bestDiff = int.MaxValue;
    for (var i = 0; i < row.Length; ++i) {
      var diff = Math.Abs(row[i] - period);
      if (diff < bestDiff) {
        bestDiff = diff;
        best = i;
      }
    }
    return best;
  }
}
