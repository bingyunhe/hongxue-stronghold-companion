using System.Drawing;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

// The detail camera centers the selected piece. Register surrounding terrain against the
// last normal board; names are deliberately excluded from this position check.
internal sealed class FieldDetailFocus
{
    private const int W = 640, H = 360;
    private static readonly Point[] Offsets = [new(-6,0),new(0,-6),new(6,0),new(0,6),new(-4,-4),new(-4,4),new(4,4),new(4,-4),new(0,0)];
    private readonly List<(int X,int Y,int[] Colors)> _landmarks = [];
    private readonly double[] _scales;
    private readonly IReadOnlyList<FieldMark> _marks;
    private readonly int[] _board;
    private int[]? _registeredDetail;
    private (FieldMark Mark,double Zoom,double CenterY)? _registration;
    private readonly double _kx, _ky;

    internal static bool Visible(BmpPixels pixels)
    {
        // The health bar spans the left detail panel in both board and bench views.
        for (int row = 450; row <= 470; row += 2)
        {
            int blue = 0;
            for (int col = 2; col < 24; col++)
            {
                int rgb = pixels.Color(col*pixels.Width/100, row*pixels.Height/1000);
                if ((rgb & 255) > 150 && ((rgb >> 8) & 255) > 95 && (rgb & 255) > (rgb >> 16) + 70) blue++;
            }
            if (blue >= 18) return true;
        }
        return false;
    }

    internal FieldDetailFocus(BmpPixels board, BoardCamera camera, IReadOnlyList<FieldMark> marks)
    {
        _marks = marks; _kx = W/(double)board.Width; _ky = H/(double)board.Height;
        _scales = camera == BoardCamera.Expanded ? [1.02,1.08,1.14,1.20] : [.86,.92,.98,1.04];
        var rgb = _board = Thumbnail(board);
        for (int row=0;row<10;row++)
            for (int col=0;col<19;col++)
            {
                int x=(int)(W*(.17+col*.73/18)), y=(int)(H*(.17+row*.42/9));
                var colors = Offsets.Select(o=>rgb[(y+o.Y)*W+x+o.X]).ToArray();
                double deviation=0;
                foreach (int shift in new[]{16,8,0})
                {
                    double mean=colors.Average(c=>(c>>shift)&255);
                    deviation+=Math.Sqrt(colors.Average(c=>Math.Pow(((c>>shift)&255)-mean,2)));
                }
                if (deviation>39) _landmarks.Add((x,y,colors));
            }
    }

    private static int[] Thumbnail(BmpPixels pixels)
    {
        var image = new int[W*H];
        for (int y=0;y<H;y++)
            for (int x=0;x<W;x++)
            {
                int x0=(int)((x+.25)*pixels.Width/W), x1=(int)((x+.75)*pixels.Width/W);
                int y0=(int)((y+.25)*pixels.Height/H), y1=(int)((y+.75)*pixels.Height/H);
                int a=pixels.Color(x0,y0), b=pixels.Color(x1,y0), c=pixels.Color(x0,y1), d=pixels.Color(x1,y1);
                int rb=((a&0xff00ff)+(b&0xff00ff)+(c&0xff00ff)+(d&0xff00ff))>>2;
                int g=((a&0xff00)+(b&0xff00)+(c&0xff00)+(d&0xff00))>>2;
                image[y*W+x]=(rb&0xff00ff)|(g&0xff00);
            }
        return image;
    }

