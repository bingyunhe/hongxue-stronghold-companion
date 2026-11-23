namespace ASPAssistant.Core.Services;

internal sealed partial class LiveFieldState
{
    private sealed class Member(long id,string name,FieldSlot slot,string map)
    {
        internal long Id => id;
        internal string Name => name;
        internal string Map => map;
        internal FieldSlot LastSlot = slot;
        internal FieldMemoryLocation Location => new(id,name,LastSlot);
        internal readonly List<FieldMark> Samples = [];
        internal readonly Dictionary<FieldSlot,(FieldMark Mark,double Score)> Recent = [];

        internal double Score(FieldMark mark)
        {
            if (Recent.TryGetValue(mark.Slot,out var last) && last.Mark.Tier==mark.Tier && last.Mark.StandaloneName==mark.StandaloneName &&
                last.Mark.Camera==mark.Camera && ReferenceEquals(last.Mark.ShapeCandidates,mark.ShapeCandidates) &&
                ReferenceEquals(last.Mark.Portrait,mark.Portrait) &&
                ReferenceEquals(last.Mark.MemoryPatch,mark.MemoryPatch)) return last.Score;
            double best=40;
            foreach (var sample in Samples)
            {
                if (sample.Tier!=mark.Tier && !(mark.Tier==0 && sample.Camera==mark.Camera && sample.Slot.Bench==mark.Slot.Bench &&
                    mark.ShapeCandidates is [var first,var second,..] && first.Name==Name && first.Tier==sample.Tier &&
                    first.Score>=.55 && first.Score-second.Score>=.065)) continue;
                if (sample.Slot.Bench!=mark.Slot.Bench && mark.StandaloneName!=Name) continue;
                if (mark.Portrait.Length>0 && sample.Portrait.Length==mark.Portrait.Length)
                {
                    double raw=Difference(sample.Portrait,mark.Portrait);
                    if (raw<=16) { best=Math.Min(best,raw); continue; }
                }
                if (sample.MemoryPatch.Length>0 && mark.MemoryPatch.Length>0)
                    best=Math.Min(best,FieldMemoryAppearance.Distance(sample.MemoryPatch,mark.MemoryPatch));
            }
            Recent[mark.Slot]=(mark,best);
            return best;
        }
    }

    private readonly Dictionary<long,Member> _members=[];
    private readonly Dictionary<FieldSlot,(Member Member,bool Strong)> _memoryChoices=[];
    private long _nextMemberId;

    private void MatchMemory(List<FieldMark> marks)
    {
        _memoryChoices.Clear();
        if (_members.Count==0) return;
        var pairs=new List<(FieldMark Mark,Member Member,double Score)>();
        foreach (var mark in marks)
        foreach (var member in _members.Values)
        {
            // A learned pose can outvote all of the base sprites. Keep that competing
            // label when the base sprites also support it, including board-specific looks.
            if (mark.StandaloneName is string label && label!=member.Name &&
                (mark.Scores==null || !mark.Scores.TryGetValue(label,out double modelError) ||
                mark.Scores.Values.Count(v=>v<modelError)<4)) continue;
            double score=member.Score(mark);
            if (score<40) pairs.Add((mark,member,score));
        }
        foreach (var group in pairs.GroupBy(p=>p.Mark.Slot))
        {
            var best=group.MinBy(p=>p.Score);
            if (best.Score>=38) continue;
            if (group.Any(p=>p.Member!=best.Member && p.Score<best.Score+5)) continue;
            var mark=best.Mark;
            double margin=best.Score<=31 ? 5 : 7;
            bool ambiguous=pairs.Any(p=>(p.Member==best.Member && p.Mark.Slot!=mark.Slot ||
                p.Member!=best.Member && p.Mark.Slot==mark.Slot) && p.Score<best.Score+margin);
            bool modelAgrees=mark.StandaloneName==best.Member.Name || mark.Scores!=null &&
                mark.Scores.TryGetValue(best.Member.Name,out double error) && mark.Scores.Values.Count(v=>v<error)<4;
            bool strong=!ambiguous && (best.Score<=16 || best.Score<=34 && modelAgrees);
            // Reappearance in a previously vacated slot can also be a new copy after a sale.
            if (mark.Slot==best.Member.LastSlot && !_observed.Units.Any(u=>u.MemberId==best.Member.Id && u.Mark.Slot==mark.Slot)
                && mark.StandaloneName!=best.Member.Name) strong=false;
            if (strong || best.Score<31 || modelAgrees)
                _memoryChoices.Add(mark.Slot,(best.Member,strong));
        }
    }

