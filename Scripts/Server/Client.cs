using System;
using System.Net;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.SDK.Control;
using WebSocketSharp;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Server {
	public class Client : IClient {
		private readonly Service   _behavior;
		private readonly WebSocket _server;

		internal Client(Service behavior, WebSocket server) {
			_behavior = behavior;
			_server   = server;
		}

		public EndPoint GetEndPoint()
			=> _behavior?.Context?.UserEndPoint;

		public bool IsConnected()
			=> _behavior?.State == WebSocketState.Open;

		public UniTask Close() {
			if (!IsConnected())
				return UniTask.CompletedTask;
			_behavior?.Context?.WebSocket?.Close();
			return UniTask.CompletedTask;
		}

		public IServer GetServer()
			=> _server;

		public UniTask Send(string ev, params object[] args) {
			if (!IsConnected() || _server.IsDisposing)
				return UniTask.CompletedTask;

			try {
				var json = new JObject {
					["event"] = ev,
					["args"]  = JArray.FromObject(args)
				}.ToString(Formatting.None);

				_behavior.Context.WebSocket.Send(json);
			} catch (Exception ex) {
				Logger.LogWarning($"Error sending message: {ex.Message}");
			}

			return UniTask.CompletedTask;
		}
	}
}