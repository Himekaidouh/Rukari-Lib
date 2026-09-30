using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed record CommandTimelineEditSnapshot(
    CommandTimelineDocument Timeline,
    SceneKey Scene,
    int PublicSlot,
    string CommandId,
    bool ReplacedExisting);

public sealed class CommandTimelineEditor
{
    private readonly CharacterTransformCommandFamilyCompiler _characterCompiler = new();

    public Result<CommandTimelineEditSnapshot> UpsertCharacterTransform(
        CommandTimelineDocument timeline,
        SceneKey scene,
        string directive)
    {
        Result input = ValidateInput(timeline, scene);
        if (!input.Success)
        {
            return Result<CommandTimelineEditSnapshot>.Fail(input.Error);
        }

        Result<CanonicalTimelineCommand> canonical =
            _characterCompiler.Canonicalize(directive);
        if (!canonical.Success || canonical.Value == null)
        {
            return Result<CommandTimelineEditSnapshot>.Fail(canonical.Error);
        }

        Result<LocatedEntry> located = LocateEntry(timeline, scene);
        if (!located.Success || located.Value == null)
        {
            return Result<CommandTimelineEditSnapshot>.Fail(located.Error);
        }

        List<CommandTimelineEntry> entries = timeline.Entries.ToList();
        List<AuthoringCommand> commands = located.Value.Entry?.Commands.ToList()
            ?? new List<AuthoringCommand>();
        Result<int?> existing = FindCharacterSlot(commands, canonical.Value.PublicSlot);
        if (!existing.Success)
        {
            return Result<CommandTimelineEditSnapshot>.Fail(existing.Error);
        }

        bool replaced;
        string commandId;
        if (existing.Value is int existingIndex)
        {
            replaced = true;
            AuthoringCommand current = commands[existingIndex];
            commandId = current.CommandId;
            commands[existingIndex] = current with
            {
                Phase = CommandTimelinePhase.SceneEnter,
                CommandType = CharacterTransformCommandFamilyCompiler.CommandTypeId,
                Directive = canonical.Value.Directive,
                Enabled = true
            };
        }
        else
        {
            replaced = false;
            commandId = Guid.NewGuid().ToString("D");
            commands.Add(new AuthoringCommand(
                commandId,
                commands.Count,
                CommandTimelinePhase.SceneEnter,
                CharacterTransformCommandFamilyCompiler.CommandTypeId,
                canonical.Value.Directive,
                Enabled: true));
        }

        CommandTimelineEntry updated = new(
            scene,
            Array.AsReadOnly(Renumber(commands)));
        if (located.Value.Index.HasValue)
        {
            entries[located.Value.Index.Value] = updated;
        }
        else
        {
            entries.Add(updated);
        }

        var edited = timeline with { Entries = Array.AsReadOnly(entries.ToArray()) };
        return Result<CommandTimelineEditSnapshot>.Ok(new(
            edited,
            scene,
            canonical.Value.PublicSlot,
            commandId,
            replaced));
    }

