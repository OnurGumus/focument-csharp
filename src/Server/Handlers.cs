// =============================================================================
// HTTP HANDLERS (API Layer)
// =============================================================================
// This module contains the HTTP handlers that bridge the web API to the
// CQRS/ES domain.
// =============================================================================

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Model;
using static FCQRS.CSharp;
using CID = FCQRS.Model.Data.CID;
using IMessageWithCID = FCQRS.Model.Data.IMessageWithCID;

namespace Server;

public static class Handlers
{
    private static ILogger? _logger;

    public static void SetLogger(ILoggerFactory loggerFactory) =>
        _logger = loggerFactory.CreateLogger("Handlers");
    // -----------------------------------------------------------------------------
    // QUERY HANDLERS (Read Side)
    // -----------------------------------------------------------------------------
    public static Query.Document[] GetDocuments(string connectionString)
    {
        var docs = ServerQuery.GetDocuments(connectionString);
        var cutoff = DateTime.UtcNow.AddMinutes(-10);

        // Filter: only show documents from last 10 minutes, unless it's the example doc
        var filtered = System.Linq.Enumerable.Where(docs,
            d => (DateTime.TryParse(d.UpdatedAt, out var updated) && updated > cutoff)
                 || (d.Title == "Hello" && d.Body == "World"));
        return System.Linq.Enumerable.ToArray(filtered);
    }

    public static Query.DocumentVersion[] GetDocumentHistory(string connectionString, HttpContext ctx)
    {
        var id = ctx.Request.RouteValues["id"]?.ToString() ?? "";
        return ServerQuery.GetDocumentHistory(connectionString, id).ToArray();
    }

    // -----------------------------------------------------------------------------
    // COMMAND HANDLERS (Write Side)
    // -----------------------------------------------------------------------------
    public static async Task<string> CreateOrUpdateDocument(
        Func<CID> getCid,
        FCQRS.Query.ISubscribe<IMessageWithCID> subs,
        ICommandHandlers commandHandler,
        HttpContext ctx)
    {
        var totalSw = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();

        _logger?.LogDebug("CreateOrUpdateDocument called");
        try
        {
            var form = await ctx.Request.ReadFormAsync();
            var title = form["Title"].ToString();
            var content = form["Content"].ToString();
            var existingId = form["Id"].ToString();
            var parseTime = sw.ElapsedMilliseconds;
            sw.Restart();

            // Input length validation
            const int maxLength = 2000;
            if (title.Length > maxLength)
                return $"Error: Title exceeds maximum length of {maxLength} characters";
            if (content.Length > maxLength)
                return $"Error: Content exceeds maximum length of {maxLength} characters";

            _logger?.LogDebug("Title: {Title}, Content length: {ContentLength}", title, content.Length);

            var docId = string.IsNullOrEmpty(existingId)
                ? Guid.NewGuid()
                : Guid.Parse(existingId);

            // Create validated domain objects
            var aggregateId = Helpers.CreateAggregateId(docId.ToString());
            _logger?.LogDebug("DocId: {DocId}, AggregateId: {AggregateId}", docId, aggregateId);

            if (!Document.TryCreate(docId, title, content, out var document, out var docError))
            {
                _logger?.LogWarning("Document validation failed: {Error}", docError);
                return $"Error: {docError}";
            }
            _logger?.LogDebug("Document validated");
            var validateTime = sw.ElapsedMilliseconds;
            sw.Restart();

            var correlationId = getCid();
            _logger?.LogDebug("CorrelationId: {CorrelationId}", correlationId);

            // Subscribe to events with this correlation ID BEFORE sending command
            using var awaiter = ISubscribeExtensions.SubscribeFor(subs, e => e.CID.Equals(correlationId), 1);

            // Send command to the actor
            var handler = commandHandler.DocumentHandler;
            await handler(
                _ => true,
                correlationId,
                aggregateId,
                new DocumentCommand.CreateOrUpdate(document));
            _logger?.LogDebug("Command sent, waiting for projection");
            var commandTime = sw.ElapsedMilliseconds;
            sw.Restart();

            // Wait for the event to be projected
            await awaiter.Task;
            _logger?.LogDebug("Event projected");
            var projectionTime = sw.ElapsedMilliseconds;
            var totalTime = totalSw.ElapsedMilliseconds;

            // Add Server-Timing header for performance analysis
            ctx.Response.Headers["Server-Timing"] =
                $"parse;dur={parseTime}, validate;dur={validateTime}, command;dur={commandTime}, projection;dur={projectionTime}, total;dur={totalTime}";

            return "Document received!";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "CreateOrUpdateDocument failed");
            return "Error: An unexpected error occurred. Please try again.";
        }
    }

    // Restores a document to a previous version (time-travel feature)
    public static async Task<string> RestoreVersion(
        string connectionString,
        Func<CID> getCid,
        FCQRS.Query.ISubscribe<IMessageWithCID> subs,
        ICommandHandlers commandHandler,
        HttpContext ctx)
    {
        try
        {
            var form = await ctx.Request.ReadFormAsync();
            var docId = form["Id"].ToString();
            var versionStr = form["Version"].ToString();

            // Input validation
            if (string.IsNullOrWhiteSpace(docId) || docId.Length > 50)
                return "Error: Invalid document ID";
            if (!long.TryParse(versionStr, out var version) || version < 0)
                return "Error: Invalid version number";

            // Look up the historical version from the read model
            var history = ServerQuery.GetDocumentHistory(connectionString, docId);
            var versionData = history.Find(v => v.Version == version);

            if (versionData is null)
                return "Error: Version not found";

            // Recreate the document from historical data
            var guid = Guid.Parse(docId);
            var aggregateId = Helpers.CreateAggregateId(docId);

            if (!Document.TryCreate(guid, versionData.Title, versionData.Body, out var document, out var docError))
                return $"Error: {docError}";

            var correlationId = getCid();

            using var awaiter = ISubscribeExtensions.SubscribeFor(subs, e => e.CID.Equals(correlationId), 1);

            var handler = commandHandler.DocumentHandler;
            await handler(
                _ => true,
                correlationId,
                aggregateId,
                new DocumentCommand.CreateOrUpdate(document));

            await awaiter.Task;

            return "Version restored!";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "RestoreVersion failed");
            return "Error: An unexpected error occurred. Please try again.";
        }
    }
}
