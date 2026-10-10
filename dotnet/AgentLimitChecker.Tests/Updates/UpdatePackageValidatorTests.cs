using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;

public class UpdatePackageValidatorTests
{
    private static UpdatePackageEntry Entry(string name, long length = 10) => new(name, length);

    private static UpdatePackageValidationResult Validate(params UpdatePackageEntry[] entries) =>
        UpdatePackageValidator.Validate(entries);

    [Fact]
    public void Validate_FlatPackageWithExecutable_IsAccepted()
    {
        var result = Validate(Entry("AgentLimitChecker.exe"), Entry("manual.html"), Entry("license.html"));

        Assert.True(result.IsValid);
        Assert.Equal(["AgentLimitChecker.exe", "manual.html", "license.html"], result.FileNames);
    }

    [Fact]
    public void Validate_ExecutableNameIsMatchedIgnoringCase()
    {
        Assert.True(Validate(Entry("agentlimitchecker.EXE")).IsValid);
    }

    [Fact]
    public void Validate_WithoutExecutableAtTop_IsRejected()
    {
        Assert.False(Validate(Entry("manual.html")).IsValid);
    }

    [Fact]
    public void Validate_EmptyPackage_IsRejected()
    {
        Assert.False(Validate().IsValid);
    }

    [Theory]
    [InlineData("AgentLimitChecker/AgentLimitChecker.exe")]
    [InlineData(@"AgentLimitChecker\AgentLimitChecker.exe")]
    [InlineData("docs/")]
    [InlineData(@"docs\")]
    [InlineData("/AgentLimitChecker.exe")]
    [InlineData(@"\AgentLimitChecker.exe")]
    [InlineData("../evil.dll")]
    public void Validate_SubpathsAndDirectories_AreRejected(string name)
    {
        Assert.False(Validate(Entry("AgentLimitChecker.exe"), Entry(name)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("C:evil.dll")]
    [InlineData("evil.dll:stream")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL.html")]
    [InlineData("COM1")]
    [InlineData("lpt9.log")]
    [InlineData("AgentLimitChecker.exe.old")]
    [InlineData("name.OLD")]
    [InlineData("what?.txt")]
    [InlineData("tab\t.txt")]
    public void Validate_DangerousNames_AreRejected(string name)
    {
        Assert.False(Validate(Entry("AgentLimitChecker.exe"), Entry(name)).IsValid);
    }

    [Fact]
    public void Validate_DuplicateNamesIgnoringCase_AreRejected()
    {
        Assert.False(Validate(Entry("AgentLimitChecker.exe"), Entry("manual.html"), Entry("MANUAL.html")).IsValid);
    }

    [Fact]
    public void Validate_EntryCountAtLimit_IsAccepted()
    {
        var entries = Enumerable.Range(1, UpdatePackageValidator.MaxEntryCount - 1)
            .Select(index => Entry($"file{index}.txt"))
            .Append(Entry("AgentLimitChecker.exe"))
            .ToArray();

        Assert.True(Validate(entries).IsValid);
    }

    [Fact]
    public void Validate_EntryCountOverLimit_IsRejected()
    {
        var entries = Enumerable.Range(1, UpdatePackageValidator.MaxEntryCount)
            .Select(index => Entry($"file{index}.txt"))
            .Append(Entry("AgentLimitChecker.exe"))
            .ToArray();

        Assert.False(Validate(entries).IsValid);
    }

    [Fact]
    public void Validate_TotalSizeAtLimit_IsAccepted()
    {
        var half = UpdatePackageValidator.MaxTotalExtractedBytes / 2;

        Assert.True(Validate(Entry("AgentLimitChecker.exe", half), Entry("manual.html", half)).IsValid);
    }

    [Fact]
    public void Validate_TotalSizeOverLimit_IsRejected()
    {
        var half = UpdatePackageValidator.MaxTotalExtractedBytes / 2;

        Assert.False(Validate(Entry("AgentLimitChecker.exe", half), Entry("manual.html", half + 1)).IsValid);
    }

    [Fact]
    public void Validate_HugeSizesDoNotOverflow()
    {
        Assert.False(Validate(Entry("AgentLimitChecker.exe", long.MaxValue), Entry("manual.html", long.MaxValue)).IsValid);
    }
}
