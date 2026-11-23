using System.Drawing;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public sealed partial class LiveFieldReader
{
    private void InspectShapes(BmpPixels frame, BoardMatch board, List<FieldMark> marks,
        List<(double X,double Y)> anchors, HashSet<FieldSlot> phantoms, bool identify,
        IReadOnlyDictionary<FieldSlot, FieldMark>? cached, bool force, CancellationToken cancellation,
        Dictionary<FieldSlot,SlotReading>? readings)
    {
        var projection=_maps.Cameras[board.Camera];
        double dx=0,dy=0;
        if (anchors.Count>=2)
        {
            dx=anchors.Select(p=>p.X).Order().ElementAt(anchors.Count/2);
            dy=anchors.Select(p=>p.Y).Order().ElementAt(anchors.Count/2);
        }
        foreach (var (cell,type) in board.Map.AllCells().OrderByDescending(c=>c.Cell.Row))
        {
            if (cell.Row>=4 || !GarrisonMap.CanDeploy(type,false)) continue;
            var slot=new FieldSlot(cell.Col,cell.Row);
            if (phantoms.Contains(slot)) continue;
            int index=marks.FindIndex(m=>m.Slot==slot);
            FieldMark? mark=index<0?null:marks[index];
            if (mark?.StandaloneName!=null && mark.Scores?.Count==1 || mark?.ShapeCandidates.Length>0 || !identify && mark!=null) continue;
            cancellation.ThrowIfCancellationRequested();
            var center=projection.Project(cell.Col+.5,cell.Row+.5);
            double tile=(projection.Project(cell.Col+1.5,cell.Row+.5).X-center.X)*frame.Width;
            double x=center.X*frame.Width+dx*tile, y=center.Y*frame.Height+dy*tile;
            bool high=type==BoardCellType.High;
            double floor=y-(high?.18:0)*tile;
            bool crowded=marks.Exists(m=>m.Slot==new FieldSlot(slot.Col,slot.Row+1));
            if (mark?.StandaloneName==null && _shapes.Value.MatchLowPose(frame,x,floor,tile,mark?.Tier??0,crowded) is { } low)
            {
                var lowFoot=new Point((int)Math.Round(x-.085*tile),(int)Math.Round(floor-.11*tile));
                mark??=new(slot,low.Tier,lowFoot,BodySample(frame,lowFoot,tile,false),null)
                    { Tile=tile,Portrait=BodySample(frame,lowFoot,tile,true) };
                mark=mark with { Tier=low.Tier, StandaloneName=identify && low.Score>=.84?low.Name:null,
                    ShapeCandidates=identify?[low]:[], LowPose=identify?low:null };
                if (index<0) marks.Add(mark); else marks[index]=mark;
                if (readings!=null) readings[slot]=readings[slot] with { Decision="矮小模型轮廓与两处局部配色相符" };
                continue;
            }
            if (mark==null && board.Camera==BoardCamera.Expanded && board.Map.Id=="city" &&
                !ChangedFromEmpty(frame,center.X*frame.Width,center.Y*frame.Height,tile,high,cell.Row==0,_cityField.Empty[slot.ToString()],out _)) continue;
            bool body=FieldBody(frame,x,floor,tile,out bool arrow);
            if (mark==null && !body) continue;
            // When the next row covers the base, the upper and middle bands must
            // independently contain a body. A neighbour's head alone fails this test.
            bool occupied=mark!=null || body && (arrow || crowded);
            var foot=new Point((int)Math.Round(x-.085*tile),(int)Math.Round(floor-.11*tile));
            // A different row may shift the board projection even when this crop stays still.
            if (force && mark != null && cached != null && cached.TryGetValue(slot,out var same) &&
                same.Foot==mark.Foot && same.Tier==mark.Tier && same.Tile==tile &&
                (mark.StandaloneName==null || mark.StandaloneName==same.StandaloneName) &&
                same.ShapeCenter==(x+.02*tile,floor+.16*tile))
            {
                marks[index]=same;
                continue;
            }
            if (mark==null && occupied && identify && cached != null && _map==board.Map.Id &&
                cached.TryGetValue(slot,out var previous) && previous.Camera==board.Camera &&
                (!force || previous.Foot==foot && previous.Tile==tile && previous.ShapeCenter==(x+.02*tile,floor+.16*tile)) &&
                previous.ShapeCandidates.Length>0)
            {
                var look=BodySample(frame,foot,tile,false);
                if (LiveFieldState.Difference(look,previous.Appearance)<5)
                {
                    marks.Add(previous with { Foot=foot, Tile=tile, Appearance=look });
                    if (readings!=null) readings[slot]=readings[slot] with { Decision="本格人物外观稳定" };
                    continue;
                }
            }
            var ranked=identify || !occupied ? _shapes.Value.Match(frame,x+.02*tile,floor+.16*tile,tile,mark?.Tier??0,cancellation) : [];
            var best=Accepted(ranked);
            bool visibleShape=ranked.Length>=2 && ranked[0].Score>=.60 && ranked[0].Score-ranked[1].Score>=.08;
            if (!occupied && !visibleShape) continue;
            if (mark==null)
            {
                mark=new(slot,best?.Tier??0,foot,BodySample(frame,foot,tile,false),null)
                    { Tile=tile,Portrait=BodySample(frame,foot,tile,true) };
                if (readings!=null) readings[slot]=readings[slot] with { Decision="人物脚下与身体轮廓匹配" };
            }
            string? name=best?.Name??mark.StandaloneName;
            // A confident-looking sparse pose can belong to a similar operator.
            // When neither colour ranking supports it, leave the identity open.
            if (best==null && name!=null && mark.Scores?.TryGetValue(name,out double error)==true &&
                mark.Scores.Values.Count(v=>v<error)>=4 && ranked.Length>0 && ranked.All(c=>c.Name!=name)) name=null;
            mark=mark with { StandaloneName=name,ShapeCandidates=ranked, ShapeCenter=(x+.02*tile,floor+.16*tile),
                Tier=mark.Tier==0?best?.Tier??0:mark.Tier };
            if (index<0) marks.Add(mark); else marks[index]=mark;
        }
        if (!identify) return;
        for (int i=0;i<marks.Count;i++)
        {
            var m=marks[i];
            if (!m.Slot.Bench || m.StandaloneName!=null || m.ShapeCandidates.Length>0) continue;
            cancellation.ThrowIfCancellationRequested();
            var ranked=_shapes.Value.Match(frame,m.Foot.X+.05*m.Tile,m.Foot.Y+.039*m.Tile,m.Tile,m.Tier,cancellation);
            marks[i]=m with { StandaloneName=Accepted(ranked)?.Name, ShapeCandidates=ranked };
        }
    }

    internal string[] ReviewVisibleParts(BmpPixels frame,FieldMark mark,IReadOnlySet<string> known)
    {
        var center=mark.Slot.Bench ? (mark.Foot.X+.05*mark.Tile,mark.Foot.Y+.039*mark.Tile) : mark.ShapeCenter;
        if (center is not {} at || mark.ShapeCandidates.Length<2) return [];
        int cut=(int)Math.Round((Math.Round(mark.Foot.Y-1.22*mark.Tile)+Math.Round(mark.Foot.Y+.32*mark.Tile))/2);
        var supported=new List<FieldShapeLibrary.Candidate>();
        foreach (var (from,until) in new[]{(cut,int.MaxValue),(int.MinValue,cut)})
        {
            var ranked=_shapes.Value.Match(frame,at.Item1,at.Item2,mark.Tile,mark.Tier,CancellationToken.None,from,until,known);
            if (ranked.Length>=2 && ranked[0].Score>=.60 && ranked[0].Score-ranked[1].Score>=.065)
                supported.Add(ranked[0]);
        }
        return supported.OrderByDescending(c=>c.Score+(known.Contains(c.Name)?.025:0)).Select(c=>c.Name).Distinct().ToArray();
    }

    internal string? ReviewVisibleBody(BmpPixels frame, FieldMark mark, int badgeTier, IEnumerable<FieldSlot> occupied)
    {
        if (mark.Slot.Bench || mark.Tile <= 0) return null;
        bool above = mark.Slot.Row > 0 && occupied.Contains(new(mark.Slot.Col, mark.Slot.Row - 1));
        bool below = occupied.Contains(new(mark.Slot.Col, mark.Slot.Row + 1));
        bool close = mark.ShapeCandidates is [var first, var second, ..] && first.Score - second.Score < .065;
        if (mark.Tier != 0 && !above && !below && !close) return null;

        var (x, y) = mark.ShapeCenter ?? (mark.Foot.X + .05 * mark.Tile, mark.Foot.Y + .039 * mark.Tile);
        int top = (int)Math.Round(mark.Foot.Y - mark.Tile * (above ? .92 : 1.20));
        int bottom = (int)Math.Round(mark.Foot.Y + mark.Tile * (below ? -.10 : .19));
        var visible = _shapes.Value.Match(frame, x, y, mark.Tile, badgeTier, CancellationToken.None, top, bottom);
        return visible is [var best, var next, ..] && best.Score >= .65 && best.Score - next.Score >= .065
            ? best.Name : null;
    }

    private static FieldShapeLibrary.Candidate? Accepted(FieldShapeLibrary.Candidate[] ranked) =>
        ranked.Length>=2 && ranked[0].Score>=.70 && ranked[0].Score-ranked[1].Score>=.065 ? ranked[0] : null;

    private static bool FieldBody(BmpPixels frame,double cx,double cy,double tile,out bool arrow)
    {
        int head=0,torso=0,feet=0;
        for (int row=0;row<17;row++)
        {
            int y=(int)Math.Round(cy+(-.65+.05*row)*tile),prev=0,edges=0;
            for (int col=0;col<=12;col++)
            {
                int rgb=frame.Color((int)Math.Round(cx+(-.24+.04*col)*tile),y);
                if (col>0 && Math.Abs((rgb>>16)-(prev>>16))+Math.Abs(((rgb>>8)&255)-((prev>>8)&255))+
                    Math.Abs((rgb&255)-(prev&255))>78) edges++;
                prev=rgb;
            }
            if (edges<3) continue;
            if (row<7) head++; else if (row<12) torso++; else feet++;
        }
        arrow=false;
        if (head<4 || torso<4 || feet<3) return false;
        foreach (int facing in new[]{-1,1})
        {
            int orange=0;
            for (int row=-3;row<=5;row++)
            for (int col=5;col<=10;col++)
            {
                int rgb=frame.Color((int)Math.Round(cx+facing*col*.04*tile),(int)Math.Round(cy+row*.035*tile));
                if (IsOrange(rgb)) orange++;
            }
            if (orange>=5) {arrow=true;break;}
        }
        if (!arrow)
        {
            // A downward-facing operator exposes the tip in front of the toes.
            int orange=0;
            for (int row=5;row<=9;row++)
            for (int col=-3;col<=3;col++)
            {
                int rgb=frame.Color((int)Math.Round(cx+col*.035*tile),(int)Math.Round(cy+row*.04*tile));
                if (IsOrange(rgb)) orange++;
            }
            arrow=orange>=5;
        }
        return true;

        static bool IsOrange(int rgb)
        {
            int r=rgb>>16,g=(rgb>>8)&255,b=rgb&255;
            return r>190 && g>.45*r && g<.79*r && b<.23*r;
        }
    }

    private static byte[] BodySample(BmpPixels frame,Point foot,double tile,bool portrait)
    {
        int width=portrait?7:5,height=portrait?8:6;
        var a=new byte[width*height*3];
        for (int row=0;row<height;row++)
        for (int col=0;col<width;col++)
        {
            double dx=(col-width/2)*(portrait?.05:.075)*tile;
            double dy=(portrait?.31+row*.078:.28+row*.075)*tile;
            int x=foot.X+(portrait?(int)Math.Round(dx):(int)dx);
            int y=foot.Y-(portrait?(int)Math.Round(dy):(int)dy);
            int rgb=frame.Color(Math.Clamp(x,0,frame.Width-1),Math.Clamp(y,0,frame.Height-1));
            int at=(row*width+col)*3;
            a[at]=(byte)(rgb>>16);a[at+1]=(byte)(rgb>>8);a[at+2]=(byte)rgb;
        }
        return a;
    }
}

