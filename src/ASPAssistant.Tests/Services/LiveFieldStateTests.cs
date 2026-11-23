using System.Drawing;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

public class LiveFieldStateTests
{
    [Fact]
    public async Task ManualCaptureWaitsForTheGameAfterForegroundHandoff()
    {
        IntPtr game = 42;
        var handles = new Queue<IntPtr>([7, 0, 0, game]);
        Assert.True(await OcrScannerService.WaitForGameForegroundAsync(game, handles.Dequeue, CancellationToken.None));
        Assert.Empty(handles);
        Assert.False(await OcrScannerService.WaitForGameForegroundAsync(game, () => 7, CancellationToken.None));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    public void IdleBodyCacheRejectsAChangeInEitherBand(int start)
    {
        var original = Enumerable.Repeat(0x303030, 17*20).ToArray();
        var moved = (int[])original.Clone();
        Assert.True(FieldMemoryAppearance.SameIdleBody(original,moved));
        for (int row=start; row<start+6; row++)
        for (int col=3; col<14; col++)
            moved[row*17+col] = 0xa03010;
        Assert.False(FieldMemoryAppearance.SameIdleBody(original,moved));
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public void ReviewedNameFollowsOnlyOneCorroboratedMove(bool ambiguous,bool newRound)
    {
        var state=State();
        state.RecordConfirmedPurchases(["烈夏","烈夏"]);
        var source=Mark(2,0,1,80) with { Portrait=[80,80,80], MemoryPatch=Enumerable.Repeat(0x505050,340).ToArray() };
        state.Observe("desert_crates",8,[source],0);
        Assert.True(state.ConfirmReviewName(state.Roster.Units.Single().Id,source.Slot,"烈夏"));
        var target=source with { Slot=new(4,0),Tier=0,ShapeCandidates=[new("烈夏",1,.65),new("陨星",1,.55)] };
        state.Observe("desert_crates",newRound?9:8,ambiguous?[target,target with {Slot=new(5,0)}]:[target],31000);
        Assert.All(state.Roster.Units,u=>
        {
            Assert.Equal(ambiguous||newRound?null:"烈夏",u.Name);
            Assert.Null(u.MemberId);
            if (u.Name!=null) Assert.Equal("本局复核外观匹配",u.IdentitySource);
        });
    }

    [Fact]
    public void ReviewingANameWithTwoPossibleCopiesDoesNotAssignOrCreateAMember()
    {
        var state=State();
        FieldMark Piece(int col,int row,byte shade) => Mark(col,row,1,shade) with
            {Portrait=[shade,shade,shade],MemoryPatch=Enumerable.Repeat(shade*0x010101,340).ToArray()};
        var bench=Piece(9,-1,20);
        var board=Piece(2,3,85);
        state.Observe("city",6,[bench,board],0);
        foreach (var u in state.Roster.Units.ToArray()) Assert.True(state.Confirm(u.Id,u.Mark.Slot,"陨星",true));
        var members=state.Roster.Units.Select(u=>u.MemberId!.Value).Order().ToArray();
        var target=Piece(4,3,190);
        state.Observe("city",10,[target],60000);
        var current=Assert.Single(state.Roster.Units);
        Assert.True(state.ConfirmReviewName(current.Id,target.Slot,"陨星"));
        state.Observe("city",10,[target],61000);
        var named=Assert.Single(state.Roster.Units);
        Assert.Equal(current.Id,named.Id);
        Assert.Equal("陨星",named.Name);
        Assert.Null(named.MemberId);
        Assert.Equal(members,state.Roster.Unlocated.Select(m=>m.Id).Order());
        state.Observe("city",10,[target with {Slot=new(5,3)}],61200);
        Assert.Null(Assert.Single(state.Roster.Units).Name);
        Assert.False(state.ConfirmReviewName(current.Id,target.Slot,"陨星"));
        var moved=Assert.Single(state.Roster.Units);
        Assert.True(state.ConfirmReviewName(moved.Id,moved.Mark.Slot,"陨星"));
        Assert.True(state.LimitMembers("陨星",0));
        Assert.Empty(state.Roster.Units);
        Assert.Empty(state.Roster.Unlocated);
    }

    [Trait("Category","LocalScreenshot")]
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task ConfirmedBenchUnitOffersReviewBeforeFollowingAnUnreadBadge(bool accept,bool repeat)
    {
        const string name="艾雅法拉";
        var data=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await ASPAssistant.Core.Data.GarrisonBoardStore.LoadAsync(data))!;
        var reader=new LiveFieldReader(data,maps);
        var board=new BoardRecognizer(maps);
        var state=new LiveFieldState(new Dictionary<string,int>{{name,4}});
        FieldSlot source=new(8,-1),dest=new(2,2); // 整备区右起第 2 席、C5
        Scan("7",7,0);
        var seat=Assert.Single(state.Roster.Units,u=>u.Mark.Slot==source);
        Assert.True(state.Confirm(seat.Id,source,name,false));
        long member=state.Roster.Units.Single(u=>u.Mark.Slot==source).MemberId!.Value;
        Scan("9",9,400000);
        var unit=Assert.Single(state.Roster.Units,u=>u.Mark.Slot==dest);
        Assert.Null(unit.Name);
        Assert.Equal(new FieldMemoryLocation(member,name,source),unit.Suggestion);
        if (accept) Assert.True(state.AcceptSuggestion(unit.Id,dest,member));
        if (repeat) Scan("9-repeat",9,428000);
        Scan("10",10,600000);
        var later=Assert.Single(state.Roster.Units,u=>u.Mark.Slot==dest);
        Assert.Equal(0,later.Mark.Tier);
        Assert.Equal(accept?name:null,later.Name);
        Assert.Equal(accept?(long?)member:null,later.MemberId);
        Assert.Null(later.Suggestion);
        if (accept) Assert.Equal(unit.Id,later.Id);
        Assert.DoesNotContain(state.Roster.Units,u=>u.MemberId==member && u.Mark.Slot!=dest);

        void Scan(string sample,int round,long now)
        {
            var file=Path.Combine(AppContext.BaseDirectory,"TestData","Board",$"short-field-r{sample}.png");
            var pixels=BmpPixels.TryRead(ImageBytes.ToBmp(File.ReadAllBytes(file)))!;
            var match=board.Recognize(pixels)!;
            state.Observe(match.Map.Id,round,reader.Read(pixels,match,true,true),now);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossZoneHintRequiresOneAvailableCopyAndOneVisuallySupportedDestination(bool toBench)
    {
        var state=State();
        var seat=Mark(toBench?2:8,toBench?2:-1,1,30) with {Portrait=[30,30,30],MemoryPatch=Enumerable.Repeat(0x1e1e1e,340).ToArray()};
        var dest=seat with {Slot=toBench?new(8,-1):new(2,2), ShapeCandidates=[new("陨星",1,.60),new("烈夏",1,.51)]};
        state.Observe("city",7,[seat],0);
        var unit=Assert.Single(state.Roster.Units);
        Assert.True(state.Confirm(unit.Id,seat.Slot,"陨星",false));
        long member=Assert.Single(state.Roster.Units).MemberId!.Value;
        state.Observe("city",8,[seat,dest],10000);
        Assert.Null(state.Roster.Units.Single(u=>u.Mark.Slot==dest.Slot).Suggestion);
        state.Observe("city",9,[dest,dest with {Slot=new(3,dest.Slot.Row)}],20000);
        Assert.All(state.Roster.Units,u=>{Assert.Null(u.Name);Assert.Null(u.Suggestion);});
        state.Observe("city",9,[dest with { ShapeCandidates=[new("烈夏",1,.65),new("陨星",1,.59)] }],22000);
        Assert.Null(Assert.Single(state.Roster.Units).Suggestion);
        state.Observe("city",9,[dest with {MemoryPatch=Enumerable.Repeat(0xc8c8c8,340).ToArray()}],22500);
        Assert.Null(Assert.Single(state.Roster.Units).Suggestion);
        state.Observe("palace",9,[dest],23000);
        Assert.Null(Assert.Single(state.Roster.Units).Suggestion);
        state.Observe("city",9,[dest],24000);
        var suggestion=Assert.Single(state.Roster.Units);
        Assert.Null(suggestion.Name);
        Assert.Equal(member,suggestion.Suggestion!.Id);
        state.RecordConfirmedPurchases(["陨星","陨星"]);
        state.Observe("city",9,[dest],25000);
        Assert.Null(Assert.Single(state.Roster.Units).Suggestion);
        Assert.False(state.AcceptSuggestion(suggestion.Id,dest.Slot,member));
        state.LimitMembers("陨星",1);
        state.Observe("city",9,[dest],26000);
        Assert.Equal(member,Assert.Single(state.Roster.Units).Suggestion!.Id);
        Assert.True(state.LimitMembers("陨星",0));
        Assert.False(state.AcceptSuggestion(suggestion.Id,dest.Slot,member));
        state.Observe("city",10,[dest],100000);
        Assert.Null(Assert.Single(state.Roster.Units).Suggestion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HiddenTierCannotCarryAcrossAReplacementOrACompetingPosition(bool replacement)
    {
        var state=State();
        var original=Mark(2,2,1,35) with {Portrait=[35,35,35],MemoryPatch=Enumerable.Repeat(0x232323,340).ToArray()};
        state.Observe("city",9,[original],0);
        var prior=Assert.Single(state.Roster.Units);
        Assert.True(state.Confirm(prior.Id,original.Slot,"陨星",true));
        var hidden=original with {Tier=0};
        state.Observe("city",10,[hidden],60000);
        Assert.Equal(prior.Id,Assert.Single(state.Roster.Units).Id);
        Assert.Equal("陨星",Assert.Single(state.Roster.Units).Name);
        List<FieldMark> marks=replacement
            ? [hidden with {MemoryPatch=Enumerable.Repeat(0xd0d0d0,340).ToArray()}]
            : [hidden,hidden with {Slot=new(3,2)}];
        state.Observe("city",11,marks,120000);
        Assert.All(state.Roster.Units,u=>{Assert.Null(u.Name);Assert.Null(u.MemberId);});
    }

    [Fact]
    public void TwoConfirmedCopiesOfTheSameNameDoNotOfferAnArbitraryBenchIdentity()
    {
        var state=State();
        var a=Mark(8,-1,1,35) with {Portrait=[35,35,35],MemoryPatch=Enumerable.Repeat(0x232323,340).ToArray()};
        var b=a with {Slot=new(6,-1)};
        state.Observe("city",7,[a,b],0);
        foreach (var unit in state.Roster.Units)
            Assert.True(state.Confirm(unit.Id,unit.Mark.Slot,"陨星",true));
        var dest=a with {Slot=new(2,2),ShapeCandidates=[new("陨星",1,.62),new("烈夏",1,.50)]};
        state.Observe("city",9,[dest],120000);
        Assert.Null(Assert.Single(state.Roster.Units).Suggestion);
        Assert.Equal(2,state.Roster.Unlocated.Count);
        state.Clear();
        state.Observe("city",1,[dest],140000);
        Assert.Null(Assert.Single(state.Roster.Units).Name);
        Assert.Null(Assert.Single(state.Roster.Units).Suggestion);
    }

    [Trait("Category","LocalScreenshot")]
    [Fact]
    public async Task RedFloorVerifiedMembersFollowTheCameraAndTheirNewCells()
    {
        var data=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await ASPAssistant.Core.Data.GarrisonBoardStore.LoadAsync(data))!;
        var board=new BoardRecognizer(maps);
        var reader=new LiveFieldReader(data,maps);
        var state=new LiveFieldState(new Dictionary<string,int>{{"摆渡人",1},{"星极",2},{"雪猎",2}});
        foreach(int round in new[]{1,2,4,9})
        {
            var path=Path.Combine(AppContext.BaseDirectory,"TestData","Board",$"redfloor-memory-r{round}.png");
            var pixels=BmpPixels.TryRead(ImageBytes.ToBmp(File.ReadAllBytes(path)))!;
            var match=board.Recognize(pixels)!;
            var marks=reader.Read(pixels,match,true,true);
            state.Observe(match.Map.Id,round,marks,round*60000);
            if(round==1) Confirm("C7","摆渡人",false);
            if(round==4) { Confirm("C5","星极",true); Confirm("C3","雪猎",false); }
        }
        foreach(var (slot,name) in new[]{("B7","摆渡人"),("C6","星极")})
        {
            var target=state.Roster.Units.Single(u=>u.Mark.Slot.ToString()==slot);
            Assert.True(target.Name==name,$"{slot}: {target.Name}, suggestion={target.Suggestion}, unlocated={string.Join(',',state.Roster.Unlocated)}");
            Assert.Single(state.Roster.Units,u=>u.Name==name);
        }
        var city=Path.Combine(AppContext.BaseDirectory,"TestData","Board","city-expanded-a6-174304.png");
        var otherPixels=BmpPixels.TryRead(ImageBytes.ToBmp(File.ReadAllBytes(city)))!;
        var otherBoard=board.Recognize(otherPixels)!;
        state.Observe(otherBoard.Map.Id,10,reader.Read(otherPixels,otherBoard,true,true),600000);
        Assert.DoesNotContain(state.Roster.Units.Where(u=>!u.Mark.Slot.Bench),u=>u.MemberId!=null);
        void Confirm(string slot,string name,bool manual)
        {
            var unit=state.Roster.Units.Single(u=>u.Mark.Slot.ToString()==slot);
            Assert.True(state.Confirm(unit.Id,unit.Mark.Slot,name,manual));
        }
    }

    [Fact]
    public void AmbiguousCopiesRequireOneCurrentConfirmationAndSaleRemovesTheMemory()
    {
        var state=State();
        var sample=Mark(0,2,1,30) with { Portrait=new byte[]{60,70,90} };
        state.Observe("city",1,[sample],0);
        var first=Assert.Single(state.Roster.Units);
        Assert.True(state.Confirm(first.Id,sample.Slot,"地灵",true));
        state.Observe("city",2,[],12000);
        var memory=Assert.Single(state.Roster.Unlocated);
        var left=sample with {Slot=new(1,2)};
        var right=sample with {Slot=new(2,2)};
        state.Observe("city",2,[left,right],20000);
        Assert.All(state.Roster.Units,u=>{Assert.Null(u.Name);Assert.Equal(memory,u.Suggestion);});
        var a=state.Roster.Units.Single(u=>u.Mark.Slot==left.Slot);
        var b=state.Roster.Units.Single(u=>u.Mark.Slot==right.Slot);
        Assert.True(state.AcceptSuggestion(a.Id,left.Slot,memory.Id));
        Assert.False(state.AcceptSuggestion(b.Id,right.Slot,memory.Id));
        Assert.Empty(state.Roster.Unlocated);
        Assert.True(state.LimitMembers("地灵",0));
        state.Observe("city",3,[left with {Portrait=new byte[]{200,210,220},StandaloneName="陨星"}],30000);
        Assert.Equal("陨星",Assert.Single(state.Roster.Units).Name);
        Assert.DoesNotContain(state.Roster.Units,u=>u.Name=="地灵" || u.Suggestion!=null);
        Assert.Empty(state.Roster.Unlocated);
        state.Clear();
        Assert.False(state.AcceptSuggestion(a.Id,left.Slot,memory.Id));
    }
    [Fact]
    public void ReplacingAnOccupiedCellDoesNotEraseThePreviousMemberOrNameItsReplacement()
    {
        var state=State();
        var old=Mark(2,2,1,60) with {Portrait=[10,20,30],MemoryPatch=Enumerable.Repeat(0x182838,340).ToArray()};
        var other=old with {Portrait=[190,180,170],MemoryPatch=Enumerable.Repeat(0xc8b8a8,340).ToArray()};
        state.Observe("city",1,[old],0);
        state.Confirm(state.Roster.Units[0].Id,old.Slot,"地灵",true);
        state.Observe("city",1,[other],200);
        var replacement=Assert.Single(state.Roster.Units);
        Assert.Null(replacement.Name);
        Assert.Null(replacement.Suggestion);
        var original=Assert.Single(state.Roster.Unlocated);
        state.Confirm(replacement.Id,other.Slot,"陨星",false);
        state.Observe("city",2,[other,old with {Slot=new(3,2)}],9000);
        Assert.Empty(state.Roster.Unlocated);
        Assert.Equal(original.Id,state.Roster.Units.Single(u=>u.Name=="地灵").MemberId);
        Assert.True(state.LimitMembers("地灵",0));
        Assert.Equal("陨星",Assert.Single(state.Roster.Units).Name);
    }

    private static LiveFieldState State() => new(new Dictionary<string,int> { ["砾"]=2, ["陨星"]=1, ["烈夏"]=1, ["地灵"]=1 });
    private static FieldMark Mark(int col,int row,int tier,byte shade,string? sprite=null) =>
        new(new(col,row),tier,new(50,80),Enumerable.Repeat(shade,90).ToArray(),sprite);
    private static ShopItem Card(string name,double x) => new() { Name=name, OcrRegion=(x,.92,.06,.03) };
    private static ShopEconomy Hud(int funds) => new(1,funds,null,false,"规程等级13",true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewlyBoughtCopyKeepsTheOriginalOnItsOccludedTile(bool observeBench)
    {
        using var image = new Bitmap(320,180);
        var frame = BmpPixels.TryRead(ImageBytes.Encode(image))!;
        var state = State();
        var original = Mark(4,1,1,50) with { StandaloneName = "烈夏" };
        var obscured = original with { StandaloneName = null };
        var deployed = Mark(2,3,1,160) with { StandaloneName = "烈夏" };
        state.Observe("city",3,[original],0);
        long originalId = Assert.Single(state.Roster.Units).Id;
        state.Observe("city",4,[original],1000);
        var filler = Card("装备",.25);
        state.Shop(frame,[filler,Card("烈夏",.53)],Hud(2) with { Round = 4 },true,1100);
        state.Shop(frame,[filler],Hud(0) with { Round = 4 },true,1200);
        long? purchasedId = null;
        if (observeBench)
        {
            state.Observe("city",4,[obscured, deployed with { Slot = new(9,-1) }],1250);
            purchasedId = state.Roster.Units.Single(u => u.Mark.Slot.Bench).Id;
        }
        state.Observe("city",4,[obscured,deployed],1400);
        Assert.Equal(originalId,state.Roster.Units.Single(u => u.Mark.Slot == original.Slot).Id);
        var copy = state.Roster.Units.Single(u => u.Mark.Slot == deployed.Slot);
        Assert.NotEqual(originalId,copy.Id);
        if (observeBench) Assert.Equal(purchasedId,copy.Id);
        Assert.All(state.Roster.Units,u => Assert.Equal("烈夏",u.Name));
        Assert.Equal("烈夏", Assert.Single(state.Roster.Bought));
        state.Observe("city",4,[deployed],1500);
        state.Observe("city",4,[original,deployed],1700);
        Assert.Equal(originalId,state.Roster.Units.Single(u => u.Mark.Slot == original.Slot).Id);
        Assert.Equal(copy.Id,state.Roster.Units.Single(u => u.Mark.Slot == deployed.Slot).Id);
    }

    [Fact]
    public void MovingTheOnlyCopyFromB7ToD5RetainsItsIdentity()
    {
        var state = State();
        var original = Mark(4,1,1,50) with { StandaloneName = "烈夏" };
        state.Observe("city",3,[original],1000);
        var id = Assert.Single(state.Roster.Units).Id;
        state.Observe("city",4,[original with { Slot = new(2,3) }],2000);
        var moved = Assert.Single(state.Roster.Units);
        Assert.Equal(id,moved.Id);
        Assert.Equal("烈夏",moved.Name);
    }

    [Fact] public void BatchKeepsDuplicatesAndDoesNotInferOrderFromShopSlots()
    {
        using var black=new Bitmap(320,180);
        var pixels=BmpPixels.TryRead(ImageBytes.Encode(black))!;
        var s=State();
        s.Observe("map",1,[],0);
        s.Shop(pixels,[Card("杰克",.25),Card("陨星",.4),Card("烈夏",.53)],Hud(4),true,100);
        s.Shop(pixels,[Card("杰克",.25)],Hud(0),true,1000);
        s.Observe("map",1,[Mark(8,-1,1,180),Mark(9,-1,1,20)],1000);
        Assert.Equal(new[]{"陨星","烈夏"},s.Roster.Bought);
        Assert.All(s.Roster.Units,u=>Assert.Null(u.Name));
        s.Observe("map",1,[Mark(8,-1,1,180,"烈夏"),Mark(9,-1,1,20,"陨星")],1200);
        s.Observe("map",1,[Mark(8,-1,1,183,"烈夏"),Mark(9,-1,1,23,"陨星")],1300);
        Assert.Equal("烈夏",s.Roster.Units[0].Name); Assert.Equal("陨星",s.Roster.Units[1].Name);
        s.Clear();
        s.Shop(pixels,[Card("杰克",.25),Card("砾",.4),Card("砾",.53)],Hud(6),true,10);
        s.Shop(pixels,[Card("杰克",.25)],Hud(0),true,600);
        s.Observe("map",1,[],610);
        Assert.Equal(new[]{"砾","砾"},s.Roster.Bought);
    }

    [Fact] public void FinalCorrectionFollowsOneMoveAcrossRoundsAndExpiresOnSaleOrNewGame()
    {
        var state=State();
        var bench=Mark(7,-1,2,120);
        state.Observe("map",1,[bench],0);
        long id=Assert.Single(state.Roster.Units).Id;
        Assert.True(state.Confirm(id,bench.Slot,"砾",true));
        var board=Mark(2,1,2,124);
        state.Observe("map",2,[board],220);
        var moved=Assert.Single(state.Roster.Units);
        Assert.Equal(id,moved.Id); Assert.True(moved.Manual); Assert.Equal("砾",moved.Name);
        Assert.False(state.Confirm(id,bench.Slot,"烈夏",true));
        Assert.False(state.Confirm(id,board.Slot,"烈夏",false));
        state.Observe("map",2,[],500);
        state.Observe("map",2,[board],800);
        Assert.NotEqual(id,Assert.Single(state.Roster.Units).Id);
        state.Clear(); Assert.Empty(state.Roster.Units); Assert.False(state.Confirm(id,board.Slot,"砾",true));
    }

    [Fact] public void BlankShopDuringPaidRefreshDoesNotClaimEveryCard()
    {
        using var black=new Bitmap(320,180);
        var pixels=BmpPixels.TryRead(ImageBytes.Encode(black))!;
        var s=State();
        s.Observe("map",1,[],0);
        s.Shop(pixels,[Card("陨星",.4),Card("烈夏",.53)],Hud(4),true,100);
        s.Shop(pixels,[],Hud(3),true,200);
        s.Observe("map",1,[],210);
        Assert.Empty(s.Roster.Bought);
    }

    private static LiveFieldState BuyPair(BmpPixels frame, string second = "烈夏", params FieldMark[] original)
    {
        var state = State();
        state.Observe("map", 1, original.ToList(), 0);
        var survivor = Card("装备", .25);
        state.Shop(frame, [survivor, Card("陨星", .40), Card(second, .53)], Hud(4), true, 100);
        state.Shop(frame, [survivor], Hud(0), true, 200);
        return state;
    }

    [Fact]
    public void ConfirmedPairUsesBothShapesAndRequiresAFreshObservation()
    {
        using var black=new Bitmap(320,180);
        var state=BuyPair(BmpPixels.TryRead(ImageBytes.Encode(black))!);
        var left=Mark(0,3,1,50) with { ShapeCandidates=[new("陨星",1,.69),new("烈夏",1,.58)] };
        var right=Mark(1,3,1,150) with { ShapeCandidates=[new("烈夏",1,.68),new("陨星",1,.60)] };
        state.Observe("map",1,[left,right],7300);
        state.Observe("map",1,[left,right],7800);
        Assert.All(state.Roster.Units,u=>Assert.Null(u.Name));
        var fresh=Mark(0,3,1,53) with { ShapeCandidates=[..left.ShapeCandidates] };
        state.Observe("map",1,[fresh,right],8100);
        Assert.Equal(new[]{"陨星","烈夏"},state.Roster.Units.OrderBy(u=>u.Mark.Slot.Col).Select(u=>u.Name));
    }

    [Fact] public void MissedBenchAndDelayedSpriteResolveTheBoardAndManualReviewCanReverseThePair()
    {
        using var black = new Bitmap(320,180);
        var state = BuyPair(BmpPixels.TryRead(ImageBytes.Encode(black))!);
        var left = Mark(0,3,1,50,"陨星");
        var right = Mark(1,3,1,150);
        state.Observe("map",1,[left,right],7300);
        Assert.All(state.Roster.Units,u=>Assert.Null(u.Name));
        state.Observe("map",1,[left,right],7750);
        Assert.All(state.Roster.Units,u=>Assert.Null(u.Name));
        state.Observe("map",1,[Mark(0,3,1,53,"陨星"),right],8000);
        var inferred = state.Roster.Units.Single(u=>u.Mark.Slot == right.Slot);
        Assert.Equal("烈夏",inferred.Name);
        Assert.True(inferred.FromPurchaseList);
        Assert.True(state.Confirm(inferred.Id,right.Slot,"陨星",false));
        Assert.Equal("烈夏",state.Roster.Units.Single(u=>u.Mark.Slot == left.Slot).Name);
        Assert.True(state.Confirm(inferred.Id,right.Slot,"烈夏",true));
        Assert.Equal("陨星",state.Roster.Units.Single(u=>u.Mark.Slot == left.Slot).Name);
        Assert.False(state.Confirm(inferred.Id,right.Slot,"陨星",false));
        state.Observe("map",2,[right,left],12000);
        Assert.True(state.Roster.Units.Single(u=>u.Mark.Slot == right.Slot).Manual);
        state.Clear();
        state.Observe("map",1,[left,right],14000);
        Assert.All(state.Roster.Units,u=>Assert.Null(u.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAdditionalUnknownSeatCannotBreakTheTie(bool seatWasAlreadyOccupied)
    {
        using var black = new Bitmap(320,180);
        var bench = Mark(9,-1,1,230);
        var state = BuyPair(BmpPixels.TryRead(ImageBytes.Encode(black))!, "烈夏", seatWasAlreadyOccupied ? [bench] : []);
        var marks = new List<FieldMark> {Mark(0,3,1,20,"陨星"),Mark(1,3,1,110),bench};
        state.Observe("map",1,marks,5000);
        marks[0] = Mark(0,3,1,23,"陨星");
        state.Observe("map",1,marks,5200);
        Assert.Null(state.Roster.Units.Single(u=>u.Mark.Slot == new FieldSlot(1,3)).Name);
        var other = state.Roster.Units.Single(u=>u.Mark.Slot == bench.Slot);
        Assert.True(state.Confirm(other.Id,bench.Slot,"地灵",true));
        Assert.Equal("烈夏",state.Roster.Units.Single(u=>u.Mark.Slot == new FieldSlot(1,3)).Name);
    }

    [Fact] public void LateUnknownSeatWithdrawsTheEarlierInference()
    {
        using var black = new Bitmap(320,180);
        var state = BuyPair(BmpPixels.TryRead(ImageBytes.Encode(black))!);
        var left = Mark(0,3,1,20,"陨星");
        var right = Mark(1,3,1,110);
        state.Observe("map",1,[left,right],210);
        state.Observe("map",1,[Mark(0,3,1,22,"陨星"),right],420);
        Assert.Equal("烈夏",state.Roster.Units.Single(u=>u.Mark.Slot == right.Slot).Name);
        state.Observe("map",1,[left,right,Mark(9,-1,1,200)],630);
        Assert.Null(state.Roster.Units.Single(u=>u.Mark.Slot == right.Slot).Name);
    }

    [Fact] public void TwoCopiesResolveWithoutAssumingSlotOrderAndASaleDoesNotNameTheReplacement()
    {
        using var black = new Bitmap(320,180);
        var state = BuyPair(BmpPixels.TryRead(ImageBytes.Encode(black))!,"陨星");
        var board = Mark(0,3,1,20);
        var bench = Mark(9,-1,1,230);
        state.Observe("map",1,[bench,board],2000);
        Assert.All(state.Roster.Units,u=>Assert.Equal("陨星",u.Name));
        state.Observe("map",1,[board],2600);
        state.Observe("map",1,[board,Mark(9,-1,1,100)],4200);
        Assert.Equal("陨星",state.Roster.Units.Single(u=>!u.Mark.Slot.Bench).Name);
        Assert.Null(state.Roster.Units.Single(u=>u.Mark.Slot.Bench).Name);
    }

    [Fact] public void TwoIndependentPurchasesFollowObservedOrderWhenBenchSlotsChange()
    {
        using var black = new Bitmap(320,180);
        var frame = BmpPixels.TryRead(ImageBytes.Encode(black))!;
        var state = State();
        var survivor = Card("装备",.25);
        var second = Card("陨星",.40);
        state.Observe("map",1,[],1);
        state.Shop(frame,[survivor,second,Card("烈夏",.53)],Hud(4),true,10);
        state.Shop(frame,[survivor,second],Hud(2),true,20);
        state.Observe("map",1,[Mark(9,-1,1,30)],21);
        state.Shop(frame,[survivor],Hud(0),true,30);
        state.Observe("map",1,[Mark(8,-1,1,200),Mark(0,3,1,33)],32);
        Assert.Equal("烈夏",state.Roster.Units.Single(u=>!u.Mark.Slot.Bench).Name);
        Assert.Equal("陨星",state.Roster.Units.Single(u=>u.Mark.Slot.Bench).Name);
    }

    [Fact] public void AmbiguousSimultaneousMovesPreserveThePairWithoutGuessingItsOrder()
    {
        using var black = new Bitmap(320,180);
        var state = BuyPair(BmpPixels.TryRead(ImageBytes.Encode(black))!);
        state.Observe("map",1,[Mark(9,-1,1,100),Mark(8,-1,1,105)],220);
        var left = Mark(0,3,1,102);
        var right = Mark(1,3,1,104);
        state.Observe("map",1,[right,left],500);
        Assert.All(state.Roster.Units,u=>Assert.Null(u.Name));
        var verified = state.Roster.Units.Single(u=>u.Mark.Slot == left.Slot);
        state.Confirm(verified.Id,left.Slot,"陨星",true);
        Assert.Equal("烈夏",state.Roster.Units.Single(u=>u.Mark.Slot == right.Slot).Name);
    }

    [Fact] public void TwoOccupiedSlotsBeforeDebitRejectAnOutOfBatchSpriteAndRequireIndependentPairEvidence()
    {
        using var black = new Bitmap(320,180);
        var frame = BmpPixels.TryRead(ImageBytes.Encode(black))!;
        var state = State();
        var old = Mark(7,0,1,180,"地灵") with { StandaloneName="地灵" };
        state.Observe("map",1,[old],0);
        state.Observe("map",1,[old],50);
        var filler = Card("装备",.25);
        state.Shop(frame,[filler,Card("陨星",.4),Card("烈夏",.53)],Hud(4),true,100);
        var left = Mark(0,3,1,60,"地灵") with { Scores = new Dictionary<string,double> { ["陨星"]=45, ["烈夏"]=63 } };
        var right = Mark(1,3,1,90,"地灵") with { Scores = new Dictionary<string,double> { ["陨星"]=50, ["烈夏"]=53 } };
        state.Shop(frame,[filler],Hud(4),true,200);
        state.Observe("map",1,[old,left,right],201);
        state.Observe("map",1,[old,left,right],301);
        Assert.All(state.Roster.Units.Where(u=>u.Mark.Slot.Row==3),u=>Assert.Null(u.Name));
        Assert.Equal("地灵",state.Roster.Units.Single(u=>u.Mark.Slot==old.Slot).Name);
        state.Shop(frame,[filler],Hud(0),true,310);
        state.Observe("map",1,[old,left,right],311);
        state.Observe("map",1,[old,left,right],410);
        Assert.All(state.Roster.Units.Where(u=>u.Mark.Slot.Row==3),u=>Assert.Null(u.Name));
        var different = left with { Appearance=Enumerable.Repeat((byte)68,90).ToArray(),
            Scores=new Dictionary<string,double>{["陨星"]=46,["烈夏"]=62} };
        state.Observe("map",1,[old,different,right],510);
        Assert.Equal("陨星",state.Roster.Units.Single(u=>u.Mark.Slot==left.Slot).Name);
        var inferred=state.Roster.Units.Single(u=>u.Mark.Slot==right.Slot);
        Assert.Equal("烈夏",inferred.Name);
        Assert.True(inferred.FromPair);
        state.Confirm(inferred.Id,right.Slot,"陨星",false);
        Assert.Equal("烈夏",state.Roster.Units.Single(u=>u.Mark.Slot==left.Slot).Name);
        Assert.Equal("游戏详情已核实",state.Roster.Units.Single(u=>u.Id==inferred.Id).IdentitySource);
    }

    [Fact] public void PurchasedPoseBreaksATieOnlyAfterAnotherObservation()
    {
        using var black = new Bitmap(320,180);
        var state = BuyPair(BmpPixels.TryRead(ImageBytes.Encode(black))!);
        var flat = new Dictionary<string,double> { ["陨星"] = 50, ["烈夏"] = 50 };
        var left = Mark(0,3,1,40) with { Scores = flat };
        var right = Mark(1,3,1,90) with { Scores = flat };
        int comparisons = 0;
        void Sample(FieldMark mark, string name)
        {
            Assert.Contains(name, flat.Keys);
            (mark.PurchasePoseScores ??= [])[name] = mark.Slot == left.Slot && name == "陨星" ? 22 : 55;
            comparisons++;
        }
        state.Observe("map",1,[left,right],300,Sample);
        state.Observe("map",1,[left,right],400,Sample);
        Assert.All(state.Roster.Units, u => Assert.Null(u.Name));
        Assert.Equal(4, comparisons);
        var fresh = Mark(0,3,1,47) with { Scores = new Dictionary<string,double>(flat) };
        state.Observe("map",1,[fresh,right],500,Sample);
        Assert.Equal(6, comparisons);
        Assert.Equal(new[] {"陨星","烈夏"}, state.Roster.Units.Select(u => u.Name));
    }

    [Fact] public void OverlappingMovesAreNotAssignedByEnumerationOrder()
    {
        var s=State();
        s.Observe("map",1,[Mark(8,-1,1,100),Mark(9,-1,1,101)],0);
        foreach(var u in s.Roster.Units) Assert.True(s.Confirm(u.Id,u.Mark.Slot,u.Mark.Slot.Col==8?"陨星":"烈夏",true));
        s.Observe("map",1,[Mark(0,0,1,102),Mark(0,1,1,103)],200);
        Assert.All(s.Roster.Units,u=> { Assert.Null(u.Name); Assert.False(u.Manual); });
    }

    [Fact] public void FourUnitsRecoverAfterPartialLossWithoutBorrowingUnrelatedPurchases()
    {
        using var black = new Bitmap(320,180);
        var frame = BmpPixels.TryRead(ImageBytes.Encode(black))!;
        var state = new LiveFieldState(new Dictionary<string,int> { ["霜叶"]=1, ["幽灵鲨"]=1, ["褐果"]=1, ["摆渡人"]=1, ["卡达"]=1 });
        var frost = Mark(3,2,1,35,"卡达") with { StandaloneName="霜叶" };
        var shark = Mark(5,2,1,190);
        var chestnut = Mark(3,1,1,75,"卡达");
        var ferry = Mark(4,2,1,130);
        state.Observe("map",1,[frost,shark],0);
        state.Confirm(state.Roster.Units[1].Id,shark.Slot,"幽灵鲨",false);
        var item = Card("装备",.25);
        state.Shop(frame,[item,Card("摆渡人",.4),Card("褐果",.53)],Hud(4),true,50);
        state.Shop(frame,[item],Hud(0),true,100);
        state.Observe("map",1,[frost,chestnut,ferry,shark],150);
        var ids = state.Roster.Units.ToDictionary(u=>u.Mark.Slot,u=>u.Id);
        state.Observe("map",1,[frost,shark],230);
        Assert.Equal(2,state.Roster.Units.Count);
        state.Observe("map",1,[shark,ferry,chestnut,frost],4330);
        Assert.All(state.Roster.Units,u=>Assert.Equal(ids[u.Mark.Slot],u.Id));
        Assert.DoesNotContain(state.Roster.Units,u=>u.Name=="卡达");
        Assert.True(state.Confirm(ids[ferry.Slot],ferry.Slot,"摆渡人",false));
        Assert.Equal("褐果",state.Roster.Units.Single(u=>u.Mark.Slot==chestnut.Slot).Name);
        Assert.Equal("霜叶",state.Roster.Units.Single(u=>u.Mark.Slot==frost.Slot).Name);
    }

    [Fact] public void JointMovesResolveForcedPairsAndRecognizableSwaps()
    {
        var state=State();
        var a=Mark(2,2,1,20);
        var b=Mark(3,2,1,44);
        state.Observe("map",1,[a,b],0);
        var first=state.Roster.Units[0].Id;
        var second=state.Roster.Units[1].Id;
        state.Confirm(first,a.Slot,"地灵",true);
        state.Confirm(second,b.Slot,"烈夏",true);
        var c=Mark(5,2,1,28);
        var d=Mark(6,2,1,60);
        state.Observe("map",1,[c,d],100);
        Assert.Equal(first,state.Roster.Units.Single(u=>u.Mark.Slot==c.Slot).Id);
        Assert.Equal(second,state.Roster.Units.Single(u=>u.Mark.Slot==d.Slot).Id);
        state.Observe("map",2,[c with { StandaloneName="烈夏" },d with { StandaloneName="地灵" }],200);
        Assert.Equal(second,state.Roster.Units.Single(u=>u.Mark.Slot==c.Slot).Id);
        Assert.Equal(first,state.Roster.Units.Single(u=>u.Mark.Slot==d.Slot).Id);
    }

    [Fact] public void VerifiedNormalViewReidentifiesAUnitAfterTrackingIsLostAndClearsOnNewGame()
    {
        var state=State();
        var original=Mark(3,1,1,15) with { Portrait=new byte[]{90,95,105} };
        var closeup=original with { Portrait=new byte[]{35,70,110} };
        state.Observe("map",1,[original],0);
        state.Confirm(state.Roster.Units[0].Id,original.Slot,"地灵",false,closeup);
        state.Observe("map",1,[],100);
        var moved=Mark(5,2,1,160) with { Portrait=new byte[]{36,70,109} };
        state.Observe("map",2,[moved],8000);
        Assert.Equal("地灵",state.Roster.Units[0].Name);
        Assert.False(state.Roster.Units[0].Final);
        state.Clear();
        state.Observe("map",1,[moved],9000);
        Assert.Null(state.Roster.Units[0].Name);
    }

    [Fact] public void VerifiedAppearancesResolveSwappedNeighborsDespiteIdenticalSparseSamples()
    {
        var state=State();
        var left=Mark(1,1,1,90) with { Portrait=new byte[]{20,30,40} };
        var right=Mark(1,2,1,90) with { Portrait=new byte[]{140,150,160} };
        state.Observe("map",1,[left,right],0);
        long a=state.Roster.Units[0].Id, b=state.Roster.Units[1].Id;
        state.Confirm(a,left.Slot,"地灵",false);
        state.Confirm(b,right.Slot,"烈夏",true);
        var swapped=new List<FieldMark>{left with { Portrait=right.Portrait },right with { Portrait=left.Portrait }};
        state.Observe("map",2,swapped,500);
        state.Observe("map",2,swapped,700);
        Assert.Equal(b,state.Roster.Units.Single(u=>u.Mark.Slot==left.Slot).Id);
        Assert.Equal(a,state.Roster.Units.Single(u=>u.Mark.Slot==right.Slot).Id);
        Assert.True(state.Roster.Units.Single(u=>u.Id==b).Manual);
    }

    [Fact] public void OneVisualReferenceDoesNotNameTwoIndistinguishableNewUnits()
    {
        var state=State();
        var unit=Mark(0,2,1,15) with { Portrait=new byte[]{60,70,90} };
        state.Observe("map",1,[unit],0);
        state.Confirm(state.Roster.Units[0].Id,unit.Slot,"地灵",true);
        state.Observe("map",1,[],100);
        state.Observe("map",2,[unit with { Slot=new(1,2) },unit with { Slot=new(2,2) }],8000);
        Assert.All(state.Roster.Units,u=>Assert.Null(u.Name));
    }

    [Fact] public void JoiningMidRoundReadsBoardAndBenchWithoutInventingPurchases()
    {
        var state=State();
        var board=Mark(0,3,1,55,"卡达") with { StandaloneName="霜叶" };
        var bench=Mark(9,-1,1,130,"卡达") with { StandaloneName="卡达" };
        var uncertain=Mark(1,3,1,90,"卡达");
        state.Observe("map",7,[board,bench,uncertain],100);
        state.Observe("map",7,[board,bench,uncertain],200);
        Assert.Empty(state.Roster.Bought);
        Assert.Equal(new string?[]{"霜叶","卡达",null},state.Roster.Units.Select(u=>u.Name));
        state.Observe("map",7,[board,bench,uncertain with { StandaloneName="卡达" }],300);
        Assert.Equal(2,state.Roster.Units.Count(u=>u.Name=="卡达"));
    }
}
