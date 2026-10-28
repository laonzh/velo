using System.Text;
using System.Text.Json;

namespace Velo;

/// <summary>
/// All issues, kept in memory and rewritten to state.json (atomically) after every change. Thread-safe.
/// </summary>
public sealed class Store
{
    private readonly Lock _gate = new();
    private readonly string _file;
    private State _state;
    private long _version = DateTime.UtcNow.Ticks; // starts anew on every load, so a version from before a restart never matches

    public Store(VeloPaths paths)
    {
        _file = paths.StateFile;
        bool exists = File.Exists(_file);
        _state = exists
            ? Read(_file, VeloJson.Files.State) ?? throw new InvalidDataException($"invalid {_file}: expected a state object")
            : new State { HomeId = Guid.NewGuid().ToString("N") };
        if (!Guid.TryParseExact(_state.HomeId, "N", out Guid id) || id == Guid.Empty)
            throw new InvalidDataException($"invalid {_file}: homeId must be a non-empty GUID in N format; legacy state files are not supported");
        if (!exists) Save(_state); // persist the identity before the engine can use it
    }

    /// <summary>The stable identity of this data set, used to namespace its Git branches.</summary>
    public string HomeId => _state.HomeId;

    /// <summary>Changes with every saved change (the ETag of the issue list).</summary>
    public long Version
    {
        get { lock (_gate) return _version; }
    }

    /// <summary>All issues, ordered by number.</summary>
    public IReadOnlyList<Issue> List()
    {
        lock (_gate) return [.. _state.Issues.OrderBy(i => i.Number)];
    }

    public Issue? Get(int number)
    {
        lock (_gate) return _state.Issues.Find(i => i.Number == number);
    }

    /// <summary>Creates an issue with the next number.</summary>
    public Issue Add(Func<int, Issue> create)
    {
        lock (_gate)
        {
            Issue issue = create(_state.NextNumber);
            Save(new State { HomeId = HomeId, NextNumber = _state.NextNumber + 1, Issues = [.. _state.Issues, issue] });
            return issue;
        }
    }

    /// <summary>
    /// Replaces an issue with <paramref name="change"/>(issue); 404 when it does not exist.
    /// <paramref name="change"/> may throw to abort.
    /// </summary>
    public Issue Update(int number, Func<Issue, Issue> change)
    {
        lock (_gate)
        {
            int index = _state.Issues.FindIndex(i => i.Number == number);
            if (index < 0) throw VeloException.NotFound($"VL-{number} not found");
            Issue issue = change(_state.Issues[index]);
            List<Issue> issues = [.. _state.Issues];
            issues[index] = issue;
            Save(new State { HomeId = HomeId, NextNumber = _state.NextNumber, Issues = issues });
            return issue;
        }
    }

    public void Remove(int number)
    {
        lock (_gate)
        {
            Save(new State { HomeId = HomeId, NextNumber = _state.NextNumber, Issues = [.. _state.Issues.Where(i => i.Number != number)] });
        }
    }

    private void Save(State next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next, VeloJson.Files.State), new UTF8Encoding(false));
        File.Move(temp, _file, overwrite: true);
        _state = next; // publish only after the file has been replaced successfully
        _version++;
    }

    /// <summary>config.json, or the defaults when it does not exist.</summary>
    public static Settings LoadSettings(VeloPaths paths)
    {
        Settings settings = File.Exists(paths.ConfigFile) ? Read(paths.ConfigFile, VeloJson.Files.Settings) ?? new Settings() : new Settings();
        if (settings.Port is < 1 or > 65535) throw new InvalidDataException($"invalid {paths.ConfigFile}: port must be 1-65535");
        if (settings.MaxConcurrency < 1) throw new InvalidDataException($"invalid {paths.ConfigFile}: maxConcurrency must be at least 1");
        return settings;
    }

    private static T? Read<T>(string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), type);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"invalid {file}: {ex.Message}");
        }
    }
}
