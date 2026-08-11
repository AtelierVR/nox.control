using System;
using System.Linq;
using Nox.Settings;

namespace Nox.Control.Runtime {
	/// <summary>
	/// Manages the lifecycle of permission settings handlers in the nox.settings page.
	/// </summary>
	public static class PermissionSettingsManager {
		private static IHandler[] _handlers = Array.Empty<IHandler>();
		private static bool _refreshing;

		public static void Refresh() {
			if (_refreshing) return;
			_refreshing = true;
			try {
				Remove();
				var provider = new Settings.PermissionSettingsProvider();
				_handlers = provider.GetPermissionHandlers();
				if (_handlers.Length == 0) return;

				try {
					var settingApi = Main.CoreAPI?.ModAPI?.GetMod("settings")?.GetInstance<ISettingAPI>();
					if (settingApi != null) {
						foreach (var h in _handlers)
							settingApi.Add(h);
					}
				} catch {
					// Settings mod may not be loaded yet
				}
			} finally {
				_refreshing = false;
			}
		}

		public static void Remove() {
			if (_handlers.Length == 0) return;

			try {
				var settingApi = Main.CoreAPI?.ModAPI?.GetMod("settings")?.GetInstance<ISettingAPI>();
				if (settingApi != null) {
					foreach (var h in _handlers)
						settingApi.Remove(h.GetPath());
				}
			} catch {
				// Best effort cleanup
			}

			var provider = new Settings.PermissionSettingsProvider();
			provider.DisposePermissionHandlers(_handlers);
			_handlers = Array.Empty<IHandler>();
		}
	}
}
