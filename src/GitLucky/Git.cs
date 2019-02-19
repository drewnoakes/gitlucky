using System;
using System.Diagnostics;
using System.Text;

namespace GitLucky
{
    internal static class Git
    {
        public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static string GetHeadCommitFile()
        {
            var headSha1 = GetProcessOutputString("rev-parse HEAD");
            var patch = GetProcessOutputString($"cat-file -p {headSha1.Trim()}");
            return patch;

            string GetProcessOutputString(string args)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git.exe",
                    Arguments = args,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding,
                    UseShellExecute = false
                };

                using (var proc = Process.Start(startInfo))
                {
                    return proc.StandardOutput.ReadToEnd();
                }
            }
        }

        public static void Amend(uint foundAuthorTime, uint foundCommitTime, string commitMessage)
        {
            using (var proc = new Process())
            {
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = "git.exe",
                    Arguments = $"commit --amend --allow-empty --date={foundAuthorTime} --file=-",
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = Encoding,
                    UseShellExecute = false,
                    Environment =
                    {
                        {"GIT_COMMITTER_DATE", foundCommitTime.ToString()}
                    }
                };

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