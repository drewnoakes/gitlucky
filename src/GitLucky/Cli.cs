using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GitLucky
{
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

            if (!Regex.IsMatch(prefix, "^[a-fA-F0-9]{1,40}$"))
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
                Console.Out.WriteLine("GitLucky");
                Console.Out.WriteLine();
                Console.Out.WriteLine("Amends the last git commit to have the desired SHA-1 prefix.");
                Console.Out.WriteLine();
                Console.Out.WriteLine("This is done by searching for negative deltas to the author and commit");
                Console.Out.WriteLine("timestamps such that the resulting commit hash starts with a given prefix.");
                Console.Out.WriteLine("The longer the prefix, the more time it will take to find a match.");
                Console.Out.WriteLine();
                Console.Out.WriteLine("Usage:");
                Console.Out.WriteLine("\tGitLucky <prefix>");
                Console.Out.WriteLine();
                Console.Out.WriteLine("\t<prefix>\tThe desired commit SHA prefix, in hex");
            }
        }
    }
}