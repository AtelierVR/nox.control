using System;
using System.Linq;
using System.Net;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.SDK.Control;
using UnityEngine.Events;
using WebSocketSharp.Server;

namespace Nox.Control.Server {
	public class WebSocket : IServer {
		private readonly WebSocketServer _implement;
		private readonly string          _address;
		private readonly int             _port;
		private          bool            _isRunning;
		public           bool            IsDisposing;
		private          MdnsService     _mdnsService;
		private readonly bool            _enableMdns;
		private readonly string          _mdnsServiceName;

		public readonly UnityEvent<Client>                   OnClientConnected    = new();
		public readonly UnityEvent<Client>                   OnClientDisconnected = new();
		public readonly UnityEvent<Client, string, object[]> OnEventReceived      = new();

		public WebSocket(IPAddress address, int port, bool enableMdns = true, string mdnsServiceName = "Nox Control Server") {
			_address         = address.ToString();
			_port            = port;
			_enableMdns      = enableMdns;
			_mdnsServiceName = mdnsServiceName;
			_implement       = new WebSocketServer(address, _port);
			_implement.AddWebSocketService<Service>("/", OnServiceRegister);
		}

		private void OnServiceRegister(Service behavior) {
			behavior.Server            = this;
			behavior.OnOpenCallback    = OnClientConnected;
			behavior.OnCloseCallback   = OnClientDisconnected;
			behavior.OnMessageCallback = OnEventReceived;
		}

		public void Listen() {
			if (_isRunning) return;

			try {
				_implement.Start();
				_isRunning = true;
				Logger.Log($"Started on {_address}:{_port}", tag: nameof(WebSocket));

				// Start mDNS advertising if enabled
				if (_enableMdns) {
					try {
						_mdnsService = new MdnsService(
							_mdnsServiceName, "_nctrl._tcp", (ushort)_port,
							$"address={_address}",
							$"protocol=websocket",
							$"version=1.0"
						);
						_mdnsService.Start();
					} catch (Exception mdnsEx) {
						Logger.LogError(new Exception("Failed to start mDNS advertising (server will continue without it)", mdnsEx), tag: nameof(WebSocket));
					}
				}
			} catch (Exception ex) {
				Logger.LogException(new Exception($"Failed to start server on {_address}:{_port}", ex), tag: nameof(WebSocket));
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
					Logger.LogError(new Exception("Error stopping mDNS service", ex), tag: nameof(WebSocket));
				}

				// Disconnect all clients
				foreach (var client in GetClients())
					try {
						client.Close().Forget();
					} catch (Exception ex) {
						Logger.LogError(new Exception("Error disconnecting client", ex), tag: nameof(WebSocket));
					}

				// Stop the server
				try {
					_implement?.Stop();
				} catch (Exception ex) {
					Logger.LogError(new Exception("Error stopping WebSocket server", ex), tag: nameof(WebSocket));
				}

				Logger.Log($"Server stopped on {_address}:{_port}", tag: nameof(WebSocket));
			} catch (Exception ex) {
				Logger.LogError(new Exception("Error stopping server", ex), tag: nameof(WebSocket));
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
			=> _implement.WebSocketServices["/"]
				.Sessions.ActiveIDs
				.Select(id => _implement.WebSocketServices["/"].Sessions[id])
				.OfType<Service>()
				.Select(behavior => behavior.Client as IClient)
				.Where(client => client != null)
				.ToArray();
	}
}