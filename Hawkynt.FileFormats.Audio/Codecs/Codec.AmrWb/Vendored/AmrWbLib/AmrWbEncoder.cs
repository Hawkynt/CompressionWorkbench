/////////////////////////////////////////////////////////////////////////////////////
//  File:   AmrWbEncoder.cs                                         24 Sep 24 PHR
/////////////////////////////////////////////////////////////////////////////////////

namespace SipLib.Media;

using AmrWbLib;

/// <summary>
/// Class for encoding linear 16-bit PCM samples into AMR-WB data.
/// </summary>
public class AmrWbEncoder : IAudioEncoder
{
    private AmrWb? m_Encoder;

    /// <summary>
    /// Constructor. Initializes the encoder.
    /// </summary>
    /// <param name="Mode">Specifies the encoder mode which determines the output bit rate. Must be a
    /// value between 0 and 8. 0 = 6.60 kbps, 1 = 8.85 kbps, 2 = 12.65 kbps, 3 = 14.25 kbps, 4 = 
    /// 15.85 kbps, 5 = 18.25 kbps, 6 = 19.85 kbps, 7 = 23.05 kbps, 8 = 23.85. The default is 2 (12.65 kbps).</param>
    /// <param name="AllowDtx">Enables or disables Discontinuous Transmit (TX). A value of false disables DTX and
    /// a value of true enables it. If DTX is enabled then the encoder will not produce speech packets during
    /// periods of silence. The default is false.</param>
    public AmrWbEncoder(int Mode = 2, bool AllowDtx = false)
    {
        short allowDtx = (short) (AllowDtx == true ? 1 : 0);
        m_Encoder = AmrWb.CreateAsEncoder((short)Mode, allowDtx);
        if (Mode < 0 || Mode > 8)
            Mode = 2;
    }

    /// <summary>
    /// Gets the RTP clock rate in samples/second
    /// </summary>
    public int ClockRate
    {
        get { return 16000; }
    }

    /// <summary>
    /// Gets the audio sample rate in samples/second
    /// </summary>
    public int SampleRate
    {
        get { return 16000; }
    }

    /// <summary>
    /// Amount to increment the RTP packet Time Stamp field by for each new packet.
    /// </summary>
    public uint TimeStampIncrement
    {
        get { return 320; }
    }

    /// <summary>
    /// Closes the encoder
    /// </summary>
    public void CloseEncoder()
    {
        m_Encoder = null;
    }

    /// <summary>
    /// Encodes linear 16-bit PCM samples into AMR-WB data to send in an RTP packet.
    /// </summary>
    /// <param name="InputSamples">Input linear 16-bit PCM samples. The length must be 320 corresponding
    /// to 20 msec. of audio data.</param>
    /// <returns>Returns the encoded AMR-WB bytes</returns>
    public byte[] Encode(short[] InputSamples)
    {
        if (m_Encoder == null)
            return new byte[InputSamples.Length];

        byte[] encodedBytes = m_Encoder.EncodePacketSamples(InputSamples);
        return encodedBytes!;
    }
}
