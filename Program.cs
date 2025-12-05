using StackExchange.Redis;

// Entry point: build the playground and run the menu loop.
await new CachePlayground().RunAsync();

// Hosts the main menus and wires strategies to the console UI.
internal sealed class CachePlayground
{
    private readonly IDatabase _db;
    private readonly List<ICacheStrategy> _strategies;

    public CachePlayground()
    {
        var redis = ConnectionMultiplexer.Connect("localhost");
        _db = redis.GetDatabase();
        _strategies =
        [
            new AbsoluteExpirationCache(_db),
            new SlidingExpirationCache(_db),
            new DependentCache(_db),
        ];
    }

    // Main menu: pick which caching strategy you want to try.
    public async Task RunAsync()
    {
        var keepGoing = true;

        while (keepGoing)
        {
            keepGoing = await ConsoleMenu.RunAsync(
                title: "Redis caching playground",
                subtitle: "Pick a pattern, then try set → get → check to see how the cache behaves.",
                options: _strategies
                    .Select(
                        (strategy, i) =>
                            new ConsoleMenu.Option(
                                Key: (i + 1).ToString(),
                                Label: strategy.Name,
                                Action: () => RunStrategyAsync(strategy)
                            )
                    )
                    .ToList(),
                exitLabel: "Exit"
            );
        }

        Console.WriteLine("See you next time!");
    }

    // Submenu for a single strategy: set/get/check using the same key.
    private async Task RunStrategyAsync(ICacheStrategy strategy)
    {
        await ConsoleMenu.RunAsync(
            title: strategy.Name,
            subtitle: $"Working key: {strategy.Key}\nStart with set, then get/check to see TTLs.",
            options:
            [
                new ConsoleMenu.Option(
                    "1",
                    "Set cache",
                    async () =>
                    {
                        await strategy.SetAsync();
                        ConsoleTheme.Pause();
                    }
                ),
                new ConsoleMenu.Option(
                    "2",
                    "Get cache",
                    async () =>
                    {
                        await strategy.GetAsync();
                        ConsoleTheme.Pause();
                    }
                ),
                new ConsoleMenu.Option(
                    "3",
                    "Check cache",
                    async () =>
                    {
                        await strategy.CheckAsync();
                        ConsoleTheme.Pause();
                    }
                ),
            ],
            exitLabel: "Back to strategies"
        );
    }
}

// Simple contract every cache strategy follows.
internal interface ICacheStrategy
{
    string Name { get; }
    string Key { get; }
    Task SetAsync(); // Write or refresh the cache.
    Task GetAsync(); // Read the cached value (may refresh TTL depending on strategy).
    Task CheckAsync(); // Inspect current value/TTL without changing it.
}

// Shows absolute expiration: value dies after the TTL no matter what.
internal sealed class AbsoluteExpirationCache : ICacheStrategy
{
    private readonly IDatabase _db;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(5);

    public AbsoluteExpirationCache(IDatabase db) => _db = db;

    public string Name => "Absolute expiration";
    public string Key => "cache:absolute:product";

    // Seed the key with a value and a hard TTL (expires at a fixed time).
    public async Task SetAsync()
    {
        var value = $"Product catalog snapshot (abs) at {DateTimeOffset.UtcNow:HH:mm:ss}";
        await _db.StringSetAsync(Key, value, _ttl);
        Console.WriteLine($"Set `{Key}` with TTL {_ttl.TotalSeconds} seconds.");
    }

    // Read whatever is in cache now (might be gone if TTL passed).
    public async Task GetAsync()
    {
        var value = await _db.StringGetAsync(Key);
        Console.WriteLine(value.HasValue ? $"Value: {value}" : "Cache miss.");
    }

    // Peek at current value plus remaining TTL.
    public async Task CheckAsync()
    {
        var value = await _db.StringGetAsync(Key);
        var ttl = await _db.KeyTimeToLiveAsync(Key);
        Console.WriteLine(
            $"Value: {(value.HasValue ? value.ToString() : "<missing>")}, TTL: {(ttl?.TotalSeconds.ToString("F0") ?? "<expired>")} seconds."
        );
    }
}

// Shows sliding expiration: each hit refreshes the TTL window.
internal sealed class SlidingExpirationCache : ICacheStrategy
{
    private readonly IDatabase _db;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(5);

    public SlidingExpirationCache(IDatabase db) => _db = db;

    public string Name => "Sliding expiration";
    public string Key => "cache:sliding:user-session";

    // Seed the key with a value; TTL will slide on every successful read.
    public async Task SetAsync()
    {
        var value = $"User session token (sliding) at {DateTimeOffset.UtcNow:HH:mm:ss}";
        await _db.StringSetAsync(Key, value, _ttl);
        Console.WriteLine(
            $"Set `{Key}` with TTL {_ttl.TotalSeconds} seconds. Each get refreshes TTL."
        );
    }

    // Read and refresh TTL to keep the session alive; miss if expired.
    public async Task GetAsync()
    {
        var value = await _db.StringGetAsync(Key);
        if (value.HasValue)
        {
            await _db.KeyExpireAsync(Key, _ttl);
            Console.WriteLine($"Value: {value}. TTL refreshed to {_ttl.TotalSeconds} seconds.");
        }
        else
        {
            Console.WriteLine("Cache miss.");
        }
    }

