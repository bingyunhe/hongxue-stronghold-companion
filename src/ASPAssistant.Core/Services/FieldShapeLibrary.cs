using System.Text;

namespace ASPAssistant.Core.Services;

// Colours are sampled only inside opaque parts of each skin's idle sprite.
// The common origin allows the same pose to be used on different maps.
internal sealed partial class FieldShapeLibrary
{
    internal sealed record Candidate(string Name, int Tier, double Score);
    private readonly record struct Pixel(int Index, int Mirrored, int Red, int Green, int Blue);
    private sealed class Sample(Pixel[] pixels)
    {
        public readonly Pixel[] Pixels=pixels;
        public readonly double Sum=pixels.Sum(p=>p.Red+p.Green+p.Blue);
        public readonly double Squares=pixels.Sum(p=>p.Red*p.Red+p.Green*p.Green+p.Blue*p.Blue);
    }
    private sealed record Pose(string Name, int Tier, Sample Full, Sample Coarse);
    private readonly Pose[] _poses;

    public FieldShapeLibrary(string path)
    {
        using var file = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (file.ReadUInt32() != 0x31485346) throw new InvalidDataException("干员轮廓模板版本不符");
        _poses = new Pose[file.ReadUInt16()];
        for (int i=0; i<_poses.Length; i++)
        {
            string name = Encoding.UTF8.GetString(file.ReadBytes(file.ReadByte()));
            file.ReadBytes(file.ReadByte()); // Each skin has its own poses in this pack.
            int tier = file.ReadByte();
            var points = new Pixel[file.ReadUInt16()];
            for (int j=0; j<points.Length; j++)
            {
                int x=file.ReadSByte(), y=file.ReadSByte(), r=file.ReadByte(), g=file.ReadByte(), b=file.ReadByte();
                int center=(y+34)*33+16;
                points[j]=new(center+x,center-x,r,g,b);
            }
            var coarse=new Pixel[24];
            for (int j=0;j<coarse.Length;j++) coarse[j]=points[(2*j+1)*points.Length/(2*coarse.Length)];
            _poses[i]=new(name,tier,new(points),new(coarse));
        }
        _lowPoses=BuildLowPoses(_poses);
    }

