using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Control;
using Nox.CCK.Utils;
using Logger = Nox.CCK.Utils.Logger;
using LogType = Nox.CCK.Utils.LogType;
using Object = UnityEngine.Object;

namespace Nox.Control.Runtime.Handlers  {
	[Serializable]
	public class LogEntryData {
		public string Type;
		public string Tag;
		public string Message;
		public long   Timestamp;
	}

	[Serializable]
	public class ProgressData {
		public bool   Active;
		public string Title;
		public string Message;
		public float  Progress;
	}

	public class LoggerHandler : IOperator {
		public string Name
			=> "logger_history";

		public string Description
			=> "Get log history since a given timestamp (Unix milliseconds).";

		public ISchema Schema => new InputSchema()
			.Property<long>("since", "Unix timestamp in milliseconds. Omit for all logs.");

		public async UniTask<IOutput> Execute(IInput args) {
			await UniTask.Yield();

			var sinceMs = args.Get<long>("since");
			var since = sinceMs > 0
				? DateTimeOffset.FromUnixTimeMilliseconds(sinceMs).UtcDateTime
				: DateTime.MinValue;

			var logs = Logger.History
				.Where(log => log.Timestamp >= since)
				.Select(log => new LogEntryData {
					Type      = log.Type.ToString().ToSnakeCase(),
					Tag       = log.Tag,
					Message   = StripRichText(log.Message),
					Timestamp = new DateTimeOffset(log.Timestamp).ToUnixTimeMilliseconds()
				}).ToArray();

			return OperatorOutput.Ok(logs);
		}

		private static string StripRichText(string msg) {
			if (string.IsNullOrEmpty(msg)) return msg;
			return System.Text.RegularExpressions.Regex.Replace(msg, @"<[^>]*>", "");
		}

		public void Listen() {
			Logger.OnProgress.AddListener(OnProgress);
			Logger.OnLog.AddListener(OnLog);
		}

		private void OnLog(LogType type, string tag, string message, Object context) {
			var entry = new LogEntryData {
				Type      = type.ToString().ToSnakeCase(),
				Tag       = tag,
				Message   = StripRichText(message),
				Timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()
			};
			foreach (var c in Main.Server.GetClients())
				c.Send("logger_log", entry)
				.Forget();
		}

		private void OnProgress(bool active, string title, string message, float progress) {
			var data = new ProgressData {
				Active   = active,
				Title    = title,
				Message  = message,
				Progress = progress
			};
			foreach (var c in Main.Server.GetClients())
				c.Send("logger_progress", data)
				.Forget();
		}

		public void Dispose() {
			Logger.OnProgress.RemoveListener(OnProgress);
			Logger.OnLog.RemoveListener(OnLog);
		}
	}
}
