using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clicky.Windows;

/// <summary>
/// One mapped route of a --replay-manifest file: what replay answers when the app requests this path
/// instead of the live Worker. Routes absent from the manifest are never answered (fail closed), so a
/// replay can only serve what the manifest explicitly staged.
/// </summary>
/// <param name="RoutePath">Absolute path the request URL must match exactly, for example "/chat".</param>
/// <param name="Status">HTTP status to answer with (100-599; the Worker's real status for error fixtures).</param>
/// <param name="Headers">Response content headers, for example {"content-type": "audio/mpeg"} for /tts.</param>
/// <param name="Body">Literal response body. Mutually exclusive with <paramref name="BodyFilePath"/>.</param>
/// <param name="BodyFilePath">
/// File whose bytes are the response body, resolved against the manifest's directory. A ".sse" file is
/// served as a paced Server-Sent-Events stream (the /chat replay path), any other file as one fixed body.
/// </param>
internal sealed record ReplayManifestRoute(
    string RoutePath,
    int Status,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    string? BodyFilePath)
{
    /// <summary>True when the route's bodyFile holds SSE events that must drip in one event per read, like the live stream.</summary>
    public bool ServesPacedSseStream =>
        BodyFilePath is not null && BodyFilePath.EndsWith(".sse", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The parsed --replay-manifest file. Maps Worker routes ("/chat", "/tts", "/transcribe-token") to recorded
/// responses and the AssemblyAI websocket to a recorded .jsonl frame file (the fake socket transport itself
/// lands in T-023; this task pins only the schema and the fail-closed HTTP routing).
/// </summary>
/// <param name="Routes">Mapped routes; requests to any other path fail the run instead of reaching the network.</param>
/// <param name="AssemblyAiSocketFramesFilePath">
/// Recorded AssemblyAI websocket frames to replay through the fake transport (T-023). Null when the manifest
/// stages no socket traffic. Present in the schema now so manifests written for later tasks keep parsing.</param>
internal sealed record ReplayManifest(
    IReadOnlyList<ReplayManifestRoute> Routes,
    string? AssemblyAiSocketFramesFilePath)
{
    /// <summary>Exact-path lookup, because replay addresses routes by their Worker path only.</summary>
    public ReplayManifestRoute? FindRouteFor(string requestAbsolutePath) =>
        Routes.SingleOrDefault(route => route.RoutePath == requestAbsolutePath);

    /// <summary>
    /// The implicit manifest behind --replay &lt;fixture.sse&gt;: exactly one /chat route. Both replay modes
    /// share one routing path, so the fail-closed guarantee is identical for a single fixture and a manifest.
    /// </summary>
    public static ReplayManifest CreateForSingleChatFixture(string sseFixturePath) =>
        new(
            [new ReplayManifestRoute("/chat", DefaultResponseStatus, new Dictionary<string, string>(), Body: null, BodyFilePath: sseFixturePath)],
            AssemblyAiSocketFramesFilePath: null);

    /// <summary>
    /// Parses and validates a manifest file, throwing <see cref="CommandLineUsageException"/> for anything a
    /// caller could have mistyped (that surfaces as exit code 2, a usage error, not a runtime failure).
    /// Unknown keys are rejected so a typo cannot silently drop a route and turn into a fail-closed surprise.
    /// </summary>
    public static ReplayManifest LoadFrom(string manifestFilePath)
    {
        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestFilePath))!;
        JsonObject manifestDocument;
        try
        {
            manifestDocument = JsonNode.Parse(File.ReadAllText(manifestFilePath))!.AsObject();
        }
        catch (JsonException manifestJsonException)
        {
            throw new CommandLineUsageException($"Replay manifest {manifestFilePath} is not valid JSON: {manifestJsonException.Message}");
        }

        List<ReplayManifestRoute> manifestRoutes = [];
        if (manifestDocument.ContainsKey("routes"))
        {
            if (manifestDocument["routes"] is not JsonObject routesObject)
            {
                throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: 'routes' must be an object mapping paths like \"/chat\".");
            }
            foreach ((string routePath, JsonNode? routeEntry) in routesObject)
            {
                manifestRoutes.Add(ReadRoute(manifestFilePath, manifestDirectory, routePath, routeEntry));
            }
        }

        string? assemblyAiSocketFramesFilePath = null;
        if (manifestDocument.ContainsKey("assemblyaiSocket"))
        {
            if (manifestDocument["assemblyaiSocket"] is not JsonObject socketObject)
            {
                throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: 'assemblyaiSocket' must be an object.");
            }
            RejectUnknownProperties(manifestFilePath, "assemblyaiSocket", socketObject, ["framesFile"]);
            if (socketObject["framesFile"] is not JsonValue framesFileValue || framesFileValue.GetValueKind() != JsonValueKind.String)
            {
                throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: assemblyaiSocket.framesFile must be a string.");
            }
            assemblyAiSocketFramesFilePath = ResolveExistingFile(manifestFilePath, manifestDirectory, (string)framesFileValue!, "assemblyaiSocket.framesFile");
        }

        RejectUnknownProperties(manifestFilePath, "the manifest root", manifestDocument, ["routes", "assemblyaiSocket"]);
        return new ReplayManifest(manifestRoutes, assemblyAiSocketFramesFilePath);
    }

    private const int DefaultResponseStatus = 200;

    private static ReplayManifestRoute ReadRoute(string manifestFilePath, string manifestDirectory, string routePath, JsonNode? routeEntry)
    {
        if (!routePath.StartsWith('/'))
        {
            throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: route key '{routePath}' must be an absolute path like \"/chat\".");
        }
        if (routeEntry is not JsonObject routeObject)
        {
            throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: route '{routePath}' must be an object.");
        }
        RejectUnknownProperties(manifestFilePath, $"route '{routePath}'", routeObject, ["status", "headers", "body", "bodyFile"]);

        int responseStatus = DefaultResponseStatus;
        if (routeObject.ContainsKey("status"))
        {
            if (routeObject["status"] is not JsonValue statusValue
                || !statusValue.TryGetValue<int>(out int parsedStatus)
                || parsedStatus < 100
                || parsedStatus > 599)
            {
                throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: route '{routePath}' status must be a number 100-599.");
            }
            responseStatus = parsedStatus;
        }

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (routeObject.ContainsKey("headers"))
        {
            if (routeObject["headers"] is not JsonObject headersObject)
            {
                throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: route '{routePath}' headers must be an object of name/value strings.");
            }
            foreach ((string headerName, JsonNode? headerValue) in headersObject)
            {
                if (string.IsNullOrWhiteSpace(headerName) || headerValue is not JsonValue headerValueNode || headerValueNode.GetValueKind() != JsonValueKind.String)
                {
                    throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: route '{routePath}' header '{headerName}' needs a non-empty name and a string value.");
                }
                responseHeaders[headerName] = (string)headerValueNode!;
            }
        }

        string? body = null;
        string? bodyFilePath = null;
        if (routeObject.ContainsKey("body") && routeObject.ContainsKey("bodyFile"))
        {
            throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: route '{routePath}' cannot have both 'body' and 'bodyFile'.");
        }
        if (routeObject.ContainsKey("body"))
        {
            if (routeObject["body"] is not JsonValue bodyValue || bodyValue.GetValueKind() != JsonValueKind.String)
            {
                throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: route '{routePath}' body must be a string.");
            }
            body = (string)bodyValue!;
        }
        if (routeObject.ContainsKey("bodyFile"))
        {
            if (routeObject["bodyFile"] is not JsonValue bodyFileValue || bodyFileValue.GetValueKind() != JsonValueKind.String)
            {
                throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: route '{routePath}' bodyFile must be a string.");
            }
            bodyFilePath = ResolveExistingFile(manifestFilePath, manifestDirectory, (string)bodyFileValue!, $"route '{routePath}' bodyFile");
        }

        return new ReplayManifestRoute(routePath, responseStatus, responseHeaders, body, bodyFilePath);
    }

    /// <summary>Relative paths resolve against the manifest's directory, so a manifest and its fixtures move together.</summary>
    private static string ResolveExistingFile(string manifestFilePath, string manifestDirectory, string referencedPath, string referencedPropertyDescription)
    {
        string resolvedPath = Path.IsPathRooted(referencedPath)
            ? referencedPath
            : Path.GetFullPath(Path.Combine(manifestDirectory, referencedPath));
        if (!File.Exists(resolvedPath))
        {
            throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: {referencedPropertyDescription} '{referencedPath}' does not exist (resolved to {resolvedPath}).");
        }
        return resolvedPath;
    }

    private static void RejectUnknownProperties(string manifestFilePath, string objectDescription, JsonObject jsonObject, IReadOnlyList<string> knownPropertyNames)
    {
        foreach (string propertyName in jsonObject.Select(property => property.Key))
        {
            if (!knownPropertyNames.Contains(propertyName))
            {
                throw new CommandLineUsageException($"Replay manifest {manifestFilePath}: unknown property '{propertyName}' on {objectDescription}; known properties: {string.Join(", ", knownPropertyNames)}.");
            }
        }
    }
}
