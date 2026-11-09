using WinStatusBar.Core.Ai;
using Xunit;
namespace WinStatusBar.Tests;
public class DashboardMetricsTests
{
    [Fact] public void ReadsObservedUsageSchemaWithoutDoubleCountingCachedTokens()
    {
        var days = DashboardMetrics.Days("""{"data":[{"start_time":1793923200,"results":[{"input_tokens":244,"output_tokens":33,"input_cached_tokens":20,"num_model_requests":2}]}],"has_more":false}""");
        var day = Assert.Single(days); Assert.Equal(244, day.Input); Assert.Equal(33, day.Output); Assert.Equal(2, day.Requests);
    }
    [Fact] public void HandlesZeroWithVerySmallScientificExponent()
    {
        var days = DashboardMetrics.Days("""{"data":[{"start_time":1793923200,"results":[{"amount":{"currency":"usd","value":0E-6176}}]}]}""");
        Assert.Equal(0, Assert.Single(days).Cost);
    }
    [Fact] public void ReadsActualCreditGrantShapeAndExpiration()
    {
        var credit = DashboardMetrics.Credits("""{"total_available":19.0,"grants":{"data":[{"grant_amount":18.0,"used_amount":0,"expires_at":1793999061.0},{"grant_amount":1.0,"used_amount":0,"expires_at":1793999061.0}]}}""");
        Assert.Equal(19m, credit.Balance); Assert.Equal(1793999061, credit.Expiration!.Value.ToUnixTimeSeconds());
    }
    [Fact] public void DoesNotInterpretMissingBalanceAsZero() => Assert.Throws<FormatException>(() => DashboardMetrics.Credits("{}"));
    [Fact] public void RejectsPartialSeries() => Assert.Throws<InvalidOperationException>(() => DashboardMetrics.Days("""{"data":[],"has_more":true}"""));
}