    // Peek at current value and time remaining.
    public async Task CheckAsync()
    {
        var value = await _db.StringGetAsync(Key);
        var ttl = await _db.KeyTimeToLiveAsync(Key);
        Console.WriteLine(
            $"Value: {(value.HasValue ? value.ToString() : "<missing>")}, TTL: {(ttl?.TotalSeconds.ToString("F0") ?? "<expired>")} seconds."
        );
    }
}

// Shows dependent caching: child data is tied to a parent version.
internal sealed class DependentCache : ICacheStrategy
{
    private readonly IDatabase _db;
    private const string ChildKey = "product:inventory";
    private const string ParentVersionKey = "product:version";

    public DependentCache(IDatabase db) => _db = db;

    public string Name => "Dependent cache (parent/child)";
    public string Key => "product:details";

    // Write the parent and version; seed child if it's missing, otherwise mark it stale.
    public async Task SetAsync()
    {
        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var childExists = await _db.KeyExistsAsync(ChildKey);

        await _db.StringSetAsync(Key, $"Product details v{version}");
        await _db.StringSetAsync(ParentVersionKey, version);

        if (childExists)
        {
            Console.WriteLine("Parent refreshed. Child is now stale until you refresh it.");
            return;
        }

        await _db.HashSetAsync(
            ChildKey,
            new[]
            {
                new HashEntry("value", $"Inventory snapshot v{version}"),
                new HashEntry("parentVersion", version),
            }
        );

        Console.WriteLine($"Set parent `{Key}` and child `{ChildKey}` with version {version}.");
    }

    // Read child; if parent version changed, drop the child to avoid serving stale data.
    public async Task GetAsync()
    {
        var parentVersion = await GetParentVersionAsync();
        var childVersion = await _db.HashGetAsync(ChildKey, "parentVersion");
        var childValue = await _db.HashGetAsync(ChildKey, "value");

        if (!childValue.HasValue)
        {
            Console.WriteLine("Child cache miss. Run set to seed both parent and child.");
            return;
        }

        if (
            !parentVersion.HasValue
            || !childVersion.HasValue
            || childVersion != parentVersion.Value
        )
        {
            await _db.KeyDeleteAsync(ChildKey);
            Console.WriteLine("Parent changed. Child invalidated and removed.");
            return;
        }

        Console.WriteLine($"Child value: {childValue} (linked to parent version {parentVersion}).");
    }

    // Inspect parent/child values and versions; warn if child is stale.
    public async Task CheckAsync()
    {
        var parentValue = await _db.StringGetAsync(Key);
        var parentVersion = await GetParentVersionAsync();
        var childValue = await _db.HashGetAsync(ChildKey, "value");
        var childVersion = await _db.HashGetAsync(ChildKey, "parentVersion");

        Console.WriteLine(
            $"Parent `{Key}`: {(parentValue.HasValue ? parentValue.ToString() : "<missing>")} (v{parentVersion?.ToString() ?? "n/a"})"
        );
        Console.WriteLine(
            $"Child `{ChildKey}`: {(childValue.HasValue ? childValue.ToString() : "<missing>")} (linked v{childVersion.ToString() ?? "n/a"})"
        );

        var isStale =
            parentVersion.HasValue && childVersion.HasValue && childVersion != parentVersion.Value;
        if (isStale)
        {
            Console.WriteLine("Child is stale and will be dropped on next get.");
        }
    }

    // Helper to fetch the parent version or return null if missing.
    private async Task<long?> GetParentVersionAsync()
    {
        var version = await _db.StringGetAsync(ParentVersionKey);
        if (!version.HasValue)
        {
            return null;
        }

        return long.TryParse(version, out var parsed) ? parsed : null;
    }
}

// Tiny helper to colorize console output for better readability.
internal static class ConsoleTheme
{
    public static void WriteTitle(string text) => WriteWithColor(text, ConsoleColor.Cyan);

    public static void WriteInfo(string text) => WriteWithColor(text, ConsoleColor.Green);

    public static void WriteMuted(string text) => WriteWithColor(text, ConsoleColor.DarkGray);

    public static void WritePrompt(string text) => WriteWithColor(text, ConsoleColor.Yellow, false);

    public static void Pause()
    {
        WriteMuted("Press ENTER to continue...");
        Console.ReadLine();
    }

    private static void WriteWithColor(string text, ConsoleColor color, bool newLine = true)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        if (newLine)
        {
            Console.WriteLine(text);
        }
        else
        {
            Console.Write(text);
        }
        Console.ForegroundColor = previous;
    }
}

// Tiny menu helper to keep the console workflow consistent.
internal static class ConsoleMenu
{
    internal sealed record Option(string Key, string Label, Func<Task> Action);

    public static async Task<bool> RunAsync(
        string title,
        string? subtitle,
        IReadOnlyList<Option> options,
        string exitLabel
    )
    {
        while (true)
        {
            Console.Clear();
            ConsoleTheme.WriteTitle(title);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                ConsoleTheme.WriteMuted(subtitle);
                Console.WriteLine();
            }

            foreach (var option in options)
            {
                ConsoleTheme.WriteInfo($"[{option.Key}] {option.Label}");
            }
            ConsoleTheme.WriteInfo($"[0] {exitLabel}");
            ConsoleTheme.WritePrompt("> ");

            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input) || input == "0")
            {
                return false;
            }

            var match = options.FirstOrDefault(o =>
                string.Equals(o.Key, input.Trim(), StringComparison.OrdinalIgnoreCase)
            );

            if (match is null)
            {
                Console.WriteLine("Unknown option. Please try again.");
                ConsoleTheme.Pause();
                continue;
            }

            await match.Action();
        }
    }
}
