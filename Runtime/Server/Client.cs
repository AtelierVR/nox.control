using System;
using System.Net;
using System.Net.WebSockets;
using Cysharp.Threading.Tasks;
using EmbedIO.WebSockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.Control.Runtime.Server.Modules;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Runtime.Server {
	/// <summary>
	/// "Client" view of a WebSocket connection on the control server.
	/// <para>
	/// Unlike the previous implementation (one websocket-sharp behavior per socket),
	/// the object no longer carries the state: it only relays to the module
	/// <see cref="EventModule"/> and its <see cref="IWebSocketContext"/>.
	/// </para>
	/// </summary>
	public class Client : IClient {
		private readonly EventModule _service;

		internal readonly IWebSocketContext Context;

		internal Client(EventModule service, IWebSocketContext context) {
			_service = service;
			Context  = context;
		}

		public EndPoint GetEndPoint()
			=> Context?.RemoteEndPoint;

		public bool IsConnected()
			=> Context?.WebSocket?.State == WebSocketState.Open;

		public UniTask Close() {
			if (!IsConnected())
				return UniTask.CompletedTask;

			_service.CloseAsync(this).AsUniTask().Forget();
			return UniTask.CompletedTask;
		}

		public IServer GetServer()
			=> _service.Server;

		public UniTask Send(string ev, params object[] args) {
			if (!IsConnected() || _service.Server.IsDisposing)
				return UniTask.CompletedTask;

			try {
				var json = new JObject {
					["event"] = ev,
					["args"]  = JArray.FromObject(args)
				}.ToString(Formatting.None);

				_service.SendToAsync(Context, json).AsUniTask().Forget();
			} catch (Exception ex) {
				Logger.LogWarning($"Error sending message: {ex.Message}");
			}

			return UniTask.CompletedTask;
		}
	}
}
