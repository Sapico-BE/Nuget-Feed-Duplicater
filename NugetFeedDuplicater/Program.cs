using System.Diagnostics;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;

AnsiConsole.Write(new FigletText("NuGet Duplicater").Color(Color.Blue));
AnsiConsole.MarkupLine("[grey]Copy packages from one NuGet feed to another[/]");
AnsiConsole.WriteLine();

// --- Source feed ---
var sourceUrl = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]Source NuGet feed URL[/] [grey](V3: https://localhost/v3/index.json, V2: https://localhost/nuget):[/]"));
var sourceApiKey = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]Source API key[/] [grey](leave empty if none):[/]")
        .AllowEmpty());

var sourceRepo = CreateSourceRepository(sourceUrl);
var sourceResource = await sourceRepo.GetResourceAsync<PackageSearchResource>();
var sourceFindResource = await sourceRepo.GetResourceAsync<FindPackageByIdResource>();

await AnsiConsole.Status().StartAsync("Testing source feed...", async ctx =>
{
    try
    {
        await sourceResource.SearchAsync("test", new SearchFilter(includePrerelease: false), 0, 1,
            NullLogger.Instance, CancellationToken.None);
        AnsiConsole.MarkupLine("[green]✓ Source feed connected successfully[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]✗ Source feed connection failed: {Markup.Escape(ex.Message)}[/]");
        Environment.Exit(1);
    }
});

// --- Destination feed ---
var destUrl = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]Destination NuGet feed URL[/] [grey](V3: https://localhost/v3/index.json, V2: https://localhost/nuget):[/]"));
var destApiKey = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]Destination API key:[/]")
        .Secret());

await AnsiConsole.Status().StartAsync("Testing destination feed...", async ctx =>
{
    try
    {
        var destRepo = CreateSourceRepository(destUrl);
        var destSearch = await destRepo.GetResourceAsync<PackageSearchResource>();
        await destSearch.SearchAsync("test", new SearchFilter(includePrerelease: false), 0, 1,
            NullLogger.Instance, CancellationToken.None);
        AnsiConsole.MarkupLine("[green]✓ Destination feed connected successfully[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]✗ Destination feed connection failed: {Markup.Escape(ex.Message)}[/]");
        Environment.Exit(1);
    }
});

// --- Options ---
var copyAllVersions = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("[green]Which versions to copy?[/]")
        .AddChoices("Latest version only", "All versions")) == "All versions";

var packageFilter = AnsiConsole.Ask<string>(
    "[green]Package name filter[/] [grey](e.g. 'BuildingBlocks' or '*' for all):[/]");

// --- Discover packages ---
AnsiConsole.WriteLine();
var packagesToMigrate = new List<(string Id, List<NuGetVersion> Versions)>();

