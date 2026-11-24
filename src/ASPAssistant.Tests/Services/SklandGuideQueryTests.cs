using System.Net.Http;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

internal static class SklandQueryAssertions
{
    internal static bool MatchesRequest(this SklandGuideQuery query, Uri uri)
    {
        var values = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (values["titleKeyword"] ?? "") == query.Title && values["seasonBondId"] == query.Core
            && values["strategyId"] == query.Strategy && query.Extra.Order().SequenceEqual((values.GetValues("regularBondIds") ?? []).Order());
    }
}

public class SklandGuideQueryTests
{
    [Fact]
    public async Task PageAfterFirstKeepsTitleAndEveryFilter()
    {
        var query = new SklandGuideQuery("谢拉格 + A&B", "kjeragShip", ["preciShip", "skillfulShip"], "band_amiya");
        using var encoded = new FormUrlEncodedContent(query.Parameters());
        var uri = new Uri("https://zonai.skland.com/index?pageToken=second&" + await encoded.ReadAsStringAsync());
        Assert.True(query.MatchesRequest(uri));
        Assert.False((query with { Title = "" }).MatchesRequest(uri));
        Assert.False((query with { Extra = ["preciShip"] }).MatchesRequest(uri));
        Assert.False(SklandGuideQuery.Empty.MatchesRequest(uri));
    }
}
