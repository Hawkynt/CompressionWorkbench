
namespace AmrWbLib;

public partial class AmrWb
{
     /*-------------------------------------------------------------------*
     * Function  Set zero()                                              *
     *           ~~~~~~~~~~                                              *
     * Set vector x[] to zero                                            *
     *-------------------------------------------------------------------*/

    private void Set_zero(
         short[] x,                           /* (o)    : vector to clear     */
         short L)                              /* (i)    : length of vector    */
    {
        short i;

        for (i = 0; i < L; i++)
        {
            x[i] = 0;
        }
    }

    // 30 Apr 25 PHR
    private unsafe void Set_zero(
         short* x,                           /* (o)    : vector to clear     */
         short L)                              /* (i)    : length of vector    */
    {
        short i;

        for (i = 0; i < L; i++)
        {
            x[i] = 0;
        }
    }

    /*-------------------------------------------------------------------*
     * Function  Copy:                                                   *
     *           ~~~~~                                                   *
     * Copy vector x[] to y[]                                            *
     *-------------------------------------------------------------------*/

    private void Copy(
         short[] x,                           /* (i)   : input vector   */
         short[] y,                           /* (o)   : output vector  */
         short L)                              /* (i)   : vector length  */
    {
        short i;

        for (i = 0; i < L; i++)
        {
            y[i] = x[i];
        }
    }

    // 28 Apr 25 PHR
    private unsafe void Copy(short[] x, short* y, short L)
    {
        short* py = y;
        for (short i=0; i < L; i++)
        {
            *y++ = x[i];
        }
    }

    // 28 Apr 25 PHR
    private unsafe void Copy(short* x, short[] y, short L)
    {
        short* px = x;
        for (short i=0; i < L; i++)
        {
            y[i] = *px++;
        }
    }

    private unsafe void Copy(
         short* x,                           /* (i)   : input vector   */
         short* y,                           /* (o)   : output vector  */
         short L)                              /* (i)   : vector length  */
    {
        short i;

        for (i = 0; i < L; i++)
        {
            y[i] = x[i];
        }
    }

    // 10 Jan 24 PHR
    private void Copy(short[] x, short[] y, int y_start_index, short L)
    {
        for (int i = 0; i < L; i++)
        {
            y[i + y_start_index] = x[i];
        }
    }
}