    private bool CanCarry(LiveUnit previous, FieldMark mark)
    {
        if (previous.MemberId is not long id || !_members.TryGetValue(id,out var member)) return true;
        if (_observed.Units.Any(u=>u.MemberId==id && u.Id!=previous.Id)) return false;
        if (mark.StandaloneName!=null && mark.StandaloneName!=member.Name) return false;
        if (_memoryChoices.TryGetValue(mark.Slot,out var choice) && choice.Strong) return choice.Member==member;
        if (_memoryChoices.Values.Any(c=>c.Strong && c.Member==member)) return false;
        if (mark.MemoryPatch.Length==0 || previous.Mark.MemoryPatch.Length==0) return true;
        return previous.Mark.Slot==mark.Slot && previous.Mark.Camera==mark.Camera &&
            Difference(previous.Mark.Portrait,mark.Portrait)<=16;
    }

    // A hidden badge can preserve a confirmed identity at the same position.
    // Use the player's confirmed image as well as the preceding observation to prevent drift.
    private bool KeepsConfirmedPosition(Member member,FieldMark previous,FieldMark mark,List<FieldMark> marks)
    {
        if (member.Map!=_observed.Map || previous.Camera!=mark.Camera || mark.MemoryPatch.Length==0 ||
            mark.StandaloneName!=null && mark.StandaloneName!=member.Name) return false;
        if (_confirmedCopies.GetValueOrDefault(member.Name)>1) return false;
        if (_memoryChoices.TryGetValue(mark.Slot,out var choice) && choice.Strong && choice.Member!=member ||
            _memoryChoices.Any(p=>p.Key!=mark.Slot && p.Value.Strong && p.Value.Member==member)) return false;
        var sample=member.Samples.LastOrDefault(s=>s.Tier!=0 && s.Slot==mark.Slot && s.Camera==mark.Camera && s.MemoryPatch.Length>0);
        if (sample==null) return false;
        double score=FieldMemoryAppearance.Distance(sample.MemoryPatch,mark.MemoryPatch);
        if (score>=24 || FieldMemoryAppearance.Distance(previous.MemoryPatch,mark.MemoryPatch)>=24) return false;
        if (_members.Values.Any(other=>other!=member && (other.Name==member.Name ||
            other.Samples.Any(s=>s.Slot.Bench==mark.Slot.Bench && s.Camera==mark.Camera && s.MemoryPatch.Length>0 &&
                FieldMemoryAppearance.Distance(s.MemoryPatch,mark.MemoryPatch)<score+5)))) return false;
        return !marks.Any(m=>m.Slot!=mark.Slot && (m.StandaloneName==member.Name ||
            m.Slot.Bench==mark.Slot.Bench && (m.Tier==0 || m.Tier==sample.Tier) && m.MemoryPatch.Length>0 &&
            FieldMemoryAppearance.Distance(sample.MemoryPatch,m.MemoryPatch)<score+5));
    }

    private void BindMemory(List<LiveUnit> units,string map)
    {
        var located=new HashSet<long>();
        for(int i=0;i<units.Count;i++)
        {
            var u=units[i] with { Suggestion=null };
            if (u.NameOnlySample==null && _memoryChoices.TryGetValue(u.Mark.Slot,out var choice) && choice.Strong)
                u=u with { Name=choice.Member.Name, MemberId=choice.Member.Id, NeedsReview=false };
            if (u.MemberId is long id)
            {
                _members[id].LastSlot=u.Mark.Slot;
                located.Add(id);
            }
            units[i]=u;
        }
        for(int i=0;i<units.Count;i++)
        {
            var unit=units[i];
            if (unit.NameOnlySample!=null || unit.MemberId!=null || unit.Purchase!=null || !_memoryChoices.TryGetValue(unit.Mark.Slot,out var choice) ||
                located.Contains(choice.Member.Id)) continue;
            if (unit.Name==null || unit.Name==unit.Mark.StandaloneName && unit.Name!=choice.Member.Name)
                units[i]=unit with { Name=null, Suggestion=choice.Member.Location };
        }
        foreach (var member in _members.Values)
        {
            int copies=_confirmedCopies.GetValueOrDefault(member.Name);
            if (member.Map!=map || located.Contains(member.Id) ||
                copies>1 && copies>1+units.Count(u=>u.Name==member.Name && u.MemberId.HasValue) ||
                _members.Values.Any(m=>m!=member && m.Name==member.Name && !located.Contains(m.Id)) ||
                units.Any(u=>u.Suggestion?.Id==member.Id || u.Name==member.Name && u.MemberId==null)) continue;
            var source=units.Find(u=>u.Mark.Slot==member.LastSlot);
            if (source!=null && (source.Name==member.Name || source.Mark.StandaloneName==member.Name ||
                (source.Mark.Tier==0 || source.Mark.Tier==tiers[member.Name]) &&
                !source.Final && source.Mark.StandaloneName==null)) continue;
            int target=-1;
            for (int i=0;i<units.Count;i++)
            {
                var u=units[i];
                var mark=u.Mark;
                if (u.Name!=null || u.Purchase!=null || u.Suggestion!=null || mark.Slot.Bench==member.LastSlot.Bench ||
                    mark.MemoryPatch.Length==0 || mark.ShapeCandidates is not [var first,var second,..] ||
                    first.Name!=member.Name || mark.Tier!=first.Tier || first.Score<.55 || first.Score-second.Score<.065) continue;
                if (!member.Samples.Any(s=>s.Slot.Bench==member.LastSlot.Bench && s.Tier==mark.Tier &&
                    s.MemoryPatch.Length>0 && FieldMemoryAppearance.Distance(s.MemoryPatch,mark.MemoryPatch)<31)) continue;
                // One remembered copy cannot distinguish two plausible destinations.
                if (target>=0) {target=-1; break;}
                target=i;
            }
            if (target>=0) units[target]=units[target] with { Suggestion=member.Location };
        }
    }

