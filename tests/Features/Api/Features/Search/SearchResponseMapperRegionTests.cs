namespace Listenarr.Tests.Features.Api.Features.Search;

public class SearchResponseMapperRegionTests
{
    [Fact]
    public void SimplifySearchResults_IncludesRegion()
    {
        var mapper = new SearchResponseMapper(
            new Mock<IAudiobookMetadataService>().Object,
            new Mock<ILogger<SearchResponseMapper>>().Object);

        var results = mapper.SimplifySearchResults(new List<SearchResult>
        {
            new()
            {
                Id = "result-1",
                Title = "Feuermond",
                Artist = "André Marx",
                Asin = "B0B1QRFPHB",
                Region = "de",
                ProductUrl = "https://www.audible.de/pd/B0B1QRFPHB",
                MetadataSource = "Audible",
                Source = "Audible"
            }
        });

        var region = results[0].GetType().GetProperty("Region")?.GetValue(results[0]);

        Assert.Equal("de", region);
    }
}
