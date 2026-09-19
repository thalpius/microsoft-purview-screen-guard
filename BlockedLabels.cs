namespace MicrosoftPurviewScreenGuard;

/// <summary>Set of blocked sensitivity label GUIDs, loaded from blocked-labels.txt.</summary>
internal sealed class BlockedLabels
{
    public const string FileName = "blocked-labels.txt";

    private readonly HashSet<Guid> _ids;

    private BlockedLabels(HashSet<Guid> ids) => _ids = ids;

    public int Count => _ids.Count;

    public bool Contains(string? labelId) =>
        Guid.TryParse(labelId, out Guid id) && _ids.Contains(id);

    /// <summary>
    /// One GUID per line; empty lines and everything after '#' are ignored.
    /// A missing or empty file yields an empty list plus a warning, never an exception.
    /// </summary>
    public static BlockedLabels Load(string directory, Action<string> warn)
    {
        string path = Path.Combine(directory, FileName);
        var ids = new HashSet<Guid>();

        if (!File.Exists(path))
        {
            warn($"{FileName} not found next to the executable ({path}). Blocked list is EMPTY.");
            return new BlockedLabels(ids);
        }

        try
        {
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine;
                int hash = line.IndexOf('#');
                if (hash >= 0)
                {
                    line = line[..hash];
                }

                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (Guid.TryParse(line, out Guid id))
                {
                    ids.Add(id);
                }
                else
                {
                    warn($"{FileName}: ignoring line that is not a GUID: \"{line}\"");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"Could not read {FileName}: {ex.Message}. Blocked list is EMPTY.");
            return new BlockedLabels(new HashSet<Guid>());
        }

        if (ids.Count == 0)
        {
            warn($"{FileName} contains no GUIDs. Blocked list is EMPTY.");
        }

        return new BlockedLabels(ids);
    }
}