await AnsiConsole.Status().StartAsync("Discovering packages...", async ctx =>
{
    var skip = 0;
    const int take = 100;
    var isWildcard = packageFilter == "*";
    // Some feeds (e.g. BaGetter) return nothing for empty search queries.
    // Use a single space or the filter itself as the search term, then filter client-side.
    var searchTerms = isWildcard ? new[] { "", " ", "a", "e", "i", "o", "u", "s", "t", "n", "r" } : [packageFilter];
    var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var searchTerm in searchTerms)
    {
        skip = 0;
        while (true)
        {
            var results = await sourceResource.SearchAsync(
                searchTerm,
                new SearchFilter(includePrerelease: true),
                skip, take,
                NullLogger.Instance, CancellationToken.None);

            var resultList = results.ToList();
            if (resultList.Count == 0)
                break;

            foreach (var package in resultList)
            {
                var id = package.Identity.Id;

                // Skip duplicates from multiple search terms
                if (!seenIds.Add(id))
                    continue;

                // Apply case-insensitive filter
                if (!isWildcard &&
                    !id.Contains(packageFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                ctx.Status($"Discovering packages... [grey]({packagesToMigrate.Count} found, checking {Markup.Escape(id)})[/]");

                if (copyAllVersions)
                {
                    using var cache = new SourceCacheContext();
                    var allVersions = await sourceFindResource.GetAllVersionsAsync(
                        id, cache, NullLogger.Instance, CancellationToken.None);
                    packagesToMigrate.Add((id, allVersions.OrderBy(v => v).ToList()));
                }
                else
                {
                    var versions = await package.GetVersionsAsync();
                    var latest = versions.MaxBy(v => v.Version);
                    if (latest is not null)
                        packagesToMigrate.Add((id, [latest.Version]));
                }
            }

            skip += take;
            if (resultList.Count < take)
                break;
        }
    }
});

if (packagesToMigrate.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No packages found matching the filter.[/]");
    return;
}

// --- Summary ---
var totalPackages = packagesToMigrate.Count;
var totalVersions = packagesToMigrate.Sum(p => p.Versions.Count);

var table = new Table().Border(TableBorder.Rounded);
table.AddColumn("Package");
table.AddColumn("Versions");

foreach (var (id, versions) in packagesToMigrate)
{
    table.AddRow(
        Markup.Escape(id),
        versions.Count == 1
            ? Markup.Escape(versions[0].ToNormalizedString())
            : $"{versions.Count} versions ({Markup.Escape(versions.First().ToNormalizedString())} → {Markup.Escape(versions.Last().ToNormalizedString())})");
}

AnsiConsole.Write(table);
AnsiConsole.WriteLine();

var versionLabel = copyAllVersions ? $"{totalVersions} total versions" : $"{totalVersions} latest versions";
var confirmed = AnsiConsole.Confirm(
    $"Migrate [bold]{totalPackages}[/] packages ({versionLabel}) from source to destination?");

if (!confirmed)
{
    AnsiConsole.MarkupLine("[yellow]Migration cancelled.[/]");
    return;
}

// --- Migration ---
var tempDir = Path.Combine(Path.GetTempPath(), "nuget-feed-duplicater");
Directory.CreateDirectory(tempDir);

var succeeded = 0;
var failed = 0;
var skipped = 0;

await AnsiConsole.Progress()
    .Columns(
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new SpinnerColumn())
    .StartAsync(async ctx =>
    {
        var overallTask = ctx.AddTask($"[green]Migrating {totalVersions} package versions[/]", maxValue: totalVersions);

        foreach (var (id, versions) in packagesToMigrate)
        {
            foreach (var version in versions)
            {
                var versionStr = version.ToNormalizedString();
                overallTask.Description = $"[green]{Markup.Escape(id)} {Markup.Escape(versionStr)}[/]";

                try
                {
                    // Clean temp folder before each package version
                    foreach (var file in Directory.GetFiles(tempDir))
                        File.Delete(file);

                    // Download .nupkg
                    var nupkgPath = Path.Combine(tempDir, $"{id}.{versionStr}.nupkg");
                    using var cache = new SourceCacheContext();
                    await using (var nupkgStream = File.Create(nupkgPath))
                    {
                        var downloaded = await sourceFindResource.CopyNupkgToStreamAsync(
                            id, version, nupkgStream, cache, NullLogger.Instance, CancellationToken.None);

                        if (!downloaded)
                        {
                            AnsiConsole.MarkupLine($"  [yellow]⚠ {Markup.Escape(id)} {Markup.Escape(versionStr)} - download failed, skipping[/]");
                            skipped++;
                            overallTask.Increment(1);
                            continue;
                        }
                    }

                    // Push .nupkg to destination
                    var pushResult = await PushPackageAsync(nupkgPath, destUrl, destApiKey);
                    if (!pushResult.Success)
                    {
                        if (pushResult.AlreadyExists)
                        {
                            skipped++;
                            overallTask.Increment(1);
                            continue;
                        }

                        AnsiConsole.MarkupLine($"  [red]✗ {Markup.Escape(id)} {Markup.Escape(versionStr)} - {Markup.Escape(pushResult.Error)}[/]");
                        failed++;
                        overallTask.Increment(1);
                        continue;
                    }

                    succeeded++;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [red]✗ {Markup.Escape(id)} {Markup.Escape(versionStr)} - {Markup.Escape(ex.Message)}[/]");
                    failed++;
                }

                overallTask.Increment(1);
            }
        }

        overallTask.Description = "[green]Migration complete[/]";
    });

// Cleanup temp folder
try { Directory.Delete(tempDir, true); } catch { }

// --- Results ---
AnsiConsole.WriteLine();
var resultsTable = new Table().Border(TableBorder.Rounded);
resultsTable.AddColumn("Result");
resultsTable.AddColumn("Count");
resultsTable.AddRow("[green]Succeeded[/]", succeeded.ToString());
resultsTable.AddRow("[yellow]Skipped (already exists)[/]", skipped.ToString());
resultsTable.AddRow("[red]Failed[/]", failed.ToString());
AnsiConsole.Write(resultsTable);

static async Task<PushResult> PushPackageAsync(string packagePath, string destUrl, string apiKey)
{
    var psi = new ProcessStartInfo("dotnet", ["nuget", "push", packagePath, "--source", destUrl, "--api-key", apiKey, "--skip-duplicate"])
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi)!;
    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode == 0)
        return new PushResult(true, false, string.Empty);

    var output = stdout + stderr;
    var alreadyExists = output.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                     || output.Contains("409", StringComparison.OrdinalIgnoreCase);

    return new PushResult(false, alreadyExists, output.Trim());
}

static SourceRepository CreateSourceRepository(string feedUrl)
{
    var packageSource = new PackageSource(feedUrl);

    // V2 feeds typically don't end with index.json and use /nuget, /api/v2, or /Packages paths
    if (feedUrl.EndsWith("index.json", StringComparison.OrdinalIgnoreCase))
    {
        return Repository.Factory.GetCoreV3(feedUrl);
    }

    // V2 feed: use the V2 factory method
    return Repository.Factory.GetCoreV2(packageSource);
}

record PushResult(bool Success, bool AlreadyExists, string Error);
