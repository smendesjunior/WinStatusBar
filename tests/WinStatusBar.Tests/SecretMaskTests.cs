using WinStatusBar.Core.Clip;
using Xunit;
namespace WinStatusBar.Tests;

public class SecretMaskTests
{
    [Fact]
    public void ApiKeysAreMaskedButNormalTextStays()
    {
        string key = "sk-proj-" + new string('a', 40) + "XYZ9";
        string masked = SecretMask.Mask("chave: " + key + " fim");
        Assert.DoesNotContain(key, masked);
        Assert.StartsWith("chave: sk-proj-", masked);
        Assert.EndsWith("XYZ9 fim", masked);
        Assert.Equal("Texto comum sem segredos, com sk-curto.", SecretMask.Mask("Texto comum sem segredos, com sk-curto."));
    }

    [Fact]
    public void PrivateKeyBlockIsHidden()
    {
        string pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEow" + new string('Q', 60) + "\n-----END RSA PRIVATE KEY-----";
        Assert.DoesNotContain("MIIEow", SecretMask.Mask(pem));
    }
}
