using System.Security.Cryptography;
using System.Text;
using OB2.Core.Updates;

namespace OB2.Core.Tests.Updates;

/// <summary>D-84: release signatures.</summary>
public sealed class ReleaseSignatureTests
{
    private static readonly byte[] Sums = Encoding.ASCII.GetBytes("abc  OB2-portable.zip\r\n");

    [Fact]
    public void A_signature_by_a_trusted_key_is_valid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = ReleaseSignature.Sign(Sums, key);

        ReleaseSignature.Verify(Sums, signature, [ReleaseSignature.PublicKeyOf(key)]).Should().BeTrue();
        ReleaseSignature.Verify(Sums, signature + "\n", [ReleaseSignature.PublicKeyOf(key)]).Should().BeTrue("the .sig file ends with a newline");
    }

    [Fact]
    public void A_changed_checksum_file_is_not_valid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = ReleaseSignature.Sign(Sums, key);
        var changed = (byte[])Sums.Clone();
        changed[0] = (byte)'b';

        ReleaseSignature.Verify(changed, signature, [ReleaseSignature.PublicKeyOf(key)]).Should().BeFalse();
    }

    [Fact]
    public void A_signature_by_any_other_key_is_not_valid()
    {
        using var developer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        ReleaseSignature.Verify(Sums, ReleaseSignature.Sign(Sums, attacker), [ReleaseSignature.PublicKeyOf(developer)]).Should().BeFalse();
        ReleaseSignature.Verify(Sums, ReleaseSignature.Sign(Sums, attacker), []).Should().BeFalse("no trusted key, nothing is valid");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]
    public void Malformed_signatures_are_not_valid(string? signature)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        ReleaseSignature.Verify(Sums, signature, [ReleaseSignature.PublicKeyOf(key)]).Should().BeFalse();
    }

    [Fact]
    public void Any_of_several_trusted_keys_will_do_and_a_broken_entry_is_skipped()
    {
        using var old = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        ReleaseSignature.Verify(Sums, ReleaseSignature.Sign(Sums, current), ["garbage", ReleaseSignature.PublicKeyOf(old), ReleaseSignature.PublicKeyOf(current)])
            .Should().BeTrue();
    }

    [Fact]
    public void The_shipped_key_list_holds_only_P256_public_keys()
    {
        foreach (var key in ReleaseKey.Trusted)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            ecdsa.KeySize.Should().Be(256);
        }
    }
}
