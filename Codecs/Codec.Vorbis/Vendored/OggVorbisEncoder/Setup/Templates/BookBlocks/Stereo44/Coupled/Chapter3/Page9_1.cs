namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter3;

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
         1, 4, 4, 6, 6, 7, 7, 8, 7, 9, 9,10,10,10,10, 6,
         5, 5, 7, 7, 8, 8,10, 8,11,10,12,12,13,13, 6, 5,
         5, 7, 7, 8, 8,10, 9,11,11,12,12,13,12,18, 8, 8,
         8, 8, 9, 9,10, 9,11,10,12,12,13,13,18, 8, 8, 8,
         8, 9, 9,10,10,11,11,13,12,14,13,18,11,11, 9, 9,
        10,10,11,11,11,12,13,12,13,14,18,11,11, 9, 8,11,
        10,11,11,11,11,12,12,14,13,18,18,18,10,11,10,11,
        12,12,12,12,13,12,14,13,18,18,18,10,11,11, 9,12,
        11,12,12,12,13,13,13,18,18,17,14,14,11,11,12,12,
        13,12,14,12,14,13,18,18,18,14,14,11,10,12, 9,12,
        13,13,13,13,13,18,18,17,16,18,13,13,12,12,13,11,
        14,12,14,14,17,18,18,17,18,13,12,13,10,12,11,14,
        14,14,14,17,18,18,18,18,15,16,12,12,13,10,14,12,
        14,15,18,18,18,16,17,16,14,12,11,13,10,13,13,14,
        15,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -522338304;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1620115456;
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
