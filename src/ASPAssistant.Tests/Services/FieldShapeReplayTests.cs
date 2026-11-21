using ASPAssistant.Core.Data;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

public class FieldShapeReplayTests
{
    [Trait("Category","LocalScreenshot")]
    [Theory]
    [InlineData("cactus-field-r4","A5","灰喉")]
    [InlineData("short-field-r10","D7",null)]
    [InlineData("short-field-r6","整备区右起第1席",null)]
    public async Task VisibleBodyCanPromoteAReviewCandidateWithoutNamingTheSlot(string fixture,string slot,string? expected)
    {
        var data=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await GarrisonBoardStore.LoadAsync(data))!;
        var reader=new LiveFieldReader(data,maps);
        var image=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"TestData","Board",fixture+".png"));
        var pixels=BmpPixels.TryRead(ImageBytes.ToBmp(image))!;
        var board=new BoardRecognizer(maps).Recognize(pixels)!;
        var readings=new Dictionary<FieldSlot,SlotReading>();
        var marks=reader.ReadWithTrace(pixels,board,true,true,CancellationToken.None,readings);
        var target=Assert.Single(marks,m=>m.Slot.ToString()==slot);
        Assert.Null(target.StandaloneName);
        Assert.Equal(expected,reader.ReviewVisibleBody(pixels,target,readings[target.Slot].BadgeTier,marks.Select(m=>m.Slot)));
        Assert.Null(target.StandaloneName);
    }

    [Trait("Category","LocalScreenshot")]
    [Theory]
    [InlineData(6,"整备区右起第1席")]
    [InlineData(10,"D7")]
    public async Task UnnamedSnakeCanBeReviewedFromTheCapturedFrame(int round,string slot)
    {
        var data=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await GarrisonBoardStore.LoadAsync(data))!;
        var reader=new LiveFieldReader(data,maps);
        var image=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"TestData","Board",$"short-field-r{round}.png"));
        var pixels=BmpPixels.TryRead(ImageBytes.ToBmp(image))!;
        var board=new BoardRecognizer(maps).Recognize(pixels)!;
        var mark=Assert.Single(reader.Read(pixels,board,true,true),m=>m.Slot.ToString()==slot);
        Assert.Null(mark.StandaloneName);
        Assert.DoesNotContain(mark.ShapeCandidates,c=>c.Name=="蛇屠箱");
        Assert.Equal("蛇屠箱",reader.ReviewVisibleParts(pixels,mark,new HashSet<string>{"蛇屠箱"})[0]);
        Assert.Null(mark.StandaloneName);
    }

    [Trait("Category","LocalScreenshot")]
    [Fact]
    public async Task CovenantSupportPoseUsesTheInGameNameOnBoardAndBench()
    {
        const string support="盟约·辅助干员";
        var data=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await GarrisonBoardStore.LoadAsync(data))!;
        var reader=new LiveFieldReader(data,maps);
        var board=new BoardRecognizer(maps);
        var state=new LiveFieldState(new Dictionary<string,int>{{support,3},{"Miss.Christine",2}});
        foreach (int round in new[]{3,5,9,10})
        {
            var image=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"TestData","Board",$"short-field-r{round}.png"));
            var pixels=BmpPixels.TryRead(ImageBytes.ToBmp(image))!;
            var match=board.Recognize(pixels)!;
            var marks=reader.Read(pixels,match,true,true);
            var d4=Assert.Single(marks,m=>m.Slot.ToString()=="D4");
            state.Observe(match.Map.Id,round,marks,round*60000);
            var unit=Assert.Single(state.Roster.Units,u=>u.Mark.Slot==d4.Slot);
            if (round==3)
            {
                Assert.Null(d4.StandaloneName);
                Assert.Equal(support,state.ReviewShapeCandidates(d4)[0]);
                Assert.True(state.Confirm(unit.Id,d4.Slot,support,true));
                continue;
            }
            string expected=round==5?support:"Miss.Christine";
            Assert.Equal(expected,d4.StandaloneName);
            Assert.Equal(expected,unit.Name);
            if (round==10)
                Assert.Equal(support,Assert.Single(marks,m=>m.Slot==new FieldSlot(8,-1)).StandaloneName);
        }
    }

    [Trait("Category","LocalScreenshot")]
    [Theory]
    [InlineData(5,"B6","Miss.Christine",false,8,1)]
    [InlineData(9,"整备区右起第4席","赫德雷",false,8,8)]
    [InlineData(10,"整备区右起第6席","赫德雷",true,8,9)]
    public async Task SingleFrameKeepsLowSpritesAndBadgedBenchSeats(int round,string slot,string name,
        bool automaticName,int deployed,int benched)
    {
        var data=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await GarrisonBoardStore.LoadAsync(data))!;
        var reader=new LiveFieldReader(data,maps);
        var image=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"TestData","Board",$"short-field-r{round}.png"));
        var pixels=BmpPixels.TryRead(ImageBytes.ToBmp(image))!;
        var board=new BoardRecognizer(maps).Recognize(pixels)!;
        var marks=reader.Read(pixels,board,true,true);
        var target=Assert.Single(marks,m=>m.Slot.ToString()==slot);
        Assert.Equal(automaticName?name:null,target.StandaloneName);
        Assert.Equal(name,target.ShapeCandidates[0].Name);
        Assert.Equal(deployed,marks.Count(m=>!m.Slot.Bench));
        Assert.Equal(benched,marks.Count(m=>m.Slot.Bench));
        Assert.DoesNotContain(marks,m=>m.Slot==new FieldSlot(0,-1));
        Assert.Equal(marks.Select(m=>m.Slot.ToString()).Order(),
            reader.Read(pixels,board,false).Select(m=>m.Slot.ToString()).Order());
        var repeated=Assert.Single(reader.Read(pixels,board,true),m=>m.Slot.ToString()==slot);
        Assert.Equal(target.StandaloneName,repeated.StandaloneName);
        Assert.Equal(name,repeated.ShapeCandidates[0].Name);
        if (target.Slot.Bench) Assert.Same(target.ShapeCandidates,repeated.ShapeCandidates);
        if (round==9)
        {
            var nextImage=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"TestData","Board","short-field-r9-repeat.png"));
            var nextPixels=BmpPixels.TryRead(ImageBytes.ToBmp(nextImage))!;
            var changed=Assert.Single(reader.Read(nextPixels,board,true),m=>m.Slot==target.Slot);
            Assert.NotSame(target.ShapeCandidates,changed.ShapeCandidates);
            Assert.Equal(name,changed.StandaloneName);
        }
        if (round!=5) return;
        var state=new LiveFieldState(new Dictionary<string,int>{{name,2}});
        state.Observe(board.Map.Id,4,[],0);
        state.RecordConfirmedPurchases([name]);
        state.Observe(board.Map.Id,5,marks,60000);
        Assert.Equal(name,Assert.Single(state.Roster.Units,u=>u.Mark.Slot==target.Slot).Name);
    }

    [Fact]
    public void PurchaseNeedsDistinctVisualEvidenceAndExpiresOnSaleOrGameEnd()
    {
        const string cat="Miss.Christine";
        var state=new LiveFieldState(new Dictionary<string,int>{{cat,2},{"星极",2}});
        var candidate=new FieldShapeLibrary.Candidate(cat,2,.79);
        var mark=new FieldMark(new(3,1),2,new(200,160),new byte[90],null);
        var visible=mark with { LowPose=candidate,ShapeCandidates=[candidate] };
        state.RecordConfirmedPurchases([cat]);
        state.Observe("desert_camo",4,[mark],10);
        Assert.Null(Assert.Single(state.Roster.Units).Name);
        state.Observe("desert_camo",5,[visible,visible with {Slot=new(5,0)}],60000);
        Assert.All(state.Roster.Units,u=>Assert.Null(u.Name));
        state.LimitMembers(cat,0);
        state.Observe("desert_camo",6,[visible],120000);
        Assert.Null(Assert.Single(state.Roster.Units).Name);
        state.RecordConfirmedPurchases([cat]);
        state.Clear();
        state.Observe("desert_camo",1,[visible],180000);
        Assert.Null(Assert.Single(state.Roster.Units).Name);
        var unit=Assert.Single(state.Roster.Units);
        Assert.True(state.Confirm(unit.Id,unit.Mark.Slot,"星极",true));
        state.RecordConfirmedPurchases([cat]);
        state.Observe("desert_camo",2,[visible],240000);
        Assert.Equal("星极",Assert.Single(state.Roster.Units).Name);
    }

    [Trait("Category","LocalScreenshot")]
    [Fact]
    public async Task ColdScanLeavesConflictingSparseNameOpenForReview()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await GarrisonBoardStore.LoadAsync(path))!;
        var source=Path.Combine(AppContext.BaseDirectory,"TestData","Board","redfloor-memory-r9.png");
        var pixels=BmpPixels.TryRead(ImageBytes.ToBmp(File.ReadAllBytes(source)))!;
        var match=new BoardRecognizer(maps).Recognize(pixels)!;
        var mark=new LiveFieldReader(path,maps).Read(pixels,match,true,true).Single(m=>m.Slot.ToString()=="B7");
        Assert.Null(mark.StandaloneName);
        Assert.Contains("摆渡人",new LiveFieldState(new Dictionary<string,int>()).ReviewShapeCandidates(mark));
    }

    [Trait("Category","LocalScreenshot")]
    [Theory]
    [InlineData("palace",3,"A6 A7 B5 B6 B7","A6=灰喉 B5=灰喉 B7=慕斯")]
    [InlineData("palace",7,"A5 A6 A7 B5 B6 B7 B8 C7","B6=折桠")]
    [InlineData("palace",11,"B5 B6 B7 B8 C4 C5 C6 C7","C4=霍尔海雅")]
    [InlineData("cactus",4,"A5 A6 A7 B5 B7 C5","B7=幽灵鲨")]
    [InlineData("cactus",6,"A5 A6 A7 B5 B6 B7 C5 C6","A7=幽灵鲨 C6=莎草")]
    public async Task SingleFrameFindsDeployedCells(string map,int round,string occupied,string identities)
    {
        var path=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await GarrisonBoardStore.LoadAsync(path))!;
        var reader=new LiveFieldReader(path,maps);
        var image=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"TestData","Board",$"{map}-field-r{round}.png"));
        var pixels=BmpPixels.TryRead(ImageBytes.ToBmp(image))!;
        var board=new BoardRecognizer(maps).Recognize(pixels)!;
        var marks=reader.Read(pixels,board,true,true).Where(m=>!m.Slot.Bench).ToDictionary(m=>m.Slot.ToString());
        Assert.Equal(occupied.Split(' '),marks.Keys.Order().ToArray());
        foreach (string entry in identities.Split(' '))
        {
            var pair=entry.Split('=');
            Assert.Equal(pair[1],marks[pair[0]].StandaloneName);
        }
        var review=reader.Read(pixels,board,false).Where(m=>!m.Slot.Bench).Select(m=>m.Slot.ToString()).Order();
        Assert.Equal(occupied.Split(' '),review);
    }
}

