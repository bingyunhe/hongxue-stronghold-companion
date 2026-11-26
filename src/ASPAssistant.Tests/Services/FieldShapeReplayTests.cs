using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

public class FieldShapeReplayTests
{
    [Fact, Trait("Category", "LocalScreenshot")]
    public async Task PalaceRosmontisAndBenchOnRecordedRounds()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = (await GarrisonBoardStore.LoadAsync(data))!;
        var reader = new LiveFieldReader(data, maps);
        var board = new BoardRecognizer(maps);
        var state = new LiveFieldState((await new JsonDataStore(data).LoadOperatorsAsync())
            .ToDictionary(op => op.Name, op => op.Tier));
        long now = 0;
        foreach (var (round, image, slot) in new[] { (9, "r9", new FieldSlot(3, -1)), (10, "r10", new FieldSlot(1, 1)),
            (12, "r12", new FieldSlot(2, 0)), (12, "r12-overlay", new FieldSlot(2, 0)) })
        {
            var file = Path.Combine(AppContext.BaseDirectory, "TestData", "Board", $"palace-rosmontis-{image}.png");
            var pixels = BmpPixels.TryRead(ImageBytes.ToBmp(File.ReadAllBytes(file)))!;
            var match = board.Recognize(pixels)!;
            for (int scan = 0; scan < 2; scan++)
            {
                var marks = reader.Read(pixels, match, true, force: scan == 0);
                Assert.Equal(slot, Assert.Single(marks, m => m.StandaloneName == "迷迭香").Slot);
                state.Observe(match.Map.Id, round, marks, now += 1000);
                Assert.Equal(slot, Assert.Single(state.Roster.Units, u => u.Name == "迷迭香").Mark.Slot);
                if (round == 12)
                {
                    Assert.Equal(new[] { 1, 4, 0, 4, 0, 6, 2, 3, 4, 1 }, Enumerable.Range(0, 10)
                        .Select(col => marks.SingleOrDefault(m => m.Slot == new FieldSlot(col, -1))?.Tier ?? 0));
                    Assert.Equal("余", state.Roster.Units.Single(u => u.Mark.Slot == new FieldSlot(1, -1)).Name);
                }
            }
        }
    }

    [Fact, Trait("Category", "LocalScreenshot")]
    public async Task ReviewOrderOnRecordedBoards()
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_REVIEW_EXCLUSIONS") is not { } exclusions) return;
        string desert = Environment.GetEnvironmentVariable("ASPASSISTANT_DESERT_REPLAY")!;
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = (await GarrisonBoardStore.LoadAsync(data))!;
        var store = new JsonDataStore(data);
        var operators = (await store.LoadOperatorsAsync()).ToDictionary(op => op.Name);
        var custom = await store.LoadCustomOperatorsAsync();
        var selected = await new SettingsManager(Path.GetDirectoryName(exclusions)!).LoadCustomOperatorSelectionAsync();
        foreach (var op in custom)
        {
            op.IsSelectedCustom = selected.Contains(op.Name);
            operators.Add(op.Name, op);
        }
        using var ocr = new MaaOcrEngine(data);
        using var connection = new GameConnectionService(new() { Source = GameCaptureSource.Adb });
        var rows = new List<string>();
        foreach (var scenario in new[] { "本局", "short-field-r10", "short-field-r6", "cactus-field-r4", "cactus-field-r6", "city-round9-four-marks" })
        {
            byte[] image = [];
            using var scanner = new OcrScannerService(() => image, new ASPAssistant.Core.GameModes.GarrisonProtocol.GarrisonOcrStrategy(),
                new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
            {
                EconomyReader = new(ocr), BoardRecognizer = new(maps), LiveReader = new(data, maps),
                FieldTiers = operators.ToDictionary(p => p.Key, p => p.Value.Tier)
            };
            var banned = scenario == "本局" ? File.ReadAllLines(exclusions).ToHashSet() : new HashSet<string>();
            scanner.SetReviewExclusions(banned);
            foreach (var file in scenario == "本局" ? new[] { "091641", "092149", "092304", "093453" } : new[] { scenario })
            {
                image = File.ReadAllBytes(scenario == "本局" ? Path.Combine(desert, $"board-{file}.png") :
                    Path.Combine(AppContext.BaseDirectory, "TestData", "Board", file + ".png"));
                await scanner.ReadLiveFieldAsync(ownBoard: true);
                var roster = scanner.LiveRoster;
                Assert.NotEmpty(roster.Units);
                var state = (LiveFieldState)typeof(OcrScannerService).GetField("_liveState",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(scanner)!;
                var known = state.KnownReviewNames();
                var observation = roster.Observations[^1];
                var pixels = BmpPixels.TryRead(ImageBytes.ToBmp(image))!;
                if (scenario == "本局")
                {
                    var counter = await ocr.RecognizeRegionAsync(image, pixels.Width*82/100, pixels.Height*53/100,
                        pixels.Width*18/100, pixels.Height*13/100);
                    var digit = await ocr.RecognizeLineAsync(image, pixels.Width*983/1000, pixels.Height*574/1000,
                        pixels.Width*12/1000, pixels.Height*30/1000);
                    rows.Add($"{file} HUD={string.Join(" ", counter.Select(w => w.Text))} digit={string.Join(" ",digit.Select(w=>w.Text))}");
                }
                foreach (var unit in roster.Units.Where(u => u.Name == null))
                {
                    var mark = unit.Mark;
                    var slot = mark.Slot;
                    var board = scanner.BoardRecognizer.Recognize(pixels)!;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var ranked = scanner.GetLiveReviewCandidates(unit);
                    var reviewMs = watch.Elapsed.TotalMilliseconds;
                    var parts = scanner.LiveReader.ReviewVisibleParts(pixels, mark, known);
                    var heads = scanner.LiveReader.RankReviewCandidates(pixels, board, slot, banned);
                    var body = scanner.LiveReader.ReviewVisibleBody(pixels, mark, observation.Readings[slot].BadgeTier, roster.Units.Select(u=>u.Mark.Slot));
                    var shape = state.ReviewShapeCandidates(mark, banned);
                    var panel = (unit.Suggestion is {} hint ? new[]{hint.Name} : Array.Empty<string>()).Concat(ranked)
                        .Distinct().Where(n=>!banned.Contains(n) && operators.TryGetValue(n,out var op) && op.IsAvailableForSelection).Take(4);
                    rows.Add($"{file} {slot} review={reviewMs:F1}ms top={string.Join("、",ranked)} panel={string.Join("、",panel)} head={string.Join("、",heads)} body={body} parts={string.Join("、",parts)} shape={string.Join("、",shape)} raw={string.Join("、",mark.ShapeCandidates.Select(c=>c.Name))}");
                }
                if (file == "091641")
                    foreach (var (slot,name) in new[] { ("A5","烈夏"),("A6","摆渡人") })
                        Assert.True(scanner.SetLiveIdentity(roster.Units.Single(u=>u.Mark.Slot.ToString()==slot),name));
                if (file == "092149") state.RecordConfirmedPurchases(["摆渡人"]);
                if (file == "092304")
                    foreach (var (slot,name) in new[] { ("A4","折桠"),("A7","青枳") })
                        Assert.True(scanner.SetLiveIdentity(roster.Units.Single(u=>u.Mark.Slot.ToString()==slot),name));
            }
        }
        File.WriteAllLines(Path.Combine(TestOutput.Directory,"review-order.txt"), rows);
    }

    [Fact, Trait("Category", "LocalScreenshot")]
    public async Task DesertA4ReadsTierAndNameOnEachIndependentFrame()
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_DESERT_REPLAY") is not { } directory) return;
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = (await GarrisonBoardStore.LoadAsync(data))!;
        foreach (var (time, name) in new[] { ("091641", "烈夏"), ("092149", "折桠"), ("092304", "折桠") })
        {
            var reader = new LiveFieldReader(data, maps);
            var image = File.ReadAllBytes(Path.Combine(directory, $"board-{time}.png"));
            var pixels = BmpPixels.TryRead(ImageBytes.ToBmp(image))!;
            var board = new BoardRecognizer(maps).Recognize(pixels)!;
            var readings = new Dictionary<FieldSlot, SlotReading>();
            var a4 = reader.ReadWithTrace(pixels, board, true, true, CancellationToken.None, readings)
                .Single(m => m.Slot.ToString() == "A4");
            Assert.Equal(1, readings[a4.Slot].BadgeTier);
            Assert.Equal(1, a4.Tier);
            Assert.Equal(name, a4.StandaloneName);
        }
    }

    [Fact, Trait("Category", "LocalScreenshot")]
    public async Task TopRowAcceptsTheGoldNumeralFromTheRecordedBossRound()
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_DESERT_REPLAY") is not { } directory) return;
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = (await GarrisonBoardStore.LoadAsync(data))!;
        using var boardImage = new System.Drawing.Bitmap(Path.Combine(directory, "board-092304.png"));
        using var bossImage = new System.Drawing.Bitmap(Path.Combine(directory, "enemy-2-0.png"));
        // The recording has a genuine gold V lower on the board. Put just that badge
        // at the contested top-row location to exercise the same recognition branch.
        using (var graphics = System.Drawing.Graphics.FromImage(boardImage))
            graphics.DrawImage(bossImage, new System.Drawing.Rectangle(628, 125, 30, 30),
                new System.Drawing.Rectangle(664, 189, 33, 33), System.Drawing.GraphicsUnit.Pixel);
        var pixels = BmpPixels.TryRead(ImageBytes.ToBmp(ImageBytes.Encode(boardImage)))!;
        var board = new BoardRecognizer(maps).Recognize(pixels)!;
        var readings = new Dictionary<FieldSlot, SlotReading>();
        new LiveFieldReader(data,maps).ReadWithTrace(pixels,board,true,true,CancellationToken.None,readings);
        Assert.Equal(5, readings.Single(r=>r.Key.ToString()=="A4").Value.BadgeTier);
    }

    [Fact]
    public async Task DesertCopiesRetainNamesAcrossObservedMove()
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_DESERT_REPLAY") is not { } dir) return;
        var data=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await GarrisonBoardStore.LoadAsync(data))!;
        var reader=new LiveFieldReader(data,maps);
        var state=new LiveFieldState((await new JsonDataStore(data).LoadOperatorsAsync())
            .GroupBy(o=>o.Name).ToDictionary(g=>g.Key,g=>g.First().Tier));
        void Observe(string time,int round)
        {
            var px=BmpPixels.TryRead(ImageBytes.ToBmp(File.ReadAllBytes(Path.Combine(dir,$"board-{time}.png"))))!;
            var match=new BoardRecognizer(maps).Recognize(px)!;
            state.Observe(match.Map.Id,round,reader.Read(px,match,true,true),round*60000);
        }
        Observe("091641",2);
        foreach (var (slot,name) in new[] { ("A4","烈夏"),("A5","烈夏"),("A6","摆渡人") })
        {
            var u=state.Roster.Units.Single(u=>u.Mark.Slot.ToString()==slot);
            Assert.True(state.Confirm(u.Id,u.Mark.Slot,name,true));
        }
        Observe("092149",3);
        Assert.Equal("折桠",state.Roster.Units.Single(u=>u.Mark.Slot.ToString()=="A4").Name);
        Assert.Equal("烈夏",state.Roster.Units.Single(u=>u.Mark.Slot.ToString()=="A5").Name);
        Assert.Equal("烈夏",state.Roster.Units.Single(u=>u.Mark.Slot.ToString()=="B7").Name);
        Assert.Equal("摆渡人",state.Roster.Units.Single(u=>u.Mark.Slot.ToString()=="A6").Name);
    }

    [Theory]
    [InlineData("092304",7)]
    [InlineData("093453",8)]
    public async Task DesertTopRowKeepsBothAdjacentOperators(string time,int deployed)
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_DESERT_REPLAY") is not { } directory) return;
        var data=Path.Combine(AppContext.BaseDirectory,"data");
        var maps=(await GarrisonBoardStore.LoadAsync(data))!;
        var reader=new LiveFieldReader(data,maps);
        var pixels=BmpPixels.TryRead(ImageBytes.ToBmp(File.ReadAllBytes(Path.Combine(directory,$"board-{time}.png"))))!;
        var board=new BoardRecognizer(maps).Recognize(pixels)!;
        var readings=new Dictionary<FieldSlot,SlotReading>();
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var marks=reader.ReadWithTrace(pixels,board,true,true,CancellationToken.None,readings);
        File.WriteAllText(Path.Combine(TestOutput.Directory,$"desert-field-{time}.txt"),
            $"{watch.ElapsedMilliseconds}ms "+string.Join("; ",marks.Select(m=>$"{m.Slot}: {m.StandaloneName}")));
        Assert.Contains(marks,m=>m.Slot.ToString()=="A5" && m.StandaloneName=="摆渡人");
        Assert.Contains(marks,m=>m.Slot.ToString()=="A6" && m.StandaloneName=="摆渡人");
        Assert.Equal(deployed,marks.Count(m=>!m.Slot.Bench));
    }

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