    internal (FieldMark Mark,double Zoom,double CenterY)? Locate(BmpPixels detail)
    {
        var image=Thumbnail(detail);
        if (_registeredDetail != null && image.AsSpan().SequenceEqual(_registeredDetail)) return _registration;
        int centerY=SelectionCenterY(image);
        if (Math.Abs(centerY-H*.69)>H*.035)
        {
            var shifted = LocateShifted(image,centerY);
            if (shifted != null) return shifted;
            if (LocateCenteredUnit(image,centerY) is { } unit)
                _registration = (unit.Mark,unit.Zoom,centerY/(double)H);
            return _registration;
        }
        var ranked=new List<(FieldMark Mark,double Zoom,double Score,int Votes)>();
        var errors=new List<double>(_landmarks.Count);
        Span<int> diff = stackalloc int[27];
        foreach (var mark in _marks)
        {
            double best=double.MaxValue, zoom=0;
            int votes=0;
            foreach (double scale in _scales)
            {
                errors.Clear();
                var offsets=Offsets.Select(o=>(int)Math.Round(o.Y*scale)*W+(int)Math.Round(o.X*scale)).ToArray();
                foreach (var patch in _landmarks)
                {
                    int x=(int)Math.Round(W*.5+(patch.X-mark.Foot.X*_kx)*scale);
                    int y=(int)Math.Round(H*.69+(patch.Y-mark.Foot.Y*_ky)*scale);
                    if(x<W*.36 || x>W*.91 || y<H*.19 || y>H*.89) continue;
                    double nearest=double.MaxValue;
                    for(int dy=-6;dy<=6;dy+=3)
                        for(int dx=-6;dx<=6;dx+=3)
                        {
                            int red=0,green=0,blue=0;
                            for(int i=0;i<offsets.Length;i++)
                            {
                                int sample=image[(y+dy)*W+x+dx+offsets[i]], src=patch.Colors[i];
                                red+=diff[3*i]=(src>>16)-(sample>>16);
                                green+=diff[3*i+1]=((src>>8)&255)-((sample>>8)&255);
                                blue+=diff[3*i+2]=(src&255)-(sample&255);
                            }
                            double sum=0;
                            for(int i=0;i<27;i+=3)
                                sum+=Math.Abs(diff[i]-red/12d)+Math.Abs(diff[i+1]-green/12d)+Math.Abs(diff[i+2]-blue/12d);
                            nearest=Math.Min(nearest,sum/27);
                        }
                    errors.Add(nearest);
                }
                if(errors.Count<16) continue;
                errors.Sort();
                double score=errors.Take(Math.Max(6,(int)(errors.Count*.28))).Average();
                if(score<best){ best=score; zoom=scale; votes=errors.Count(e=>e<11); }
            }
            ranked.Add((mark,zoom,best,votes));
        }
        var top=ranked.OrderBy(r=>r.Score).Take(2).ToArray();
        bool terrain = top.Length>0 && top[0].Score<=14 && top[0].Votes>=4;
        if(terrain && (top.Length==1 || top[1].Score-top[0].Score>=1)) return (top[0].Mark,top[0].Zoom,.69);
        if(_marks.Count>=2)
        {
            // On repeating floor tiles two offsets fit the terrain. Compare the arrangement
            // of the visible tier badges in both cameras before associating the name.
            // Three or more visible badges can register a cluster even when the skill overlay hides terrain.
            bool cluster = _marks.Count>=3;
            var badges = ranked.Where(r=>cluster || terrain && r.Score<=14 && r.Votes>=4 && r.Score-top[0].Score<1)
                .SelectMany(r=>_scales.Select(z=>(r.Mark, Zoom:z, Error:BadgeError(image,r.Mark,z))))
                .GroupBy(r=>r.Mark.Slot).Select(g=>g.MinBy(r=>r.Error)).OrderBy(r=>r.Error).Take(2).ToArray();
            if(badges.Length==2 && badges[0].Error<=(cluster?23:25) && double.IsFinite(badges[1].Error) &&
                badges[1].Error-badges[0].Error>=(cluster?8:4)) return (badges[0].Mark,badges[0].Zoom,.69);
            if(cluster && LocateCenteredUnit(image,centerY) is { } centered) return (centered.Mark,centered.Zoom,centerY/(double)H);
        }
        return LocateShifted(image,centerY);
    }

    // The left and right corners of the white selection diamond move vertically with the camera.
    private static int SelectionCenterY(int[] image)
    {
        bool White(int x,int y)
        {
            int rgb=image[y*W+x];
            return (rgb>>16)>213 && ((rgb>>8)&255)>213 && (rgb&255)>213;
        }
        bool Edge(int x,int y) => White(x-1,y) || White(x,y) || White(x+1,y);
        int best=0, center=(int)Math.Round(H*.69);
        for(int y=H/2;y<H*.81;y++)
        for(int x=(int)(W*.63);x<W*.74;x++)
        {
            if(!White(x,y) || !Edge(W-1-x,y)) continue;
            int votes=0;
            for(int offset=4;offset<=16;offset+=4)
            {
                int upper=x-(int)Math.Round(offset*1.2), lower=x-offset;
                if(Edge(upper,y-offset) && Edge(W-1-upper,y-offset)) votes++;
                if(Edge(lower,y+offset) && Edge(W-1-lower,y+offset)) votes++;
            }
            if(votes>=5 && votes>best) { best=votes; center=y; }
        }
        return center;
    }

