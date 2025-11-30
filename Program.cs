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
        while (true)
        {
            Console.Clear();
            ConsoleTheme.WriteTitle("Redis caching playground");
            Console.WriteLine("Choose caching strategy to explore:");
            for (int i = 0; i < _strategies.Count; i++)
            {
                ConsoleTheme.WriteInfo($"[{i + 1}] {_strategies[i].Name}");
            }
            ConsoleTheme.WriteInfo("[0] Exit");
            ConsoleTheme.WritePrompt("> ");

            var input = Console.ReadLine();
            if (input == "0" || string.IsNullOrWhiteSpace(input))
            {
                break;
            }

            if (int.TryParse(input, out var option) && option > 0 && option <= _strategies.Count)
            {
                await RunStrategyAsync(_strategies[option - 1]);
            }
            else
            {
                Console.WriteLine("Unknown option. Please try again.");
            }
        }

        Console.WriteLine("See you next time!");
    }

    // Submenu for a single strategy: set/get/check using the same key.
    private async Task RunStrategyAsync(ICacheStrategy strategy)
    {
        var keepGoing = true;

        while (keepGoing)
        {
            Console.Clear();
            ConsoleTheme.WriteTitle($"{strategy.Name}");
            ConsoleTheme.WriteMuted($"Working key: {strategy.Key}");
            Console.WriteLine();
            ConsoleTheme.WriteInfo("[1] Set cache");
            ConsoleTheme.WriteInfo("[2] Get cache");
            ConsoleTheme.WriteInfo("[3] Check cache");
            ConsoleTheme.WriteInfo("[0] Back to strategies");
            ConsoleTheme.WritePrompt("> ");

            var input = Console.ReadLine();
            switch (input)
            {
                case "1":
                    await strategy.SetAsync();
                    ConsoleTheme.Pause();
                    break;
                case "2":
                    await strategy.GetAsync();
                    ConsoleTheme.Pause();
                    break;
                case "3":
                    await strategy.CheckAsync();
                    ConsoleTheme.Pause();
                    break;
                case "0":
                    keepGoing = false;
                    break;
                default:
                    Console.WriteLine("Unknown option. Please try again.");
                    ConsoleTheme.Pause();
                    break;
            }
        }
    }
}

// Simple contract every cache strategy follows.
internal interface ICacheStrategy
{
    string Name { get; }
    string Key { get; }
    Task SetAsync();   // Write or refresh the cache.
    Task GetAsync();   // Read the cached value (may refresh TTL depending on strategy).
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

        if (!parentVersion.HasValue || !childVersion.HasValue || childVersion != parentVersion.Value)
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
