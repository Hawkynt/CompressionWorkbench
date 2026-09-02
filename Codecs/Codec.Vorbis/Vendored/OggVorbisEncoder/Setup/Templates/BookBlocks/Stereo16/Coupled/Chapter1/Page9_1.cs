namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Coupled.Chapter1;

/// <summary>
/// Represents a page 9 1.
/// </summary>
public class Page9_1 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 4, 4, 4, 8, 8,12,13,14,14,14,14,14,14, 6,
         6, 6, 6, 6,10, 9,14,14,14,14,14,14,14,14, 7, 6,
         5, 6, 6,10, 9,12,13,13,13,13,13,13,13,13, 7, 7,
         9, 9,11,11,12,13,13,13,13,13,13,13,13, 7, 7, 8,
         8,11,12,13,13,13,13,13,13,13,13,13,12,12,10,10,
        13,12,13,13,13,13,13,13,13,13,13,12,12,10,10,13,
        13,13,13,13,13,13,13,13,13,13,13,13,13,12,13,12,
        13,13,13,13,13,13,13,13,13,13,13,13,12,13,13,13,
        13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,
        13,13,13,13,13,13,13,13,13,13,13,13,12,13,13,13,
        13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,
        13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,
        13,13,13,13,13,13,13,13,13,12,13,13,13,13,13,13,
        13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,
        13,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -520986624;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1620377600;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 4;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        7,
        6,
        8,
        5,
        9,
        4,
        10,
        3,
        11,
        2,
        12,
        1,
        13,
        0,
        14,
    };
}
