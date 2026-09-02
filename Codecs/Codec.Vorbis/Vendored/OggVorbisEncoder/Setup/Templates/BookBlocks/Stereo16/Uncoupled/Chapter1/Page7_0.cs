namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Uncoupled.Chapter1;

/// <summary>
/// Represents a page 7 0.
/// </summary>
public class Page7_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 4;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 4, 4, 8, 8, 4, 8, 8, 5,11, 9, 8,12,11, 8,
        12,11, 5,10,11, 8,11,12, 8,11,12, 4,11,11,11,14,
        13,10,13,13, 8,14,13,12,14,16,12,16,15, 8,14,14,
        13,16,14,12,15,16, 4,11,11,10,14,13,11,14,14, 8,
        15,14,12,15,15,12,14,16, 8,14,14,11,16,15,12,15,
        13,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -529137664;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1618345984;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 2;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        1,
        0,
        2,
    };
}