    private long Remember(LiveUnit unit,string name,FieldMark sample)
    {
        long? bound=unit.Final || unit.Name==name ? unit.MemberId : null;
        long id=bound ?? (unit.Suggestion?.Name==name ? unit.Suggestion.Id : ++_nextMemberId);
        if (!_members.TryGetValue(id,out var member) || member.Name!=name)
            _members[id]=member=new(id,name,unit.Mark.Slot,_observed.Map!);
        member.LastSlot=unit.Mark.Slot;
        if (sample.Slot==unit.Mark.Slot && sample.Tier==unit.Mark.Tier && sample.Portrait.Length>0)
        {
            var similar=member.Samples.FindIndex(s=>s.Camera==sample.Camera && s.Slot.Bench==sample.Slot.Bench &&
                s.Portrait.Length==sample.Portrait.Length && Difference(s.Portrait,sample.Portrait)<=8);
            if(similar>=0) member.Samples.RemoveAt(similar);
            if(member.Samples.Count>=4)
            {
                int sameView=member.Samples.FindIndex(s=>s.Camera==sample.Camera && s.Slot.Bench==sample.Slot.Bench);
                member.Samples.RemoveAt(Math.Max(0,sameView));
            }
            member.Samples.Add(sample);
            member.Recent.Clear();
        }
        return id;
    }

    internal bool AcceptSuggestion(long track,FieldSlot slot,long memberId)
    {
        var u=Roster.Units.FirstOrDefault(u=>u.Id==track && u.Mark.Slot==slot);
        return u is {Name:null,Suggestion:{} hint} && hint.Id==memberId && _members.ContainsKey(memberId) &&
            Confirm(track,slot,hint.Name,true);
    }

    internal bool Forget(long id)
    {
        if (!_members.Remove(id)) return false;
        foreach (var key in _missing.Where(p=>p.Value.Unit.MemberId==id).Select(p=>p.Key).ToArray())
        { _missing.Remove(key); _partialLoss.Remove(key); }
        _memoryChoices.Clear();
        _observed=_observed with {Units=_observed.Units.Where(u=>u.MemberId!=id)
            .Select(u=>u.Suggestion?.Id==id ? u with {Suggestion=null} : u).ToArray()};
        Publish(true);
        return true;
    }

    internal bool LimitMembers(string name,int remaining)
    {
        if (_confirmedCopies.TryGetValue(name,out var copies) && copies>remaining) _confirmedCopies[name]=remaining;
        var matching=_members.Values.Where(m=>m.Name==name).OrderBy(m=>_observed.Units.Any(u=>u.MemberId==m.Id))
            .ThenByDescending(m=>m.Id).ToArray();
        int remove=matching.Length-remaining;
        foreach(var member in matching.Take(remove)) Forget(member.Id);
        if (remaining==0 && _observed.Units.Any(u=>u.NameOnlySample!=null && u.Name==name))
        {
            _observed=_observed with {Units=_observed.Units.Where(u=>u.NameOnlySample==null || u.Name!=name).ToArray()};
            Publish(true);
            return true;
        }
        return remove>0;
    }
}
