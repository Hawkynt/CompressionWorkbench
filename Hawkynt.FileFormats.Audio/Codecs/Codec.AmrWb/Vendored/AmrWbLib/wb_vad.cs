/*-------------------------------------------------------------------*
 *                         WB_VAD.C                   *
 *-------------------------------------------------------------------*
 * Voice Activity Detection.                     *
 *-------------------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private const int FRAME_LEN = 256;         /* Length (samples) of the input frame          */
    private const int COMPLEN = 12;            /* Number of sub-bands used by VAD              */

    private const int UNIRSHFT = 7;            /* = log2(MAX_16/UNITY), UNITY = 256      */
    private const int SCALE = 128;             /* (UNITY*UNITY)/512 */

    private const short TONE_THR = (short)(0.65 * short.MaxValue);     /* Threshold for tone detection   */

    /* constants for speech level estimation */
    private const int SP_EST_COUNT = 80;
    private const int SP_ACTIVITY_COUNT = 25;
    private const short ALPHA_SP_UP = (short)((1.0 - 0.85) * short.MaxValue);
    private const short ALPHA_SP_DOWN = (short)((1.0 - 0.85)*short.MaxValue);

    private const int NOM_LEVEL = 2050;                     /* about -26 dBov Q15 */
    private const int SPEECH_LEVEL_INIT = NOM_LEVEL;        /* initial speech level */
    private const short MIN_SPEECH_LEVEL1 = (short)(NOM_LEVEL * 0.063);  /* NOM_LEVEL -24 dB */
    private const short MIN_SPEECH_LEVEL2 = (short)(NOM_LEVEL * 0.2);    /* NOM_LEVEL -14 dB */
    private const int MIN_SPEECH_SNR = 4096;                /* 0 dB, lowest SNR estimation, Q12 */

    /* Time constants for background spectrum update */
    private const short ALPHA_UP1 =  (short)((1.0 - 0.95)*short.MaxValue);       /* Normal update, upwards:   */
    private const short ALPHA_DOWN1 = (short)((1.0 - 0.936) * short.MaxValue);   /* Normal update, downwards  */
    private const short ALPHA_UP2 = (short)((1.0 - 0.985) * short.MaxValue);     /* Forced update, upwards    */
    private const short ALPHA_DOWN2 = (short)((1.0 - 0.943) * short.MaxValue);   /* Forced update, downwards  */
    private const short ALPHA3 = (short)((1.0 - 0.95) * short.MaxValue);       /* Update downwards          */
    private const short ALPHA4 = (short)((1.0 - 0.9) * short.MaxValue);        /* For stationary estimation */
    private const short ALPHA5 = (short)((1.0 - 0.5) * short.MaxValue);        /* For stationary estimation */

    /* Constants for VAD threshold */
    private const short THR_MIN = (short)(1.6 * SCALE);       /* Minimum threshold               */
    private const short THR_HIGH = (short)(6 * SCALE);        /* Highest threshold               */
    private const short THR_LOW = (short)(1.7 * SCALE);       /* Lowest threshold               */
    private const short NO_P1 = 31744;                        /* ilog2(1), Noise level for highest threshold */
    private const short NO_P2 = 19786;                        /* ilog2(0.1*MAX_16), Noise level for lowest threshold */
    private const short NO_SLOPE = (short)(short.MaxValue * (float)(THR_LOW - THR_HIGH) / (float)(NO_P2 - NO_P1));

    private const short SP_CH_MIN = (short)(-0.75 * SCALE);
    private const short SP_CH_MAX = (short)(0.75 * SCALE);
    private const short SP_P1 = 22527;                        /* ilog2(NOM_LEVEL/4) */
    private const short SP_P2 = 17832;                        /* ilog2(NOM_LEVEL*4) */
    private const short SP_SLOPE = (short)(short.MaxValue * (float)(SP_CH_MAX - SP_CH_MIN) / (float)(SP_P2 - SP_P1));

    /* Constants for hangover length */
    private const short HANG_HIGH = 12;                      /* longest hangover               */
    private const short HANG_LOW = 2;                        /* shortest hangover               */
    private const short HANG_P1 = THR_LOW;                   /* threshold for longest hangover */
    private const short HANG_P2 = (short)(4 * SCALE);        /* threshold for shortest hangover */
    private const short HANG_SLOPE = (short)(short.MaxValue * (float)(HANG_LOW - HANG_HIGH) / (float)(HANG_P2 - HANG_P1));

    /* Constants for burst length */
    private const short BURST_HIGH = 8;                       /* longest burst length         */
    private const short BURST_LOW = 3;                        /* shortest burst length        */
    private const short BURST_P1 = THR_HIGH;                  /* threshold for longest burst */
    private const short BURST_P2 = THR_LOW;                   /* threshold for shortest burst */
    private const short BURST_SLOPE = (short)(short.MaxValue * (float)(BURST_LOW - BURST_HIGH) / (float)(BURST_P2 - BURST_P1));

    /* Parameters for background spectrum recovery function */
    private const short STAT_COUNT = 20;                      /* threshold of stationary detection counter         */

    private const short STAT_THR_LEVEL = 184;                 /* Threshold level for stationarity detection        */
    private const short STAT_THR = 1000;                      /* Threshold for stationarity detection              */

    /* Limits for background noise estimate */
    private const short NOISE_MIN = 40;                       /* minimum */
    private const short NOISE_MAX = 20000;                    /* maximum */
    private const short NOISE_INIT = 150;                     /* initial */

    /* Thresholds for signal power (now calculated on 2 frames) */
    private const int VAD_POW_LOW = (int)30000;         /* If input power is lower than this, VAD is set to 0 */
    private const int POW_TONE_THR = (int)686080;       /* If input power is lower,tone detection flag is ignored */

    /* Constants for the filter bank */
    private const short COEFF3 = 13363;                     /* coefficient for the 3rd order filter     */
    private const short COEFF5_1 = 21955;                   /* 1st coefficient the for 5th order filter */
    private const short COEFF5_2 = 6390;                    /* 2nd coefficient the for 5th order filter */
    private const short F_5TH_CNT = 5;                      /* number of 5th order filters */
    private const short F_3TH_CNT = 6;                      /* number of 3th order filters */

    /******************************************************************************
     *                         DEFINITION OF DATA TYPES
     ******************************************************************************/

    private class VadVars
    {
        public short[] bckr_est = new short[COMPLEN];              /* background noise estimate                */
        public short[] ave_level = new short[COMPLEN];             /* averaged input components for stationary */
        /* estimation                               */
        public short[] old_level = new short[COMPLEN];             /* input levels of the previous frame       */
        public short[] sub_level = new short[COMPLEN];             /* input levels calculated at the end of a frame (lookahead)  */

        //short a_data5[F_5TH_CNT][2];          /* memory for the filter bank               */
        public short[,] a_data5 = new short[F_5TH_CNT,2];  /* memory for the filter bank               */

        //short a_data3[F_3TH_CNT];             /* memory for the filter bank               */
        public short[] a_data3 = new short[F_3TH_CNT]; /* memory for the filter bank               */

        public short burst_count;                    /* counts length of a speech burst          */
        public short hang_count;                     /* hangover counter                         */
        public short stat_count;                     /* stationary counter                       */

        /* Note that each of the following two variables holds 15 flags. Each flag reserves 1 bit of the
         * variable. The newest flag is in the bit 15 (assuming that LSB is bit 1 and MSB is bit 16). */
        public short vadreg;                         /* flags for intermediate VAD decisions     */
        public short tone_flag;                      /* tone detection flags                     */

        public short sp_est_cnt;                     /* counter for speech level estimation      */
        public short sp_max;                         /* maximum level                            */
        public short sp_max_cnt;                     /* counts frames that contains speech       */
        public short speech_level;                   /* estimated speech level                   */
        public int prev_pow_sum;                     /* power of previous frame                  */

        public VadVars()
        {
        }
    }

    /******************************************************************************
    * log2
    *
    *  Calculate Log2 and scale the signal:
    *
    *    ilog2(int in) = -1024*log10(in * 2^-31)/log10(2), where in = [1, 2^31-1]
    *
    *  input   output
    *  32768   16384
    *  1       31744
    *
    * When input is in the range of [1,2^16], max error is 0.0380%.
    *
    *
    */

    private short ilog2(                              /* return: output value of the log2 */
         short mant                           /* i: value to be converted */
    )
    {
        short i, ex, ex2, res;
        int l_temp;

        if (mant <= 0)
        {
            mant = 1;
        }
        ex = norm_s(mant);
        mant = shl(mant, ex);

        for (i = 0; i < 3; i++)
            mant = mult(mant, mant);
        l_temp = L_mult(mant, mant);

        ex2 = norm_l(l_temp);
        mant = extract_h(L_shl(l_temp, ex2));

        res = shl(add(ex, 16), 10);
        res = add(res, shl(ex2, 6));
        res = sub(add(res, 127), shr(mant, 8));
        return (res);
    }

    /******************************************************************************
    *
    *     Function     : filter5
    *     Purpose      : Fifth-order half-band lowpass/highpass filter pair with
    *                    decimation.
    *
    */
