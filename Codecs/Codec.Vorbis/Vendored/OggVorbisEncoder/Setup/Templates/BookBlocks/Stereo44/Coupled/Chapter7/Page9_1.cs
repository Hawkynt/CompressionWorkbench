namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter7;

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
         1, 4, 4, 7, 7, 7, 7, 7, 6, 8, 8, 8, 8, 6, 6, 6,
         8, 8, 9, 8, 8, 7, 9, 8,11,10, 5, 6, 6, 8, 8, 9,
         8, 8, 8,10, 9,11,11,16, 8, 8, 9, 8, 9, 9, 9, 8,
        10, 9,11,10,16, 8, 8, 9, 9,10,10, 9, 9,10,10,11,
        11,16,13,13, 9, 9,10,10, 9,10,11,11,12,11,16,13,
        13, 9, 8,10, 9,10,10,10,10,11,11,16,14,16, 8, 9,
         9, 9,11,10,11,11,12,11,16,16,16, 9, 7,10, 7,11,
        10,11,11,12,11,16,16,16,12,12, 9,10,11,11,12,11,
        12,12,16,16,16,12,10,10, 7,11, 8,12,11,12,12,16,
        16,15,16,16,11,12,10,10,12,11,12,12,16,16,16,15,
        15,11,11,10,10,12,12,12,12,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -518889472;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1622704128;
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
        6,
        5,
        7,
        4,
        8,
        3,
        9,
        2,
        10,
        1,
        11,
        0,
        12,
    };
}
