using System;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Events;
using WebSocketSharp;
using WebSocketSharp.Server;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Server {
	public class Service : WebSocketBehavior {
		public WebSocket                            Server;
		public UnityEvent<Client>                   OnOpenCallback    = new();
		public UnityEvent<Client>                   OnCloseCallback   = new();
		public UnityEvent<Client, string, object[]> OnMessageCallback = new();

		public Client Client;

		protected override void OnOpen() {
			if (Server.IsDisposing) {
				Context.WebSocket.Close();
				return;
			}

			Client = new Client(this, Server);
			OnOpenCallback?.Invoke(Client);
		}

		protected override void OnClose(CloseEventArgs e) {
			if (Client == null) return;
			if (Server.IsDisposing) return;
			OnCloseCallback.Invoke(Client);
		}

		protected override void OnMessage(MessageEventArgs e)
			=> OnMessageSync(e).Forget();

		private async UniTask OnMessageSync(MessageEventArgs e) {
			if (Server.IsDisposing || !Server.IsRunning()) return;

			try {
				var json = JObject.Parse(e.Data);
				var ev   = json["event"]?.ToString();
				var data = json["args"] as JArray ?? new JArray();
				
				await UniTask.SwitchToMainThread();
				OnMessageCallback.Invoke(Client, ev, data.ToObject<object[]>() ?? Array.Empty<object>());
			} catch (Exception ex) {
				if (!Server.IsDisposing)
					Logger.LogError($"Error parsing message: {ex.Message}");
			}
		}

		protected override void OnError(ErrorEventArgs e) {
			if (!Server.IsDisposing) return;
			Logger.LogError(e.Exception);
		}
	}
}