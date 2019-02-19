# GitLucky 🍀

Amends the last git commit to have the desired SHA-1 prefix.

This is done by searching for negative deltas to the author and commit
timestamps such that the resulting commit hash starts with a given prefix.
The longer the prefix, the more time it will take to find a match.

## Installation

Install as a global tool on your computer with:

```
$ dotnet tool install -g GitLucky
```

## Usage

```
gitlucky <prefix>
```

Where `<prefix>` is the desired commit SHA prefix, in hex.

## Example

```
$ git log --oneline -1
cd1e69a (HEAD -> master) Most recent commit

$ gitlucky 123456
128,161,098 hashes in 27,286 ms (4,696,812/sec)
Match found

$ git log --oneline -1
1234560 (HEAD -> master) Most recent commit
```

## Disclaimer

This is just for fun. Use it at your own risk.