    // In the raised detail camera the ground stretches differently in X and Y.
    // Count matching terrain patches under one transform, so repeating tiles cannot each pick their own shift.
    private (FieldMark Mark,double Zoom,double CenterY)? LocateShifted(int[] image,int centerY)
    {
        const int width=W/2, height=H/2;
        int[] Reduce(int[] input)
        {
            var reduced=new int[width*height];
            for(int y=0;y<height;y++)
            for(int x=0;x<width;x++)
            {
                int at=2*y*W+2*x;
                int a=input[at], b=input[at+1], c=input[at+W], d=input[at+W+1];
                reduced[y*width+x]=((((a&0xff00ff)+(b&0xff00ff)+(c&0xff00ff)+(d&0xff00ff))>>2)&0xff00ff)
                    | ((((a&0xff00)+(b&0xff00)+(c&0xff00)+(d&0xff00))>>2)&0xff00);
            }
            return reduced;
        }
        static int Distance(int a,int b) => Math.Abs((a>>16)-(b>>16))+
            Math.Abs(((a>>8)&255)-((b>>8)&255))+Math.Abs((a&255)-(b&255));
        var board=Reduce(_board);
        var detail=Reduce(image);
        (int X,int Y)[] offsets=[(0,0),(2,0),(0,2),(-2,0),(0,-2),(3,3),(-3,-3)];
        var patches=new List<(int X,int Y,int[] Colors)>();
        for(int y=25;y<115;y+=3)
        for(int x=42;x<293;x+=3)
        {
            int at=y*width+x;
            if(Distance(board[at+2],board[at-2])+Distance(board[at+2*width],board[at-2*width])<108) continue;
            patches.Add((x,y,offsets.Select(p=>board[at+p.Y*width+p.X]).ToArray()));
        }
        var ranks=new List<(FieldMark Mark,double Zoom,double Score)>();
        Span<double> votes=stackalloc double[15];
        foreach(var mark in _marks)
        {
            double best=0, zoom=0;
            foreach(double sx in _scales[^1]>1.1 ? new[]{1.14,1.20,1.24,1.28} : _scales)
            foreach(double sy in new[]{sx,sx*1.10})
            {
                var shifted=offsets.Select(p=>(int)Math.Round(p.X*sx)+(int)Math.Round(p.Y*sy)*width).ToArray();
                votes.Clear();
                foreach(var p in patches)
                {
                    int x=(int)Math.Round(width*.5+(p.X-mark.Foot.X*_kx*.5)*sx);
                    int y=(int)Math.Round(centerY*.5+(p.Y-mark.Foot.Y*_ky*.5)*sy);
                    if(x<124 || x>298 || y<32 || y>150) continue;
                    int bin=0;
                    for(int dy=-10;dy<=2;dy+=3)
                    for(int dx=-2;dx<=2;dx+=2,bin++)
                    {
                        int at=(y+dy)*width+x+dx;
                        int a=p.Colors[0],b=detail[at];
                        int r=((a>>16)-(b>>16))*3/5, g=(((a>>8)&255)-((b>>8)&255))*3/5, blue=((a&255)-(b&255))*3/5;
                        int sum=0;
                        for(int i=0;i<shifted.Length && sum<336;i++)
                        {
                            a=p.Colors[i];b=detail[at+shifted[i]];
                            sum+=Math.Abs((a>>16)-(b>>16)-r)+Math.Abs(((a>>8)&255)-((b>>8)&255)-g)+Math.Abs((a&255)-(b&255)-blue);
                        }
                        if(sum<336) votes[bin]+=1-sum/336d;
                    }
                }
                foreach(var score in votes) if(score>best){best=score; zoom=sx;}
            }
            ranks.Add((mark,zoom,best));
        }
        var top=ranks.OrderByDescending(r=>r.Score).Take(2).ToArray();
        _registeredDetail = image;
        return _registration = top.Length>0 && top[0].Score>=13 && (top.Length==1 || top[0].Score>=1.45*top[1].Score)
            ? (top[0].Mark,top[0].Zoom,centerY/(double)H) : null;
    }

