using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.Control.Server;
using WebSocketSharp;
using WebSocketSharp.Server;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Runtime.Server.Mcp  {
	/// <summary>
	/// WebSocket behavior that implements the Model Context Protocol (MCP)
	/// using JSON-RPC 2.0 as the message format.
	/// Delegates tool operations to <see cref="OperationRegistry"/>.
	/// </summary>
	public class McpService : WebSocketBehavior {
		public WebSocket Server;

		private bool _initialized;
		private string _clientName;
		private string _clientVersion;

		#region JSON-RPC 2.0 Message Types

		private class JsonRpcRequest {
			[JsonProperty("jsonrpc")] public string JsonRpc;
			[JsonProperty("id")] public JRaw Id;
			[JsonProperty("method")] public string Method;
			[JsonProperty("params")] public JToken Params;
		}

		private class JsonRpcResponse {
			[JsonProperty("jsonrpc")] public string JsonRpc = "2.0";
			[JsonProperty("id")] public JRaw Id;
			[JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] public JToken Result;
			[JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public JsonRpcError Error;
		}

		private class JsonRpcError {
			[JsonProperty("code")] public int Code;
			[JsonProperty("message")] public string Message;
			[JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public JToken Data;
		}

		#endregion

		#region Lifecycle

		protected override void OnOpen() {
			_initialized = false;
			Logger.Log($"MCP client connected: {Context?.UserEndPoint}", tag: nameof(McpService));
		}

		protected override void OnClose(CloseEventArgs e) {
			_initialized = false;
			Logger.Log($"MCP client disconnected: {Context?.UserEndPoint}", tag: nameof(McpService));
		}

		protected override void OnError(ErrorEventArgs e) {
			if (!Server.IsDisposing)
				Logger.LogError(e.Exception, tag: nameof(McpService));
		}

		protected override void OnMessage(MessageEventArgs e)
			=> OnMessageAsync(e).Forget();

		#endregion

		#region Message Handling

		private async UniTask OnMessageAsync(MessageEventArgs e)
		{
			if (Server.IsDisposing || !Server.IsRunning()) return;

			JsonRpcRequest request;
			try {
				request = JsonConvert.DeserializeObject<JsonRpcRequest>(e.Data);
				if (request == null || request.JsonRpc != "2.0")
				{
					SendError(null, -32600, "Invalid Request");
					return;
				}
			} catch (Exception) {
				SendError(null, -32700, "Parse error");
				return;
			}

			// Handle notifications (no id field)
			if (request.Id == null) {
				await HandleNotificationAsync(request);
				return;
			}

			// Handle requests (has id field)
			await HandleRequestAsync(request);
		}

		private async UniTask HandleRequestAsync(JsonRpcRequest request)
		{
			// Check initialization requirement
			if (!_initialized && request.Method != "initialize") {
				SendError(request.Id, -32002, "Not initialized");
				return;
			}

			try
			{
				var result = await McpDispatcher.DispatchAsync(
					request.Method, request.Params,
					(name, version) => MarkInitialized(name, version)
				);

				SendResult(request.Id, result);
			} catch (KeyNotFoundException) {
				SendError(request.Id, -32601, $"Method not found: {request.Method}");
			} catch (ArgumentException ex) {
				SendError(request.Id, -32602, $"Invalid params: {ex.Message}");
			} catch (Exception ex) {
				Logger.LogError($"MCP dispatch error for {request.Method}: {ex}", tag: nameof(McpService));
				SendError(request.Id, -32603, $"Internal error: {ex.Message}");
			}
		}

		private async UniTask HandleNotificationAsync(JsonRpcRequest request)
		{
			switch (request.Method)
			{
				case "initialized":
					// Client confirms initialization complete
					Logger.Log($"MCP client initialized: {_clientName} v{_clientVersion}", tag: nameof(McpService));
					break;

				case "notifications/initialized":
					break;

				default:
					// Unknown notification – silently ignore per JSON-RPC spec
					break;
			}

			await UniTask.CompletedTask;
		}

		#endregion

		#region Response Helpers

		private void SendResult(JRaw id, JToken result)
		{
			if (Server.IsDisposing) return;

			var response = new JsonRpcResponse
			{
				Id = id,
				Result = result
			};

			Send(JsonConvert.SerializeObject(response, new JsonSerializerSettings
			{
				NullValueHandling = NullValueHandling.Ignore
			}));
		}

		private void SendError(JRaw id, int code, string message, JToken data = null)
		{
			if (Server.IsDisposing) return;

			var response = new JsonRpcResponse
			{
				Id = id,
				Error = new JsonRpcError
				{
					Code = code,
					Message = message,
					Data = data
				}
			};

			Send(JsonConvert.SerializeObject(response, new JsonSerializerSettings
			{
				NullValueHandling = NullValueHandling.Ignore
			}));
		}

		/// <summary>
		/// Sends a JSON-RPC notification (no id) to the client.
		/// </summary>
		public void SendNotification(string method, JToken @params = null)
		{
			if (Server.IsDisposing || !_initialized) return;

			var notification = new JObject
			{
				["jsonrpc"] = "2.0",
				["method"] = method
			};
			if (@params != null)
				notification["params"] = @params;

			Send(notification.ToString(Formatting.None));
		}

		#endregion

		#region Initialization (called by McpToolRegistry)

		internal void MarkInitialized(string clientName, string clientVersion)
		{
			_clientName = clientName;
			_clientVersion = clientVersion;
			_initialized = true;
		}

		#endregion
	}
}