    public Result<CommandTimelineEditSnapshot> RemoveCharacterTransform(
        CommandTimelineDocument timeline,
        SceneKey scene,
        int publicSlot)
    {
        Result input = ValidateInput(timeline, scene);
        if (!input.Success)
        {
            return Result<CommandTimelineEditSnapshot>.Fail(input.Error);
        }

        if (publicSlot is < 1 or > 5)
        {
            return Result<CommandTimelineEditSnapshot>.Fail(
                "Physical character slot must be between 1 and 5.");
        }

        Result<LocatedEntry> located = LocateEntry(timeline, scene);
        if (!located.Success || located.Value?.Entry == null
            || !located.Value.Index.HasValue)
        {
            return Result<CommandTimelineEditSnapshot>.Fail(
                located.Success
                    ? "No command entry exists for the requested scene."
                    : located.Error);
        }

        List<AuthoringCommand> commands = located.Value.Entry.Commands.ToList();
        Result<int?> existing = FindCharacterSlot(commands, publicSlot);
        if (!existing.Success)
        {
            return Result<CommandTimelineEditSnapshot>.Fail(existing.Error);
        }

        if (!existing.Value.HasValue)
        {
            return Result<CommandTimelineEditSnapshot>.Fail(
                $"No character transform command exists for slot {publicSlot} in the requested scene.");
        }

        int commandIndex = existing.Value.Value;
        string commandId = commands[commandIndex].CommandId;
        commands.RemoveAt(commandIndex);
        List<CommandTimelineEntry> entries = timeline.Entries.ToList();
        if (commands.Count == 0)
        {
            entries.RemoveAt(located.Value.Index.Value);
        }
        else
        {
            entries[located.Value.Index.Value] = new CommandTimelineEntry(
                scene,
                Array.AsReadOnly(Renumber(commands)));
        }

        var edited = timeline with { Entries = Array.AsReadOnly(entries.ToArray()) };
        return Result<CommandTimelineEditSnapshot>.Ok(new(
            edited,
            scene,
            publicSlot,
            commandId,
            ReplacedExisting: true));
    }

    private Result<int?> FindCharacterSlot(
        IReadOnlyList<AuthoringCommand> commands,
        int publicSlot)
    {
        int? match = null;
        for (int index = 0; index < commands.Count; index++)
        {
            AuthoringCommand command = commands[index];
            if (!string.Equals(
                    command.CommandType,
                    CharacterTransformCommandFamilyCompiler.CommandTypeId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            Result<CanonicalTimelineCommand> canonical =
                _characterCompiler.Canonicalize(command.Directive);
            if (!canonical.Success || canonical.Value == null)
            {
                return Result<int?>.Fail(
                    $"Existing command {command.CommandId} is invalid: {canonical.Error}");
            }

            if (canonical.Value.PublicSlot != publicSlot)
            {
                continue;
            }

            if (match.HasValue)
            {
                return Result<int?>.Fail(
                    $"Scene contains multiple character transform commands for slot {publicSlot}.");
            }

            match = index;
        }

        return Result<int?>.Ok(match);
    }

    private static Result<LocatedEntry> LocateEntry(
        CommandTimelineDocument timeline,
        SceneKey scene)
    {
        int? index = null;
        for (int entryIndex = 0; entryIndex < timeline.Entries.Count; entryIndex++)
        {
            CommandTimelineEntry entry = timeline.Entries[entryIndex];
            if (!SameLocation(entry.Scene, scene))
            {
                continue;
            }

            if (!string.Equals(
                    entry.Scene.Fingerprint,
                    scene.Fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result<LocatedEntry>.Fail(
                    "Existing command entry has a stale scene fingerprint.");
            }

            if (index.HasValue)
            {
                return Result<LocatedEntry>.Fail(
                    "Command timeline contains duplicate entries for the requested scene.");
            }

            index = entryIndex;
        }

        return Result<LocatedEntry>.Ok(new LocatedEntry(
            index,
            index.HasValue ? timeline.Entries[index.Value] : null));
    }

    private static Result ValidateInput(
        CommandTimelineDocument timeline,
        SceneKey scene)
    {
        if (timeline == null || timeline.Entries == null)
        {
            return Result.Fail("Command timeline is missing.");
        }

        if (scene == null
            || string.IsNullOrWhiteSpace(scene.NodeGuid)
            || scene.SceneIndex < 0
            || string.IsNullOrWhiteSpace(scene.Fingerprint))
        {
            return Result.Fail("Target scene identity is invalid.");
        }

        return Result.Ok();
    }

    private static AuthoringCommand[] Renumber(
        IReadOnlyList<AuthoringCommand> commands) =>
        commands.Select((command, index) => command with { Order = index }).ToArray();

    private static bool SameLocation(SceneKey left, SceneKey right) =>
        string.Equals(left.NodeGuid, right.NodeGuid, StringComparison.OrdinalIgnoreCase)
        && left.SceneIndex == right.SceneIndex;

    private sealed record LocatedEntry(int? Index, CommandTimelineEntry? Entry);
}
