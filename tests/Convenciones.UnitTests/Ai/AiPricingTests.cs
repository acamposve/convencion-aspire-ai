using Convenciones.Ai;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Ai;

/// <summary>
/// Cost is an internal estimate from reported tokens. An unreported count must yield no cost at all,
/// never zero, or an unknown charge would be presented as free.
/// </summary>
public sealed class AiPricingTests
{
    private static readonly AiPricing Pricing = new("test-v1", "USD", 0.61m, 1.82m);

    [Fact]
    public void Cost_uses_the_per_million_prices()
    {
        Pricing.EstimateCost(1_000_000, 1_000_000).ShouldBe(2.43m);
    }

    [Fact]
    public void Cost_scales_for_partial_millions_and_rounds_to_six_decimals()
    {
        Pricing.EstimateCost(1_000, 500).ShouldBe(0.00152m);
    }

    [Fact]
    public void A_missing_token_count_produces_no_cost()
    {
        Pricing.EstimateCost(null, 500).ShouldBeNull();
        Pricing.EstimateCost(1_000, null).ShouldBeNull();
        Pricing.EstimateCost(null, null).ShouldBeNull();
    }

    [Fact]
    public void Different_prices_produce_different_costs()
    {
        var cheaper = new AiPricing("test-v2", "USD", 0.10m, 0.20m);

        cheaper.EstimateCost(1_000_000, 1_000_000).ShouldBe(0.30m);
        Pricing.EstimateCost(1_000_000, 1_000_000).ShouldBe(2.43m);
    }

    [Fact]
    public void Configuration_maps_to_a_currency_tagged_price_list()
    {
        var configured = AiPricing.FromConfiguration("configured-2026-11", 0.61, 1.82);

        configured.Version.ShouldBe("configured-2026-11");
        configured.Currency.ShouldBe("USD");
        configured.InputPerMillion.ShouldBe(0.61m);
        configured.OutputPerMillion.ShouldBe(1.82m);
    }
}
