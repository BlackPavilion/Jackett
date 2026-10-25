using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Jackett.Common;
using Jackett.Common.Helpers;
using Jackett.Common.Indexers.Definitions;
using Jackett.Common.Models;
using Jackett.Common.Utils.Clients;
using Jackett.Test.TestHelpers;
using NLog;
using NUnit.Framework;
using WebRequest = Jackett.Common.Utils.Clients.WebRequest;

namespace Jackett.Test.Common.Indexers
{
    [TestFixture]
    public class GrantorrentTests
    {
        private readonly TestWebClient _webClient = new TestWebClient();
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly TestCacheService _cacheService = new TestCacheService();

        private static Grantorrent NewIndexer(TestWebClient webClient) =>
            new Grantorrent(null, webClient, LogManager.GetCurrentClassLogger(), null, new TestCacheService());

        [Test]
        public async Task TestPerformQuerySearchAsync()
        {
            // the search page has 2 cards; the detail page is fetched per card and one
            // release is emitted per download option (row of the options table)
            var webClient = new TestWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=dory", "grantorrent-search-multi.html");
            // a general search (no categories) also searches series; "dory" has none
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=dory&filtro=series", "grantorrent-search-empty.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/buscando-a-dory/", "grantorrent-detail-dory.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/operacion-dragon/", "grantorrent-detail.html");
            var indexer = NewIndexer(webClient);

            var result = await indexer.ResultsForQuery(new TorznabQuery { QueryType = "search", SearchTerm = "dory" }, false);
            var releases = result.Releases.ToList();

            // "Buscando a Dory" has 5 rows, but row 2 has no data-src (skipped) => 4 releases
            // "Operación Dragón" has 1 row => 1 release
            // Total: 5 releases
            releases.Should().HaveCount(5);

            // "Buscando a Dory" releases (4)
            var doryReleases = releases.Where(r => r.Title.StartsWith("Buscando a Dory")).ToList();
            doryReleases.Should().HaveCount(4);

            // Row 0: HDRip -> DVDRip, 1.73 Gb. => Movies/SD (year 2016 from the detail <h1>)
            var hdrip = doryReleases.Single(r => r.Title == "Buscando a Dory (2016) DVDRip SPANISH");
            hdrip.Size.Should().Be((long)(1.73 * 1024 * 1024 * 1024));
            hdrip.Link.Should().Be(new Uri("https://grantorrent.foo/buscando-a-dory/?opt=0"));
            hdrip.Guid.Should().Be(hdrip.Link);
            hdrip.Details.Should().Be(new Uri("https://grantorrent.foo/buscando-a-dory/"));
            hdrip.Category.Should().Contain(TorznabCatType.MoviesSD.ID);
            hdrip.PublishDate.Should().Be(new DateTime(2024, 3, 15));
            hdrip.Poster.Should().NotBeNull();

            // Row 1: MicroHD-1080p, 4.02 Gb. => Movies/HD
            var microhd = doryReleases.Single(r => r.Title == "Buscando a Dory (2016) MicroHD-1080p SPANISH");
            microhd.Size.Should().Be((long)(4.02 * 1024 * 1024 * 1024));
            microhd.Link.Should().Be(new Uri("https://grantorrent.foo/buscando-a-dory/?opt=1"));
            microhd.Category.Should().Contain(TorznabCatType.MoviesHD.ID);

            // Row 3: BDRemux-1080p, 19.4 Gb. => Movies/HD
            var bdremux = doryReleases.Single(r => r.Title == "Buscando a Dory (2016) BDRemux-1080p SPANISH");
            bdremux.Size.Should().Be((long)(19.4 * 1024 * 1024 * 1024));
            bdremux.Link.Should().Be(new Uri("https://grantorrent.foo/buscando-a-dory/?opt=3"));
            bdremux.Category.Should().Contain(TorznabCatType.MoviesHD.ID);

            // Row 4: 3D -> BluRay-1080p 3D, 7.95 Gb. => Movies/HD
            var threeD = doryReleases.Single(r => r.Title == "Buscando a Dory (2016) BluRay-1080p 3D SPANISH");
            threeD.Size.Should().Be((long)(7.95 * 1024 * 1024 * 1024));
            threeD.Link.Should().Be(new Uri("https://grantorrent.foo/buscando-a-dory/?opt=4"));
            threeD.Category.Should().Contain(TorznabCatType.MoviesHD.ID);

            // Row 2 (BluRay-1080p, no data-src) is skipped (not downloadable)
            doryReleases.Should().NotContain(r => r.Title == "Buscando a Dory (2016) BluRay-1080p SPANISH");

            // "Operación Dragón" release (1 row, year 1973 from the detail <h1>,
            // quality from the "Calidad" column: BluRay-Rip)
            var dragon = releases.Single(r => r.Title == "Operación Dragón (1973) BluRay-Rip SPANISH");
            dragon.Details.Should().Be(new Uri("https://grantorrent.foo/operacion-dragon/"));
            dragon.Link.Should().Be(new Uri("https://grantorrent.foo/operacion-dragon/?opt=0"));
            dragon.Guid.Should().Be(dragon.Link);
        }

