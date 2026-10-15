using System.ComponentModel;
using Cloudflare.Console.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cloudflare.Console.Commands.Cache;

/// <summary>
/// Removes cached copies from Cloudflare's edge, so the next request fetches the page from the
/// origin again. Purge the origin's own cache first, or Cloudflare fetches the old page back.
///
/// By URL, sent in batches of 30 (the per-request limit on the Free plan); the cache key is the
/// exact URL, so /page and /page/ are different entries. Or --everything for the whole zone.
///
/// Needs the Cache Purge permission.
/// </summary>
public sealed class PurgeCacheCommand : AsyncCommand<PurgeCacheCommand.Settings>
{
    private const int BatchSize = 30;

    public sealed class Settings : ZoneSettings
    {
        [CommandOption("--url <URL>")]
        [Description("A full URL to purge, e.g. https://example.com/page; repeat the option for more")]
        public string[] Urls { get; init; } = [];

        [CommandOption("--everything")]
        [Description("Purge every cached file in the zone")]
        public bool Everything { get; init; }

        public override ValidationResult Validate()
        {
            if (Everything && Urls.Length > 0) return ValidationResult.Error("Use --url or --everything, not both.");
            if (!Everything && Urls.Length == 0) return ValidationResult.Error("Give at least one --url, or --everything.");
            if (Urls.Any(u => !Uri.TryCreate(u, UriKind.Absolute, out _)))
                return ValidationResult.Error("Every --url must be absolute, e.g. https://example.com/page.");
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var client = settings.CreateClient();
        var zoneId = await client.ResolveZoneIdAsync(settings.Zone);

        if (settings.Everything)
        {
            await client.PostAsync($"zones/{zoneId}/purge_cache", new { purge_everything = true });
            AnsiConsole.MarkupLine($"[green]Purged everything in {Markup.Escape(settings.Zone)}.[/]");
            return 0;
        }

        var urls = settings.Urls.Distinct(StringComparer.Ordinal).ToList();
        for (var i = 0; i < urls.Count; i += BatchSize)
        {
            var batch = urls.Skip(i).Take(BatchSize).ToArray();
            await client.PostAsync($"zones/{zoneId}/purge_cache", new { files = batch });
        }

        YamlOutput.Write(new Dictionary<string, object?> { ["purged"] = urls });
        AnsiConsole.MarkupLine($"[green]Purged {urls.Count} URL(s) in {Markup.Escape(settings.Zone)}.[/]");
        return 0;
    }
}
