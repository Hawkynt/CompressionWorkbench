/////////////////////////////////////////////////////////////////////////////////////
//  File:   AmrWb.cs                                                20 May 25 PHR
/////////////////////////////////////////////////////////////////////////////////////

namespace AmrWbLib;

/// <summary>
/// Class for encoding and decoding Advanced Multi-Rate Wide Band data.
/// </summary>
public partial class AmrWb
{
    private AmrWb()
    {
    }

    /// <summary>
    /// Creates a new AmrWb object that can be used to encode voice data into AMR-WB encoded data.
    /// </summary>
    /// <param name="Mode">Specifies the codec encoding mode. Allowable values are in the range of 0 - 8 which
    /// correspond to bit rates: 6.60 kb/s, 8.85 kb/s, 12.65 kb/s, 14.25 kb/s, 15.85 kb/s, 18.25 kb/s, 19.85 kb/s,
    /// 23.05 kb/s and 23.85 kb/s.
    /// </param>
    /// <param name="AllowDtx">Enables or disables Discontinuous Transmit (TX). A value of 0 disables DTX and
    /// a value of 1 enables it. If DTX is enabled then the encoder will not produce speech packets during
    /// periods of silence.</param>
    /// <returns>Returns a new AmrWb object that can be used to encode voice packets into AMR-WB binary arrays
    /// that can be sent as the payload of RTP packets.</returns>
    public static AmrWb CreateAsEncoder(short Mode, short AllowDtx)
    {
        AmrWb amrWb = new AmrWb();
        amrWb.InitializeEncoder(Mode, AllowDtx);
        return amrWb;
    }

    /// <summary>
    /// Creates a new AmrWb object that can be used to decode AMR-WB encoded data into voice data.
    /// </summary>
    /// <returns>Returns a new AmrWb object that can be used to decode AMR-WB encoded data into voice data.</returns>
    public static AmrWb CreateAsDecoder()
    {
        AmrWb amrWb = new AmrWb();
        amrWb.InitializeDecoder();
        return amrWb;
    }

    /// <summary>
    /// Gets the packed size of the encoded data given the encoding mode.
    /// </summary>
    /// <param name="Mode">Encoding mode. Must be 0 - 15 inclusive.</param>
    /// <returns>Returns the packed size of the encoded data. The size may be 0.</returns>
    public int GetPacketSize(short Mode)
    {
        // Copied from mime_io.tab
        int[] packed_size = new int[16] { 17, 23, 32, 36, 40, 46, 50, 58, 60, 5, 0, 0, 0, 0, 0, 0 };

        if (Mode < 0 || Mode > 15)
            return 0;
        else
            return packed_size[Mode];
    }
}
