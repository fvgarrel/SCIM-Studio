namespace ScimStudio.DemoServer;

/// <summary>How <see cref="DemoScimServer"/> starts.</summary>
public sealed record DemoServerOptions {
    /// <summary>The port on the loopback interface; 0 lets the operating system pick a free one.</summary>
    public int Port { get; init; }

    /// <summary>The bearer token clients must send; null generates a random one.</summary>
    public string? Token { get; init; }

    /// <summary>Whether the server starts with sample users and groups.</summary>
    public bool Seed { get; init; } = true;

    /// <summary>The most resources one page of a list response holds.</summary>
    public int MaxResults { get; init; } = 200;
}
