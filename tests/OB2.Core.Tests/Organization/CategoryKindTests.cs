using OB2.Core.Organization;

namespace OB2.Core.Tests.Organization;

/// <summary>D-86: a category's icon comes from what most of its extensions are.</summary>
public sealed class CategoryKindTests
{
    /// <summary>The five categories every database starts with (migration 002), extension for extension.</summary>
    [Theory]
    [InlineData("pdf,doc,docx,xls,xlsx,ppt,pptx,txt,rtf,odt,ods,odp,csv,epub,md", CategoryKind.Documents)]
    [InlineData("mp3,flac,wav,m4a,aac,ogg,oga,wma,opus,mid", CategoryKind.Music)]
    [InlineData("mp4,mkv,avi,mov,wmv,webm,flv,m4v,mpg,mpeg,ts,3gp", CategoryKind.Video)]
    [InlineData("exe,msi,msix,msixbundle,appx,apk,bat,cmd,ps1,dmg,deb,rpm,appimage", CategoryKind.Programs)]
    [InlineData("zip,rar,7z,tar,gz,tgz,bz2,xz,zst,iso,img,cab", CategoryKind.Compressed)]
    public void Each_starting_category_gets_its_own_kind(string extensions, CategoryKind expected) =>
        CategoryKinds.Of(Category.ParseExtensions(extensions)).Should().Be(expected);

    [Fact]
    public void A_category_the_user_adds_is_read_by_its_extensions_not_its_name() =>
        CategoryKinds.Of(["jpg", "png", "webp"]).Should().Be(CategoryKind.Pictures);

    [Fact]
    public void The_majority_wins_when_a_category_mixes_types() =>
        CategoryKinds.Of(["mp3", "flac", "wav", "mp4"]).Should().Be(CategoryKind.Music);

    [Theory]
    [InlineData("mp3,mp4")]
    [InlineData("mp3,mp4,xyz")]
    [InlineData("jpg,mp3,zip")]
    public void A_tie_has_no_kind(string extensions) =>
        CategoryKinds.Of(Category.ParseExtensions(extensions)).Should().Be(CategoryKind.Other);

    [Fact]
    public void No_extensions_or_only_unknown_ones_have_no_kind()
    {
        CategoryKinds.Of([]).Should().Be(CategoryKind.Other);
        CategoryKinds.Of(["xyz", "torrent"]).Should().Be(CategoryKind.Other);
    }

    [Fact]
    public void Double_extensions_dots_and_case_are_read_like_the_resolver_reads_them() =>
        CategoryKinds.Of(["tar.gz", ".ZIP", " 7z "]).Should().Be(CategoryKind.Compressed);

    /// <summary>D-87: Grab links sorts single files with the same table.</summary>
    [Theory]
    [InlineData("Talk.MP4", CategoryKind.Video)]
    [InlineData("backup.tar.gz", CategoryKind.Compressed)]
    [InlineData("C:\\folder.v2\\notes.pdf", CategoryKind.Documents)]
    [InlineData("data.torrent", CategoryKind.Other)]
    [InlineData("README", CategoryKind.Other)]
    [InlineData("trailing.", CategoryKind.Other)]
    [InlineData("", CategoryKind.Other)]
    [InlineData(null, CategoryKind.Other)]
    public void A_single_file_is_read_by_its_last_extension(string? fileName, CategoryKind expected) =>
        CategoryKinds.OfFile(fileName).Should().Be(expected);
}
