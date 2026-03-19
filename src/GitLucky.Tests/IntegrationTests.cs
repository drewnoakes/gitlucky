using System.Diagnostics;
using System.Text;

namespace GitLucky.Tests;

public class IntegrationTests : IDisposable
{
    private readonly string _repoDir;

    public IntegrationTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "gitlucky-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoDir);

        RunGitInDir("init");
        RunGitInDir("config user.email test@test.com");
        RunGitInDir("config user.name TestUser");
        RunGitInDir("config commit.gpgsign false");
        RunGitInDir("commit --allow-empty -m \"initial commit\"");
    }

    public void Dispose()
    {
        // Make all files writable before deleting (git marks some read-only)
        foreach (var file in Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_repoDir, recursive: true);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("00")]
    [InlineData("000")]
    [InlineData("ab")]
    [InlineData("abc")]
    public void AmendProducesExpectedPrefix(string prefix)
    {
        var exitCode = RunGitLucky(prefix);

        Assert.Equal(0, exitCode);

        var actualHash = RunGitInDir("rev-parse HEAD").Trim();
        Assert.StartsWith(prefix, actualHash);
    }

    [Fact]
    public void AmendPreservesCommitMessage()
    {
        var message = "test message for preservation";
        RunGitInDir($"commit --allow-empty -m \"{message}\"");

        RunGitLucky("0");

        var actualMessage = RunGitInDir("log -1 --format=%s").Trim();
        Assert.Equal(message, actualMessage);
    }

    [Fact]
    public void AmendPreservesTimezone()
    {
        // Get the original timezone offset
        var originalTz = RunGitInDir("log -1 --format=%ai").Trim();
        var tzOffset = originalTz[^5..]; // e.g. "+1100"

        RunGitLucky("0");

        // Verify timezone is preserved
        var newTz = RunGitInDir("log -1 --format=%ai").Trim();
        var newTzOffset = newTz[^5..];
        Assert.Equal(tzOffset, newTzOffset);
    }

    [Fact]
    public void WorksWithUnsignedCommitWhenGpgSignEnabled()
    {
        // Enable gpg signing globally — the tool should still work
        // because it uses --no-gpg-sign
        RunGitInDir("config commit.gpgsign true");

        var exitCode = RunGitLucky("0");

        Assert.Equal(0, exitCode);

        var actualHash = RunGitInDir("rev-parse HEAD").Trim();
        Assert.StartsWith("0", actualHash);

        // Clean up
        RunGitInDir("config commit.gpgsign false");
    }

    private int RunGitLucky(string prefix)
    {
        var exitCode = Program.Run(new[] { prefix }, _repoDir);
        return exitCode;
    }

    private string RunGitInDir(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _repoDir,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false
        };

        using var proc = Process.Start(startInfo)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var error = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {arguments} failed ({proc.ExitCode}): {error}");
        }

        return output;
    }
}
