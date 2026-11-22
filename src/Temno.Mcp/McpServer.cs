using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Mcp;

public class McpServer
{
    private HttpListener? _listener;
    private readonly int _port;
    private readonly string _host;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private bool _isRunning;
    private CancellationTokenSource? _cts;
    private const int MaxRequestBodyChars = 20 * 1024 * 1024;

    public bool IsRunning => _isRunning;

    /// <summary>The loopback port this server binds.</summary>
    public int Port => _port;

    /// <summary>Per-launch secret required on POST /mcp.</summary>
    public string AuthToken { get; }

    /// <param name="port">Null binds this build's port (<see cref="McpDefaults.Port"/>).</param>
    public McpServer(string host = McpDefaults.Host, int? port = null, string? authToken = null)
    {
        if (!IsLoopbackHost(host))
            throw new ArgumentException("MCP host must be a loopback address.", nameof(host));
        var bound = port ?? McpDefaults.Port;
        if (bound is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        _host = host;
        _port = bound;
        AuthToken = string.IsNullOrWhiteSpace(authToken) ? McpPreferences.NewAuthToken() : authToken.Trim();
    }

    public void Start()
    {
        if (_isRunning) return;

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        
        // Bind to loopback IP only as per WP-10 sacred rule
        _listener.Prefixes.Add($"http://{_host}:{_port}/");
        _listener.Start();
        _isRunning = true;

        Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();
        
        try
        {
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }
        }
        catch (Exception)
        {
            // Ignore socket shutdown exception
        }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (_isRunning && _listener != null && _listener.IsListening)
        {
            try
            {
                if (token.IsCancellationRequested) break;

                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context), token);
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested || !_isRunning) break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        var origin = request.Headers["Origin"];
        if (!string.IsNullOrWhiteSpace(origin) && !IsLoopbackOrigin(origin))
        {
            await WriteJsonResponseAsync(response, HttpStatusCode.Forbidden, new
            {
                error = "Browser origin is not allowed to access the Temno MCP server.",
            });
            return;
        }

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.Close();
            return;
        }

        try
        {
            var rawPath = request.Url?.AbsolutePath ?? "/";
            var path = rawPath.TrimEnd('/');

            if (request.HttpMethod == "GET")
            {
                if (path == "/.well-known/oauth-protected-resource")
                {
                    await WriteJsonResponseAsync(response, HttpStatusCode.OK, new
                    {
                        resource = $"http://{_host}:{_port}",
                    });
                    return;
                }

                if (path is "" or "/health")
                {
                    await WriteJsonResponseAsync(response, HttpStatusCode.OK, new { status = "healthy" });
                    return;
                }

                if (path == "/mcp")
                {
                    // Streamable HTTP clients probe GET for SSE. We are POST JSON-RPC only;
                    // 405 is the spec signal to fall back to POST (200 JSON on GET breaks Cursor).
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    response.AddHeader("Allow", "POST");
                    response.Close();
                    return;
                }

                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            if (request.HttpMethod == "POST" && (path == "" || path == "/mcp"))
            {
                if (!HasValidAuthToken(request))
                {
                    await WriteJsonResponseAsync(response, HttpStatusCode.Unauthorized, new
                    {
                        error = $"Missing or invalid {McpDefaults.AuthHeaderName}.",
                    });
                    return;
                }

                string requestBody;
                try
                {
                    if (request.ContentLength64 > MaxRequestBodyChars)
                        throw new InvalidDataException("MCP request body is too large.");
                    requestBody = await ReadRequestBodyAsync(request.InputStream);
                }
                catch (InvalidDataException ex)
                {
                    await WriteJsonResponseAsync(response, HttpStatusCode.RequestEntityTooLarge, new
                    {
                        error = ex.Message,
                    });
                    return;
                }

                JsonRpcRequest? rpcRequest = null;
                try
                {
                    rpcRequest = JsonSerializer.Deserialize<JsonRpcRequest>(requestBody);
                }
                catch (JsonException)
                {
                    await WriteJsonResponseAsync(response, HttpStatusCode.BadRequest, new JsonRpcResponse
                    {
                        Error = new JsonRpcError
                        {
                            Code = -32700,
                            Message = "Parse error"
                        }
                    });
                    return;
                }

                if (rpcRequest == null)
                {
                    await WriteJsonResponseAsync(response, HttpStatusCode.BadRequest, new JsonRpcResponse
                    {
                        Error = new JsonRpcError
                        {
                            Code = -32600,
                            Message = "Invalid Request"
                        }
                    });
                    return;
                }

                JsonRpcResponse? rpcResponse;
                await _requestGate.WaitAsync();
                try
                {
                    rpcResponse = ProcessJsonRpcRequest(rpcRequest);
                }
                finally
                {
                    _requestGate.Release();
                }
                if (rpcResponse == null)
                {
                    // If it was a notification (like initialized), return 204 No Content
                    response.StatusCode = (int)HttpStatusCode.NoContent;
                    response.Close();
                    return;
                }

                await WriteJsonResponseAsync(response, HttpStatusCode.OK, rpcResponse);
                return;
            }

            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.Close();
        }
        catch (Exception ex)
        {
            try
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                using var writer = new StreamWriter(response.OutputStream, Encoding.UTF8);
                await writer.WriteAsync(ex.Message);
            }
            catch
            {
                // Ignore nested writing issues
            }
        }
    }

    private static async Task<string> ReadRequestBodyAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var body = new StringBuilder();
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            if (body.Length + read > MaxRequestBodyChars)
                throw new InvalidDataException("MCP request body is too large.");
            body.Append(buffer, 0, read);
        }
        return body.ToString();
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        var normalized = host.Trim('[', ']');
        return IPAddress.TryParse(normalized, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return uri.IsLoopback;
    }

    private bool HasValidAuthToken(HttpListenerRequest request)
    {
        var sent = request.Headers[McpDefaults.AuthHeaderName] ?? string.Empty;
        var expected = Encoding.UTF8.GetBytes(AuthToken);
        var actual = Encoding.UTF8.GetBytes(sent);
        return actual.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // WP-309: the skill index and its pointer from one scan, as they are when this client connects.
    private static string McpInstructions()
    {
        var (section, pointer) = Temno.Core.Agent.Skills.SkillLibrary.Index();
        return Temno.Core.Agent.AgentInstructions.Mcp(section, pointer);
    }

    private JsonRpcResponse? ProcessJsonRpcRequest(JsonRpcRequest request)
    {
        // Handle notifications (requests with no ID)
        if (request.Id == null && request.Method.StartsWith("notifications/"))
        {
            return null; // Notifications return no response
        }

        var response = new JsonRpcResponse
        {
            Id = request.Id
        };

        try
        {
            switch (request.Method)
            {
                case "initialize":
                    response.Result = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new
                        {
                            resources = new { subscribe = false, listChanged = false },
                            tools = new { listChanged = false }
                        },
                        serverInfo = new
                        {
                            name = "temno",
                            version = "1.0.0"
                        },
                        // WP-309: the skills as they are when this client connects.
                        instructions = McpInstructions()
                    };
                    break;

                case "tools/list":
                    response.Result = new
                    {
                        // Only advertise tools the executor actually implements, so the agent never
                        // discovers advertised-but-unimplemented tools that error at call time.
                        tools = McpToolRegistry.Tools
                            .Where(t => ToolExecutor.ImplementedTools.Contains(t.Name))
                            .ToList()
                    };
                    break;

                case "tools/call":
                    if (request.Params == null)
                    {
                        response.Error = new JsonRpcError { Code = -32602, Message = "Invalid params" };
                        break;
                    }

                    var nameToken = request.Params["name"];
                    if (nameToken == null)
                    {
                        response.Error = new JsonRpcError { Code = -32602, Message = "Missing tool name" };
                        break;
                    }

                    var toolName = nameToken.GetValue<string>();
                    var toolArgs = request.Params["arguments"] as JsonObject;
                    
                    response.Result = ToolExecutor.Execute(toolName, toolArgs);
                    break;

                case "resources/list":
                    response.Result = new ResourceListResult
                    {
                        // WP-193: temno:// is canonical; palmier:// stays as an alias, not a redirect.
                        Resources =
                        [
                            new() {
                                Name = "Video Models",
                                Uri = "temno://models/video",
                                Description = "Available AI video generation models and their capabilities",
                                MimeType = "application/json"
                            },
                            new() {
                                Name = "Image Models",
                                Uri = "temno://models/image",
                                Description = "Available AI image generation models and their capabilities",
                                MimeType = "application/json"
                            },
                            new() {
                                Name = "Video Models (legacy alias)",
                                Uri = "palmier://models/video",
                                Description = "Alias of temno://models/video",
                                MimeType = "application/json"
                            },
                            new() {
                                Name = "Image Models (legacy alias)",
                                Uri = "palmier://models/image",
                                Description = "Alias of temno://models/image",
                                MimeType = "application/json"
                            }
                        ]
                    };
                    break;

                case "resources/read":
                    if (request.Params == null)
                    {
                        response.Error = new JsonRpcError { Code = -32602, Message = "Invalid params" };
                        break;
                    }

                    var uriToken = request.Params["uri"];
                    if (uriToken == null)
                    {
                        response.Error = new JsonRpcError { Code = -32602, Message = "Missing resource URI" };
                        break;
                    }

                    var uri = uriToken.GetValue<string>();
                    response.Result = new ResourceReadResult
                    {
                        Contents =
                        [
                            new() {
                                Uri = uri,
                                MimeType = "application/json",
                                Text = "[]" // Mock response matching MCPService.swift fallback
                            }
                        ]
                    };
                    break;

                default:
                    response.Error = new JsonRpcError
                    {
                        Code = -32601,
                        Message = $"Method '{request.Method}' not found"
                    };
                    break;
            }
        }
        catch (Exception ex)
        {
            response.Error = new JsonRpcError
            {
                Code = -32603,
                Message = $"Internal error: {ex.Message}"
            };
        }

        return response;
    }

    private static async Task WriteJsonResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, object data)
    {
        response.ContentType = "application/json";
        response.StatusCode = (int)statusCode;
        
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}
