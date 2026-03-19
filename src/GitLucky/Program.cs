using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace GitLucky
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (!Cli.Parse(args, out var prefixBytes, out var trailingNibble))
                return 1;

            var commitFile = Git.GetHeadCommitFile();

            // Strip gpgsig/gpgsig-sha256 headers. These are multi-line headers
            // where continuation lines start with a space. We strip them because
            // git commit --amend would generate a new (different) signature,
            // making the predicted hash wrong. We use --no-gpg-sign instead.
            commitFile = Regex.Replace(commitFile, @"^gpgsig(?:-sha256)? .*\n(?: .*\n)*", "", RegexOptions.Multiline);

            var commitMessageStartsAt = commitFile.IndexOf("\n\n", StringComparison.Ordinal) + 2;
            var commitMessage = commitFile.Substring(commitMessageStartsAt);
            var commitText = $"commit {commitFile.Length}\0{commitFile}";
            var done = 0;
            var foundAuthorTime = 0u;
            var foundCommitTime = 0u;
            var authorTz = "";
            var committerTz = "";
            var threadCount = Environment.ProcessorCount;
            var hashCountTotal = 0L;

            var threads = Enumerable.Range(0, threadCount)
                .Select(threadId => new Thread(
                    () =>
                    {
                        var bytes = Git.Encoding.GetBytes(commitText);
                        var authorTimeSpan = FindTime(bytes, "author", out uint originalAuthorTime, out var origAuthorTz);
                        var commitTimeSpan = FindTime(bytes, "committer", out uint originalCommitTime, out var origCommitterTz);
                        var prefixSpan = prefixBytes.AsSpan();
                        var enumerator = Deltas();

                        for (var i = 0; i < threadId; i++)
                            enumerator.MoveNext();

                        var hashCount = 0L;

                        while (done == 0)
                        {
                            var (authorTime, commitTime) = enumerator.Current;

                            WriteNum(authorTimeSpan, originalAuthorTime - authorTime);
                            WriteNum(commitTimeSpan, originalCommitTime - commitTime);

                            var hash = SHA1.HashData(bytes);

                            hashCount++;

                            if (hash.AsSpan().StartsWith(prefixSpan))
                            {
                                if (trailingNibble == null || (hash[prefixSpan.Length] & 0xF0) == trailingNibble)
                                {
                                    if (Interlocked.CompareExchange(ref done, 1, 0) == 0)
                                    {
                                        foundAuthorTime = originalAuthorTime - authorTime;
                                        foundCommitTime = originalCommitTime - commitTime;
                                        authorTz = origAuthorTz;
                                        committerTz = origCommitterTz;
                                        break;
                                    }
                                }
                            }

                            for (var i = 0; i < threadCount; i++)
                                enumerator.MoveNext();
                        }

                        Interlocked.Add(ref hashCountTotal, hashCount);
                    },
                    maxStackSize: 1024))
                .ToList();

            var sw = Stopwatch.StartNew();

            foreach (var thread in threads)
                thread.Start();

            foreach (var thread in threads)
                thread.Join();

            Console.WriteLine($"{hashCountTotal:N0} hashes in {sw.Elapsed.TotalMilliseconds:N0} ms ({hashCountTotal / sw.Elapsed.TotalSeconds:N0}/sec)");

            if (done == 0)
            {
                Console.Error.WriteLine("No match found");
                return 2;
            }

            Console.Out.WriteLine("Match found");

            Git.Amend(foundAuthorTime, authorTz, foundCommitTime, committerTz, commitMessage);

            return 0;

            Span<byte> FindTime(byte[] bytes, string label, out uint baseTime, out string timezone)
            {
                var regex = new Regex($@"^{label}.+> ([0-9]+) ([\+\-]\d{{4}})", RegexOptions.Multiline);
                var match = regex.Match(commitText);
                var group = match.Groups[1];
                baseTime = uint.Parse(group.Value);
                timezone = match.Groups[2].Value;
                return bytes.AsSpan(group.Index, group.Length);
            }

            static IEnumerator<(uint author, uint commit)> Deltas()
            {
                yield return (0, 0);

                var i = 1u;
                while (true)
                {
                    for (var j = 0u; j < i - 1; j++)
                        yield return (j, i);
                    for (var j = 0u; j <= i; j++)
                        yield return (i, j);
                    i++;
                }
            }

            static void WriteNum(Span<byte> span, uint number)
            {
                var t = span.Length - 1;

                while (t >= 0)
                {
                    span[t] = (byte) (number == 0 ? ' ' : '0' + (number % 10));
                    number /= 10;
                    t--;
                }
            }
        }
    }
}
