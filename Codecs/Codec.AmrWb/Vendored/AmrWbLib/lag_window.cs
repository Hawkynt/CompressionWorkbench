/*---------------------------------------------------------*
 *                         LAG_WIND.C             *
 *---------------------------------------------------------*
 * Lag_window on autocorrelations.                         *
 *                                                         *
 * r[i] *= lag_wind[i]                                     *
 *                                                         *
 *  r[i] and lag_wind[i] are in special double precision.  *
 *  See "oper_32b.c" for the format                        *
 *---------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    //#include "lag_wind.tab"
    /*-----------------------------------------------------*
     | Table of lag_window for autocorrelation.            |
     | noise floor = 1.0001   = (0.9999  on r[1] ..r[16])  |
     | Bandwidth expansion = 60 Hz                         |
     | Sampling frequency  = 12800 Hz                      |
     |                                                     |
     | Special double precision format. See "math_op.c"    |
     |                                                     |
     | lag_wind[0] =  1.00000000    (not stored)           |
     | lag_wind[1] =  0.99946642                           |
     | lag_wind[2] =  0.99816680                           |
     | lag_wind[3] =  0.99600452                           |
     | lag_wind[4] =  0.99298513                           |
     | lag_wind[5] =  0.98911655                           |
     | lag_wind[6] =  0.98440880                           |
     | lag_wind[7] =  0.97887397                           |
     | lag_wind[8] =  0.97252619                           |
     | lag_wind[9] =  0.96538186                           |
     | lag_wind[10]=  0.95745903                           |
     | lag_wind[11]=  0.94877797                           |
     | lag_wind[12]=  0.93936038                           |
     | lag_wind[13]=  0.92922986                           |
     | lag_wind[14]=  0.91841155                           |
     | lag_wind[15]=  0.90693212                           |
     | lag_wind[16]=  0.89481968                           |
     ------------------------------------------------------*/

    //#define M 16

    private static short[] lag_h = new short[M]
    {
      32750,
      32707,
      32637,
      32538,
      32411,
      32257,
      32075,
      31867,
      31633,
      31374,
      31089,
      30780,
      30449,
      30094,
      29718,
      29321
    };

    private static short[] lag_l = new short[M]
    {
      16896,
      30464,
       2496,
       4480,
      12160,
       3520,
      24320,
      24192,
      20736,
        576,
      18240,
      31488,
        128,
      16704,
      11520,
      14784
    };

    void Lag_window(
         short[] r_h,                         /* (i/o)   : Autocorrelations  (msb)          */
         short[] r_l)                          /* (i/o)   : Autocorrelations  (lsb)          */
    {
        short i;
        int x;

        for (i = 1; i <= M; i++)
        {
            x = Mpy_32(r_h[i], r_l[i], lag_h[i - 1], lag_l[i - 1]);
            L_Extract(x, ref r_h[i], ref r_l[i]);
        }
    }
}
