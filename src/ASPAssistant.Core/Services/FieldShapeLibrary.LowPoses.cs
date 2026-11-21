namespace ASPAssistant.Core.Services;

internal sealed partial class FieldShapeLibrary
{
    private sealed record LowPose(Pose Pose,Pixel Anchor,Pixel[] Accent);
    private readonly LowPose[] _lowPoses;

    private static LowPose[] BuildLowPoses(Pose[] poses)
    {
        var low=new List<LowPose>();
        foreach (var pose in poses)
        {
            var pixels=pose.Full.Pixels;
            int height=pixels.Max(p=>p.Index/33)-pixels.Min(p=>p.Index/33);
            int width=pixels.Max(p=>p.Index%33)-pixels.Min(p=>p.Index%33);
            if (height>15 || width<height) continue;

            // Find two distinct patches of colour inside the opaque silhouette.
            // An isolated colourful pixel or a largely monochrome skin cannot identify a unit this way.
            var vivid=pixels.Where(p=>Math.Max(p.Red,Math.Max(p.Green,p.Blue))-
                Math.Min(p.Red,Math.Min(p.Green,p.Blue))>=60).ToArray();
            foreach (var anchor in vivid)
            {
                if (vivid.Count(p=>Math.Abs((p.Red-p.Green)-(anchor.Red-anchor.Green))+
                    Math.Abs((p.Blue-p.Green)-(anchor.Blue-anchor.Green))<45)<2) continue;
                var accent=vivid.Where(p=>Math.Abs((p.Red-p.Green)-(anchor.Red-anchor.Green))+
                    Math.Abs((p.Blue-p.Green)-(anchor.Blue-anchor.Green))>=125).ToArray();
                if (accent.Length<4 || accent.Length<=vivid.Length/2) continue;
                low.Add(new(pose,anchor,accent));
                break;
            }
        }
        return low.ToArray();
    }

    internal Candidate? MatchLowPose(BmpPixels frame,double x,double floor,double tile,int tier,bool crowded)
    {
        // The shape pack uses offsets of at most .8 tiles on either side of the foot.
        if (x-tile<0 || x+tile>=frame.Width || floor-1.8*tile<0 || floor+.5*tile>=frame.Height) return null;
        Candidate? best=null;
        string? competing=null;
        foreach (var shape in _lowPoses)
        {
            if (tier!=0 && tier!=shape.Pose.Tier) continue;
            foreach (int facing in new[]{-1,1})
            {
                var anchor=shape.Anchor;
                double step=tile/20;
                int ex=(int)Math.Round(x+facing*(anchor.Index%33-16)*step);
                int ey=(int)Math.Round(floor+.15*tile+(anchor.Index/33-34)*step);
                int reach=(int)Math.Ceiling(tile*.10);
                for (int ay=Math.Max(0,ey-reach);ay<=Math.Min(frame.Height-1,ey+reach);ay++)
                for (int ax=Math.Max(0,ex-reach);ax<=Math.Min(frame.Width-1,ex+reach);ax++)
                {
                    if (!SameColour(frame.Color(ax,ay),anchor)) continue;
                    int radius=Math.Max(1,(int)Math.Round(tile*.018));
                    int accents=0;
                    foreach (var p in shape.Accent)
                    {
                        int sx=ax+(int)Math.Round(facing*(p.Index%33-anchor.Index%33)*step);
                        int sy=ay+(int)Math.Round((p.Index/33-anchor.Index/33)*step);
                        bool found=false;
                        for (int iy=-radius;iy<=radius && !found;iy++)
                        for (int ix=-radius;ix<=radius;ix++)
                        {
                            if (!SameColour(frame.Color(sx+ix,sy+iy),p)) continue;
                            found=true; break;
                        }
                        if (found) accents++;
                    }
                    if (accents*3<shape.Accent.Length*2) continue;
                    int matched=0,upper=0,upperMatched=0;
                    var pixels=shape.Pose.Full.Pixels;
                    foreach (var p in pixels)
                    {
                        int sx=ax+(int)Math.Round(facing*(p.Index%33-anchor.Index%33)*step);
                        int sy=ay+(int)Math.Round((p.Index/33-anchor.Index/33)*step);
                        int rgb=frame.Color(sx,sy);
                        bool close=Math.Abs((rgb>>16)-p.Red)+Math.Abs(((rgb>>8)&255)-p.Green)+Math.Abs((rgb&255)-p.Blue)<=100;
                        if (close) matched++;
                        if (p.Index/33<=anchor.Index/33+3) { upper++; if (close) upperMatched++; }
                    }
                    double agreement=(double)matched/pixels.Length;
                    // The next row may hide the feet. The visible upper silhouette
                    // and both distinctive colours must still agree at the same alignment.
                    if (crowded && agreement>=.50 && accents>=.80*shape.Accent.Length)
                        agreement=Math.Max(agreement,(double)upperMatched/upper);
                    if (agreement<.58) continue;
                    double score=.65*agreement+.35*accents/shape.Accent.Length;
                    if (score<.72) continue;
                    if (best!=null && best.Name!=shape.Pose.Name) competing=shape.Pose.Name;
                    if (best==null || score>best.Score) best=new(shape.Pose.Name,shape.Pose.Tier,score);
                }
            }
        }
        return competing==null?best:null;

        static bool SameColour(int rgb,Pixel reference)
        {
            int r=rgb>>16,g=(rgb>>8)&255,b=rgb&255;
            return Math.Abs((r-g)-(reference.Red-reference.Green))<44 &&
                Math.Abs((b-g)-(reference.Blue-reference.Green))<43 &&
                Math.Abs(r-reference.Red)+Math.Abs(g-reference.Green)+Math.Abs(b-reference.Blue)<110;
        }
    }
}