    // A selected operator's head and torso stay near the center even when skill stripes obscure the tile and badges.
    private (FieldMark Mark,double Zoom)? LocateCenteredUnit(int[] image, int centerY)
    {
        var ranks = new List<(FieldMark Mark,double Zoom,double Error)>();
        Span<int> errors=stackalloc int[56];
        foreach (var mark in _marks)
        {
            double tile=mark.Tile*_kx, best=double.MaxValue, zoom=0;
            if (tile<=0) continue;
            foreach (var scale in _scales)
            for (int dy=-8;dy<=8;dy+=2)
            for (int dx=-6;dx<=6;dx+=2)
            {
                int index=0;
                for (int row=0;row<8;row++)
                    for (int col=-3;col<=3;col++)
                    {
                        double ox=col*.065*tile, oy=-(.30+row*.082)*tile;
                        int x=(int)Math.Round(mark.Foot.X*_kx+ox), y=(int)Math.Round(mark.Foot.Y*_ky+oy);
                        int tx=(int)Math.Round(W*.5+dx+ox*scale), ty=(int)Math.Round(centerY+dy+oy*scale);
                        int a=_board[Math.Clamp(y,0,H-1)*W+Math.Clamp(x,0,W-1)], b=image[ty*W+tx];
                        errors[index++]=Math.Abs((a>>16)-(b>>16))+Math.Abs(((a>>8)&255)-((b>>8)&255))+Math.Abs((a&255)-(b&255));
                    }
                errors.Sort();
                int sum=0;
                foreach (int delta in errors[..40]) sum+=delta;
                double error=sum/120d;
                if (error<best) { best=error; zoom=scale; }
            }
            ranks.Add((mark,zoom,best));
        }
        var top=ranks.OrderBy(v=>v.Error).Take(2).ToArray();
        return top.Length==2 && top[0].Error<=20 && top[1].Error-top[0].Error>=6 ? (top[0].Mark,top[0].Zoom) : null;
    }

    private double BadgeError(int[] detail, FieldMark selected, double zoom)
    {
        double total=0;
        int visible=0;
        foreach(var mark in _marks)
        {
            // Reverse the badge-to-foot offset used by LiveFieldReader at the thumbnail scale.
            double cx=mark.Foot.X*_kx-35*W/1920d, cy=mark.Foot.Y*_ky-(mark.Slot.Bench?132:112)*W/1920d;
            int bx=(int)Math.Round(cx), by=(int)Math.Round(cy);
            int x=(int)Math.Round(W*.5+(cx-selected.Foot.X*_kx)*zoom);
            int y=(int)Math.Round(H*.69+(cy-selected.Foot.Y*_ky)*zoom);
            if(bx<6 || bx>=W-6 || by<6 || by>=H-6 || x<W*.39 || x>W*.84 || y<H*.32 || y>H*.80)
                continue;
            visible++;
            int best=int.MaxValue;
            for(int dy=-7;dy<=7;dy++)
            for(int dx=-7;dx<=7;dx++)
            {
                int error=0;
                for(int j=-6;j<=6;j+=2)
                for(int i=-6;i<=6;i+=2)
                {
                    int a=_board[(by+j)*W+bx+i];
                    int b=detail[(y+dy+(int)Math.Round(j*zoom))*W+x+dx+(int)Math.Round(i*zoom)];
                    error+=Math.Abs(Gray(a)-Gray(b));
                }
                best=Math.Min(best,error);
            }
            total+=best/49d;
        }
        return visible >= Math.Min(3,_marks.Count) ? total/visible : double.PositiveInfinity;
        static int Gray(int rgb) => (77*(rgb>>16)+150*((rgb>>8)&255)+29*(rgb&255))>>8;
    }
}
