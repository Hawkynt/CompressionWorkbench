/*-------------------------------------------------------------------*
 *                         D_GAIN2.C                   *
 *-------------------------------------------------------------------*
 * Decode the pitch and codebook gains                               *
 *-------------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "oper_32b.h"
//#include "math_op.h"
//#include "log2.h"
//#include "cnst.h"
//#include "acelp.h"
//#include "count.h"
//#include "q_gain2.tab"

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{

    private const int MEAN_ENER = 30;
    private const int PRED_ORDER = 4;

    private const int L_LTPHIST = 5;

    private static readonly short[] pdown_unusable = new short[7] { 32767, 31130, 29491, 24576, 7537, 1638, 328 };
    private static readonly short[] cdown_unusable = new short[7] { 32767, 16384, 8192, 8192, 8192, 4915, 3277 };
    private static readonly short[] pdown_usable = new short[7] { 32767, 32113, 31457, 24576, 7537, 1638, 328 };
    private static readonly short[] cdown_usable = new short[7] { 32767, 32113, 32113, 32113, 32113, 32113, 22938 };

    /* MA prediction coeff ={0.5, 0.4, 0.3, 0.2} in Q13 */
    private static readonly short[] pred = new short[PRED_ORDER] { 4096, 3277, 2458, 1638 };

    private unsafe void Init_D_gain2(
         short* mem                          /* output  :static memory (4 words)      */
    )
    {
        short i;

        /* 4nd order quantizer energy predictor (init to -14.0 in Q10) */
        mem[0] = -14336;   /* past_qua_en[0] */
        mem[1] = -14336;   /* past_qua_en[1] */
        mem[2] = -14336;   /* past_qua_en[2] */
        mem[3] = -14336;   /* past_qua_en[3] */

        mem[4] = 0;   /* *past_gain_pit  */
        mem[5] = 0;   /* *past_gain_code */
        mem[6] = 0;   /* *prev_gc */

        for (i = 0; i < 5; i++)
        {
            mem[i + 7] = 0;   /* pbuf[i] */
        }
        for (i = 0; i < 5; i++)
        {
            mem[i + 12] = 0;   /* gbuf[i] */
        }
        for (i = 0; i < 5; i++)
        {
            mem[i + 17] = 0;   /* pbuf2[i] */
        }
        mem[22] = 21845;                       /* seed */

        return;
    }

    private void Init_D_gain2(
         short[] mem                          /* output  :static memory (4 words)      */
    )
    {
        short i;

        /* 4nd order quantizer energy predictor (init to -14.0 in Q10) */
        mem[0] = -14336;   /* past_qua_en[0] */
        mem[1] = -14336;   /* past_qua_en[1] */
        mem[2] = -14336;   /* past_qua_en[2] */
        mem[3] = -14336;   /* past_qua_en[3] */

        mem[4] = 0;   /* *past_gain_pit  */
        mem[5] = 0;   /* *past_gain_code */
        mem[6] = 0;   /* *prev_gc */

        for (i = 0; i < 5; i++)
        {
            mem[i + 7] = 0;   /* pbuf[i] */
        }
        for (i = 0; i < 5; i++)
        {
            mem[i + 12] = 0;   /* gbuf[i] */
        }
        for (i = 0; i < 5; i++)
        {
            mem[i + 17] = 0;   /* pbuf2[i] */
        }
        mem[22] = 21845;                       /* seed */

        return;
    }

    private unsafe void D_gain2(
         short index,                        /* (i)     : index of quantization.      */
         short nbits,                        /* (i)     : number of bits (6 or 7)     */
         short[] code,                       /* (i) Q9  : Innovative vector.          */
         short L_subfr,                      /* (i)     : Subframe lenght.            */
         short* gain_pit,                   /* (o) Q14 : Pitch gain.                 */
         int* gain_cod,                   /* (o) Q16 : Code gain.                  */
         short bfi,                          /* (i)     : bad frame indicator         */
         short prev_bfi,                     /* (i)     : Previous BF indicator       */
         short state,                        /* (i)     : State of BFH                */
         short unusable_frame,               /* (i)     : UF indicator                */
         short vad_hist,                     /* (i)     : number of non-speech frames */
         short* mem                         /* (i/o)   : static memory (4 words)     */
    )
    {
        short* p;
        short* past_gain_pit, past_gain_code, past_qua_en, gbuf, pbuf, prev_gc;
        short* pbuf2, seed;
        short i, tmp, exp, frac, gcode0, exp_gcode0, qua_ener, gcode_inov;
        short g_code;
        int L_tmp;

        past_qua_en = mem; 
        past_gain_pit = mem + 4; 
        past_gain_code = mem + 5; 
        prev_gc = mem + 6; 
        pbuf = mem + 7; 
        gbuf = mem + 12; 
        pbuf2 = mem + 17; 
        seed = mem + 22;

        // 17 May 25 PHR
        short* p_t_qua_gain6b = stackalloc short[t_qua_gain6b.Length];
        Copy(t_qua_gain6b, p_t_qua_gain6b, (short) t_qua_gain6b.Length);
        short* p_t_qua_gain7b = stackalloc short[t_qua_gain7b.Length];
        Copy(t_qua_gain7b, p_t_qua_gain7b, (short) t_qua_gain7b.Length);


        /*-----------------------------------------------------------------*
         *  Find energy of code and compute:                               *
         *                                                                 *
         *    L_tmp = 1.0 / sqrt(energy of code/ L_subfr)                  *
         *-----------------------------------------------------------------*/
        exp = 0;
        L_tmp = Dot_product12(code, code, L_subfr, ref exp);
        exp = sub(exp, 18 + 6);                /* exp: -18 (code in Q9), -6 (/L_subfr) */

        Isqrt_n(ref L_tmp, ref exp);

        gcode_inov = extract_h(L_shl(L_tmp, sub(exp, 3)));  /* g_code_inov in Q12 */

        /*-------------------------------*
         * Case of erasure.              *
         *-------------------------------*/
        if (bfi != 0)
        {
            tmp = median5(&pbuf[2]);
            *past_gain_pit = tmp;
            
            if (sub(*past_gain_pit, 15565) > 0)
            {
                *past_gain_pit = 15565;        /* 0.95 in Q14 */
            }
            
            if (unusable_frame != 0)
            {
                *gain_pit = mult(pdown_unusable[state], *past_gain_pit);
            }
            else
            {
                *gain_pit = mult(pdown_usable[state], *past_gain_pit);
            }
            tmp = median5(&gbuf[2]);
            
            if (sub(vad_hist, 2) > 0)
            {
                *past_gain_code = tmp;
            }
            else
            {
                if (unusable_frame != 0)
                {
                    *past_gain_code = mult(cdown_unusable[state], tmp);
                }
                else
                {
                    *past_gain_code = mult(cdown_usable[state], tmp);
                }
            }

            /* update table of past quantized energies */

            L_tmp = L_mult(past_qua_en[0], 8192);   /* x 0.25 */
            L_tmp = L_mac(L_tmp, past_qua_en[1], 8192);     /* x 0.25 */
            L_tmp = L_mac(L_tmp, past_qua_en[2], 8192);     /* x 0.25 */
            L_tmp = L_mac(L_tmp, past_qua_en[3], 8192);     /* x 0.25 */
            qua_ener = extract_h(L_tmp);

            qua_ener = sub(qua_ener, 3072);    /* -3 in Q10 */
            
            if (sub(qua_ener, -14336) < 0)
                qua_ener = -14336;   /* -14 in Q10 */

            past_qua_en[3] = past_qua_en[2];
            past_qua_en[2] = past_qua_en[1];
            past_qua_en[1] = past_qua_en[0];
            past_qua_en[0] = qua_ener;

            for (i = 1; i < 5; i++)
            {
                gbuf[i - 1] = gbuf[i];
            }
            gbuf[4] = *past_gain_code;

            for (i = 1; i < 5; i++)
            {
                pbuf[i - 1] = pbuf[i];
            }
            pbuf[4] = *past_gain_pit;

            /* adjust gain according to energy of code */
            /* past_gain_code(Q3) * gcode_inov(Q12) => Q16 */
            *gain_cod = L_mult(*past_gain_code, gcode_inov);

            return;
        }
        /*-----------------------------------------------------------------*
         * Compute gcode0.                                                 *
         *  = Sum(i=0,1) pred[i]*past_qua_en[i] + mean_ener - ener_code    *
         *-----------------------------------------------------------------*/

        L_tmp = L_deposit_h(MEAN_ENER);        /* MEAN_ENER in Q16 */
        L_tmp = L_shl(L_tmp, 8);               /* From Q16 to Q24 */
        L_tmp = L_mac(L_tmp, pred[0], past_qua_en[0]);      /* Q13*Q10 -> Q24 */
        L_tmp = L_mac(L_tmp, pred[1], past_qua_en[1]);      /* Q13*Q10 -> Q24 */
        L_tmp = L_mac(L_tmp, pred[2], past_qua_en[2]);      /* Q13*Q10 -> Q24 */
        L_tmp = L_mac(L_tmp, pred[3], past_qua_en[3]);      /* Q13*Q10 -> Q24 */

        gcode0 = extract_h(L_tmp);             /* From Q24 to Q8  */

        /*-----------------------------------------------------------------*
         * gcode0 = pow(10.0, gcode0/20)                                   *
         *        = pow(2, 3.321928*gcode0/20)                             *
         *        = pow(2, 0.166096*gcode0)                                *
         *-----------------------------------------------------------------*/

        L_tmp = L_mult(gcode0, 5443);          /* *0.166096 in Q15 -> Q24     */
        L_tmp = L_shr(L_tmp, 8);               /* From Q24 to Q16             */
        exp_gcode0 = 0;
        frac = 0;
        L_Extract(L_tmp, ref exp_gcode0, ref frac);  /* Extract exponant of gcode0  */

        gcode0 = extract_l(Pow2(14, frac));    /* Put 14 as exponant so that  */
        /* output of Pow2() will be:   */
        /* 16384 < Pow2() <= 32767     */
        exp_gcode0 = sub(exp_gcode0, 14);

        /* Read the quantized gains */

        if (sub(nbits, 6) == 0)
        {
            //p = &t_qua_gain6b[add(index, index)];
            p = p_t_qua_gain6b + add(index, index);     // 17 May 25 PHR
        }
        else
        {
            //p = &t_qua_gain7b[add(index, index)];
            p = p_t_qua_gain7b + add(index, index);     // 17 May 25 PHR
        }

        *gain_pit = *p++;  /* selected pitch gain in Q14 */
        g_code = *p++;     /* selected code gain in Q11  */

        L_tmp = L_mult(g_code, gcode0);        /* Q11*Q0 -> Q12 */
        L_tmp = L_shl(L_tmp, add(exp_gcode0, 4));   /* Q12 -> Q16 */

        *gain_cod = L_tmp;  /* gain of code in Q16 */
        
        if ((sub(prev_bfi, 1) == 0))
        {
            L_tmp = L_mult(*prev_gc, 5120);    /* prev_gc(Q3) * 1.25(Q12) = Q16 */
            /* if((*gain_cod > ((*prev_gc) * 1.25)) && (*gain_cod > 100.0)) */
            if ((L_sub(*gain_cod, L_tmp) > 0) && (L_sub(*gain_cod, 6553600) > 0))
            {
                *gain_cod = L_tmp;
            }
        }
        /* keep past gain code in Q3 for frame erasure (can saturate) */
        *past_gain_code = round(L_shl(*gain_cod, 3));
        *past_gain_pit = *gain_pit;

        *prev_gc = *past_gain_code;
        for (i = 1; i < 5; i++)
        {
            gbuf[i - 1] = gbuf[i];
        }
        gbuf[4] = *past_gain_code;

        for (i = 1; i < 5; i++)
        {
            pbuf[i - 1] = pbuf[i];
        }
        pbuf[4] = *past_gain_pit;

        for (i = 1; i < 5; i++)
        {
            pbuf2[i - 1] = pbuf2[i];
        }
        pbuf2[4] = *past_gain_pit;

        /* adjust gain according to energy of code */
        L_Extract(*gain_cod, ref exp, ref frac);
        L_tmp = Mpy_32_16(exp, frac, gcode_inov);
        *gain_cod = L_shl(L_tmp, 3);  /* gcode_inov in Q12 */

        /*---------------------------------------------------*
         * qua_ener = 20*log10(g_code)                       *
         *          = 6.0206*log2(g_code)                    *
         *          = 6.0206*(log2(g_codeQ11) - 11)          *
         *---------------------------------------------------*/

        L_tmp = L_deposit_l(g_code);
        Log2(L_tmp, ref exp, ref frac);
        exp = sub(exp, 11);
        L_tmp = Mpy_32_16(exp, frac, 24660);   /* x 6.0206 in Q12 */

        qua_ener = extract_l(L_shr(L_tmp, 3)); /* result in Q10 */

        /* update table of past quantized energies */

        past_qua_en[3] = past_qua_en[2];
        past_qua_en[2] = past_qua_en[1];
        past_qua_en[1] = past_qua_en[0];
        past_qua_en[0] = qua_ener;

        return;
    }

    // 12 May 25 PHR
    // This is the definition of D_gain2() that is used in dec_main.
    // Fix the parameter types and use the the previous definition that was re-coded from the C code that uses pointer
    // arithmetic on the mem[] array and takes a short array instead of a short* for the code array.
    private unsafe void D_gain2(
         short index,                        /* (i)     : index of quantization.      */
         short nbits,                        /* (i)     : number of bits (6 or 7)     */
         short* code,                       /* (i) Q9  : Innovative vector.          */
         short L_subfr,                      /* (i)     : Subframe lenght.            */
         short* gain_pit,                   /* (o) Q14 : Pitch gain.                 */
         int* gain_cod,                   /* (o) Q16 : Code gain.                  */
         short bfi,                          /* (i)     : bad frame indicator         */
         short prev_bfi,                     /* (i)     : Previous BF indicator       */
         short state,                        /* (i)     : State of BFH                */
         short unusable_frame,               /* (i)     : UF indicator                */
         short vad_hist,                     /* (i)     : number of non-speech frames */
         short[] mem                         /* (i/o)   : static memory (4 words)     */
    )
    {
        short[] code_array = new short[L_SUBFR];
        Copy(code, code_array, L_SUBFR);
        short* p_mem = stackalloc short[mem.Length];
        Copy(mem, p_mem, (short) mem.Length);   // mem[] is an input

        D_gain2(index, nbits, code_array, L_subfr, gain_pit, gain_cod, bfi, prev_bfi, state, unusable_frame,
            vad_hist, p_mem);
        // Load the results that are in p_mem into the mem[] output.
        Copy(p_mem, mem, (short) mem.Length);

        return;
    }

    /*------------------------------------------------------*
     * Tables for function q_gain2()                        *
     *                                                      *
     *  g_pitch(Q14),  g_code(Q11)                          *
     *                                                      *
     * pitch gain are ordered in table to reduce complexity *
     * during quantization of gains.                        *
     *------------------------------------------------------*/

    // From q_gain2.tab
    private const int nb_qua_gain6b = 64;     /* Number of quantization level */
    private const int nb_qua_gain7b = 128;    /* Number of quantization level */

    private static short[] t_qua_gain6b = new short[64 * 2]  
    {
       1566,  1332,
       1577,  3557,
       3071,  6490,
       4193, 10163,
       4496,  2534,
       5019,  4488,
       5586, 15614,
       5725,  1422,
       6453,   580,
       6724,  6831,
       7657,  3527,
       8072,  2099,
       8232,  5319,
       8827,  8775,
       9740,  2868,
       9856,  1465,
      10087, 12488,
      10241,  4453,
      10859,  6618,
      11321,  3587,
      11417,  1800,
      11643,  2428,
      11718,   988,
      12312,  5093,
      12523,  8413,
      12574, 26214,
      12601,  3396,
      13172,  1623,
      13285,  2423,
      13418,  6087,
      13459, 12810,
      13656,  3607,
      14111,  4521,
      14144,  1229,
      14425,  1871,
      14431,  7234,
      14445,  2834,
      14628, 10036,
      14860, 17496,
      15161,  3629,
      15209,  5819,
      15299,  2256,
      15518,  4722,
      15663,  1060,
      15759,  7972,
      15939, 11964,
      16020,  2996,
      16086,  1707,
      16521,  4254,
      16576,  6224,
      16894,  2380,
      16906,   681,
      17213,  8406,
      17610,  3418,
      17895,  5269,
      18168, 11748,
      18230,  1575,
      18607, 32767,
      18728, 21684,
      19137,  2543,
      19422,  6577,
      19446,  4097,
      19450,  9056,
      20371, 14885};

    private static readonly short[] t_qua_gain7b = new short[128 * 2] 
    {
        204,   441,
        464,  1977,
        869,  1077,
       1072,  3062,
       1281,  4759,
       1647,  1539,
       1845,  7020,
       1853,   634,
       1995,  2336,
       2351, 15400,
       2661,  1165,
       2702,  3900,
       2710, 10133,
       3195,  1752,
       3498,  2624,
       3663,   849,
       3984,  5697,
       4214,  3399,
       4415,  1304,
       4695,  2056,
       5376,  4558,
       5386,   676,
       5518, 23554,
       5567,  7794,
       5644,  3061,
       5672,  1513,
       5957,  2338,
       6533,  1060,
       6804,  5998,
       6820,  1767,
       6937,  3837,
       7277,   414,
       7305,  2665,
       7466, 11304,
       7942,   794,
       8007,  1982,
       8007,  1366,
       8326,  3105,
       8336,  4810,
       8708,  7954,
       8989,  2279,
       9031,  1055,
       9247,  3568,
       9283,  1631,
       9654,  6311,
       9811,  2605,
      10120,   683,
      10143,  4179,
      10245,  1946,
      10335,  1218,
      10468,  9960,
      10651,  3000,
      10951,  1530,
      10969,  5290,
      11203,  2305,
      11325,  3562,
      11771,  6754,
      11839,  1849,
      11941,  4495,
      11954,  1298,
      11975, 15223,
      11977,   883,
      11986,  2842,
      12438,  2141,
      12593,  3665,
      12636,  8367,
      12658,  1594,
      12886,  2628,
      12984,  4942,
      13146,  1115,
      13224,   524,
      13341,  3163,
      13399,  1923,
      13549,  5961,
      13606,  1401,
      13655,  2399,
      13782,  3909,
      13868, 10923,
      14226,  1723,
      14232,  2939,
      14278,  7528,
      14439,  4598,
      14451,   984,
      14458,  2265,
      14792,  1403,
      14818,  3445,
      14899,  5709,
      15017, 15362,
      15048,  1946,
      15069,  2655,
      15405,  9591,
      15405,  4079,
      15570,  7183,
      15687,  2286,
      15691,  1624,
      15699,  3068,
      15772,  5149,
      15868,  1205,
      15970,   696,
      16249,  3584,
      16338,  1917,
      16424,  2560,
      16483,  4438,
      16529,  6410,
      16620, 11966,
      16839,  8780,
      17030,  3050,
      17033, 18325,
      17092,  1568,
      17123,  5197,
      17351,  2113,
      17374,   980,
      17566, 26214,
      17609,  3912,
      17639, 32767,
      18151,  7871,
      18197,  2516,
      18202,  5649,
      18679,  3283,
      18930,  1370,
      19271, 13757,
      19317,  4120,
      19460,  1973,
      19654, 10018,
      19764,  6792,
      19912,  5135,
      20040,  2841,
      21234, 19833};

}