using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace GitLucky;

internal static class Program
{
    private static int Main(string[] args) => Run(args, workingDirectory: null);

    internal static int Run(string[] args, string? workingDirectory)
    {
        if (!Cli.Parse(args, out var prefixBytes, out var trailingNibble, out var useMaxCores))
            return 1;

        // Lower process priority so the machine stays responsive during search
        using var currentProcess = Process.GetCurrentProcess();
        currentProcess.PriorityClass = ProcessPriorityClass.BelowNormal;

        var objectFormat = Git.GetObjectFormat(workingDirectory);
        var useSha256 = objectFormat == "sha256";
        var commitFile = Git.GetHeadCommitFile(workingDirectory);

        // Strip gpgsig/gpgsig-sha256 headers. These are multi-line headers
        // where continuation lines start with a space. We strip them because
        // git commit --amend would generate a new (different) signature,
        // making the predicted hash wrong. We use --no-gpg-sign instead.
        commitFile = Regex.Replace(commitFile, @"^gpgsig(?:-sha256)? .*\n(?: .*\n)*", "", RegexOptions.Multiline);

        var commitMessageStartsAt = commitFile.IndexOf("\n\n", StringComparison.Ordinal) + 2;
        var commitMessage = commitFile.Substring(commitMessageStartsAt);

        // Build the git object as bytes. Use byte count (not string char count)
        // for the header, as they differ for non-ASCII content.
        var commitContentBytes = Git.Encoding.GetBytes(commitFile);
        var objectHeader = Git.Encoding.GetBytes($"commit {commitContentBytes.Length}\0");
        var objectTemplate = new byte[objectHeader.Length + commitContentBytes.Length];
        objectHeader.CopyTo(objectTemplate, 0);
        commitContentBytes.CopyTo(objectTemplate, objectHeader.Length);

        var prefixHexLength = prefixBytes.Length * 2 + (trailingNibble != null ? 1 : 0);
        var expectedHashes = Math.Pow(16, prefixHexLength) / 2;

        var done = 0;
        using ManualResetEventSlim doneEvent = new(false);

        var foundAuthorTime = 0u;
        var foundCommitTime = 0u;
        var authorTz = "";
        var committerTz = "";
        var threadCount = useMaxCores
            ? Environment.ProcessorCount
            : Math.Max(1, Environment.ProcessorCount - 1);
        var hashCountTotal = 0L;
        
        const int FlushInterval = 100_000;

        var threads = Enumerable.Range(0, threadCount)
            .Select(threadId => new Thread(
                () =>
                {
                    var bytes = (byte[])objectTemplate.Clone();
                    var authorTimeSpan = FindTime(bytes, "author", out uint originalAuthorTime, out var origAuthorTz);
                    var commitTimeSpan = FindTime(bytes, "committer", out uint originalCommitTime, out var origCommitterTz);

                    // Minimum timestamps that preserve the original digit count.
                    // WriteNum overwrites a fixed-width span, so shrinking the digit
                    // count would leave a leading space that git would never write.
                    var minAuthorTime = MinSameDigitCount(originalAuthorTime);
                    var minCommitTime = MinSameDigitCount(originalCommitTime);

                    var prefixSpan = prefixBytes.AsSpan();
                    var enumerator = Deltas();

                    for (var i = 0; i < threadId; i++)
                        enumerator.MoveNext();

                    var hashCount = 0L;
                    Span<byte> hashBuf = stackalloc byte[useSha256 ? 32 : 20];

                    while (done == 0)
                    {
                        var (authorTime, commitTime) = enumerator.Current;

                        var newAuthorTime = originalAuthorTime - authorTime;
                        var newCommitTime = originalCommitTime - commitTime;

                        // Skip deltas that would reduce the digit count
                        if (newAuthorTime < minAuthorTime || newCommitTime < minCommitTime)
                        {
                            for (var i = 0; i < threadCount; i++)
                                enumerator.MoveNext();
                            continue;
                        }

                        WriteNum(authorTimeSpan, newAuthorTime);
                        WriteNum(commitTimeSpan, newCommitTime);

                        if (useSha256)
                            SHA256.TryHashData(bytes, hashBuf, out _);
                        else
                            SHA1.TryHashData(bytes, hashBuf, out _);

                        hashCount++;

                        if (hashCount % FlushInterval == 0)
                        {
                            Interlocked.Add(ref hashCountTotal, FlushInterval);
                        }

                        if (hashBuf.StartsWith(prefixSpan))
                        {
                            if (trailingNibble == null || (hashBuf[prefixSpan.Length] & 0xF0) == trailingNibble)
                            {
                                if (Interlocked.Exchange(ref done, 1) == 0)
                                {
                                    doneEvent.Set();
                                    foundAuthorTime = newAuthorTime;
                                    foundCommitTime = newCommitTime;
                                    authorTz = origAuthorTz;
                                    committerTz = origCommitterTz;
                                    break;
                                }
                            }
                        }

                        for (var i = 0; i < threadCount; i++)
                            enumerator.MoveNext();
                    }

                    // Flush remaining unflushed hashes
                    Interlocked.Add(ref hashCountTotal, hashCount % FlushInterval);
                },
                maxStackSize: 1024))
            .ToList();

        var showProgress = !Console.IsOutputRedirected;
        var sw = Stopwatch.StartNew();

        foreach (var thread in threads)
            thread.Start();

        // Progress loop: wait for signal or 500ms timeout
        if (showProgress)
        {
            while (!doneEvent.Wait(500))
            {
                var totalHashes = Interlocked.Read(ref hashCountTotal);
                var elapsed = sw.Elapsed;
                var rate = elapsed.TotalSeconds > 0 ? totalHashes / elapsed.TotalSeconds : 0;
                var fraction = totalHashes / expectedHashes;

                WriteProgress(totalHashes, rate, fraction, expectedHashes, elapsed);
            }
        }

        foreach (var thread in threads)
            thread.Join();

        sw.Stop();
        var finalTotal = Interlocked.Read(ref hashCountTotal);
        var finalRate = sw.Elapsed.TotalSeconds > 0 ? finalTotal / sw.Elapsed.TotalSeconds : 0;

        if (showProgress)
            Console.Write("\r" + new string(' ', GetConsoleWidth()) + "\r");

        Console.WriteLine($"{finalTotal:N0} hashes in {sw.Elapsed.TotalMilliseconds:N0} ms ({finalRate:N0}/sec)");

        if (done == 0)
        {
            Console.Error.WriteLine("No match found");
            return 2;
        }

        Console.Out.WriteLine("Match found");

        Git.Amend(foundAuthorTime, authorTz, foundCommitTime, committerTz, commitMessage, workingDirectory);

        return 0;

        static Span<byte> FindTime(byte[] bytes, string label, out uint baseTime, out string timezone)
        {
            // Scan the byte array directly rather than using regex on a string,
            // as string char indexes diverge from byte indexes for non-ASCII content.
            var span = bytes.AsSpan();
            var needle = Git.Encoding.GetBytes($"\n{label} ");
            int labelPos = span.IndexOf(needle);
            if (labelPos < 0)
                throw new InvalidOperationException($"Could not find {label} in commit object");

            // Find "> " after the label (precedes the timestamp)
            ReadOnlySpan<byte> gtSpace = [(byte)'>', (byte)' '];
            int gtPos = span.Slice(labelPos).IndexOf(gtSpace);
            int timeStart = labelPos + gtPos + 2;

            // Read timestamp digits
            int timeEnd = timeStart;
            while (timeEnd < span.Length && span[timeEnd] >= (byte)'0' && span[timeEnd] <= (byte)'9')
                timeEnd++;

            baseTime = 0;
            for (int k = timeStart; k < timeEnd; k++)
                baseTime = baseTime * 10 + (uint)(span[k] - '0');

            // Timezone is 5 chars after the space: e.g. "+0530"
            timezone = Git.Encoding.GetString(bytes, timeEnd + 1, 5);

            return bytes.AsSpan(timeStart, timeEnd - timeStart);
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

        static uint MinSameDigitCount(uint value)
        {
            uint min = 1;
            while (min * 10 <= value)
                min *= 10;
            return min;
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

        static void WriteProgress(long totalHashes, double rate, double fraction, double expectedHashes, TimeSpan elapsed)
        {
            var width = GetConsoleWidth();
            // Build the status text first so we can size the bar
            var hashStr = FormatCount(totalHashes);
            var rateStr = rate > 0 ? FormatCount((long)rate) + "/s" : "...";
            var etaStr = rate > 0 ? "~" + FormatDuration(TimeSpan.FromSeconds(Math.Max(0, (expectedHashes - totalHashes) / rate))) : "...";
            var pct = Math.Min(fraction, 0.999);
            var status = $" {pct:P0} | {hashStr} | {rateStr} | ETA {etaStr}";

            // Bar takes remaining width: [ + bar + ] + status
            var barWidth = width - status.Length - 3; // 3 = '[' + ']' + ' ' padding
            if (barWidth < 5) barWidth = 5;

            var filled = (int)(pct * barWidth);
            var bar = $"[{new string('\u2588', filled)}{new string('\u2591', barWidth - filled)}]{status}";

            if (bar.Length > width)
                bar = bar.Substring(0, width);

            Console.Write("\r" + bar);
        }

        static string FormatCount(long count) => count switch
        {
            >= 1_000_000_000 => $"{count / 1_000_000_000.0:F1}B",
            >= 1_000_000 => $"{count / 1_000_000.0:F1}M",
            >= 1_000 => $"{count / 1_000.0:F1}K",
            _ => count.ToString("N0")
        };

        static string FormatDuration(TimeSpan ts) => ts.TotalSeconds switch
        {
            < 1 => "<1s",
            < 60 => $"{ts.TotalSeconds:F0}s",
            < 3600 => $"{(int)ts.TotalMinutes}m {ts.Seconds}s",
            _ => $"{(int)ts.TotalHours}h {ts.Minutes}m"
        };

        static int GetConsoleWidth()
        {
            try { return Console.WindowWidth; }
            catch { return 80; }
        }
    }
}
