using OB2.Core.Grabber;

namespace OB2.Core.Tests.Grabber;

/// <summary>D-87: Grab links finds every address in whatever text the user pastes.</summary>
public sealed class LinkExtractorTests
{
    private static string[] Find(string text) => [.. LinkExtractor.Extract(text).Select(u => u.AbsoluteUri)];

    [Fact]
    public void A_list_of_addresses_one_per_line_keeps_its_order()
    {
        Find("https://a.example/one.zip\r\nhttp://b.example/two.pdf\n\nhttps://c.example/three.mp4")
            .Should().Equal("https://a.example/one.zip", "http://b.example/two.pdf", "https://c.example/three.mp4");
    }

    [Fact]
    public void Addresses_in_running_text_lose_the_sentence_punctuation_around_them()
    {
        const string email = """
            Hi! The files are at https://files.example/report.pdf, https://files.example/data.xlsx;
            and the video (https://cdn.example/talk.mp4). Is https://files.example/a.zip? Yes: https://files.example/b.zip!
            Bold in Markdown: **https://files.example/c.zip** and <https://files.example/d.zip>.
            """;
        Find(email).Should().Equal(
            "https://files.example/report.pdf", "https://files.example/data.xlsx", "https://cdn.example/talk.mp4",
            "https://files.example/a.zip", "https://files.example/b.zip", "https://files.example/c.zip", "https://files.example/d.zip");
    }

    [Fact]
    public void A_bracket_the_address_opened_stays_one_it_did_not_open_goes()
    {
        Find("See https://en.wikipedia.org/wiki/File_(1).pdf and [the manual](https://docs.example/manual.pdf).")
            .Should().Equal("https://en.wikipedia.org/wiki/File_(1).pdf", "https://docs.example/manual.pdf");
    }

    [Fact]
    public void Markdown_that_shows_the_address_as_its_own_text_gives_it_once() =>
        Find("[https://x.example/a.zip](https://x.example/a.zip)").Should().Equal("https://x.example/a.zip");

    [Fact]
    public void Arabic_and_CJK_punctuation_after_an_address_is_not_part_of_it()
    {
        Find("الملف هنا https://x.example/a.zip، والثاني https://x.example/b.zip؟ 文件 https://x.example/c.zip。")
            .Should().Equal("https://x.example/a.zip", "https://x.example/b.zip", "https://x.example/c.zip");
    }

    [Fact]
    public void Characters_an_address_does_end_in_are_kept() =>
        Find("https://x.example/get?t=YWJjZA== https://x.example/dir/ https://x.example/name_")
            .Should().Equal("https://x.example/get?t=YWJjZA==", "https://x.example/dir/", "https://x.example/name_");

    [Fact]
    public void A_page_source_gives_the_addresses_of_its_links_attributes_and_scripts()
    {
        const string html = """
            <a href="https://x.example/a.zip?x=1&amp;y=2">A</a>
            <a href='https://x.example/b.zip'>B</a>
            <script>var player = {"src":"https:\/\/cdn.example\/v.mp4?k=1\u0026s=2"};</script>
            """;
        Find(html).Should().Equal("https://x.example/a.zip?x=1&y=2", "https://x.example/b.zip", "https://cdn.example/v.mp4?k=1&s=2");
    }

    [Fact]
    public void Each_address_is_listed_once_and_the_part_after_the_hash_does_not_make_it_another()
    {
        Find("https://x.example/a.zip https://x.example/a.zip#part2 HTTPS://X.EXAMPLE/a.zip https://x.example/b.zip")
            .Should().Equal("https://x.example/a.zip", "https://x.example/b.zip");
    }

    [Fact]
    public void Only_http_and_https_count()
    {
        Find("ftp://x.example/a.zip magnet:?xt=urn:btih:abc file:///C:/a.zip mailto:a@b.example www.x.example/a.zip http:// https://")
            .Should().BeEmpty();
    }

    [Fact]
    public void An_address_longer_than_OB2_accepts_is_left_out()
    {
        var tooLong = "https://x.example/" + new string('a', 2100);
        Find($"{tooLong} https://x.example/ok.zip").Should().Equal("https://x.example/ok.zip");
    }

    [Fact]
    public void An_address_with_a_user_name_and_password_is_found_the_window_asks_before_adding_it() =>
        Find("ftp is out, but https://user:pass@x.example/a.zip is in").Should().Equal("https://user:pass@x.example/a.zip");

    [Fact]
    public void International_addresses_are_found_and_escaped_the_way_they_are_sent()
    {
        var link = LinkExtractor.Extract("الرابط: https://例え.jp/ファイル.zip").Should().ContainSingle().Subject;
        link.IdnHost.Should().Be("xn--r8jz45g.jp");
        link.AbsolutePath.Should().Be("/%E3%83%95%E3%82%A1%E3%82%A4%E3%83%AB.zip");
    }

    [Fact]
    public void At_most_the_limit_is_returned_and_the_rest_are_counted()
    {
        var text = string.Join(' ', Enumerable.Range(1, 12).Select(i => $"https://x.example/{i}.zip"));
        var links = LinkExtractor.Extract(text + " https://x.example/1.zip", out var leftOut, max: 10);
        links.Should().HaveCount(10);
        links[^1].AbsoluteUri.Should().Be("https://x.example/10.zip");
        leftOut.Should().Be(2, "a repeat of a listed address is not one left out");
    }

    [Fact]
    public void No_text_or_text_without_addresses_finds_nothing()
    {
        Find(string.Empty).Should().BeEmpty();
        Find("Nothing to download here.").Should().BeEmpty();
        LinkExtractor.ContainsLink(null).Should().BeFalse();
        LinkExtractor.ContainsLink("see https://x.example/a.zip").Should().BeTrue();
    }

    [Fact]
    public void A_large_pasted_page_is_read_quickly()
    {
        // A 4 MB page of base64 noise with addresses in it: the pattern never backtracks over it.
        var noise = string.Concat(Enumerable.Repeat("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo//+/", 100_000));
        var text = noise + " https://x.example/a.zip " + noise + " https://x.example/b.zip";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Find(text).Should().Equal("https://x.example/a.zip", "https://x.example/b.zip");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }
}