        [Test]
        public async Task TestPerformQueryTvCategoriesForceTvAsync()
        {
            // A TV category request (tvmovie=) searches SERIES (?s=...&filtro=series), because
            // the site's plain search (?s=) only covers movies. The series are categorized as
            // TV (the /series/ URL is the authoritative signal), so Sonarr finds them in the
            // configured categories (otherwise it reports "no results in the configured
            // categories").
            var webClient = new TestWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=peaky&filtro=series", "grantorrent-search-series.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/series/peaky-blinders/", "grantorrent-detail-series.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/series/the-pitt/", "grantorrent-detail-series.html");
            var indexer = NewIndexer(webClient);

            var result = await indexer.ResultsForQuery(
                new TorznabQuery { QueryType = "tvsearch", SearchTerm = "peaky", Categories = new[] { 5000, 5030, 5040, 5045 } }, false);
            var releases = result.Releases.ToList();

            // 2 series cards, each with 2 downloadable rows (row 2 has no data-src, skipped)
            // => 4 releases
            releases.Should().HaveCount(4);

            // all releases are categorized as TV (not movies)
            releases.Should().OnlyContain(r => r.Category.Contains(TorznabCatType.TV.ID));
            releases.Should().NotContain(r => r.Category.Contains(TorznabCatType.Movies.ID));

            // the episode is included in the title (SxxEyy) so Sonarr can match it; no year
            // and no quality (series have neither)
            var s01e01 = releases.Single(r => r.Title == "Peaky Blinders S01E01 SPANISH");
            s01e01.Category.Should().Contain(TorznabCatType.TV.ID);
            s01e01.Link.Should().Be(new Uri("https://grantorrent.foo/series/peaky-blinders/?opt=0"));
            s01e01.Guid.Should().Be(s01e01.Link);
            s01e01.Details.Should().Be(new Uri("https://grantorrent.foo/series/peaky-blinders/"));

            // row 0 has "0GB" (unparseable) => the size falls back to the estimate (2 GB)
            s01e01.Size.Should().Be(2L * 1024 * 1024 * 1024);

            // row 1 has "1.5GB" => the real size
            var s01e02 = releases.Single(r => r.Title == "Peaky Blinders S01E02 SPANISH");
            s01e02.Size.Should().Be((long)(1.5 * 1024 * 1024 * 1024));
            s01e02.Link.Should().Be(new Uri("https://grantorrent.foo/series/peaky-blinders/?opt=1"));
        }

        [Test]
        public async Task TestPerformQueryStripsSeasonFromSearchTermAsync()
        {
            // Sonarr can include the season in the term ("peaky S01"). The site's search
            // returns nothing with that suffix, so the indexer must strip it and use it
            // only to filter the rows.
            var webClient = new TestWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=peaky&filtro=series", "grantorrent-search-series.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/series/peaky-blinders/", "grantorrent-detail-series.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/series/the-pitt/", "grantorrent-detail-series.html");
            var indexer = NewIndexer(webClient);

            var result = await indexer.ResultsForQuery(
                new TorznabQuery { QueryType = "tvsearch", SearchTerm = "peaky S01", Categories = new[] { 5000 } }, false);
            var releases = result.Releases.ToList();

            // 2 cards x 2 downloadable rows, all S01 in the fixture
            releases.Should().HaveCount(4);
            releases.Should().OnlyContain(r => r.Title.Contains("S01E"));
        }

        [Test]
        public async Task TestPerformQueryFiltersBySeasonAsync()
        {
            var webClient = new TestWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=peaky&filtro=series", "grantorrent-search-series.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/series/peaky-blinders/", "grantorrent-detail-series.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/series/the-pitt/", "grantorrent-detail-series.html");
            var indexer = NewIndexer(webClient);

            // the fixture only has S01 rows, so a S02 search must not return them
            var result = await indexer.ResultsForQuery(
                new TorznabQuery { QueryType = "tvsearch", SearchTerm = "peaky", Season = 2, Categories = new[] { 5000 } }, false);

            result.Releases.Should().BeEmpty();
        }

