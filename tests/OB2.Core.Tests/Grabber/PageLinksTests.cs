using OB2.Core.Grabber;

namespace OB2.Core.Tests.Grabber;

/// <summary>D-87: the file links on a web page that Grab links reads.</summary>
public sealed class PageLinksTests
{
    private static readonly Uri Page = new("https://site.example/games/title/");

    private static string[] Find(string html, Uri? page = null, IReadOnlyCollection<string>? extensions = null) =>
        [.. PageLinks.Find(html, page ?? Page, extensions).Select(u => u.AbsoluteUri)];

    [Fact]
    public void Links_to_files_are_found_in_page_order_and_links_to_pages_are_not()
    {
        const string html = """
            <nav><a href="/">Home</a> <a href="/games/">Games</a> <a href="https://other.example/about">About</a></nav>
            <a href="files/setup.exe">Installer</a>
            <a class="button" href="https://cdn.example/manual.PDF?v=2">Manual</a>
            <a href="/media/trailer.mp4">Trailer</a>
            <a href="#comments">Comments</a> <a href="javascript:void(0)">Menu</a> <a href="mailto:me@site.example">Mail</a>
            """;
        Find(html).Should().Equal(
            "https://site.example/games/title/files/setup.exe", "https://cdn.example/manual.PDF?v=2", "https://site.example/media/trailer.mp4");
    }

    [Fact]
    public void Each_file_is_listed_once_and_never_the_page_itself()
    {
        const string html = """
            <a href="a.zip">A</a> <a href="./a.zip">A again</a> <a href="a.zip#part2">A, part two</a>
            <a href="https://site.example/games/title/">This page</a>
            """;
        Find(html).Should().Equal("https://site.example/games/title/a.zip");
    }

    [Fact]
    public void A_link_marked_download_is_a_file_whatever_its_address_says() =>
        Find("""<a href="/get.php?id=7&amp;t=abc" download>Get it</a> <a href="/get.php?id=8">Not marked</a>""")
            .Should().Equal("https://site.example/get.php?id=7&t=abc");

    [Fact]
    public void Media_elements_and_media_meta_tags_count_whatever_their_address_says()
    {
        const string html = """
            <meta property="og:video" content="https://cdn.example/stream?id=1">
            <video src="/v/clip"><source src="/v/clip-hd" type="video/mp4"></video>
            <audio src="blob:https://site.example/1234"></audio>
            <picture><source srcset="/img/a.webp"></picture>
            """;
        Find(html).Should().Equal("https://cdn.example/stream?id=1", "https://site.example/v/clip", "https://site.example/v/clip-hd");
    }

    [Fact]
    public void A_base_element_moves_where_relative_links_point() =>
        Find("""<head><base href="https://files.example/dl/"></head><a href="a.zip">A</a> <a href="/b.zip">B</a>""")
            .Should().Equal("https://files.example/dl/a.zip", "https://files.example/b.zip");

    [Fact]
    public void Attributes_in_any_quoting_and_case_are_read()
    {
        const string html = """<A HREF='one.zip'>1</A> <a title="x > y" href=two.zip>2</a> <a data-x="1"href="three.zip">3</a>""";
        Find(html).Should().Equal(
            "https://site.example/games/title/one.zip", "https://site.example/games/title/two.zip", "https://site.example/games/title/three.zip");
    }

    [Fact]
    public void Pictures_count_as_links_but_not_as_addresses_in_scripts()
    {
        const string html = """
            <a href="/wallpapers/4k.png">Wallpaper</a> <img src="/thumbs/small.jpg">
            <script>var config = {"poster":"https:\/\/cdn.example\/poster.jpg","file":"https:\/\/cdn.example\/movie.mkv","app":"https://site.example/app.js"};</script>
            """;
        Find(html).Should().Equal("https://site.example/wallpapers/4k.png", "https://cdn.example/movie.mkv");
    }

    [Fact]
    public void Addresses_in_scripts_count_only_for_media_and_the_capture_types()
    {
        const string html = """
            <script>
              load("https://cdn.example/track.mp3"); load("https://cdn.example/notes.txt");
              load("https://cdn.example/seg-001.ts"); load("https://cdn.example/patch.bin");
            </script>
            """;
        Find(html).Should().Equal("https://cdn.example/track.mp3");
        Find(html, extensions: ["bin", ".TXT"]).Should().Equal("https://cdn.example/track.mp3", "https://cdn.example/notes.txt", "https://cdn.example/patch.bin");
    }

    [Fact]
    public void A_type_the_user_added_to_the_capture_rules_counts_as_a_file_link()
    {
        Find("""<a href="/t/game.torrent">Torrent</a>""").Should().BeEmpty();
        Find("""<a href="/t/game.torrent">Torrent</a>""", extensions: ["torrent"]).Should().Equal("https://site.example/t/game.torrent");
    }

    [Fact]
    public void A_page_without_links_finds_nothing()
    {
        Find(string.Empty).Should().BeEmpty();
        Find("<html><body><p>Nothing here.</p></body></html>").Should().BeEmpty();
    }

    [Theory]
    [InlineData("<a title=\"never closed ")]
    [InlineData("<a href='x\"y' <a ")]
    [InlineData("<a ")]
    public void A_broken_page_is_still_read_quickly(string broken)
    {
        // About 1 MB of tags that never end, then one good link.
        var html = string.Concat(Enumerable.Repeat(broken, 1_000_000 / broken.Length)) + "<p>ok</p> <a href=\"https://cdn.example/f.zip\">F</a>";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var links = Find(html);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        links.Should().Contain("https://cdn.example/f.zip");
    }

    [Fact]
    public void At_most_the_limit_is_returned()
    {
        var html = string.Concat(Enumerable.Range(1, 50).Select(i => $"<a href=\"f{i}.zip\">{i}</a>"));
        PageLinks.Find(html, Page, max: 10).Should().HaveCount(10);
    }
}
