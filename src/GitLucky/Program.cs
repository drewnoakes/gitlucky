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
            var commitMessageStartsAt = commitFile.IndexOf("\n\n", StringComparison.Ordinal) + 2;
            var commitMessage = commitFile.Substring(commitMessageStartsAt);
            var commitText = $"commit {commitFile.Length}\0{commitFile}";
            var done = 0;
            var foundAuthorTime = 0u;
            var foundCommitTime = 0u;
            var threadCount = Environment.ProcessorCount;
            var hashCountTotal = 0;

            var threads = Enumerable.Range(0, threadCount)
                .Select(threadId => new Thread(
                    () =>
                    {
                        var bytes = Git.Encoding.GetBytes(commitText);
                        var authorTimeSpan = FindTime(bytes, "author", out uint originalAuthorTime);
                        var commitTimeSpan = FindTime(bytes, "committer", out uint originalCommitTime);
                        var sha = new SHA1CryptoServiceProvider();
                        var prefixSpan = prefixBytes.AsSpan();
                        var enumerator = Deltas().GetEnumerator();

                        for (var i = 0; i < threadId; i++)
                            enumerator.MoveNext();

                        int hashCount = 0;

                        while (done == 0)
                        {
                            var delta = enumerator.Current;

                            WriteNum(authorTimeSpan, originalAuthorTime - delta.author);
                            WriteNum(commitTimeSpan, originalCommitTime - delta.commit);

                            var hash = sha.ComputeHash(bytes);

                            hashCount++;

                            if (hash.AsSpan().StartsWith(prefixSpan))
                            {
                                if (trailingNibble == null || (hash[prefixSpan.Length] & 0xF0) == trailingNibble)
                                {
                                    if (Interlocked.CompareExchange(ref done, 1, 0) == 0)
                                    {
                                        foundAuthorTime = originalAuthorTime - delta.author;
                                        foundCommitTime = originalCommitTime - delta.commit;
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

            Git.Amend(foundAuthorTime, foundCommitTime, commitMessage);

            return 0;

            Span<byte> FindTime(byte[] bytes, string label, out uint baseTime)
            {
                var regex = new Regex($@"^{label}.+> ([0-9]*)", RegexOptions.Multiline);
                var group = regex.Match(commitText).Groups[1];
                baseTime = uint.Parse(group.Value);
                return bytes.AsSpan(group.Index, group.Length);
            }

            IEnumerable<(uint author, uint commit)> Deltas()
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

            void WriteNum(Span<byte> span, uint number)
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
