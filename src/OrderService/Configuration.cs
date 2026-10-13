using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.Oidc;

namespace OrderService;

public sealed record AppConfig(string Uri, string AllowedHost, string Database, bool AllowWrites,
    bool SingleShard, string ApiToken, string Auth = "entra", string? Username = null, string? Password = null)
{
    public static AppConfig FromEnvironment() => new(
        Env("DOCUMENTDB_URI"), Env("DOCUMENTDB_ALLOWED_HOST"), Env("DOCUMENTDB_DATABASE"),
        Env("DOCUMENTDB_ALLOW_WRITES") == "true", Env("DOCUMENTDB_CONFIRM_SINGLE_SHARD") == "true",
        Env("ORDER_API_TOKEN"), Environment.GetEnvironmentVariable("DOCUMENTDB_AUTH") ?? "entra",
        Environment.GetEnvironmentVariable("DOCUMENTDB_USERNAME"), Environment.GetEnvironmentVariable("DOCUMENTDB_PASSWORD"));
    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    public void Validate(bool requireApiToken = true)
    {
        ClientFactory.ValidateDestination(Uri, AllowedHost);
        if (!Regex.IsMatch(Database, "^orders_demo_[a-z0-9_]{3,40}$", RegexOptions.CultureInvariant))
            throw new ConfigurationException("Use a disposable orders_demo_ database (lowercase letters, digits, underscores).");
        if (requireApiToken && (ApiToken.Length < 32 || ApiToken.Length > 256 || ApiToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            throw new ConfigurationException("ORDER_API_TOKEN must be 32–256 URL-safe characters.");
        if (Auth is not ("entra" or "scram") || Auth == "scram" && (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password)))
            throw new ConfigurationException("Invalid database authentication configuration.");
    }

    public void DemandWrites()
    {
        Validate(false);
        if (!AllowWrites || !SingleShard)
            throw new ConfigurationException("Writes require DOCUMENTDB_ALLOW_WRITES=true and DOCUMENTDB_CONFIRM_SINGLE_SHARD=true.");
    }
}

public sealed class ConfigurationException(string message) : Exception(message);

public static class ClientFactory
{
    public static Uri ValidateDestination(string value, string allowedHost)
    {
        if (!System.Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "mongodb+srv" ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.AbsolutePath is not ("" or "/") ||
            uri.Fragment.Length != 0 || !Regex.IsMatch(uri.Host, @"^[a-z0-9-]+\.mongocluster\.cosmos\.azure\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            !string.Equals(uri.Host, allowedHost, StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException("Use a credential-free managed Azure DocumentDB SRV URI and its exact allowed host.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !keys.Add(pair[0]) ||
                !(pair[0].Equals("tls", StringComparison.OrdinalIgnoreCase) && pair[1] == "true" ||
                  pair[0].Equals("retrywrites", StringComparison.OrdinalIgnoreCase) && pair[1] == "false" ||
                  pair[0].Equals("authsource", StringComparison.OrdinalIgnoreCase) && pair[1] == "admin"))
                throw new ConfigurationException("Only tls=true, retrywrites=false and authSource=admin URI options are allowed.");
        }
        return uri;
    }

    public static MongoClientSettings Settings(AppConfig config, Func<TokenCredential>? credentialFactory = null)
    {
        config.Validate(false); // Destination validation precedes token acquisition / client construction.
        var settings = MongoClientSettings.FromConnectionString(config.Uri);
        settings.UseTls = true;
        settings.AllowInsecureTls = false;
        settings.RetryWrites = false;
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        settings.ConnectTimeout = TimeSpan.FromSeconds(10);
        settings.SocketTimeout = TimeSpan.FromSeconds(10);
        settings.WaitQueueTimeout = TimeSpan.FromSeconds(10);
        settings.MaxConnectionPoolSize = 100;
        settings.MaxConnectionIdleTime = TimeSpan.FromMinutes(2);
        settings.ApplicationName = "azure-documentdb-order-service";
        settings.Credential = config.Auth == "scram"
            ? new MongoCredential("SCRAM-SHA-256", new MongoInternalIdentity("admin", config.Username), new PasswordEvidence(config.Password))
            : MongoCredential.CreateOidcCredential(new DatabaseTokenCallback((credentialFactory ?? (() => new DefaultAzureCredential(
                new DefaultAzureCredentialOptions { ExcludeInteractiveBrowserCredential = true })))()));
        return settings;
    }
}

public sealed class DatabaseTokenCallback(TokenCredential credential) : IOidcCallback
{
    public const string Scope = "https://ossrdbms-aad.database.windows.net/.default";
    public OidcAccessToken GetOidcAccessToken(OidcCallbackParameters parameters, CancellationToken cancellationToken) =>
        Convert(credential.GetToken(new TokenRequestContext([Scope]), cancellationToken));
    public async Task<OidcAccessToken> GetOidcAccessTokenAsync(OidcCallbackParameters parameters, CancellationToken cancellationToken) =>
        Convert(await credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken));
    private static OidcAccessToken Convert(AccessToken token)
    {
        var remaining = token.ExpiresOn - DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(token.Token) || remaining <= TimeSpan.FromSeconds(30))
            throw new InvalidOperationException("No usable database token.");
        return new OidcAccessToken(token.Token, remaining);
    }
}
