using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;

public class UpdateDownloadRedirectPolicyTests
{
    [Theory]
    [InlineData("https://github.com/release.zip", true)]
    [InlineData("https://github.com:443/release.zip", true)]
    [InlineData("https://objects.githubusercontent.com/release.zip", true)]
    [InlineData("https://release-assets.githubusercontent.com/release.zip", true)]
    [InlineData("https://githubusercontent.com/release.zip", false)]
    [InlineData("https://evilgithubusercontent.com/release.zip", false)]
    [InlineData("https://github.com.evil.example/release.zip", false)]
    public void IsAllowedLocation_UsesGitHubHostAndLabelBoundaries(string location, bool expected)
    {
        Assert.Equal(expected, UpdateDownloadRedirectPolicy.IsAllowedLocation(location));
    }

    [Theory]
    [InlineData("//github.com/release.zip")]
    [InlineData("/release.zip")]
    [InlineData("http://github.com/release.zip")]
    [InlineData("https://user@github.com/release.zip")]
    [InlineData("https://github.com:444/release.zip")]
    [InlineData("https://github.com/release.zip#fragment")]
    [InlineData("https://github.com/re lease.zip")]
    [InlineData("https://github.com/release.zip\\path")]
    [InlineData("https://github.com/release.zip\nnext")]
    public void IsAllowedLocation_RejectsUnsafeLocation(string location)
    {
        Assert.False(UpdateDownloadRedirectPolicy.IsAllowedLocation(location));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public void TryGetTarget_FollowsSupportedStatusCodes(int statusCode)
    {
        Assert.True(UpdateDownloadRedirectPolicy.TryGetTarget(
            statusCode,
            redirectsFollowed: 0,
            "https://github.com/release.zip",
            out var target));
        Assert.Equal("https://github.com/release.zip", target!.OriginalString);
    }

    [Fact]
    public void TryGetTarget_RejectsNonRedirectAndRedirectBeyondLimit()
    {
        Assert.False(UpdateDownloadRedirectPolicy.TryGetTarget(200, 0, "https://github.com/a.zip", out _));
        Assert.False(UpdateDownloadRedirectPolicy.TryGetTarget(302, UpdateDownloadRedirectPolicy.MaxRedirectCount, "https://github.com/a.zip", out _));
    }

    [Fact]
    public void CreateRequestUri_PreservesSignedQueryCharacters()
    {
        const string url = "https://objects.githubusercontent.com/release.zip?sig=a+b%2Bc%3Bd";

        var requestUri = UpdateDownloadRedirectPolicy.CreateRequestUri(url);
        Assert.Equal(url, requestUri.OriginalString);
        Assert.Equal("/release.zip?sig=a+b%2Bc%3Bd", requestUri.PathAndQuery);
    }
}
