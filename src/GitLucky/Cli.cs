using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GitLucky;

internal static class Cli
{
    public static bool Parse(string[] args, [NotNullWhen(returnValue: true)] out byte[]? prefixBytes, out int? trailingNibble)
    {
        prefixBytes = default;
        trailingNibble = default;

        if (args.Length != 1)
        {
            Console.Error.WriteLine("Must pass a prefix as an argument.");
            Console.Out.WriteLine();
            PrintUsage();
            return false;
        }

        var prefix = args[0];

        switch (args[0])
        {
            case "--help":
            case "-h":
            case "/?":
            case "-?":
                PrintUsage();
                return false;
        }

        if (!Regex.IsMatch(prefix, "^[a-fA-F0-9]{1,64}$"))
        {
            Console.Error.WriteLine($"Unable to parse prefix \"{prefix}\".");
            Console.Out.WriteLine();
            PrintUsage();
            return false;
        }

        prefix = prefix.ToLower();

        var byteCount = prefix.Length >> 1;
        prefixBytes = new byte[byteCount];

        for (int b = 0, h = 0; b < byteCount; b++)
        {
            prefixBytes[b] = (byte)(
                (ToHexNibble(prefix[h++]) << 4) +
                ToHexNibble(prefix[h++]));
        }

        trailingNibble = prefix.Length % 2 != 0
            ? ToHexNibble(prefix[^1]) << 4
            : default;
        return true;

        static int ToHexNibble(char c) => c - (c < '9' ? '0' : 87);

        static void PrintUsage()
        {
            var clover = Console.OutputEncoding.CodePage == 65001 ? " 🍀" : "";
            Console.Out.WriteLine($"""
                GitLucky{clover}

                Amends the last git commit to have the desired hash prefix.

                This is done by searching for negative deltas to the author and commit
                timestamps such that the resulting commit hash starts with a given prefix.
                The longer the prefix, the more time it will take to find a match.

                Supports both SHA-1 and SHA-256 repositories.

                Usage:
                	GitLucky <prefix>

                	<prefix>	The desired commit SHA prefix, in hex
                """);
        }
    }
}