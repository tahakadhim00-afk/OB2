using System.Net.Http.Headers;
using OB2.Core.Files;
using OB2.Tests.Common;

namespace OB2.Core.Tests.Files;

public sealed class FileNameSanitizerTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData(@"..\..\Windows\evil.exe", "evil.exe")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts", "hosts")]
    [InlineData("..", "download")]
    [InlineData("...", "download")]
    [InlineData("", "download")]
    [InlineData("   ", "download")]
    [InlineData(null, "download")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    [InlineData("trailing dot.exe.", "trailing dot.exe")]
    [InlineData("trailing space.exe  ", "trailing space.exe")]
    [InlineData(".hidden", "hidden")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9.doc", "_LPT9.doc")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("تقرير.pdf", "تقرير.pdf")]
    public void Sanitizes(string? input, string expected)
    {
        FileNameSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void Removes_bidi_override_that_disguises_extensions()
    {
        // "invoice<RLO>fdp.exe" renders as "invoiceexe.pdf".
        FileNameSanitizer.Sanitize("invoice\u202Efdp.exe").Should().Be("invoicefdp.exe");
    }

    [Fact]
    public void Limits_length_to_200_and_keeps_extension()
    {
        var result = FileNameSanitizer.Sanitize(new string('a', 500) + ".tar.gz");

        result.Length.Should().Be(200);
        result.Should().EndWith(".gz");
    }
}

public sealed class SafePathTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(@"..\escape.exe")]
    [InlineData("../../escape.exe")]
    [InlineData(@"sub\..\..\escape.exe")]
    [InlineData(@"C:\Windows\escape.exe")]
    [InlineData(@"\\server\share\escape.exe")]
    public void Hostile_names_resolve_inside_the_folder(string name)
    {
        var path = SafePath.Combine(_temp.Path, name);

        Path.GetDirectoryName(path).Should().Be(_temp.Path);
        Path.GetFileName(path).Should().Be("escape.exe");
    }

    [Fact]
    public void EnsureInside_rejects_other_folders()
    {
        var act = () => SafePath.EnsureInside(_temp.Path, Path.Combine(_temp.Path, "..", "x.exe"));

        act.Should().Throw<UnsafePathException>();
    }
}

public sealed class FileNameResolverTests
{
    [Fact]
    public void Prefers_rfc5987_filename_star()
    {
        var header = ContentDispositionHeaderValue.Parse("attachment; filename=\"fallback.pdf\"; filename*=UTF-8''%D8%AA%D9%82%D8%B1%D9%8A%D8%B1.pdf");

        FileNameResolver.Resolve(header, null, new Uri("https://x.test/a.bin"), null).Should().Be("تقرير.pdf");
    }

    [Fact]
    public void Uses_quoted_filename()
    {
        var header = ContentDispositionHeaderValue.Parse("attachment; filename=\"my file.zip\"");

        FileNameResolver.Resolve(header, null, null, null).Should().Be("my file.zip");
    }

    [Fact]
    public void Falls_back_to_hint_then_final_url_then_original_url()
    {
        FileNameResolver.Resolve(null, "hint.exe", new Uri("https://cdn.test/x/final.bin"), null).Should().Be("hint.exe");
        FileNameResolver.Resolve(null, null, new Uri("https://cdn.test/x/final%20name.bin?token=abc"), new Uri("https://x.test/orig.bin")).Should().Be("final name.bin");
        FileNameResolver.Resolve(null, null, new Uri("https://cdn.test/"), new Uri("https://x.test/orig.bin")).Should().Be("orig.bin");
        FileNameResolver.Resolve(null, null, new Uri("https://cdn.test/"), new Uri("https://x.test/")).Should().Be("download");
    }

    [Fact]
    public void Encoded_traversal_in_url_is_sanitized()
    {
        FileNameResolver.Resolve(null, null, new Uri("https://x.test/files/..%2F..%2Fevil.exe"), null).Should().Be("evil.exe");
    }
}

public sealed class UniquePathTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Free_name_is_kept()
    {
        UniquePath.Allocate(_temp.Combine("a.zip")).Should().Be(_temp.Combine("a.zip"));
    }

    [Fact]
    public void Existing_file_partial_file_or_reserved_name_is_skipped()
    {
        File.WriteAllText(_temp.Combine("a.zip"), "");
        File.WriteAllText(_temp.Combine("a (1).zip.pgpart"), "");
        var reserved = _temp.Combine("a (2).zip");

        UniquePath.Allocate(_temp.Combine("a.zip"), p => p == reserved).Should().Be(_temp.Combine("a (3).zip"));
    }

    [Fact]
    public void Works_without_extension()
    {
        File.WriteAllText(_temp.Combine("README"), "");

        UniquePath.Allocate(_temp.Combine("README")).Should().Be(_temp.Combine("README (1)"));
    }
}
