# NuGet Feed Duplicater

Interactive CLI tool to copy NuGet packages from one feed to another. Supports both V2 and V3 feeds.

## Usage

```
dotnet run --project NugetFeedDuplicater
```

The tool will prompt for:
- Source and destination feed URLs
- API keys
- Copy latest version only or all versions
- Package name filter (`BuildingBlocks` for partial match, `*` for all)

## Feed URL examples

- V3: `https://bagetter.example.com/v3/index.json`
- V2: `https://nuget.example.com/nuget`

## Publish

```
dotnet publish NugetFeedDuplicater -c Release
```
