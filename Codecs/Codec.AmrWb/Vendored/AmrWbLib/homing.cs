/*------------------------------------------------------------------------*
 *                         HOMING.C                                       *
 *------------------------------------------------------------------------*
 * Performs the homing routines                                           *
 *------------------------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    /*-----------------------------------------------------*
     | Tables for homing                   |
     *-----------------------------------------------------*/
    private const int DHF_PARMS_MAX = 32; /* homing frame pattern             */
    private const int NUM_OF_SPMODES = 9;

    private const int PRML = 15;
    private const int PRMN_7k = NBBITS_7k / PRML + 1;
    private const int PRMN_9k = NBBITS_9k / PRML + 1;
    private const int PRMN_12k = NBBITS_12k / PRML + 1;
    private const int PRMN_14k = NBBITS_14k / PRML + 1;
    private const int PRMN_16k = NBBITS_16k / PRML + 1;
    private const int PRMN_18k = NBBITS_18k / PRML + 1;
    private const int PRMN_20k = NBBITS_20k / PRML + 1;
    private const int PRMN_23k = NBBITS_23k / PRML + 1;
    private const int PRMN_24k = NBBITS_24k / PRML + 1;

    private static short[] prmnofsf = new short[NUM_OF_SPMODES]
    {
        63,  81, 100,
       108, 116, 128,
       136, 152, 156
    };

    private static short[] dfh_M7k = new short[PRMN_7k]
    {
      3168, 29954, 29213, 16121,
        64, 13440, 30624, 16430,
     19008
    };

    private static short[] dfh_M9k = new short[PRMN_9k]
    {
       3168, 31665,  9943, 9123,
      15599,  4358, 20248, 2048,
      17040, 27787, 16816, 13888
    };

    private static short[] dfh_M12k = new short[PRMN_12k]
    {
      3168, 31665,  9943,  9128,
      3647,  8129, 30930, 27926,
     18880, 12319,   496,  1042,
      4061, 20446, 25629, 28069,
     13948
    };

    private static short[] dfh_M14k = new short[PRMN_14k]
    {
        3168, 31665,  9943,  9131,
       24815,   655, 26616, 26764,
        7238, 19136,  6144,    88,
        4158, 25733, 30567, 30494,
        221, 20321, 17823,
        0       // 1 May 25 PHR -- 1 element too short for initialization
    };

    private static short[] dfh_M16k = new short[PRMN_16k]
    {
        3168, 31665,  9943,  9131,
       24815,   700,  3824,  7271,
       26400,  9528,  6594, 26112,
         108,  2068, 12867, 16317,
       23035, 24632,  7528,  1752,
        6759, 24576
    };

    private static short[] dfh_M18k = new short[PRMN_18k]
    {
         3168, 31665,  9943,  9135,
        14787, 14423, 30477, 24927,
        25345, 30154,   916,  5728,
        18978,  2048,   528, 16449,
         2436,  3581, 23527, 29479,
         8237, 16810, 27091, 19052,
            0
    };

    private static short[] dfh_M20k = new short[PRMN_20k]
    {
         3168, 31665,  9943,  9129,
         8637, 31807, 24646,   736,
        28643,  2977,  2566, 25564,
        12930, 13960,  2048,   834,
         3270,  4100, 26920, 16237,
        31227, 17667, 15059, 20589,
        30249, 29123, 0
    };

    private static short[] dfh_M23k = new short[PRMN_23k]
    {
         3168, 31665,  9943,  9132,
        16748,  3202, 28179, 16317,
        30590, 15857, 19960,  8818,
        21711, 21538,  4260, 16690,
        20224,  3666,  4194,  9497,
        16320, 15388,  5755, 31551,
        14080,  3574, 15932,    50,
        23392, 26053, 31216
    };

    private static short[] dfh_M24k = new short[PRMN_24k]
    {
         3168, 31665,  9943,  9134,
        24776,  5857, 18475, 28535,
        29662, 14321, 16725,  4396,
        29353, 10003, 17068, 20504,
          720,     0,  8465, 12581,
        28863, 24774,  9709, 26043,
         7941, 27649, 13965, 15236,
        18026, 22047, 16681,  3968
    };

    /* overall table with the parameters of the decoder homing frames for all modes */
    private static short[][] dhf = new short[][]
    {
      dfh_M7k,
      dfh_M9k,
      dfh_M12k,
      dfh_M14k,
      dfh_M16k,
      dfh_M18k,
      dfh_M20k,
      dfh_M23k,
      dfh_M24k,
      dfh_M24k
    };

    private short encoder_homing_frame_test(short[] input_frame)
    {
        short i, j = 0;

        /* check 320 input samples for matching EHF_MASK: defined in e_homing.h */
        for (i = 0; i < L_FRAME16k; i++)
        {
            j = (short)(input_frame[i] ^ EHF_MASK);

            if (j != 0)
                break;
        }

        //return (short)(!j);
        if (j == 0)
            return 1;
        else
            return 0;
    }

    private short dhf_test(short[] input_frame, short mode, short nparms)
    {
        short i, j, tmp, shift;
        short[] param = new short[DHF_PARMS_MAX]; ;
        short[] prms;

        prms = input_frame;
        j = 0;
        i = 0;

        int CurrentIndex = 0;
        if (sub(mode, MRDTX) != 0)
        {
            if (sub(mode, MODE_24k) != 0)
            {
                /* convert the received serial bits */
                tmp = sub(nparms, 15);
                while (sub(tmp, j) > 0)
                {
                    param[i] = Serial_parm(15, prms, ref CurrentIndex);
                    j = add(j, 15);
                    i = add(i, 1);
                }
                tmp = sub(nparms, j);
                param[i] = Serial_parm(tmp, prms, ref CurrentIndex);
                shift = sub(15, tmp);
                param[i] = shl(param[i], shift);
            }
            else
            {
                /*If mode is 23.85Kbit/s, remove high band energy bits */
                for (i = 0; i < 10; i++)
                {
                    param[i] = Serial_parm(15, prms, ref CurrentIndex);
                }
                param[10] = (short) (Serial_parm(15, prms, ref CurrentIndex) & 0x61FF);
                for (i = 11; i < 17; i++)
                {
                    param[i] = Serial_parm(15, prms, ref CurrentIndex);
                }
                param[17] = (short) (Serial_parm(15, prms, ref CurrentIndex) & 0xE0FF);
                for (i = 18; i < 24; i++)
                {
                    param[i] = Serial_parm(15, prms, ref CurrentIndex);
                }
                param[24] = (short) (Serial_parm(15, prms, ref CurrentIndex) & 0x7F0F);
                for (i = 25; i < 31; i++)
                {
                    param[i] = Serial_parm(15, prms, ref CurrentIndex);
                }
                tmp = Serial_parm(8, prms, ref CurrentIndex);
                param[31] = shl(tmp, 7);
                shift = 0;
            }

            /* check if the parameters matches the parameters of the corresponding decoder homing frame */
            tmp = i;
            j = 0;
            for (i = 0; i < tmp; i++)
            {
                j = (short)(param[i] ^ dhf[mode][i]);
                if (j != 0)
                    break;
            }
            tmp = 0x7fff;
            tmp = shr(tmp, shift);
            tmp = shl(tmp, shift);
            tmp = (short)(dhf[mode][i] & tmp);
            tmp = (short)(param[i] ^ tmp);
            j = (short)(j | tmp);

        }
        else
        {
            j = 1;
        }

        //return (short)(!j);
        if (j == 0)
            return 1;
        else
            return 0;
    }


    private short decoder_homing_frame_test(short[] input_frame, short mode)
    {
        /* perform test for COMPLETE parameter frame */
        return dhf_test(input_frame, mode, nb_of_bits[mode]);
    }


    private short decoder_homing_frame_test_first(short[] input_frame, short mode)
    {
        /* perform test for FIRST SUBFRAME of parameter frame ONLY */
        return dhf_test(input_frame, mode, prmnofsf[mode]);
    }
}
