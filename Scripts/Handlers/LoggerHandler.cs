using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Nox.SDK.Control;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using LogType = Nox.CCK.Utils.LogType;
using Object = UnityEngine.Object;

namespace Nox.Control.Handlers {
	public class LoggerHandler {
		public static void Listen() {
			Logger.OnProgress.AddListener(OnProgress);
			Logger.OnLog.AddListener(OnLog);
		}

		public static void Handle(IClient client, string eventName, params object[] args) {
			if (eventName != "logger:history") return;
			var timestamp = args.Length > 0 && args[0] is int
				? DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(args[0].ToString())).UtcDateTime
				: DateTime.MinValue;
			SendHistory(client, timestamp).Forget();
		}

		private static async UniTaskVoid SendHistory(IClient client, DateTime since) {
			var logs = Logger.History.Where(log => log.Timestamp >= since);
			await client.Send(
				"logger:history",
				logs.Select(
						log => new {
							type      = log.Type.ToString(),
							tag       = log.Tag,
							message   = log.Message,
							timestamp = new DateTimeOffset(log.Timestamp).ToUnixTimeMilliseconds()
						}
					)
					.ToArray<object>()
			);
		}

		
		private static void OnLog(LogType type, string message, string tag, Object context) {
			if (Main.Server == null || !Main.Server.IsRunning()) return;
			
			var clients = Main.Server.GetClients();
			foreach (var client in clients) {
				if (client.IsConnected())
					client.Send("logger:log", type.ToString(), tag, message).Forget();
			}
		}

		private static void OnProgress(bool active, string title, string message, float progress) {
			if (Main.Server == null || !Main.Server.IsRunning()) return;
			
			var clients = Main.Server.GetClients();
			foreach (var client in clients) {
				if (client.IsConnected())
					client.Send("logger:progress", active, title, message, progress).Forget();
			}
		}

		public static void Dispose() {
			Logger.OnProgress.RemoveListener(OnProgress);
			Logger.OnLog.RemoveListener(OnLog);
		}
	}
}