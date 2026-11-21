namespace ASPAssistant.Core.Services;

internal static class FieldMemoryAppearance
{
    // Both shop cameras use tile-relative coordinates. Keep a wider margin around the
    // torso so that the comparison can align a shifted badge or a mirrored pose.
    internal static int[] Sample(BmpPixels frame, FieldMark mark)
    {
        var patch = new int[17 * 20];
        int radius = Math.Max(1,(int)Math.Round(mark.Tile * .018));
        int area=(2*radius+1)*(2*radius+1);
        for (int row=0; row<20; row++)
        for (int col=0; col<17; col++)
        {
            int x=Math.Clamp(mark.Foot.X+(int)Math.Round((col-8)*.05*mark.Tile),radius,frame.Width-1-radius);
            int y=Math.Clamp(mark.Foot.Y+(int)Math.Round((row-19)*.05*mark.Tile),radius,frame.Height-1-radius);
            int r=0,g=0,b=0;
            for(int dy=-radius;dy<=radius;dy++)
            for(int dx=-radius;dx<=radius;dx++)
            {
                int color=frame.Color(x+dx,y+dy);
                r+=color>>16; g+=(color>>8)&255; b+=color&255;
            }
            patch[row*17+col]=(r/area<<16)|(g/area<<8)|b/area;
        }
        return patch;
    }

    internal static double Distance(int[] reference,int[] observed)
    {
        const int channels=13*7*3;
        int best=40*channels; // Scores above the suggestion limit do not affect matching.
        for (int flip=0;flip<2;flip++)
        for (int dy=-3;dy<=3;dy++)
        for (int dx=-5;dx<=5;dx++)
        {
            int error=0;
            for (int row=3;row<16 && error<best;row++)
            for (int col=5;col<12;col++)
            {
                int a=reference[row*17+(flip==0?col:16-col)], b=observed[(row+dy)*17+col+dx];
                error+=Math.Min(240,Math.Abs((a>>16)-(b>>16))+
                    Math.Abs(((a>>8)&255)-((b>>8)&255))+Math.Abs((a&255)-(b&255)));
            }
            if (error<best) best=error;
        }
        return (double)best/channels;
    }
}
