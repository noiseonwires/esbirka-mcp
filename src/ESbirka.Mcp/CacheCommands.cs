using System.CommandLine;
using System.Text.Json;
using ESbirka.Client;

namespace ESbirka.Mcp;

public static class CacheCommands
{
    public static async Task<int> RunAsync(string[] args, IESbirkaCacheAdministration administration)
    {
        var root = new RootCommand("Local e-Sbirka cache administration. No cache commands are exposed over MCP.");
        var cache = new Command("cache", "Revalidate or purge local cache entries.");
        root.Subcommands.Add(cache);

        void AddSimple(string name, string description, Func<string, CancellationToken, Task> action)
        {
            var publication = new Argument<string>("publicationNumber");
            var command = new Command(name, description);
            command.Arguments.Add(publication);
            command.SetAction(async (parse, cancellationToken) => await action(parse.GetValue(publication)!, cancellationToken));
            cache.Subcommands.Add(command);
        }
        AddSimple("revalidate-current", "Revalidate metadata and atomically switch to a new complete snapshot.", administration.RevalidateCurrentAsync);
        AddSimple("purge-current-alias", "Remove the current alias and version list, retaining canonical snapshots.", administration.PurgeCurrentAliasAsync);

        var number = new Argument<string>("publicationNumber");
        var effectiveFrom = new Option<string>("--effective-from") { Required = true, Description = "Canonical effective date yyyy-MM-dd." };
        var version = new Command("purge-version", "Remove one canonical effective snapshot and its aliases.");
        version.Arguments.Add(number);
        version.Options.Add(effectiveFrom);
        version.SetAction(async (parse, cancellationToken) => await administration.PurgeVersionAsync(parse.GetValue(number)!,
            LawTools.ParseVersion(parse.GetValue(effectiveFrom), "current").AsOf!.Value, cancellationToken));
        cache.Subcommands.Add(version);

        var provisionNumber = new Argument<string>("publicationNumber");
        var section = new Option<string>("--section") { Required = true };
        var paragraph = new Option<string?>("--paragraph");
        var asOf = new Option<string?>("--as-of");
        var provision = new Command("purge-provision", "Discard derived indexes and negative lookups; retain raw pages. Does not fetch upstream.");
        provision.Arguments.Add(provisionNumber);
        provision.Options.Add(section);
        provision.Options.Add(paragraph);
        provision.Options.Add(asOf);
        provision.SetAction(async (parse, cancellationToken) => await administration.PurgeProvisionAsync(parse.GetValue(provisionNumber)!,
            LawTools.ParseVersion(parse.GetValue(asOf), "current"), new(Section: parse.GetValue(section), Paragraph: parse.GetValue(paragraph)), cancellationToken));
        cache.Subcommands.Add(provision);

        var documentNumber = new Argument<string>("publicationNumber");
        var history = new Option<bool>("--include-history");
        var confirm = new Option<bool>("--confirm");
        var dryRun = new Option<bool>("--dry-run");
        var document = new Command("purge-document", "Remove current document state; historical removal requires explicit confirmation.");
        document.Arguments.Add(documentNumber);
        document.Options.Add(history);
        document.Options.Add(confirm);
        document.Options.Add(dryRun);
        document.SetAction(async (parse, cancellationToken) =>
        {
            if (parse.GetValue(history) && !parse.GetValue(confirm) && !parse.GetValue(dryRun))
                throw new ESbirkaException("confirmation_required", "Use --confirm to purge all historical versions, or preview with --dry-run.");
            var result = await administration.PurgeDocumentAsync(parse.GetValue(documentNumber)!, parse.GetValue(history), parse.GetValue(dryRun), cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });
        cache.Subcommands.Add(document);
        try
        {
            return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (ESbirkaException exception)
        {
            Console.Error.WriteLine(exception.Code + ": " + exception.Message);
            return 1;
        }
    }
}