//    private void filter5(
//         ref short in0,                         /* i/o : input values; output low-pass part  */
//         ref short in1,                         /* i/o : input values; output high-pass part */
//         short[] data                         /* i/o : filter memory                       */
//)
//    {
//        short temp0, temp1, temp2;

//        temp0 = sub(in0, mult(COEFF5_1, data[0]));
//        temp1 = add(data[0], mult(COEFF5_1, temp0));
//        data[0] = temp0; 

//        temp0 = sub(in1, mult(COEFF5_2, data[1]));
//        temp2 = add(data[1], mult(COEFF5_2, temp0));
//        data[1] = temp0; 

//        in0 = extract_h(L_shl(L_add(temp1, temp2), 15)); 
//        in1 = extract_h(L_shl(L_sub(temp1, temp2), 15)); 
//    }

    private void filter5(
         ref short in0,                         /* i/o : input values; output low-pass part  */
         ref short in1,                         /* i/o : input values; output high-pass part */
         short[,] data,                          /* i/o : filter memory                       */
         int DataRowIndex       // 11 Jan 24 PHR
)
    {
        short temp0, temp1, temp2;

        temp0 = sub(in0, mult(COEFF5_1, data[DataRowIndex,0]));
        temp1 = add(data[DataRowIndex,0], mult(COEFF5_1, temp0));
        data[DataRowIndex,0] = temp0;

        temp0 = sub(in1, mult(COEFF5_2, data[DataRowIndex,1]));
        temp2 = add(data[DataRowIndex,1], mult(COEFF5_2, temp0));
        data[DataRowIndex,1] = temp0;

        in0 = extract_h(L_shl(L_add(temp1, temp2), 15));
        in1 = extract_h(L_shl(L_sub(temp1, temp2), 15));
    }


    /******************************************************************************
    *
    *     Function     : filter3
    *     Purpose      : Third-order half-band lowpass/highpass filter pair with
    *                    decimation.
    *
    */
    private void filter3(
         ref short in0,                         /* i/o : input values; output low-pass part  */
         ref short in1,                         /* i/o : input values; output high-pass part */
         ref short data                         /* i/o : filter memory                       */
    )
    {
        short temp1, temp2;

        temp1 = sub(in1, mult(COEFF3, data));
        temp2 = add(data, mult(COEFF3, temp1));
        data = temp1; 

        in1 = extract_h(L_shl(L_sub(in0, temp2), 15)); 
        in0 = extract_h(L_shl(L_add(in0, temp2), 15)); 
    }

    /******************************************************************************
    *
    *     Function   : level_calculation
    *     Purpose    : Calculate signal level in a sub-band. Level is calculated
    *                  by summing absolute values of the input data.
    *
    *                  Signal level calculated from of the end of the frame
    *                  (data[count1 - count2]) is stored to (*sub_level)
    *                  and added to the level of the next frame.
    *
    */
    private short level_calculation(           /* return: signal level */
         short[] data,                        /* i   : signal buffer                                    */
         ref short sub_level,                   /* i   : level calculated at the end of the previous frame*/
         /* o   : level of signal calculated from the last         */
         /*       (count2 - count1) samples                        */
         short count1,                        /* i   : number of samples to be counted                  */
         short count2,                        /* i   : number of samples to be counted                  */
         short ind_m,                         /* i   : step size for the index of the data buffer       */
         short ind_a,                         /* i   : starting index of the data buffer                */
         short scale                          /* i   : scaling for the level calculation                */
    )
    {
        int l_temp1, l_temp2;
        short level, i;

        l_temp1 = 0;
        for (i = count1; i < count2; i++)
        {
            l_temp1 = L_mac(l_temp1, 1, abs_s(data[ind_m * i + ind_a]));
        }

        l_temp2 = L_add(l_temp1, L_shl(sub_level, sub(16, scale)));
        sub_level = extract_h(L_shl(l_temp1, scale)); 

        for (i = 0; i < count1; i++)
        {
            l_temp2 = L_mac(l_temp2, 1, abs_s(data[ind_m * i + ind_a]));
        }
        level = extract_h(L_shl(l_temp2, scale));

        return level;
    }

    /******************************************************************************
    *
    *     Function     : filter_bank
    *     Purpose      : Divide input signal into bands and calculate level of
    *                    the signal in each band
    *
    */
    private void filter_bank(
         VadVars st,                       /* i/o : State struct               */
         short[] inf,                          /* i   : input frame                */
         short[] level                         /* 0   : signal levels at each band */
)
    {
        short i;
        short[] tmp_buf = new short[FRAME_LEN];

        /* shift input 1 bit down for safe scaling */
        for (i = 0; i < FRAME_LEN; i++)
        {
            tmp_buf[i] = shr(inf[i], 1); 
        }

        /* run the filter bank */
        for (i = 0; i < FRAME_LEN / 2; i++)
        {
            //filter5(&tmp_buf[2 * i], &tmp_buf[2 * i + 1], st.a_data5[0]);
            filter5(ref tmp_buf[2 * i], ref tmp_buf[2 * i + 1], st.a_data5, 0);
        }
        for (i = 0; i < FRAME_LEN / 4; i++)
        {
            //filter5(&tmp_buf[4 * i], &tmp_buf[4 * i + 2], st.a_data5[1]);
            filter5(ref tmp_buf[4 * i], ref tmp_buf[4 * i + 2], st.a_data5, 1);
            //filter5(&tmp_buf[4 * i + 1], &tmp_buf[4 * i + 3], st.a_data5[2]);
            filter5(ref tmp_buf[4 * i + 1], ref tmp_buf[4 * i + 3], st.a_data5, 2);
        }
        for (i = 0; i < FRAME_LEN / 8; i++)
        {
            //filter5(&tmp_buf[8 * i], &tmp_buf[8 * i + 4], st.a_data5[3]);
            filter5(ref tmp_buf[8 * i], ref tmp_buf[8 * i + 4], st.a_data5, 3);

            //filter5(&tmp_buf[8 * i + 2], &tmp_buf[8 * i + 6], st.a_data5[4]);
            filter5(ref tmp_buf[8 * i + 2], ref tmp_buf[8 * i + 6], st.a_data5, 4);

            //filter3(&tmp_buf[8 * i + 3], &tmp_buf[8 * i + 7], &st.a_data3[0]);
            filter3(ref tmp_buf[8 * i + 3], ref tmp_buf[8 * i + 7], ref st.a_data3[0]);
        }
        for (i = 0; i < FRAME_LEN / 16; i++)
        {
            filter3(ref tmp_buf[16 * i + 0], ref tmp_buf[16 * i + 8], ref st.a_data3[1]);
            filter3(ref tmp_buf[16 * i + 4], ref tmp_buf[16 * i + 12], ref st.a_data3[2]);
            filter3(ref tmp_buf[16 * i + 6], ref tmp_buf[16 * i + 14], ref st.a_data3[3]);
        }

        for (i = 0; i < FRAME_LEN / 32; i++)
        {
            filter3(ref tmp_buf[32 * i + 0], ref tmp_buf[32 * i + 16], ref st.a_data3[4]);
            filter3(ref tmp_buf[32 * i + 8], ref tmp_buf[32 * i + 24], ref st.a_data3[5]);
        }

        /* calculate levels in each frequency band */

        /* 4800 - 6400 Hz */
        level[11] = level_calculation(tmp_buf, ref st.sub_level[11],
            FRAME_LEN / 4 - 48, FRAME_LEN / 4, 4, 1, 14); 
        /* 4000 - 4800 Hz */
        level[10] = level_calculation(tmp_buf, ref st.sub_level[10],
            FRAME_LEN / 8 - 24, FRAME_LEN / 8, 8, 7, 15); 
        /* 3200 - 4000 Hz */
        level[9] = level_calculation(tmp_buf, ref st.sub_level[9],
            FRAME_LEN / 8 - 24, FRAME_LEN / 8, 8, 3, 15); 
        /* 2400 - 3200 Hz */
        level[8] = level_calculation(tmp_buf, ref st.sub_level[8],
            FRAME_LEN / 8 - 24, FRAME_LEN / 8, 8, 2, 15); 
        /* 2000 - 2400 Hz */
        level[7] = level_calculation(tmp_buf, ref st.sub_level[7],
            FRAME_LEN / 16 - 12, FRAME_LEN / 16, 16, 14, 16); 
        /* 1600 - 2000 Hz */
        level[6] = level_calculation(tmp_buf, ref st.sub_level[6],
            FRAME_LEN / 16 - 12, FRAME_LEN / 16, 16, 6, 16); 
        /* 1200 - 1600 Hz */
        level[5] = level_calculation(tmp_buf, ref st.sub_level[5],
            FRAME_LEN / 16 - 12, FRAME_LEN / 16, 16, 4, 16); 
        /* 800 - 1200 Hz */
        level[4] = level_calculation(tmp_buf, ref st.sub_level[4],
            FRAME_LEN / 16 - 12, FRAME_LEN / 16, 16, 12, 16); 
        /* 600 - 800 Hz */
        level[3] = level_calculation(tmp_buf, ref st.sub_level[3],
            FRAME_LEN / 32 - 6, FRAME_LEN / 32, 32, 8, 17); 
        /* 400 - 600 Hz */
        level[2] = level_calculation(tmp_buf, ref st.sub_level[2],
            FRAME_LEN / 32 - 6, FRAME_LEN / 32, 32, 24, 17); 
        /* 200 - 400 Hz */
        level[1] = level_calculation(tmp_buf, ref st.sub_level[1],
            FRAME_LEN / 32 - 6, FRAME_LEN / 32, 32, 16, 17); 
        /* 0 - 200 Hz */
        level[0] = level_calculation(tmp_buf, ref st.sub_level[0],
            FRAME_LEN / 32 - 6, FRAME_LEN / 32, 32, 0, 17); 
    }

    /******************************************************************************
    *
    *     Function   : update_cntrl
    *     Purpose    : Control update of the background noise estimate.
    *
    */
    private void update_cntrl(
         ref VadVars st,                         /* i/o : State structure                    */
         short[] level                        /* i   : sub-band levels of the input frame */
)
    {
        short i, temp, stat_rat, exp;
        short num, denom;
        short alpha;

        /* if a tone has been detected for a while, initialize stat_count */
        if (sub((short)(st.tone_flag & 0x7c00), 0x7c00) == 0)
        {
            st.stat_count = STAT_COUNT; 
        }
        else
        {
            /* if 8 last vad-decisions have been "0", reinitialize stat_count */
            if ((st.vadreg & 0x7f80) == 0)
            {
                st.stat_count = STAT_COUNT; 
            }
            else
            {
                stat_rat = 0; 
                for (i = 0; i < COMPLEN; i++)
                {
                    if (sub(level[i], st.ave_level[i]) > 0)
                    {
                        num = level[i]; 
                        denom = st.ave_level[i]; 
                    }
                    else
                    {
                        num = st.ave_level[i]; 
                        denom = level[i]; 
                    }
                    /* Limit nimimum value of num and denom to STAT_THR_LEVEL */
                    if (sub(num, STAT_THR_LEVEL) < 0)
                    {
                        num = STAT_THR_LEVEL; 
                    }
                    if (sub(denom, STAT_THR_LEVEL) < 0)
                    {
                        denom = STAT_THR_LEVEL; 
                    }
                    exp = norm_s(denom);
                    denom = shl(denom, exp);

                    /* stat_rat = num/denom * 64 */
                    temp = div_s(shr(num, 1), denom);
                    stat_rat = add(stat_rat, shr(temp, sub(8, exp)));
                }

                /* compare stat_rat with a threshold and update stat_count */
                if (sub(stat_rat, STAT_THR) > 0)
                {
                    st.stat_count = STAT_COUNT; 
                }
                else
                {
                    if ((st.vadreg & 0x4000) != 0)
                    {
                        if (st.stat_count != 0)
                        {
                            st.stat_count = sub(st.stat_count, 1); 
                        }
                    }
                }
            }
        }

        /* Update average amplitude estimate for stationarity estimation */
        alpha = ALPHA4; 
        if (sub(st.stat_count, STAT_COUNT) == 0)
        {
            alpha = 32767; 
        }
        else if ((st.vadreg & 0x4000) == 0)
        {
            alpha = ALPHA5; 
        }
        for (i = 0; i < COMPLEN; i++)
        {
            st.ave_level[i] = add(st.ave_level[i],
                mult_r(alpha, sub(level[i], st.ave_level[i]))); 
        }
    }

    /******************************************************************************
    *
    *     Function     : hangover_addition
    *     Purpose      : Add hangover after speech bursts
    *
    */

    private short hangover_addition(           /* return: VAD_flag indicating final VAD decision */
         ref VadVars st,                         /* i/o : State structure                     */
         short low_power,                     /* i   : flag power of the input frame    */
         short hang_len,                      /* i   : hangover length */
         short burst_len                      /* i   : minimum burst length for hangover addition */
    )
    {
        /* if the input power (pow_sum) is lower than a threshold, clear counters and set VAD_flag to "0"         */
        if (low_power != 0)
        {
            st.burst_count = 0; 
            st.hang_count = 0; 
            return 0;
        }
        /* update the counters (hang_count, burst_count) */
        if ((st.vadreg & 0x4000) != 0)
        {
            st.burst_count = add(st.burst_count, 1); 
            if (sub(st.burst_count, burst_len) >= 0)
            {
                st.hang_count = hang_len; 
            }
            return 1;
        }
        else
        {
            st.burst_count = 0; 
            if (st.hang_count > 0)
            {
                st.hang_count = sub(st.hang_count, 1); 
                return 1;
            }
        }
        return 0;
    }

    /******************************************************************************
    *
    *     Function   : noise_estimate_update
    *     Purpose    : Update of background noise estimate
    *
    */

    private void noise_estimate_update(
         ref VadVars st,                      /* i/o : State structure                       */
         short[] level                        /* i   : sub-band levels of the input frame */
)
    {
        short i, alpha_up, alpha_down, bckr_add;

        /* Control update of bckr_est[] */
        update_cntrl(ref st, level);

        /* Reason for using bckr_add is to avoid problems caused by fixed-point dynamics when noise level and
         * required change is very small. */
        bckr_add = 2; 

        /* Choose update speed */
        if ((0x7800 & st.vadreg) == 0)
        {
            alpha_up = ALPHA_UP1; 
            alpha_down = ALPHA_DOWN1; 
        }
        else
        {
            if ((st.stat_count == 0))
            {
                alpha_up = ALPHA_UP2; 
                alpha_down = ALPHA_DOWN2; 
            }
            else
            {
                alpha_up = 0; 
                alpha_down = ALPHA3; 
                bckr_add = 0; 
            }
        }

        /* Update noise estimate (bckr_est) */
        for (i = 0; i < COMPLEN; i++)
        {
            short temp;

            temp = sub(st.old_level[i], st.bckr_est[i]);

            if (temp < 0)
            {                                  /* update downwards */
                st.bckr_est[i] = add(-2, add(st.bckr_est[i],
                        mult_r(alpha_down, temp))); 

                /* limit minimum value of the noise estimate to NOISE_MIN */
                if (sub(st.bckr_est[i], NOISE_MIN) < 0)
                {
                    st.bckr_est[i] = NOISE_MIN; 
                }
            }
            else
            {                                  /* update upwards */
                st.bckr_est[i] = add(bckr_add, add(st.bckr_est[i],
                        mult_r(alpha_up, temp))); 

                /* limit maximum value of the noise estimate to NOISE_MAX */
                if (sub(st.bckr_est[i], NOISE_MAX) > 0)
                {
                    st.bckr_est[i] = NOISE_MAX; 
                }
            }
        }

        /* Update signal levels of the previous frame (old_level) */
        for (i = 0; i < COMPLEN; i++)
        {
            st.old_level[i] = level[i]; 
        }
    }

    /******************************************************************************
    *
    *     Function     : vad_decision
    *     Purpose      : Calculates VAD_flag
    *
    */

    private short vad_decision(                /* return value : VAD_flag */
         ref VadVars st,                         /* i/o : State structure                       */
         short[] level,                /* i   : sub-band levels of the input frame */
         int pow_sum                        /* i   : power of the input frame           */
    )
    {
        short i;
        int L_snr_sum;
        int L_temp;
        short vad_thr, temp, noise_level;
        short low_power_flag;
        short hang_len, burst_len;
        short ilog2_speech_level, ilog2_noise_level;
        short temp2;

        /* Calculate squared sum of the input levels (level) divided by the background noise components
         * (bckr_est). */
        L_snr_sum = 0;
        for (i = 0; i < COMPLEN; i++)
        {
            short exp;

            exp = norm_s(st.bckr_est[i]);
            temp = shl(st.bckr_est[i], exp);
            temp = div_s(shr(level[i], 1), temp);
            temp = shl(temp, sub(exp, UNIRSHFT - 1));
            L_snr_sum = L_mac(L_snr_sum, temp, temp);
        }

        /* Calculate average level of estimated background noise */
        L_temp = 0;
        for (i = 1; i < COMPLEN; i++)          /* ignore lowest band */
        {
            L_temp = L_add(L_temp, st.bckr_est[i]);
        }

        noise_level = extract_h(L_shl(L_temp, 12));
        /* if SNR is lower than a threshold (MIN_SPEECH_SNR), and increase speech_level */
        temp = shl(mult(noise_level, MIN_SPEECH_SNR), 3);

        if (sub(st.speech_level, temp) < 0)
        {
            st.speech_level = temp; 
        }
        ilog2_noise_level = ilog2(noise_level);

        /* If SNR is very poor, speech_level is probably corrupted by noise level. This is correctred by
         * subtracting MIN_SPEECH_SNR*noise_level from speech level */
        ilog2_speech_level = ilog2(sub(st.speech_level, temp));

        temp = add(mult(NO_SLOPE, sub(ilog2_noise_level, NO_P1)), THR_HIGH);

        temp2 = add(SP_CH_MIN, mult(SP_SLOPE, sub(ilog2_speech_level, SP_P1)));
        if (sub(temp2, SP_CH_MIN) < 0)
        {
            temp2 = SP_CH_MIN; 
        }
        if (sub(temp2, SP_CH_MAX) > 0)
        {
            temp2 = SP_CH_MAX; 
        }
        vad_thr = add(temp, temp2);

        if (sub(vad_thr, THR_MIN) < 0)
        {
            vad_thr = THR_MIN; 
        }
        /* Shift VAD decision register */
        st.vadreg = shr(st.vadreg, 1); 

        /* Make intermediate VAD decision */
        if (L_sub(L_snr_sum, L_mult(vad_thr, 512 * COMPLEN)) > 0)
        {
            st.vadreg = (short)(st.vadreg | 0x4000);
        }
        /* check if the input power (pow_sum) is lower than a threshold" */
        if (L_sub(pow_sum, VAD_POW_LOW) < 0)
        {
            low_power_flag = 1; 
        }
        else
        {
            low_power_flag = 0; 
        }
        /* Update background noise estimates */
        noise_estimate_update(ref st, level);

        /* Calculate values for hang_len and burst_len based on vad_thr */
        hang_len = add(mult(HANG_SLOPE, sub(vad_thr, HANG_P1)), HANG_HIGH);
        if (sub(hang_len, HANG_LOW) < 0)
        {
            hang_len = HANG_LOW; 
        };

        burst_len = add(mult(BURST_SLOPE, sub(vad_thr, BURST_P1)), BURST_HIGH);

        return (hangover_addition(ref st, low_power_flag, hang_len, burst_len));
    }

    /******************************************************************************
    *
    *     Estimate_Speech()
    *     Purpose      : Estimate speech level
    *
    * Maximum signal level is searched and stored to the variable sp_max.
    * The speech frames must locate within SP_EST_COUNT number of frames.
    * Thus, noisy frames having occasional VAD = "1" decisions will not
    * affect to the estimated speech_level.
    *
    */
    private void Estimate_Speech(
         VadVars st,                         /* i/o : State structure    */
         short in_level                       /* level of the input frame */
    )
    {
        short alpha;

        /* if the required activity count cannot be achieved, reset counters */
        /* if (SP_ACTIVITY_COUNT  > SP_EST_COUNT - st.sp_est_cnt + st.sp_max_cnt) */
        if (sub(sub(st.sp_est_cnt, st.sp_max_cnt), SP_EST_COUNT - SP_ACTIVITY_COUNT) > 0)
        {
            st.sp_est_cnt = 0; 
            st.sp_max = 0; 
            st.sp_max_cnt = 0; 
        }
        st.sp_est_cnt = add(st.sp_est_cnt, 1); 

        if (((st.vadreg & 0x4000) != 0 || (sub(in_level, st.speech_level) > 0))
            && (sub(in_level, MIN_SPEECH_LEVEL1) > 0))
        {
            /* update sp_max */
            if (sub(in_level, st.sp_max) > 0)
            {
                st.sp_max = in_level; 
            }
            st.sp_max_cnt = add(st.sp_max_cnt, 1); 
            if (sub(st.sp_max_cnt, SP_ACTIVITY_COUNT) >= 0)
            {
                short tmp;

                /* update speech estimate */
                tmp = shr(st.sp_max, 1);      /* scale to get "average" speech level */

                /* select update speed */
                if (sub(tmp, st.speech_level) > 0)
                {
                    alpha = ALPHA_SP_UP; 
                }
                else
                {
                    alpha = ALPHA_SP_DOWN; 
                }
                if (sub(tmp, MIN_SPEECH_LEVEL2) > 0)
                {
                    st.speech_level = add(st.speech_level,
                        mult_r(alpha, sub(tmp, st.speech_level))); 
                }
                /* clear all counters used for speech estimation */
                st.sp_max = 0; 
                st.sp_max_cnt = 0; 
                st.sp_est_cnt = 0; 
            }
        }
    }

    /******************************************************************************
    *                         PUBLIC PROGRAM CODE
    ******************************************************************************/

    /******************************************************************************
    *
    *  Function:   wb_vad_init
    *  Purpose:    Allocates state memory and initializes state memory
    *
    */

    short wb_vad_init(                        /* return: non-zero with error, zero for ok. */
         ref VadVars state)                      /* i/o : State structure    */
    {
        state = new VadVars();
        wb_vad_reset(state);
        return 0;
    }

    /******************************************************************************
    *
    *  Function:   wb_vad_reset
    *  Purpose:    Initializes state memory
    *
    */
    short wb_vad_reset(                       /* return: non-zero with error, zero for ok. */
         VadVars state)                       /* i/o : State structure    */
    {
        short i, j;
        state.tone_flag = 0;
        state.vadreg = 0;
        state.hang_count = 0;
        state.burst_count = 0;
        state.hang_count = 0;

        /* initialize memory used by the filter bank */
        for (i = 0; i < F_5TH_CNT; i++)
        {
            for (j = 0; j < 2; j++)
            {
                state.a_data5[i,j] = 0;
            }
        }

        for (i = 0; i < F_3TH_CNT; i++)
        {
            state.a_data3[i] = 0;
        }

        /* initialize the rest of the memory */
        for (i = 0; i < COMPLEN; i++)
        {
            state.bckr_est[i] = NOISE_INIT;
            state.old_level[i] = NOISE_INIT;
            state.ave_level[i] = NOISE_INIT;
            state.sub_level[i] = 0;
        }

        state.sp_est_cnt = 0;
        state.sp_max = 0;
        state.sp_max_cnt = 0;
        state.speech_level = SPEECH_LEVEL_INIT;
        state.prev_pow_sum = 0;
        return 0;
    }

    /******************************************************************************
    *
    *     Function     : wb_vad_tone_detection
    *     Purpose      : Search maximum pitch gain from a frame. Set tone flag if
    *                    pitch gain is high. This is used to detect
    *                    signaling tones and other signals with high pitch gain.
    *
    */
    void wb_vad_tone_detection(
         VadVars st,                         /* i/o : State struct            */
         short p_gain                         /* pitch gain      */
    )
    {
        /* update tone flag */
        st.tone_flag = shr(st.tone_flag, 1); 

        /* if (pitch_gain > TONE_THR) set tone flag */
        if (sub(p_gain, TONE_THR) > 0)
        {
            st.tone_flag = (short)(st.tone_flag | 0x4000);
        }
    }

    /******************************************************************************
    *
    *     Function     : wb_vad
    *     Purpose      : Main program for Voice Activity Detection (VAD) for AMR
    *
    */
    short wb_vad(                             /* Return value : VAD Decision, 1 = speech, 0 = noise */
         VadVars st,                         /* i/o : State structure                 */
         short[] in_buf                       /* i   : samples of the input frame   */
)
    {
        short[] level = new short[COMPLEN];
        short i;
        short VAD_flag, temp;
        int L_temp, pow_sum;

        /* Calculate power of the input frame. */
        L_temp = 0;
        for (i = 0; i < FRAME_LEN; i++)
        {
            L_temp = L_mac(L_temp, in_buf[i], in_buf[i]);
        }

        /* pow_sum = power of current frame and previous frame */
        pow_sum = L_add(L_temp, st.prev_pow_sum);

        /* save power of current frame for next call */
        st.prev_pow_sum = L_temp;

        /* If input power is very low, clear tone flag */
        if (L_sub(pow_sum, POW_TONE_THR) < 0)
        {
            st.tone_flag = (short)(st.tone_flag & 0x1fff);
        }
        /* Run the filter bank and calculate signal levels at each band */
        filter_bank(st, in_buf, level);

        /* compute VAD decision */
        VAD_flag = vad_decision(ref st, level, pow_sum);

        /* Calculate input level */
        L_temp = 0;
        for (i = 1; i < COMPLEN; i++)          /* ignore lowest band */
        {
            L_temp = L_add(L_temp, level[i]);
        }

        temp = extract_h(L_shl(L_temp, 12));

        Estimate_Speech(st, temp);             /* Estimate speech level */
        return (VAD_flag);
    }
}
