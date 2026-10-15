using System.ComponentModel;
using Cloudflare.Console.Infrastructure;
using Spectre.Console.Cli;

namespace Cloudflare.Console.Commands;

/// <summary>
/// Answers "which token is this, does it work, and what can it reach" before running anything
/// that changes a zone. Verifies the token, then shows the user, the accounts and zones it can
/// see, and its permissions where the token is allowed to read them.
///
/// Only the verify call is required; everything else needs its own read permission (User
/// Details, Account Settings, Zone, API Tokens), and a missing one is reported in place instead
/// of failing the command, so the output also shows which permissions the token lacks.
/// The token value itself is never printed.
/// </summary>
public sealed class WhoamiCommand : AsyncCommand<WhoamiCommand.Settings>
{
    public sealed class Settings : ApiSettings
    {
        [CommandOption("--account-id <ID>")]
        [Description("Account id, to verify an account-owned token (or set CLOUDFLARE_ACCOUNT_ID env var)")]
        public string? AccountId { get; init; }

        [CommandOption("--permissions")]
        [Description("List every permission of the token instead of a count per policy")]
        public bool ListPermissions { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var client = settings.CreateClient();
        var accountId = settings.AccountId ?? Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID");

        // User tokens verify at user/tokens/verify; account-owned tokens only at the account's
        // own endpoint, so fall back to that when an account id is known.
        var owner = "user";
        object? verify;
        try
        {
            verify = await client.GetAsync("user/tokens/verify");
        }
        catch (CloudflareApiException userError)
        {
            if (string.IsNullOrWhiteSpace(accountId))
                throw new CloudflareApiException(
                    $"{userError.Message} (an account-owned token can only be verified with --account-id or CLOUDFLARE_ACCOUNT_ID)");
            verify = await client.GetAsync($"accounts/{accountId}/tokens/verify");
            owner = $"account {accountId}";
        }

        var token = verify as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        var tokenId = token.GetValueOrDefault("id") as string;
        var status = token.GetValueOrDefault("status") as string;

        var tokenPath = owner == "user" ? $"user/tokens/{tokenId}" : $"accounts/{accountId}/tokens/{tokenId}";
        var details = tokenId is null ? null : await SafeGetAsync(client, tokenPath);
        // An account-owned token belongs to no user, so the user endpoint always refuses it.
        var user = owner == "user" ? await SafeGetAsync(client, "user") : "none (account-owned token)";
        var accounts = await SafeGetAsync(client, "accounts?per_page=50");
        var zones = await SafeGetAsync(client, "zones?per_page=50");

        YamlOutput.Write(new Dictionary<string, object?>
        {
            ["token"] = new Dictionary<string, object?>
            {
                ["source"] = settings.ApiToken is not null ? "--api-token" : "CLOUDFLARE_API_TOKEN",
                ["owner"] = owner,
                ["id"] = tokenId,
                ["status"] = status,
                ["name"] = (details as Dictionary<string, object?>)?.GetValueOrDefault("name"),
                ["not_before"] = token.GetValueOrDefault("not_before"),
                ["expires_on"] = token.GetValueOrDefault("expires_on") ?? "never",
                ["permissions"] = Permissions(details, settings.ListPermissions)
            },
            ["user"] = user is Dictionary<string, object?> u
                ? new Dictionary<string, object?> { ["email"] = u.GetValueOrDefault("email"), ["id"] = u.GetValueOrDefault("id") }
                : user,
            ["accounts"] = Pick(accounts, "name", "id"),
            ["zones"] = Pick(zones, "name", "status", "id")
        });

        return status == "active" ? 0 : 1;
    }

    /// <summary>A missing permission should not sink the whole report - show what is readable.</summary>
    private static async Task<object?> SafeGetAsync(CloudflareClient client, string path)
    {
        try
        {
            return await client.GetAsync(path);
        }
        catch (CloudflareApiException ex)
        {
            return $"unavailable: {ex.Message}";
        }
    }

    private static object? Pick(object? items, params string[] fields)
    {
        if (items is not List<object?> list) return items;
        return list.OfType<Dictionary<string, object?>>()
            .Select(item => fields.ToDictionary(f => f, f => item.GetValueOrDefault(f)))
            .ToList();
    }

    /// <summary>
    /// One entry per policy: its effect, what it covers and how many permissions it grants
    /// (a broad token has hundreds), or with --permissions every permission name, sorted.
    /// </summary>
    private static object? Permissions(object? details, bool listAll)
    {
        if (details is string unavailable) return unavailable;
        if (details is not Dictionary<string, object?> d || d.GetValueOrDefault("policies") is not List<object?> policies)
            return null;

        return policies.OfType<Dictionary<string, object?>>().Select(policy =>
        {
            var names = policy.GetValueOrDefault("permission_groups") is List<object?> g
                ? g.OfType<Dictionary<string, object?>>().Select(x => x.GetValueOrDefault("name") as string ?? "?").Order().ToList()
                : [];
            var resources = policy.GetValueOrDefault("resources") is Dictionary<string, object?> r
                ? string.Join(", ", r.Keys)
                : "";
            return new Dictionary<string, object?>
            {
                ["effect"] = policy.GetValueOrDefault("effect"),
                ["resources"] = resources,
                ["permissions"] = listAll ? names : $"{names.Count} (list them with --permissions)"
            };
        }).ToList();
    }
}