        [Test]
        public async Task TestPerformQuerySeriesSeasonPageAsync()
        {
            // The per-season pages (/series/<show>-temporada-N/) use a different table:
            // the "Peso" column carries the QUALITY (not the size) and the last episode
            // cell ends with a word ("1x06 Final"). Both must be handled: normalized
            // episode, quality in the title and a size estimated from the quality.
            var webClient = new TestWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=peaky&filtro=series", "grantorrent-search-series-season.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/series/peaky-blinders-temporada-1/", "grantorrent-detail-series-season.html");
            var indexer = NewIndexer(webClient);

            var result = await indexer.ResultsForQuery(
                new TorznabQuery { QueryType = "tvsearch", SearchTerm = "peaky S01", Categories = new[] { 5000 } }, false);
            var releases = result.Releases.ToList();

            releases.Select(r => r.Title).Should().BeEquivalentTo(new[]
            {
                "Peaky Blinders S01E01 DVDRip SPANISH",
                "Peaky Blinders S01E01 720p SPANISH",
                "Peaky Blinders S01E06 DVDRip SPANISH"
            });
            releases.Should().OnlyContain(r => !r.Title.Contains("Temporada"));
            releases.Should().OnlyContain(r => r.Size > 0);
        }

        [Test]
        public async Task TestPerformQueryFiltersByEpisodeAsync()
        {
            var webClient = new TestWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=peaky&filtro=series", "grantorrent-search-series-season.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/series/peaky-blinders-temporada-1/", "grantorrent-detail-series-season.html");
            var indexer = NewIndexer(webClient);

            var result = await indexer.ResultsForQuery(
                new TorznabQuery
                {
                    QueryType = "tvsearch",
                    SearchTerm = "peaky",
                    Season = 1,
                    Episode = "6",
                    Categories = new[] { 5000 }
                }, false);

            result.Releases.Should().ContainSingle()
                .Which.Title.Should().Be("Peaky Blinders S01E06 DVDRip SPANISH");
        }

        [Test]
        public async Task TestDownloadAsync()
        {
            _webClient.RegisterRequestCallback("https://grantorrent.foo/operacion-dragon/", "grantorrent-detail.html");
            // the data-src of the detail page decodes to
            // https://dl.grantorrent.wf/torrents/peliculas/Operacion-dragon.mkv80.torrent (dead host),
            // the indexer must transform it to the functional URL below
            _webClient.RegisterRequestCallback("https://grantorrent.foo/files/torrents/peliculas/Operacion-dragon.mkv80.torrent", "grantorrent-sample.torrent");
            var indexer = NewIndexer(_webClient);

            var bytes = await indexer.Download(new Uri("https://grantorrent.foo/operacion-dragon/"));

            var expected = TestUtil.LoadTestFile("grantorrent-sample.torrent");
            bytes.Should().Equal(Encoding.UTF8.GetBytes(expected));
        }

        [Test]
        public async Task TestDownloadSeriesAsync()
        {
            // a series detail page (/series/<slug>/): the data-src decodes to a
            // /wp-content/uploads/... path on the functional host (used as-is, no
            // /files/ prefix). The option index (?opt=N) selects the row.
            _webClient.RegisterRequestCallback("https://grantorrent.foo/series/peaky-blinders/", "grantorrent-detail-series.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/wp-content/uploads/2026/03/Peaky-Blinders2-(central).torrent", "grantorrent-sample.torrent");
            var indexer = NewIndexer(_webClient);

            // opt=1 => row 1 => Peaky-Blinders2
            var bytes = await indexer.Download(new Uri("https://grantorrent.foo/series/peaky-blinders/?opt=1"));

            var expected = TestUtil.LoadTestFile("grantorrent-sample.torrent");
            bytes.Should().Equal(Encoding.UTF8.GetBytes(expected));
        }

