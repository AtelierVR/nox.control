using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using EmbedIO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.Control.Runtime.Server.Modules;
using Nox.Control.Server;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Runtime.Server {
	/// <summary>
	/// Control server: <b>a single port</b> for everything, through EmbedIO.
	/// <list type="bullet">
	///   <item><description>Events WebSocket on <c>/</c> (see <see cref="EventModule"/>)</description></item>
	///   <item><description>REST API on <c>/api/*</c> (see <see cref="ApiModule"/>)</description></item>
	///   <item><description>MCP JSON-RPC on <c>POST /mcp</c> (see <see cref="McpModule"/>)</description></item>
	/// </list>
	/// <para>
	/// The routes are disjoint, which avoids any resolution ambiguity between modules
	/// (unlike two servers on two ports, the client only has one address to know).
	/// </para>
	/// <para>
	/// EmbedIO listens on the socket itself (<c>HttpListenerMode.EmbedIO</c>): no more
	/// <c>HttpListener</c>, hence no longer needing the <c>netsh http add urlacl</c> that
	/// the previous separate HTTP API required.
	/// </para>
	/// </summary>
	public class ControlServer : IServer {
		private readonly string _address;
		private readonly int    _port;
		private readonly string _listenHost;
		private readonly bool   _enableMdns;
		private readonly string _mdnsServiceName;

		private readonly WebServer    _server;
		private readonly EventModule  _events;

		private MdnsService _mdnsService;
		private bool        _isRunning;

		/// <summary>True while shutting down: callbacks must then emit nothing.</summary>
		public bool IsDisposing;

		public readonly UnityEvent<Client>                   OnClientConnected    = new();
		public readonly UnityEvent<Client>                   OnClientDisconnected = new();

		/// <summary>
		/// Raised for every message received from a client. Kept for the mod's public API;
		/// as in the previous implementation, the events socket runs the operators
		/// itself and does not raise this event.
		/// </summary>
		public readonly UnityEvent<Client, string, object[]> OnEventReceived      = new();

		public bool EnableMcp { get; set; }

		public ControlServer(
			IPAddress address,
			int port,
			string listenHost = "localhost",
			bool enableMdns = true,
			string mdnsServiceName = "Nox Control Server",
			bool enableMcp = false
		) {
			_address         = address.ToString();
			_port            = port;
			_listenHost      = listenHost;
			_enableMdns      = enableMdns;
			_mdnsServiceName = mdnsServiceName;
			EnableMcp        = enableMcp;

			_events = new EventModule("/", this);

			_server = new WebServer(o => o
				.WithUrlPrefix($"http://{_listenHost}:{_port}/")
				.WithMode(HttpListenerMode.EmbedIO)
			);

			_server.WithModule(new PassThroughModule());
			_server.WithModule(new ApiModule("/api"));
			if (EnableMcp)
				_server.WithModule(new McpModule("/mcp"));
			_server.WithModule(_events);
		}

		public void Listen() {
			if (_isRunning) return;

			try {
				_server.Start();
				_isRunning = true;
				Logger.Log($"Started on {_address}:{_port}", tag: nameof(ControlServer));

				// Start mDNS advertising if enabled
				if (_enableMdns)
					try {
						_mdnsService = new MdnsService(
							_mdnsServiceName, "_nctrl._tcp", (ushort)_port,
							$"address={_address}",
							$"protocol=http",
							$"version=1.0"
						);
						_mdnsService.Start();
					} catch (Exception mdnsEx) {
						Logger.LogError(new Exception("Failed to start mDNS advertising (server will continue without it)", mdnsEx), tag: nameof(ControlServer));
					}
			} catch (Exception ex) {
				Logger.LogError(new Exception($"Failed to start server on {_address}:{_port}", ex), tag: nameof(ControlServer));
				throw;
			}
		}

		public void Dispose() {
			if (!_isRunning && !IsDisposing) return;
			IsDisposing = true;
			_isRunning  = false;

			try {
				// Stop mDNS advertising first
				try {
					_mdnsService?.Stop();
					_mdnsService?.Dispose();
					_mdnsService = null;
				} catch (Exception ex) {
					Logger.LogError(new Exception("Error stopping mDNS service", ex), tag: nameof(ControlServer));
				}

				// Disconnect all clients
				foreach (var client in GetClients())
					try {
						client.Close().Forget();
					} catch (Exception ex) {
						Logger.LogError(new Exception("Error disconnecting client", ex), tag: nameof(ControlServer));
					}

				// Stop the server: EmbedIO dispose releases the listening socket (the port is
				// given back to the system immediately, which avoids conflicts on the next reload).
				try {
					_server?.Dispose();
				} catch (Exception ex) {
					Logger.LogError(new Exception("Error stopping server", ex), tag: nameof(ControlServer));
				}

				Logger.Log($"Server stopped on {_address}:{_port}", tag: nameof(ControlServer));
			} catch (Exception ex) {
				Logger.LogError(new Exception("Error stopping server", ex), tag: nameof(ControlServer));
			} finally {
				IsDisposing = false;
			}
		}

		public UniTask Broadcast(string ev, params object[] args)
			=> UniTask.WhenAll(GetClients().Select(client => client.Send(ev, args)));

		public int GetPort()
			=> _port;

		public bool IsRunning()
			=> _isRunning;

		public IClient[] GetClients()
			=> _events.GetClients();

		/// <summary>
		/// Identified clients (validated "hello" handshake) holding the requested permission.
		/// </summary>
		public IClient[] GetAuthorizedClients(string permission)
			=> _events.GetAuthorizedClients(permission);

		// ── Helpers shared by the HTTP modules ─────────────────────────

		/// <summary>
		/// Requested path, relative to the module route. EmbedIO may provide the full
		/// or the relative path depending on the routing strategy: both are handled.
		/// </summary>
		internal static string RelativePath(IHttpContext context, string baseRoute) {
			var path = context.RequestedPath ?? "/";
			if (path.StartsWith(baseRoute, StringComparison.OrdinalIgnoreCase))
				path = path.Substring(baseRoute.Length);
			return path.Trim('/');
		}

		/// <summary>Writes a JSON response (CORS wide open, as the previous HTTP API did).</summary>
		internal static async Task SendJsonAsync(IHttpContext context, int statusCode, JToken data) {
			context.Response.StatusCode = statusCode;
			await context.SendStringAsync(data.ToString(Formatting.None), "application/json", Encoding.UTF8);
		}

		/// <summary>True when the request carries the expected API token (Bearer).</summary>
		internal static bool IsAuthorized(IHttpContext context) {
			var configuredToken = McpDispatcher.GetOrCreateToken();
			if (string.IsNullOrEmpty(configuredToken))
				return true;

			var authHeader = context.Request.Headers["Authorization"];
			return !string.IsNullOrEmpty(authHeader)
				&& string.Equals(authHeader, "Bearer " + configuredToken, StringComparison.Ordinal);
		}
	}
}
