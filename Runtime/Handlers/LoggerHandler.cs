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

		public string[] RequiredPermissions => new[] { "logger:read" };

		public ISchema Schema => new InputSchema()
            .Property<long>("since", "Unix timestamp in milliseconds. Omit for all logs.")
            .WithPagination();

        public async UniTask<IOutput> Execute(IInput args) {
            await UniTask.Yield();

            var sinceMs = args.Get<long>("since");
            var since = sinceMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(sinceMs).UtcDateTime
                : DateTime.MinValue;

            var all = Logger.History
                .Where(log => log.Timestamp >= since)
                .Select(log => new LogEntryData {
                    Type      = log.Type.ToString().ToSnakeCase(),
                    Tag       = log.Tag,
                    Message   = StripRichText(log.Message),
                    Timestamp = new DateTimeOffset(log.Timestamp).ToUnixTimeMilliseconds()
                }).ToArray();

            // The output stays a bare array (backward compatibility): offset/limit only
            // slice the requested window.
            var logs = Pagination.Read(args).Apply(all, out _);
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

		/// <summary>
		/// Broadcasts a log line to the clients allowed to read the logs.
		/// <para>
		/// Never throws: the handler runs inside the <c>Logger.OnLog</c> event, so an exception here
		/// would break the caller (a NRE during shutdown aborted the server restart, see
		/// <c>Main.ReloadAsync</c>). With no server — startup, shutdown, failed start, editor — there
		/// is simply nothing to broadcast to.
		/// </para>
		/// </summary>
		private void OnLog(LogType type, string tag, string message, Object context) {
			try {
				var clients = Main.Server?.GetAuthorizedClients("logger:read");
				if (clients == null)
					return;

				var entry = new LogEntryData {
					Type      = type.ToString().ToSnakeCase(),
					Tag       = tag,
					Message   = StripRichText(message),
					Timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()
				};

				foreach (var c in clients)
					c.Send("logger_log", entry)
						.Forget();
			} catch {
				// Ignored on purpose: a broadcast failure must never break the code that logged.
			}
		}

		private void OnProgress(bool active, string title, string message, float progress) {
			try {
				var clients = Main.Server?.GetAuthorizedClients("logger:read");
				if (clients == null)
					return;

				var data = new ProgressData {
					Active   = active,
					Title    = title,
					Message  = message,
					Progress = progress
				};

				foreach (var c in clients)
					c.Send("logger_progress", data)
						.Forget();
			} catch {
				// Ignored on purpose (see OnLog).
			}
		}

		public void Dispose() {
			Logger.OnProgress.RemoveListener(OnProgress);
			Logger.OnLog.RemoveListener(OnLog);
		}
	}
}