        [TestCaseSource(typeof(ToFunctionalTorrentUrlTestData), nameof(ToFunctionalTorrentUrlTestData.Cases))]
        public void TestToFunctionalTorrentUrl(string decodedUrl, string expected)
        {
            Grantorrent.ToFunctionalTorrentUrl(decodedUrl).Should().Be(expected);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        // Edge cases (A3 QA): all offline and deterministic (TestWebClient throws on any unmocked URL,
        // so no test can reach the network).

        [Test]
        public async Task TestPerformQueryEmptySearchAsync()
        {
            // a search with zero results: the page has the movie-list container but no cards
            _webClient.RegisterRequestCallback("https://grantorrent.foo/?s=the%20pitt", "grantorrent-search-empty.html");
            // a general search (no categories) also searches series; "the pitt" has none here
            _webClient.RegisterRequestCallback("https://grantorrent.foo/?s=the%20pitt&filtro=series", "grantorrent-search-empty.html");
            var indexer = NewIndexer(_webClient);

            var result = await indexer.ResultsForQuery(new TorznabQuery { QueryType = "search", SearchTerm = "the pitt" }, false);

            result.Releases.Should().BeEmpty();
        }

        [Test]
        public async Task TestPerformQueryHomePageAsync()
        {
            // no search term => the home page (latest movies) and the series listing are
            // requested; both empty => 0 releases
            _webClient.RegisterRequestCallback("https://grantorrent.foo/", "grantorrent-search-empty.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/series_p/", "grantorrent-search-empty.html");
            var indexer = NewIndexer(_webClient);

            var result = await indexer.ResultsForQuery(new TorznabQuery { QueryType = "search" }, false);

            result.Releases.Should().BeEmpty();
        }

        [Test]
        public async Task TestPerformQuerySanitizedSearchTermAsync()
        {
            // "dory 2024" => the trailing year is stripped from the search term (SanitizeTorznabQuery),
            // so the requested URL is "?s=dory" (if the year were not stripped, TestWebClient would
            // throw because "?s=dory%202024" is not mocked)
            var webClient = new TestWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=dory", "grantorrent-search-multi.html");
            // a general search (no categories) also searches series; "dory" has none
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=dory&filtro=series", "grantorrent-search-empty.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/buscando-a-dory/", "grantorrent-detail-dory.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/operacion-dragon/", "grantorrent-detail.html");
            var indexer = NewIndexer(webClient);

            var result = await indexer.ResultsForQuery(new TorznabQuery { QueryType = "search", SearchTerm = "dory 2024" }, false);

            result.Releases.Should().HaveCount(5);
        }

        [Test]
        public async Task TestPerformQueryEdgeCasesAsync()
        {
            _webClient.RegisterRequestCallback("https://grantorrent.foo/?s=edge", "grantorrent-search-edge.html");
            // a general search (no categories) also searches series; "edge" has none
            _webClient.RegisterRequestCallback("https://grantorrent.foo/?s=edge&filtro=series", "grantorrent-search-empty.html");
            // the detail page is fetched per card; all cards use the same single-row fixture
            // (quality HDRip, 1.5 Gb.) so the category is determined by the detail quality
            // and the TV/movie heuristic from the title
            _webClient.RegisterRequestCallback("https://grantorrent.foo/la-casa-de-papel-s01e01/", "grantorrent-detail-single.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/operacion-aventura/", "grantorrent-detail-single.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/sarahs-oil/", "grantorrent-detail-single.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/dragon-relativo/", "grantorrent-detail-single.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/el-dragon-cap-12/", "grantorrent-detail-single.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/dragones-temporada-2/", "grantorrent-detail-single.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/pelicula-sin-fecha/", "grantorrent-detail-single.html");
            var indexer = NewIndexer(_webClient);

            var result = await indexer.ResultsForQuery(new TorznabQuery { QueryType = "search", SearchTerm = "edge" }, false);
            var releases = result.Releases.ToList();

            // 8 cards in the fixture, the one without a title (c5) must be skipped => 7 releases
            releases.Should().HaveCount(7);
            releases.Should().NotContain(r => r.Details.ToString() == "https://grantorrent.foo/sin-titulo/");

            // c1: TV show (SxxEyy) + HDRip->DVDRip (detail) => TV/SD, 1.5 Gb.
            // (no year: the single-row fixture has no <h1> and the search term has no year)
            var tv720 = releases.Single(r => r.Title == "La Casa de Papel S01E01 DVDRip SPANISH");
            tv720.Category.Should().Contain(TorznabCatType.TVSD.ID);
            tv720.Size.Should().Be((long)(1.5 * 1024 * 1024 * 1024));
            tv720.PublishDate.Should().Be(new DateTime(2021, 1, 1));
            tv720.Details.Should().Be(new Uri("https://grantorrent.foo/la-casa-de-papel-s01e01/"));
            tv720.Link.Should().Be(new Uri("https://grantorrent.foo/la-casa-de-papel-s01e01/?opt=0"));

            // c2: HTML entity &amp; in the title is decoded; HDRip->DVDRip (detail) => Movies/SD
            var amp = releases.Single(r => r.Title == "Operación & Aventura DVDRip SPANISH");
            amp.Category.Should().Contain(TorznabCatType.MoviesSD.ID);
            amp.Size.Should().Be((long)(1.5 * 1024 * 1024 * 1024));
            amp.PublishDate.Should().Be(new DateTime(2022, 2, 2));

            // c3: HTML entity &#39; in the title is decoded; HDRip->DVDRip (detail) => Movies/SD
            var quote = releases.Single(r => r.Title == "Sarah's Oil: El oro negro DVDRip SPANISH");
            quote.Category.Should().Contain(TorznabCatType.MoviesSD.ID);
            quote.Size.Should().Be((long)(1.5 * 1024 * 1024 * 1024));
            quote.PublishDate.Should().Be(new DateTime(2023, 3, 3));

            // c4: relative detail URL is resolved against the site link
            var relative = releases.Single(r => r.Title == "Dragón Relativo DVDRip SPANISH");
            relative.Details.Should().Be(new Uri("https://grantorrent.foo/dragon-relativo/"));
            relative.Link.Should().Be(new Uri("https://grantorrent.foo/dragon-relativo/?opt=0"));
            relative.Guid.Should().Be(relative.Link);

            // c6: TV show ("Cap.") + HDRip->DVDRip (detail) => TV/SD
            var cap = releases.Single(r => r.Title == "El Dragón Cap. 12 DVDRip SPANISH");
            cap.Category.Should().Contain(TorznabCatType.TVSD.ID);
            cap.Size.Should().Be((long)(1.5 * 1024 * 1024 * 1024));
            cap.PublishDate.Should().Be(new DateTime(2020, 6, 6));

            // c7: TV show ("Temporada") + HDRip->DVDRip (detail) => TV/SD
            var temporada = releases.Single(r => r.Title == "Dragones: Temporada 2 DVDRip SPANISH");
            temporada.Category.Should().Contain(TorznabCatType.TVSD.ID);
            temporada.Size.Should().Be((long)(1.5 * 1024 * 1024 * 1024));
            temporada.PublishDate.Should().Be(new DateTime(2019, 7, 7));

            // c8: movie + HDRip->DVDRip (detail) but no date => Movies/SD, publish date falls back to "now"
            var noDate = releases.Single(r => r.Title == "Pelicula Sin Fecha DVDRip SPANISH");
            noDate.Category.Should().Contain(TorznabCatType.MoviesSD.ID);
            noDate.Size.Should().Be((long)(1.5 * 1024 * 1024 * 1024));
            noDate.PublishDate.Should().BeAfter(DateTime.Now.AddMinutes(-10));
            noDate.PublishDate.Should().BeOnOrBefore(DateTime.Now.AddMinutes(1));
        }

        [Test]
        public async Task TestPerformQueryRelativePosterAsync()
        {
            // M2: a card with a RELATIVE img[src] must not throw (a UriFormatException
            // would propagate to the catch in PerformQuery and drop every release).
            // The relative poster is resolved against the site link and the other
            // releases are not lost.
            _webClient.RegisterRequestCallback("https://grantorrent.foo/?s=poster", "grantorrent-search-relativeposter.html");
            // a general search (no categories) also searches series; "poster" has none
            _webClient.RegisterRequestCallback("https://grantorrent.foo/?s=poster&filtro=series", "grantorrent-search-empty.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/pelicula-absoluta/", "grantorrent-detail-single.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/pelicula-relativa/", "grantorrent-detail-single.html");
            _webClient.RegisterRequestCallback("https://grantorrent.foo/pelicula-control/", "grantorrent-detail-single.html");
            var indexer = NewIndexer(_webClient);

            var result = await indexer.ResultsForQuery(new TorznabQuery { QueryType = "search", SearchTerm = "poster" }, false);
            var releases = result.Releases.ToList();

            // all 3 cards are returned (the relative-poster card is not dropped)
            releases.Should().HaveCount(3);

            // the relative poster is resolved against the site link
            var relative = releases.Single(r => r.Title == "Pelicula Relativa DVDRip SPANISH");
            relative.Poster.Should().Be(new Uri("https://grantorrent.foo/wp-content/uploads/2024/02/Pelicula-Relativa-(poster).jpg"));

            // the absolute posters are untouched
            var absolute = releases.Single(r => r.Title == "Pelicula Absoluta DVDRip SPANISH");
            absolute.Poster.Should().Be(new Uri("https://grantorrent.foo/wp-content/uploads/2024/01/Pelicula-Absoluta-(poster).jpg"));
        }

        [Test]
        public async Task TestDownloadMissingButtonAsync()
        {
            // detail page without any a.linktorrent button => clear exception (no NRE)
            _webClient.RegisterRequestCallback("https://grantorrent.foo/sin-boton/", "grantorrent-detail-nobtn.html");
            var indexer = NewIndexer(_webClient);

            var act = () => indexer.Download(new Uri("https://grantorrent.foo/sin-boton/"));

            var ex = (await act.Should().ThrowAsync<Exception>()).And;
            ex.Message.Should().Contain("Could not find the download button");
            ex.Should().NotBeOfType<NullReferenceException>();
        }

        [Test]
        public async Task TestDownloadMissingDataSrcAsync()
        {
            // a.linktorrent present but without the data-src attribute => clear exception (no NRE)
            _webClient.RegisterRequestCallback("https://grantorrent.foo/sin-datasrc/", "grantorrent-detail-nodata.html");
            var indexer = NewIndexer(_webClient);

            var act = () => indexer.Download(new Uri("https://grantorrent.foo/sin-datasrc/"));

            var ex = (await act.Should().ThrowAsync<Exception>()).And;
            ex.Message.Should().Contain("Could not find the download button");
            ex.Should().NotBeOfType<NullReferenceException>();
        }

        [Test]
        public async Task TestDownloadInvalidBase64Async()
        {
            // data-src is not valid base64 => clear exception (no NRE, no raw FormatException)
            _webClient.RegisterRequestCallback("https://grantorrent.foo/base64-invalido/", "grantorrent-detail-badbase64.html");
            var indexer = NewIndexer(_webClient);

            var act = () => indexer.Download(new Uri("https://grantorrent.foo/base64-invalido/"));

            var ex = (await act.Should().ThrowAsync<Exception>()).And;
            ex.Message.Should().Contain("not valid base64");
            ex.Should().NotBeOfType<NullReferenceException>();
            ex.Should().NotBeOfType<FormatException>();
        }

        [Test]
        public async Task TestDownloadDecodedNotUrlAsync()
        {
            // data-src is valid base64 but decodes to a non-URL string => clear exception (no NRE)
            _webClient.RegisterRequestCallback("https://grantorrent.foo/no-es-url/", "grantorrent-detail-noturl.html");
            var indexer = NewIndexer(_webClient);

            var act = () => indexer.Download(new Uri("https://grantorrent.foo/no-es-url/"));

            var ex = (await act.Should().ThrowAsync<Exception>()).And;
            ex.Message.Should().Contain("does not decode to a valid URL");
            ex.Should().NotBeOfType<NullReferenceException>();
            ex.Should().NotBeOfType<UriFormatException>();
        }

        [Test]
        public async Task TestPerformQueryHttpErrorAsync()
        {
            // HTTP 404 on the search request => exception with the response content (Jackett pattern)
            var webClient = new StatusWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/?s=dragon", "grantorrent-search-empty.html");
            webClient.RegisterStatus("https://grantorrent.foo/?s=dragon", HttpStatusCode.NotFound);
            var indexer = NewIndexer(webClient);

            var act = () => indexer.ResultsForQuery(new TorznabQuery { QueryType = "search", SearchTerm = "dragon" }, false);

            var ex = (await act.Should().ThrowAsync<IndexerException>()).And;
            ex.InnerException.Should().BeOfType<ExceptionWithConfigData>();
        }

        [Test]
        public async Task TestDownloadHttpErrorDetailPageAsync()
        {
            // HTTP 404 on the detail page => clear exception with the status code
            var webClient = new StatusWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/operacion-dragon/", "grantorrent-detail.html");
            webClient.RegisterStatus("https://grantorrent.foo/operacion-dragon/", HttpStatusCode.NotFound);
            var indexer = NewIndexer(webClient);

            var act = () => indexer.Download(new Uri("https://grantorrent.foo/operacion-dragon/"));

            var ex = (await act.Should().ThrowAsync<Exception>()).And;
            ex.Message.Should().Contain("Failed to load the detail page");
            ex.Message.Should().Contain("404");
        }

        [Test]
        public async Task TestDownloadHttpErrorTorrentAsync()
        {
            // detail page OK but HTTP 404 on the .torrent request => clear exception with the status code
            var webClient = new StatusWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/operacion-dragon/", "grantorrent-detail.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/files/torrents/peliculas/Operacion-dragon.mkv80.torrent", "grantorrent-sample.torrent");
            webClient.RegisterStatus("https://grantorrent.foo/files/torrents/peliculas/Operacion-dragon.mkv80.torrent", HttpStatusCode.NotFound);
            var indexer = NewIndexer(webClient);

            var act = () => indexer.Download(new Uri("https://grantorrent.foo/operacion-dragon/"));

            var ex = (await act.Should().ThrowAsync<Exception>()).And;
            ex.Message.Should().Contain("Failed to download the torrent");
            ex.Message.Should().Contain("404");
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        // M1: super-enlace fallback (the primary data-src URL is still the main path;
        // the fallback only activates when the primary does not respond).

        [Test]
        public void TestDecodeSuperEnlaceRealValue()
        {
            // the i parameter of the real fixture (5x base64 + ROT13) decodes to the
            // functional torrent URL (A1 fact #20)
            var href = "https://super-enlace.com/s.php?i=VjJ0YWEyRXlTbk5oTTJ4WVZrWndUbHBXVW5Ka01XUnpXWHBHYUZKdE9UVlZNakUwV1Zaa1JtTkhPVmhpYlhONFdsZDRjMlJHVGxsWGJYQk9UVmM0ZUZkVVNYaFhiVkp6WWpOc1ZHSllhR2hXTUZaM1lqRmtjVk5zY0d0aVIzUTJWVEp3VjJFeVNuSlRibHBoVm0xTmVGbHNWak5rTVU1MFkwZHdhVlpzYjNoWFZFSnZXVmRHVjJKR1VrNVhSMmhvVm01d1ZtUXhaRmhOVlZwclZqRmFNVmxWYUVOVlJrcEdVbXBHV0dGcmNFeFphMXAzVjFaT2RHRkhSbXRsYWtFMQ==&st=gtn";

            Grantorrent.DecodeSuperEnlace(href).Should().Be("https://grantorrent.foo/files/torrents/peliculas/Operacion-dragon.mkv80.torrent");
        }

        [TestCase("https://example.com/s.php?i=VjJ0YWEy&st=gtn")] // not a super-enlace host
        [TestCase("https://super-enlace.com/s.php?st=gtn")] // no i parameter
        [TestCase("https://super-enlace.com/s.php?i=!!!not-base64!!!&st=gtn")] // invalid base64
        [TestCase("not-a-url")] // not a URL at all
        [TestCase(null)]
        [TestCase("")]
        public void TestDecodeSuperEnlaceInvalid(string href)
        {
            // a non-super-enlace href, a missing i, invalid base64 or null/empty input
            // => null (no NRE, no raw exception)
            Grantorrent.DecodeSuperEnlace(href).Should().BeNull();
        }

        [Test]
        public async Task TestDownloadFallbackUsedWhenPrimaryFailsAsync()
        {
            // the primary URL (data-src transformed) 404s, but the super-enlace fallback
            // resolves to a functional URL that 200s => the fallback bytes are returned
            var webClient = new StatusWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/fallback/", "grantorrent-detail-fallback.html");
            // primary (data-src -> dead host -> transformed) 404s
            webClient.RegisterRequestCallback("https://grantorrent.foo/files/torrents/peliculas/Primary-404.torrent", "grantorrent-sample.torrent");
            webClient.RegisterStatus("https://grantorrent.foo/files/torrents/peliculas/Primary-404.torrent", HttpStatusCode.NotFound);
            // fallback (super-enlace decoded) 200s
            webClient.RegisterRequestCallback("https://grantorrent.foo/files/torrents/peliculas/Fallback-200.torrent", "grantorrent-sample.torrent");
            var indexer = NewIndexer(webClient);

            var bytes = await indexer.Download(new Uri("https://grantorrent.foo/fallback/"));

            var expected = TestUtil.LoadTestFile("grantorrent-sample.torrent");
            bytes.Should().Equal(Encoding.UTF8.GetBytes(expected));
        }

        [Test]
        public async Task TestDownloadBothFailAsync()
        {
            // both the primary URL (data-src transformed) and the super-enlace fallback
            // 404 => clear exception mentioning both URLs
            var webClient = new StatusWebClient();
            webClient.RegisterRequestCallback("https://grantorrent.foo/fallback/", "grantorrent-detail-fallback.html");
            webClient.RegisterRequestCallback("https://grantorrent.foo/files/torrents/peliculas/Primary-404.torrent", "grantorrent-sample.torrent");
            webClient.RegisterStatus("https://grantorrent.foo/files/torrents/peliculas/Primary-404.torrent", HttpStatusCode.NotFound);
            webClient.RegisterRequestCallback("https://grantorrent.foo/files/torrents/peliculas/Fallback-200.torrent", "grantorrent-sample.torrent");
            webClient.RegisterStatus("https://grantorrent.foo/files/torrents/peliculas/Fallback-200.torrent", HttpStatusCode.NotFound);
            var indexer = NewIndexer(webClient);

            var act = () => indexer.Download(new Uri("https://grantorrent.foo/fallback/"));

            var ex = (await act.Should().ThrowAsync<Exception>()).And;
            ex.Message.Should().Contain("Failed to download the torrent");
            ex.Message.Should().Contain("Primary-404.torrent");
            ex.Message.Should().Contain("Fallback-200.torrent");
            ex.Message.Should().Contain("404");
        }
    }

    public class ToFunctionalTorrentUrlTestData
    {
        public static IEnumerable Cases
        {
            get
            {
                // the 4 items of the A1 stability table (data-src decodes to the dead host dl.grantorrent.wf)
                yield return new TestCaseData(
                    "https://dl.grantorrent.wf/torrents/peliculas/Operacion-dragon.mkv80.torrent",
                    "https://grantorrent.foo/files/torrents/peliculas/Operacion-dragon.mkv80.torrent");
                yield return new TestCaseData(
                    "https://dl.grantorrent.wf/torrents/peliculas/Dragonkeeper-(Guardiana-de-dragones)-(2024)-cast.mp427.torrent",
                    "https://grantorrent.foo/files/torrents/peliculas/Dragonkeeper-(Guardiana-de-dragones)-(2024)-cast.mp427.torrent");
                yield return new TestCaseData(
                    "https://dl.grantorrent.wf/torrents/peliculas/Hansan-Rising-Dragon-(2022)-cast.mp458.torrent",
                    "https://grantorrent.foo/files/torrents/peliculas/Hansan-Rising-Dragon-(2022)-cast.mp458.torrent");
                yield return new TestCaseData(
                    "https://dl.grantorrent.wf/torrents/peliculas/La_casa_de_papel_1988_HDRip.torrent",
                    "https://grantorrent.foo/files/torrents/peliculas/La_casa_de_papel_1988_HDRip.torrent");
                // A1 H2: if the dead host changes, the transformation must keep working
                yield return new TestCaseData(
                    "https://dl2.grantorrent.wf/torrents/peliculas/Operacion-dragon.mkv80.torrent",
                    "https://grantorrent.foo/files/torrents/peliculas/Operacion-dragon.mkv80.torrent");
                // an already functional URL is used as-is
                yield return new TestCaseData(
                    "https://grantorrent.foo/files/torrents/peliculas/Operacion-dragon.mkv80.torrent",
                    "https://grantorrent.foo/files/torrents/peliculas/Operacion-dragon.mkv80.torrent");
                // A3: the host is already functional but the path lacks the /files/ prefix => it is added
                yield return new TestCaseData(
                    "https://grantorrent.foo/torrents/peliculas/Operacion-dragon.mkv80.torrent",
                    "https://grantorrent.foo/files/torrents/peliculas/Operacion-dragon.mkv80.torrent");
                // series: the data-src decodes to a /wp-content/uploads/... path on the
                // functional host => it is used as-is (no /files/ prefix)
                yield return new TestCaseData(
                    "https://grantorrent.foo/wp-content/uploads/2026/03/Peaky-Blinders861-(central).torrent",
                    "https://grantorrent.foo/wp-content/uploads/2026/03/Peaky-Blinders861-(central).torrent");
            }
        }
    }

    // TestWebClient only supports HTTP 200 responses; this subclass lets a test
    // register a non-OK status for a mocked URL (e.g. 404) to exercise the
    // indexer's error handling. It stays fully offline: unmocked URLs still throw.
    internal sealed class StatusWebClient : TestWebClient
    {
        private readonly Dictionary<string, HttpStatusCode> _statusOverrides = new Dictionary<string, HttpStatusCode>();

        public void RegisterStatus(string requestUrl, HttpStatusCode status) => _statusOverrides[requestUrl] = status;

        public override async Task<WebResult> GetResultAsync(WebRequest request)
        {
            var result = await base.GetResultAsync(request);
            if (_statusOverrides.TryGetValue(request.Url, out var status))
                result.Status = status;
            return result;
        }
    }
}
