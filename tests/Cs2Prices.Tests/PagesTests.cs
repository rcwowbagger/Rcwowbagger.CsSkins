using Bunit;
using Cs2Prices.Core.Catalog;
using Cs2Prices.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Cs2Prices.Tests;

public class PagesTests : BunitContext
{
    private readonly FakeCatalogQueries fake = new();
    private readonly FakeItemRefresher refresher = new();

    public PagesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        ComponentFactories.AddStub<Radzen.Blazor.RadzenChart>();
        Services.AddSingleton<ICatalogQueries>(fake);
        Services.AddSingleton<Cs2Prices.Core.Providers.IItemRefresher>(refresher);
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<Radzen.DialogService>(); Services.AddScoped<Radzen.NotificationService>(); Services.AddScoped<Radzen.TooltipService>(); Services.AddScoped<Radzen.ContextMenuService>();
    }

    [Fact]
    public void Catalog_lists_items_for_the_first_market()
    {
        var cut = Render<Home>();
        cut.WaitForAssertion(() => Assert.Contains("Redline", cut.Markup));

        Assert.Contains("Asiimov", cut.Markup);
        Assert.Equal((byte)1, fake.Queries.Last().MarketId);
    }

    [Fact]
    public void Catalog_shows_empty_state_without_markets()
    {
        fake.Markets.Clear();
        var cut = Render<Home>();
        cut.WaitForAssertion(() => Assert.Contains("No prices collected yet", cut.Markup));
    }

    [Fact]
    public void Typing_in_search_updates_the_url_and_filters()
    {
        var cut = Render<Home>(p => p.Add(x => x.SearchDebounceMs, 0));
        cut.WaitForAssertion(() => Assert.Contains("Redline", cut.Markup));

        cut.Find("input[type=search]").Input("awp");

        cut.WaitForAssertion(() =>
        {
            var nav = Services.GetRequiredService<NavigationManager>();
            Assert.Contains("q=awp", nav.Uri);
        });
    }

    [Fact]
    public void Query_string_selects_market_and_search()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/?market=steam&q=awp");
        var cut = Render<Home>();

        cut.WaitForAssertion(() => Assert.Contains("Asiimov", cut.Markup));
        Assert.Equal((byte)2, fake.Queries.Last().MarketId);
        Assert.Equal("awp", fake.Queries.Last().Search);
    }

    [Fact]
    public void Watch_star_toggles_without_opening_the_item()
    {
        var cut = Render<Home>();
        cut.WaitForAssertion(() => Assert.Contains("Redline", cut.Markup));
        var nav = Services.GetRequiredService<NavigationManager>();
        var before = nav.Uri;

        cut.Find("button.watch-toggle").Click();

        Assert.Equal((10, true), fake.WatchCalls.Single());
        Assert.Equal(before, nav.Uri);
    }

    [Fact]
    public void Item_page_shows_a_card_per_market_with_ladder_when_available()
    {
        var cut = Render<ItemDetail>(p => p.Add(x => x.ItemId, 10));
        cut.WaitForAssertion(() => Assert.Contains("Skinport", cut.Markup));

        Assert.Equal(2, cut.FindAll("section.book-card").Count);
        Assert.Single(cut.FindAll("table.asks"));          // only Steam has depth
        Assert.Contains("$31.00", cut.Markup);
    }

    [Fact]
    public void Item_page_loads_history_and_reloads_on_range_change()
    {
        var cut = Render<ItemDetail>(p => p.Add(x => x.ItemId, 10));
        cut.WaitForAssertion(() => Assert.NotEmpty(fake.HistoryCalls));

        Assert.Equal((10, (byte)1, HistoryRange.Week), fake.HistoryCalls[0]);

        cut.Find("select").Change("2");
        cut.WaitForAssertion(() => Assert.Contains(fake.HistoryCalls, c => c.Market == 2));
    }

    [Fact]
    public void Item_page_reports_missing_items()
    {
        fake.Item = null;
        var cut = Render<ItemDetail>(p => p.Add(x => x.ItemId, 999));
        cut.WaitForAssertion(() => Assert.Contains("Item not found", cut.Markup));
    }

    [Fact]
    public void Item_page_watch_toggle_calls_through()
    {
        var cut = Render<ItemDetail>(p => p.Add(x => x.ItemId, 10));
        cut.WaitForAssertion(() => Assert.Contains("Watch", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Contains("Watch")).Click();

        Assert.Equal((10, true), fake.WatchCalls.Single());
    }

    [Fact]
    public void Item_page_shows_the_image_linked_to_the_original()
    {
        var cut = Render<ItemDetail>(p => p.Add(x => x.ItemId, 10));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("a.item-image")));

        Assert.EndsWith("/iconhash123", cut.Find("a.item-image").GetAttribute("href"));
        Assert.EndsWith("/iconhash123/360fx360f", cut.Find("a.item-image img").GetAttribute("src"));
    }

    [Fact]
    public void Item_page_has_no_image_when_none_is_known()
    {
        fake.Item!.IconUrl = null;
        var cut = Render<ItemDetail>(p => p.Add(x => x.ItemId, 10));
        cut.WaitForAssertion(() => Assert.Contains("Skinport", cut.Markup));
        Assert.Empty(cut.FindAll("a.item-image"));
        Assert.Contains("No picture", cut.Markup);
    }

    [Fact]
    public void Chart_explains_when_there_is_a_single_point()
    {
        var cut = Render<Cs2Prices.Web.Components.Market.PriceHistory>(p => p
            .Add(x => x.Points, [new Cs2Prices.Core.Catalog.PricePoint(DateTime.UtcNow, 5m, null, null)]));
        Assert.Contains("Only 1 price point", cut.Markup);
    }

    [Fact]
    public void Opening_an_item_refreshes_that_item_from_the_markets()
    {
        var cut = Render<ItemDetail>(p => p.Add(x => x.ItemId, 10));

        cut.WaitForAssertion(() => Assert.Contains("Steam: updated just now", cut.Markup));
        Assert.Equal(("AK-47 | Redline (Field-Tested)", false), refresher.Calls.Single());
        Assert.Contains("Skinport: rate limited", cut.Markup);
    }

    [Fact]
    public void Refresh_button_forces_a_refresh_and_reloads_prices()
    {
        var cut = Render<ItemDetail>(p => p.Add(x => x.ItemId, 10));
        cut.WaitForAssertion(() => Assert.Contains("Steam: updated just now", cut.Markup));
        fake.Books[1].Ask = 99m;

        cut.FindAll("button").First(b => b.TextContent.Contains("Refresh")).Click();

        cut.WaitForAssertion(() => Assert.Equal(2, refresher.Calls.Count));
        Assert.True(refresher.Calls[1].Force);
        cut.WaitForAssertion(() => Assert.Contains("$99.00", cut.Markup));
    }

    [Fact]
    public void Page_shows_stored_prices_while_the_refresh_is_running()
    {
        refresher.Gate = new TaskCompletionSource();
        var cut = Render<ItemDetail>(p => p.Add(x => x.ItemId, 10));

        cut.WaitForAssertion(() => Assert.Contains("Updating prices", cut.Markup));
        Assert.Contains("$31.00", cut.Markup);      // stored Steam ladder is already visible
        Assert.Contains("Refreshing…", cut.Markup);

        refresher.Gate.SetResult();
        cut.WaitForAssertion(() => Assert.DoesNotContain("Updating prices", cut.Markup));
    }
}