    internal Candidate[] Match(BmpPixels frame, double x, double y, double tile, int tier, CancellationToken cancellation,
        int fromY=int.MinValue, int untilY=int.MaxValue, IReadOnlySet<string>? known=null)
    {
        bool partial=fromY!=int.MinValue || untilY!=int.MaxValue;
        // The camera projection supplies the skeleton origin. Four neighbouring
        // alignments cheaply shortlist poses before the finer scale/offset search.
        int radius=Math.Max(1,(int)Math.Round(tile*.014));
        int left=Math.Max(radius,(int)Math.Floor(x-tile)),right=Math.Min(frame.Width-1-radius,(int)Math.Ceiling(x+tile));
        int top=Math.Max(radius,(int)Math.Floor(y-1.95*tile)),bottom=Math.Min(frame.Height-1-radius,(int)Math.Ceiling(y+.36*tile));
        int width=right-left+1;
        var blurred=new int[width*(bottom-top+1)];
        for (int sy=top;sy<=bottom;sy++)
        for (int sx=left;sx<=right;sx++)
        {
            int a=frame.Color(sx-radius,sy), b=frame.Color(sx+radius,sy);
            int c=frame.Color(sx,sy-radius), d=frame.Color(sx,sy+radius);
            blurred[(sy-top)*width+sx-left]=((a&0xfcfcfc)+(b&0xfcfcfc)+(c&0xfcfcfc)+(d&0xfcfcfc))>>2;
        }
        var coarse = new List<int[]>();
        foreach (double dy in new[]{-.08,0,.08})
            foreach (double dx in new[]{-.04,0,.04})
            {
                var sample=new int[33*39];
                Crop(dx,dy,1,sample);
                coarse.Add(sample);
            }
        var ranked=new List<(Pose Pose,double Score)>();
        foreach (var pose in _poses)
        {
            if (tier!=0 && pose.Tier!=tier) continue;
            double best=double.NegativeInfinity;
            foreach (var sample in coarse)
            {
                best=Math.Max(best,Math.Max(Fit(pose.Coarse,sample,false),Fit(pose.Coarse,sample,true)));
            }
            ranked.Add((pose,best));
        }
        // Keep multiple poses of each candidate, but one operator's many skins
        // must not crowd the runner-up out of the decision.
        var shortlist=ranked.OrderByDescending(v=>v.Score).GroupBy(v=>v.Pose.Name)
            .Where((g,index)=>index<24 || known?.Contains(g.Key)==true).SelectMany(g=>g.Take(4)).Select(v=>v.Pose).ToArray();
        var fits=new Dictionary<string,(Pose Pose,double Score,double X,double Y,double Scale,bool Flip)>();
        var crop=new int[33*39];
        foreach (double scale in new[]{1,.95,1.05})
        {
            cancellation.ThrowIfCancellationRequested();
            for (int iy=-2;iy<=2;iy++)
            for (int ix=-1;ix<=2;ix++)
            {
                Crop(ix*.04-.02,iy*.04,scale,crop);
                foreach (var pose in shortlist)
                {
                    double forward=Fit(pose.Full,crop,false), mirrored=Fit(pose.Full,crop,true);
                    double score=Math.Max(forward,mirrored);
                    if (!fits.TryGetValue(pose.Name,out var old) || score>old.Score)
                        fits[pose.Name]=(pose,score,ix*.04-.02,iy*.04,scale,mirrored>forward);
                }
            }
        }
        return fits.Values.OrderByDescending(f=>f.Score).Take(6).Select(f=>
        {
            double best=f.Score;
            for (int iy=-1;iy<=1;iy++)
            for (int ix=-1;ix<=1;ix++)
            {
                if (ix==0 && iy==0) continue;
                Crop(f.X+ix*.02,f.Y+iy*.02,f.Scale,crop);
                best=Math.Max(best,Fit(f.Pose.Full,crop,f.Flip));
            }
            return new Candidate(f.Pose.Name,f.Pose.Tier,best);
        }).OrderByDescending(c=>c.Score).Take(5).ToArray();

        double Fit(Sample pose,int[] image,bool flip) =>
            partial ? VisibleScore(pose,image,flip) : Score(pose,image,flip);

        void Crop(double dx,double dy,double scale,int[] result)
        {
            Span<int> xs=stackalloc int[33];
            for (int col=0;col<33;col++)
                xs[col]=Math.Clamp((int)Math.Round(x+((col-16)*scale/20+dx)*tile),left,right)-left;
            for (int row=0;row<39;row++)
            {
                int sy=Math.Clamp((int)Math.Round(y+((row-34)*scale/20+dy)*tile),top,bottom);
                if (sy<fromY || sy>=untilY)
                {
                    Array.Fill(result,-1,row*33,33);
                    continue;
                }
                int offset=(sy-top)*width;
                for (int col=0;col<33;col++)
                    result[row*33+col]=blurred[offset+xs[col]];
            }
        }
    }

    private static double VisibleScore(Sample pose,int[] image,bool flip)
    {
        int sum=0,squares=0,product=0,count=0,templateSum=0,templateSquares=0;
        foreach (var p in pose.Pixels)
        {
            int rgb=image[flip?p.Mirrored:p.Index];
            if (rgb<0) continue;
            int r=rgb>>16,g=(rgb>>8)&255,b=rgb&255;
            count++;
            sum+=r+g+b;
            squares+=r*r+g*g+b*b;
            product+=r*p.Red+g*p.Green+b*p.Blue;
            templateSum+=p.Red+p.Green+p.Blue;
            templateSquares+=p.Red*p.Red+p.Green*p.Green+p.Blue*p.Blue;
        }
        if (count*2<pose.Pixels.Length) return -2;
        return Correlation(count*3,sum,squares,product,templateSum,templateSquares);
    }

    private static double Score(Sample pose,int[] image,bool flip)
    {
        int sum=0,squares=0,product=0;
        foreach (var p in pose.Pixels)
        {
            int rgb=image[flip?p.Mirrored:p.Index], r=rgb>>16,g=(rgb>>8)&255,b=rgb&255;
            sum+=r+g+b;
            squares+=r*r+g*g+b*b;
            product+=r*p.Red+g*p.Green+b*p.Blue;
        }
        return Correlation(pose.Pixels.Length*3,sum,squares,product,pose.Sum,pose.Squares);
    }

    private static double Correlation(double n,int sum,int squares,int product,double templateSum,double templateSquares)
    {
        double v=templateSquares-templateSum*templateSum/n;
        double corr=(product-sum*templateSum/n)/Math.Sqrt(Math.Max(1,v)*Math.Max(1,squares-(double)sum*sum/n));
        double rms=Math.Sqrt(Math.Max(0,(squares+templateSquares-2*product)/n));
        return .75*corr+.25*(1-rms/160);
    }
}

