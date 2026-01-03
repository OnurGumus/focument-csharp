// =============================================================================
// PROJECTION (Read Model Builder)
// =============================================================================
// This module implements the QUERY side of CQRS by projecting events into
// a read-optimized SQLite database.
// =============================================================================

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Dapper;
using Model;
using IMessageWithCID = FCQRS.Model.Data.IMessageWithCID;

namespace Server;

public static class Projection
{
    // -----------------------------------------------------------------------------
    // SCHEMA INITIALIZATION
    // -----------------------------------------------------------------------------
    public static void EnsureTables(string connString)
    {
        using var conn = new SqliteConnection(connString);
        conn.Open();

        // SQLite performance optimizations for concurrent read/write
        conn.Execute("PRAGMA journal_mode=WAL");
        conn.Execute("PRAGMA synchronous=NORMAL");
        conn.Execute("PRAGMA busy_timeout=5000");
        conn.Execute("PRAGMA cache_size=10000");

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Documents (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Body TEXT NOT NULL,
                Version INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                ApprovalStatus TEXT NOT NULL DEFAULT 'Pending'
            )
            """);

        // Migration: Add ApprovalStatus column if it doesn't exist
        try
        {
            conn.Execute("ALTER TABLE Documents ADD COLUMN ApprovalStatus TEXT NOT NULL DEFAULT 'Pending'");
        }
        catch (SqliteException) { /* Column already exists */ }

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Offsets (
                OffsetName TEXT PRIMARY KEY,
                OffsetCount INTEGER NOT NULL
            )
            """);

        conn.Execute("""
            INSERT OR IGNORE INTO Offsets (OffsetName, OffsetCount) VALUES ('DocumentProjection', 0)
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS DocumentVersions (
                Id TEXT NOT NULL,
                Version INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Body TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY (Id, Version)
            )
            """);
    }

    // -----------------------------------------------------------------------------
    // EVENT HANDLER (Projection Logic)
    // -----------------------------------------------------------------------------
    public static IList<IMessageWithCID> HandleEventWrapper(
        ILoggerFactory loggerFactory,
        string connString,
        long offsetValue,
        object eventObj)
    {
        var log = loggerFactory.CreateLogger("Projection");
        log.LogInformation("Event: {Event} Offset: {Offset}", eventObj, offsetValue);

        try
        {
            using var conn = new SqliteConnection(connString);
            conn.Open();
            using var transaction = conn.BeginTransaction();

            var dataEvents = new List<IMessageWithCID>();

            // Try to extract document event from various wrappers
            FCQRS.Common.Event<DocumentEvent>? docEvent = null;

            if (eventObj is FCQRS.Common.Event<DocumentEvent> directEvent)
            {
                docEvent = directEvent;
            }
            // Only process aggregate events - saga events are for recovery only

            if (docEvent != null)
            {
                var eventTime = docEvent.CreationDate.ToString("o");

                if (docEvent.EventDetails is DocumentEvent.CreatedOrUpdated created)
                {
                    var doc = created.Document;
                    var docId = doc.Id.ToString();
                    var title = doc.Title.ToString();
                    var content = doc.Content.ToString();

                    // Get next document version (only counts CreatedOrUpdated events)
                    var maxVersion = conn.QueryFirstOrDefault<long?>(
                        "SELECT MAX(Version) FROM DocumentVersions WHERE Id = @Id",
                        new { Id = docId },
                        transaction);
                    var docVersion = (maxVersion ?? 0) + 1;

                    var existing = conn.QueryFirstOrDefault<string>(
                        "SELECT Id FROM Documents WHERE Id = @Id",
                        new { Id = docId },
                        transaction);

                    if (existing is null)
                    {
                        conn.Execute(
                            """
                            INSERT INTO Documents (Id, Title, Body, Version, CreatedAt, UpdatedAt, ApprovalStatus)
                            VALUES (@Id, @Title, @Body, @Version, @CreatedAt, @UpdatedAt, 'Pending')
                            """,
                            new
                            {
                                Id = docId,
                                Title = title,
                                Body = content,
                                Version = docVersion,
                                CreatedAt = eventTime,
                                UpdatedAt = eventTime
                            },
                            transaction);
                    }
                    else
                    {
                        conn.Execute(
                            """
                            UPDATE Documents
                            SET Title = @Title, Body = @Body, Version = @Version, UpdatedAt = @UpdatedAt, ApprovalStatus = 'Pending'
                            WHERE Id = @Id
                            """,
                            new
                            {
                                Id = docId,
                                Title = title,
                                Body = content,
                                Version = docVersion,
                                UpdatedAt = eventTime
                            },
                            transaction);
                    }

                    // Store version history
                    conn.Execute(
                        """
                        INSERT OR IGNORE INTO DocumentVersions (Id, Version, Title, Body, CreatedAt)
                        VALUES (@Id, @Version, @Title, @Body, @CreatedAt)
                        """,
                        new
                        {
                            Id = docId,
                            Version = docVersion,
                            Title = title,
                            Body = content,
                            CreatedAt = eventTime
                        },
                        transaction);

                    dataEvents.Add(docEvent);
                }
                else if (docEvent.EventDetails is DocumentEvent.Approved approved)
                {
                    conn.Execute(
                        "UPDATE Documents SET ApprovalStatus = 'Approved', UpdatedAt = @UpdatedAt WHERE Id = @Id",
                        new { Id = approved.DocumentId.Value.ToString(), UpdatedAt = eventTime },
                        transaction);

                    dataEvents.Add(docEvent);
                }
                else if (docEvent.EventDetails is DocumentEvent.Rejected rejected)
                {
                    conn.Execute(
                        "UPDATE Documents SET ApprovalStatus = 'Rejected', UpdatedAt = @UpdatedAt WHERE Id = @Id",
                        new { Id = rejected.DocumentId.Value.ToString(), UpdatedAt = eventTime },
                        transaction);

                    dataEvents.Add(docEvent);
                }
            }

            conn.Execute(
                "UPDATE Offsets SET OffsetCount = @Offset WHERE OffsetName = @Name",
                new { Offset = offsetValue, Name = "DocumentProjection" },
                transaction);

            transaction.Commit();
            return dataEvents;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Projection failed for event at offset {Offset}: {EventType}",
                offsetValue, eventObj.GetType().Name);
            throw;
        }
    }
}
