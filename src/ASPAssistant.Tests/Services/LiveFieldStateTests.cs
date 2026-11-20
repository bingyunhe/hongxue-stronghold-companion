using System.Drawing;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

public class LiveFieldStateTests
{
    private static LiveFieldState State() => new(new Dictionary<string,int> { ["砾"]=2, ["陨星"]=1, ["烈夏"]=1, ["地灵"]=1 });
    private static FieldMark Mark(int col,int row,int tier,byte shade,string? sprite=null) =>
        new(new(col,row),tier,new(50,80),Enumerable.Repeat(shade,90).ToArray(),sprite);
    private static ShopItem Card(string name,double x) => new() { Name=name, OcrRegion=(x,.92,.06,.03) };
    private static ShopEconomy Hud(int funds) => new(1,funds,null,false,"规程等级13",true);

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
