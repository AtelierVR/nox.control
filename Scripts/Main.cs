using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Events;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Utils;
using Nox.Control.Handlers;
using Nox.Control.Server;
using Nox.SDK.Control;
using EventHandler = Nox.Control.Handlers.EventHandler;

namespace Nox.Control {
	public class Main : IMainModInitializer {
		internal static WebSocket      Server;
		internal static MainModCoreAPI CoreAPI;

		private EventSubscription[] _events = Array.Empty<EventSubscription>();

		public void OnInitializeMain(MainModCoreAPI api) {
			CoreAPI = api;
			ReloadAsync().Forget();
			LoggerHandler.Listen();
			_events = new[] { api.EventAPI.Subscribe(null, EventHandler.OnEvent) };
		}

		private static async UniTaskVoid ReloadAsync() {
			if (Server != null) {
				Server.Dispose();
				Server = null;

				// Attendre un peu pour s'assurer que toutes les tâches asynchrones sont terminées
				await UniTask.Delay(100);
			}

			var address       = IPAddress.Parse(Config.Load().Get("settings.control.address", "0.0.0.0"));
			var preferredPort = Config.Load().Get("settings.control.port", 8000);
			var port          = IsUsablePort(preferredPort, GetFreePort());

			Server = new WebSocket(address, port);

			Server.OnClientConnected.AddListener(OnClientConnected);
			Server.OnClientDisconnected.AddListener(OnClientDisconnected);
			Server.OnEventReceived.AddListener(OnDataReceived);

			try {
				Server.Listen();
				CoreAPI.LoggerAPI.Log($"Control Server started on port {Server.GetPort()}");
			} catch (SocketException ex) {
				CoreAPI.LoggerAPI.LogError($"Failed to start Control Server on port {port}: {ex.Message}");

				// Try to get a different free port and retry
				var freePort = GetFreePort();
				if (freePort != port) {
					CoreAPI.LoggerAPI.Log($"Retrying with alternative port {freePort}...");
					Server = new WebSocket(address, freePort);
					Server.OnClientConnected.AddListener(OnClientConnected);
					Server.OnClientDisconnected.AddListener(OnClientDisconnected);
					Server.OnEventReceived.AddListener(OnDataReceived);

					try {
						Server.Listen();
						CoreAPI.LoggerAPI.Log($"Control Server started on alternative port {Server.GetPort()}");
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
			ConfigHandler.Handle(arg0, arg1, arg2);
			HierarchyHandler.Handle(arg0, arg1, arg2);
			ModHandler.Handle(arg0, arg1, arg2);
			LoggerHandler.Handle(arg0, arg1, arg2);
			#if UNITY_EDITOR
			EditorHandler.Handle(arg0, arg1, arg2);
			#endif
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
				LoggerHandler.Dispose();

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
				CoreAPI = null;
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