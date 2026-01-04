// =============================================================================
// COMMAND HANDLER FACTORY
// =============================================================================
// This module creates the command handlers that route commands to actors.
// It serves as the entry point for the command side of CQRS.
// =============================================================================

using Model;
using static FCQRS.CSharp;

namespace Server;

// -----------------------------------------------------------------------------
// COMMAND HANDLER INTERFACE
// -----------------------------------------------------------------------------
public interface ICommandHandlers
{
    Handler<DocumentCommand, DocumentEvent> DocumentHandler { get; }

    static ICommandHandlers Create(FCQRS.Common.IActor actorApi) =>
        new CommandHandlers(actorApi);
}

file sealed class CommandHandlers(FCQRS.Common.IActor actorApi) : ICommandHandlers
{
    public Handler<DocumentCommand, DocumentEvent> DocumentHandler =>
        DocumentShard.Handler(actorApi);
}
