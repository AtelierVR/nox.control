using System;
using System.Collections.Generic;
using System.Linq;
using Nox.CCK.Convertors;
using Nox.CCK.Settings;
using Nox.Control.Runtime.Handlers;
using Nox.Control.Runtime.Registers;
using Nox.Settings;
using UnityEngine;

namespace Nox.Control.Runtime.Settings {
	public class PermissionToggleSetting : ToggleHandler {
		private readonly string _clientId;
		private readonly string _permission;
		private readonly Action<string, string, bool> _onChanged;

		public PermissionToggleSetting(string clientId, string permission, string clientName, string permissionDescription, bool granted, Action<string, string, bool> onChanged) {
			_clientId = clientId;
			_permission = permission;
			_onChanged = onChanged;

			SetLabelKey("value", clientName, permissionDescription);
			if (granted)
				SetValue(true, notify: false);
		}

		public override string[] GetPath() => new[] { "permissions", _clientId, _permission };
		public override int GetOrder() => 0;

		protected override GameObject GetPrefab()
			=> Nox.Control.Runtime.Main.CoreAPI?.AssetAPI?.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		protected override void OnValueChanged(bool value) {
			_onChanged?.Invoke(_clientId, _permission, value);
		}
	}

	public class PermissionSettingsProvider {
		public IHandler[] GetPermissionHandlers() {
			var entries = RegistredManager.LoadAll();
			var handlers = new List<IHandler>();

			foreach (var entry in entries) {
				var clientName = ResolveName(entry.Name);
				foreach (var perm in entry.Permissions) {
					var granted = perm.Type == PermissionState.Granted;
					handlers.Add(new PermissionToggleSetting(entry.Id, perm.Id, clientName,
						RegisteredEntry.GetPermissionDescription(perm.Id), granted,
						(clientId, permission, value) => {
							if (value)
								RegistredManager.GrantPermission(clientId, permission);
							else
								RegistredManager.DenyPermission(clientId, permission);
						}));
				}
			}

			return handlers.ToArray();
		}

		public void DisposePermissionHandlers(IHandler[] handlers) {
			if (handlers == null) return;
			foreach (var h in handlers) {
				if (h is IDisposable d)
					d.Dispose();
			}
		}

		private static string ResolveName(TranslatedString ts) {
			if (ts == null) return "(unnamed)";
			var lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
			if (ts.TryGetValue(lang, out var v)) return v;
			if (ts.TryGetValue("en-US", out v)) return v;
			foreach (var kv in ts) return kv.Value;
			return "(unnamed)";
		}
	}
}
