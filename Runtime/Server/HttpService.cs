using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.Control.Runtime;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Server
{
	/// <summary>
	/// Simple HTTP REST API endpoint that exposes OperationRegistry operations.
	/// Routes:
	///   GET  /api/tools           → list all operations (MCP-compatible format)
	///   GET  /api/operations      → list operation names only
	///   POST /api/call/{name}     → call an operation with JSON body as args
	/// </summary>
	public class HttpService
	{
		private HttpListener _listener;
		private bool _running;
		private readonly int _port;

		public int Port => _port;

		public HttpService(int port)
		{
			_port = port;
		}

		public void Start()
		{
			if (_running) return;

			try
			{
				_listener = new HttpListener();
				_listener.Prefixes.Add($"http://+:{_port}/");
				_listener.Start();
				_running = true;

				Logger.Log($"HTTP API started on port {_port}", tag: nameof(HttpService));
				ListenLoop().Forget();
			}
			catch (HttpListenerException ex) when (ex.ErrorCode == 5)
			{
				Logger.LogWarning(
					$"HTTP API requires admin rights for port {_port}. " +
					$"Run: netsh http add urlacl url=http://+:{_port}/ user=Everyone",
					tag: nameof(HttpService));
			}
			catch (PlatformNotSupportedException)
			{
				Logger.LogWarning("HTTP API not supported on this platform", tag: nameof(HttpService));
			}
			catch (Exception ex)
			{
				Logger.LogError($"Failed to start HTTP API on port {_port}: {ex.Message}", tag: nameof(HttpService));
			}
		}

		public void Stop()
		{
			_running = false;
			try
			{
				_listener?.Stop();
				_listener?.Close();
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Error stopping HTTP API: {ex.Message}", tag: nameof(HttpService));
			}
			finally
			{
				_listener = null;
			}
		}

		private async UniTaskVoid ListenLoop()
		{
			while (_running && _listener?.IsListening == true)
			{
				try
				{
					var context = await _listener.GetContextAsync();
					HandleRequestAsync(context).Forget();
				}
				catch (HttpListenerException) { break; }
				catch (ObjectDisposedException) { break; }
				catch (Exception ex)
				{
					Logger.LogWarning($"HTTP listen error: {ex.Message}", tag: nameof(HttpService));
				}
			}
		}

		private async UniTaskVoid HandleRequestAsync(HttpListenerContext context)
		{
			var request = context.Request;
			var response = context.Response;

			try
			{
				response.ContentType = "application/json; charset=utf-8";
				response.Headers.Add("Access-Control-Allow-Origin", "*");
				response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
				response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

				if (request.HttpMethod == "OPTIONS")
				{
					response.StatusCode = 204;
					response.Close();
					return;
				}

				var path = request.Url.AbsolutePath.TrimStart('/');

				switch (request.HttpMethod)
				{
					case "GET" when path == "api/tools":
					{
						var tools = new JArray();
						if (Main.Instance != null)
						{
							foreach (var op in Main.Instance.GetRegistered())
							{
								var tool = new JObject { 
									["name"] = op.Name, 
									["description"] = op.Description,
									["inputSchema"] = op.Schema.ToJObject()
								};
								tools.Add(tool);
							}
						}
						await WriteJsonAsync(response, 200, JObject.FromObject(new { tools }));
						break;
					}

					case "GET" when path == "api/operations":
					{
						var names = Main.Instance != null
							? new JArray(Main.Instance.GetRegistered().Select(op => op.Name))
							: new JArray();
						await WriteJsonAsync(response, 200, names);
						break;
					}

					case "POST" when path.StartsWith("api/call/"):
						var operationName = path.Substring("api/call/".Length);
						var body = await ReadBodyAsync(request);
						JToken args = null;
						if (!string.IsNullOrEmpty(body))
						{
							try { args = JToken.Parse(body); }
							catch { args = new JValue(body); }
						}

						var result = Main.Instance != null
							? await Main.Instance.ExecuteAsync(operationName, args)
							: JObject.FromObject(new { error = "Control API not available" });
						await WriteJsonAsync(response, 200, result);
						break;

					case "POST" when path == "mcp":
						await HandleMcpPostAsync(request, response);
						break;

					case "GET" when path == "" || path == "api":
						await WriteJsonAsync(response, 200, JObject.FromObject(new
						{
							service = "Nox.Control HTTP API",
							version = "1.0",
							operations = Main.Instance?.GetRegistered().Length ?? 0,
							endpoints = new[]
							{
								"GET  /api/tools",
								"GET  /api/operations",
								"POST /api/call/{name}",
								"POST /mcp"
							}
						}));
						break;

					default:
						await WriteJsonAsync(response, 404, JObject.FromObject(new { error = "Not found" }));
						break;
				}
			}
			catch (KeyNotFoundException)
			{
				await WriteJsonAsync(response, 404, JObject.FromObject(new { error = "Operation not found" }));
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"HTTP request error: {ex.Message}", tag: nameof(HttpService));
				await WriteJsonAsync(response, 500, JObject.FromObject(new { error = ex.Message }));
			}
		}

		private static async UniTask WriteJsonAsync(HttpListenerResponse response, int statusCode, JToken data)
		{
			response.StatusCode = statusCode;
			var json = data.ToString(Formatting.None);
			var buffer = Encoding.UTF8.GetBytes(json);
			response.ContentLength64 = buffer.Length;
			await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
			response.OutputStream.Close();
		}

		private async UniTask HandleMcpPostAsync(HttpListenerRequest request, HttpListenerResponse response)
		{
			var body = await ReadBodyAsync(request);
			if (string.IsNullOrEmpty(body))
			{
				await WriteJsonAsync(response, 400, JObject.FromObject(new {
					jsonrpc = "2.0",
					error = new { code = -32600, message = "Invalid Request" }
				}));
				return;
			}

			JsonRpcRequest mcpRequest;
			try
			{
				mcpRequest = JsonConvert.DeserializeObject<JsonRpcRequest>(body);
				if (mcpRequest == null || mcpRequest.JsonRpc != "2.0")
				{
					await WriteJsonAsync(response, 400, JObject.FromObject(new {
						jsonrpc = "2.0",
						error = new { code = -32600, message = "Invalid Request" }
					}));
					return;
				}
			}
			catch
			{
				await WriteJsonAsync(response, 400, JObject.FromObject(new {
					jsonrpc = "2.0",
					error = new { code = -32700, message = "Parse error" }
				}));
				return;
			}

			// Notification: no response body per JSON-RPC spec
			if (mcpRequest.Id == null)
			{
				response.StatusCode = 202;
				response.Close();
				return;
			}

			JToken result;
			try
			{
				result = await McpDispatcher.DispatchAsync(mcpRequest.Method, mcpRequest.Params);

				await WriteJsonAsync(response, 200, JObject.FromObject(new {
					jsonrpc = "2.0",
					id = mcpRequest.Id,
					result
				}));
			}
			catch (ArgumentException ex)
			{
				await WriteJsonAsync(response, 200, JObject.FromObject(new {
					jsonrpc = "2.0",
					id = mcpRequest.Id,
					error = new { code = -32602, message = $"Invalid params: {ex.Message}" }
				}));
			}
			catch (KeyNotFoundException)
			{
				await WriteJsonAsync(response, 200, JObject.FromObject(new {
					jsonrpc = "2.0",
					id = mcpRequest.Id,
					error = new { code = -32601, message = $"Method not found: {mcpRequest.Method}" }
				}));
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"MCP error: {ex.Message}", tag: nameof(HttpService));
				await WriteJsonAsync(response, 200, JObject.FromObject(new {
					jsonrpc = "2.0",
					id = mcpRequest.Id,
					error = new { code = -32603, message = $"Internal error: {ex.Message}" }
				}));
			}
		}

		private class JsonRpcRequest
		{
			[JsonProperty("jsonrpc")] public string JsonRpc;
			[JsonProperty("id")] public JRaw Id;
			[JsonProperty("method")] public string Method;
			[JsonProperty("params")] public JToken Params;
		}

		private static async UniTask<string> ReadBodyAsync(HttpListenerRequest request)
		{
			if (!request.HasEntityBody) return null;
			using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
			return await reader.ReadToEndAsync();
		}
	}
}
