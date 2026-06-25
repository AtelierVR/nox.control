using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Nox.CCK.Control;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Events;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Utils;
using Nox.Control.Runtime.Handlers;
using Nox.Control.Runtime.Server;
using Nox.Control.Server;
using EventHandler = Nox.Control.Runtime.Handlers.EventHandler;

namespace Nox.Control.Runtime {
	public class Main : IMainModInitializer, IControlAPI {
		internal static WebSocket        Server;
		internal static HttpService      Http;
		internal static IMainModCoreAPI  CoreAPI;
		internal static Main             Instance;

		private OperationManager _manager;
		private EventSubscription[] _events = Array.Empty<EventSubscription>();

		private LoggerHandler _logger;

		private (uint, IOperator)[] _operators = Array.Empty<(uint, IOperator)>();

		public void OnInitializeMain(IMainModCoreAPI api) {
			CoreAPI  = api;
			Instance = this;
			_manager = new OperationManager();

			_logger = new LoggerHandler();

			// Register all operators (like terminal's _defaultCommands)
			_operators = new (uint, IOperator)[] {
				(0u, new ConfigGet()),
				(0u, new ConfigSet()),
				(0u, new ConfigReload()),
				(0u, new HierarchyScenesList()),
				(0u, new HierarchyScenesGet()),
				(0u, new ModList()),
				(0u, new ModGet()),
				(0u, _logger),
				#if UNITY_EDITOR
				(0u, new EditorGetPlayState()),
				(0u, new EditorPlay()),
				(0u, new EditorStop()),
				#endif
			};

			for (var i = 0; i < _operators.Length; i++)
				_operators[i].Item1 = _manager.Register(_operators[i].Item2);

			ReloadAsync().Forget();
			_logger.Listen();
			_events = new[] {
				api.EventAPI.Subscribe(null, EventHandler.OnEvent)
			};
		}

		private static async UniTaskVoid ReloadAsync() {
			if (Server != null) {
				Http?.Stop();
				Http = null;
				Server.Dispose();
				Server = null;

				// Attendre un peu pour s'assurer que toutes les tâches asynchrones sont terminées
				await UniTask.Delay(100);
			}

			var cfg            = Config.Load();
			var address        = IPAddress.Parse(cfg.Get("settings.control.address", IPAddress.Any.ToString()));
			var preferredPort  = cfg.Get("settings.control.port", 8000);
			var port           = IsUsablePort(preferredPort, GetFreePort());
			var mcpEnabled     = cfg.Get("settings.control.mcp", true);

			Server = new WebSocket(address, port, enableMcp: mcpEnabled);

			Server.OnClientConnected.AddListener(OnClientConnected);
			Server.OnClientDisconnected.AddListener(OnClientDisconnected);
			Server.OnEventReceived.AddListener(OnDataReceived);

			try {
				Server.Listen();
				CoreAPI.LoggerAPI.Log($"Control Server started on port {Server.GetPort()}");

				// Start HTTP API on port + 1
				var httpPort = Config.Load().Get("settings.control.http_port", port + 1);
				Http = new HttpService(httpPort);
				Http.Start();
			} catch (SocketException ex) {
				CoreAPI.LoggerAPI.LogError($"Failed to start Control Server on port {port}: {ex.Message}");

				// Try to get a different free port and retry
				var freePort = GetFreePort();
				if (freePort != port) {
					CoreAPI.LoggerAPI.Log($"Retrying with alternative port {freePort}...");
					Server = new WebSocket(address, freePort, enableMcp: mcpEnabled);
					Server.OnClientConnected.AddListener(OnClientConnected);
					Server.OnClientDisconnected.AddListener(OnClientDisconnected);
					Server.OnEventReceived.AddListener(OnDataReceived);

					try {
						Server.Listen();
						CoreAPI.LoggerAPI.Log($"Control Server started on alternative port {Server.GetPort()}");

						var httpPort = Config.Load().Get("settings.control.http_port", freePort + 1);
						Http = new HttpService(httpPort);
						Http.Start();
					} catch (SocketException retryEx) {
						CoreAPI.LoggerAPI.LogError($"Failed to start Control Server on alternative port {freePort}: {retryEx.Message}");
						Server = null;
						throw;
					}
				} else {
					Server = null;
					throw;
				}
			}
		}

