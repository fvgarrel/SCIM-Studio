using ScimStudio.App.Localization;
using ScimStudio.Core.Dialects;

namespace ScimStudio.App.Settings;

public enum ThemeChoice {
    System,
    Light,
    Dark,
}

/// <summary>What the tool remembers between starts.</summary>
public sealed class AppSettings {
    public string Language { get; set; } = Localizer.SystemLanguage;

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public List<ConnectionProfile> Profiles { get; set; } = [];

    public Guid? LastProfileId { get; set; }
}

/// <summary>A server the tool connects to: where it is, how to get in, and how to write to it.</summary>
public sealed class ConnectionProfile {
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The token as the vault stores it; never the token itself on Windows. Null when it is not remembered.</summary>
    public string? StoredToken { get; set; }

    public bool RememberToken { get; set; } = true;

    public DialectKind Dialect { get; set; } = DialectKind.Rfc7644;

    public bool AcceptInvalidCertificates { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}
