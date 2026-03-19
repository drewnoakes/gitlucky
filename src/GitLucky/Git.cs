using System;
using System.Diagnostics;
using System.Text;

namespace GitLucky
{
    internal static class Git
    {
        public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static string GetHeadCommitFile(string? workingDirectory = null)
        {
            var headSha = RunGit("rev-parse HEAD", workingDirectory).Trim();
            var content = RunGit($"cat-file -p {headSha}", workingDirectory);
            // Normalize line endings to LF. Git objects use LF internally,
            // but on Windows, process output may contain CRLF.
            return content.Replace("\r\n", "\n");
        }

        public static string GetObjectFormat(string? workingDirectory = null)
        {
            return RunGit("rev-parse --show-object-format", workingDirectory).Trim();
        }

        internal static string RunGit(string args, string? workingDirectory = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding,
                UseShellExecute = false
            };

            if (workingDirectory != null)
                startInfo.WorkingDirectory = workingDirectory;

            using (var proc = Process.Start(startInfo)!)
            {
                return proc.StandardOutput.ReadToEnd();
            }
        }

        public static void Amend(uint foundAuthorTime, string authorTz, uint foundCommitTime, string committerTz, string commitMessage, string? workingDirectory = null)
        {
            using (var proc = new Process())
            {
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"commit --amend --allow-empty --no-gpg-sign --date=\"{foundAuthorTime} {authorTz}\" --file=-",
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = Encoding,
                    UseShellExecute = false,
                    Environment =
                    {
                        {"GIT_COMMITTER_DATE", $"{foundCommitTime} {committerTz}"}
                    }
                };

                if (workingDirectory != null)
                    proc.StartInfo.WorkingDirectory = workingDirectory;

                proc.Start();

                proc.StandardInput.Write(commitMessage);
                proc.StandardInput.Flush();
                proc.StandardInput.Close();

                var output = proc.StandardOutput.ReadToEnd();
                var error = proc.StandardError.ReadToEnd();

                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    Console.Error.WriteLine($"Amend returned non-zero exit code {proc.ExitCode}:");
                    if (!string.IsNullOrWhiteSpace(output))
                        Console.Error.WriteLine(output);
                    if (!string.IsNullOrWhiteSpace(error))
                        Console.Error.WriteLine(error);
                }
            }
        }
    }
}