		private static void OnDataReceived(IClient arg0, string arg1, params object[] arg2) {
			if (CoreAPI == null) return;
			List<object> data = new() { arg0, arg1 };
			data.AddRange(arg2);
			CoreAPI.EventAPI.Emit("control:data", data.ToArray());

			// Route to the matching operator via ExecuteAsync (same path as MCP)
			var jArgs = arg2.Length > 0
				? JToken.FromObject(arg2.Length == 1 ? arg2[0] : arg2)
				: JObject.Parse("{}");
			Instance.ExecuteAsync(arg1, jArgs)
				.ContinueWith(result => arg0.Send(arg1, result).Forget())
				.Forget();
		}

		private static void OnClientDisconnected(IClient arg0) {
			if (CoreAPI == null) return;
			CoreAPI.LoggerAPI.Log($"Client disconnected: {arg0.GetEndPoint()}");
			CoreAPI.EventAPI.Emit("control:disconnected", arg0);
		}

		private static void OnClientConnected(IClient arg0) {
			if (CoreAPI == null) return;
			CoreAPI.LoggerAPI.Log($"Client connected: {arg0.GetEndPoint()}");
			CoreAPI.EventAPI.Emit("control:connected", arg0);
		}

		public void OnDisposeMain() {
			try {
				foreach (var sub in _events)
					CoreAPI.EventAPI.Unsubscribe(sub);
				_logger?.Dispose();

				// Unregister all operators
				for (var i = 0; i < _operators.Length; i++)
					_manager.Unregister(_operators[i].Item1);
				_operators = Array.Empty<(uint, IOperator)>();

				Http?.Stop();
				Http = null;

				if (Server != null) {
					var port = Server.GetPort();

					// Remove all listeners before disposing to avoid calls during dispose
					Server.OnClientConnected.RemoveAllListeners();
					Server.OnClientDisconnected.RemoveAllListeners();
					Server.OnEventReceived.RemoveAllListeners();

					Server.Dispose();
					CoreAPI?.LoggerAPI.Log($"Control Server stopped on port {port}");
					Server = null;
				}
			} catch (Exception ex) {
				CoreAPI?.LoggerAPI.LogError($"Error disposing Control Server: {ex.Message}");
			} finally {
				CoreAPI  = null;
				Instance = null;
				_manager = null;
			}
		}

		#region IControlAPI

		public IOperator[] GetRegistered()
			=> _manager.Operators
				.ConvertAll(o => o.Item2)
				.ToArray();

		public uint Register(IOperator op)
			=> _manager.Register(op);

		public void Unregister(uint id)
			=> _manager.Unregister(id);

		#endregion

		public async UniTask<JToken> ExecuteAsync(string name, JToken args) {
			var op = _manager.Operators
				.Select(o => o.Item2)
				.FirstOrDefault(o => o.Name == name) 
				?? throw new KeyNotFoundException($"Operator '{name}' not found");

            try {
				var input = new OperatorInput(args);
				var output = await op.Execute(input);
				return JObject.FromObject(output);
			} catch (Exception ex) {
				Logger.LogError($"Operator '{name}' failed: {ex.Message}", tag: nameof(OperationManager));
				return JObject.FromObject(new { error = ex.Message });
			}
		}

		private static int IsUsablePort(int port, int fallbackPort) {
			try {
				var listener = new TcpListener(IPAddress.Any, port);
				listener.Start();
				listener.Stop();
				return port;
			} catch (SocketException ex) {
				Logger.LogWarning($"Port {port} is not available ({ex.Message}), using fallback port {fallbackPort}");
				return fallbackPort;
			} catch (Exception ex) {
				Logger.LogWarning($"Unable to test port {port} ({ex.Message}), using fallback port {fallbackPort}");
				return fallbackPort;
			}
		}

		private static int GetFreePort() {
			try {
				var listener = new TcpListener(IPAddress.Any, 0);
				listener.Start();
				var port = ((IPEndPoint)listener.LocalEndpoint).Port;
				listener.Stop();
				return port;
			} catch (Exception ex) {
				Logger.LogError($"Failed to get free port: {ex.Message}");
				// Return a high port number as last resort
				return 8000 + new Random().Next(1000, 9999);
			}
		}
	}
}