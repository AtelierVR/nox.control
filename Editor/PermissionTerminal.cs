using System;
using System.Collections.Generic;
using System.Linq;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.Control.Runtime.Handlers;
using Nox.Control.Runtime.Registers;
using Nox.Editor.Panel;
using UnityEngine.UIElements;
using IPanel = Nox.Editor.Panel.IPanel;

namespace Nox.Control.Editor {
	public class PermissionTerminal : IEditorModInitializer, IPanel {
		private static readonly string[] PanelPath = { "control", "permissions" };
		internal IEditorModCoreAPI API;

		public void OnInitializeEditor(IEditorModCoreAPI api) => API = api;
		public void OnDisposeEditor() => API = null;

		public string[] GetPath() => PanelPath;
		public string GetLabel() => "Control/Permissions";

		internal PermissionTerminalInstance Instance;

		public IInstance[] GetInstances()
			=> Instance != null ? new IInstance[] { Instance } : Array.Empty<IInstance>();

		public IInstance Instantiate(IWindow window, Dictionary<string, object> data) {
			if (Instance != null)
				throw new InvalidOperationException("PermissionTerminal only supports a single instance.");
			return Instance = new PermissionTerminalInstance(this, window);
		}
	}

	public class PermissionTerminalInstance : IInstance {
		private readonly PermissionTerminal _panel;
		private readonly IWindow _window;

		private VisualElement _content;
		private VisualElement _list;
		private VisualElement _empty;
		private Button _refresh;

		public PermissionTerminalInstance(PermissionTerminal panel, IWindow window) {
			_panel = panel;
			_window = window;
		}

		public IPanel GetPanel() => _panel;
		public IWindow GetWindow() => _window;
		public string GetTitle() => "Permissions";

		public void OnDestroy() {
			_panel.Instance = null;
		}

		public VisualElement GetContent() {
			if (_content != null) return _content;

			var root = _panel.API.AssetAPI
				.GetAsset<VisualTreeAsset>("panels/permission-terminal.uxml")
				.CloneTree();

			_list    = root.Q<VisualElement>("list");
			_empty   = root.Q<VisualElement>("empty");
			_refresh = root.Q<Button>("refresh");

			_refresh?.RegisterCallback<ClickEvent>(_ => RefreshClients());

			RefreshClients();

			return _content = root;
		}

		private void RefreshClients() {
			_list?.Clear();

			var entries = RegistredManager.LoadAll();
			if (entries.Count == 0) {
				if (_empty != null) _empty.style.display = DisplayStyle.Flex;
				if (_list  != null) _list.style.display  = DisplayStyle.None;
				return;
			}

			if (_empty != null) _empty.style.display = DisplayStyle.None;
			if (_list  != null) _list.style.display  = DisplayStyle.Flex;

			var itemAsset = _panel.API.AssetAPI
				.GetAsset<VisualTreeAsset>("panels/permission-item.uxml");

			foreach (var entry in entries) {
				var item = itemAsset.CloneTree();

				var shortId = (entry.Id?.Length > 24 ? entry.Id[..24] + "\u2026" : entry.Id) ?? "?";
				item.Q<Label>("id").text = shortId;

				var authorized = entry.Permissions?.Any(p => p.Type == PermissionState.Granted) == true;
				var statusLabel = item.Q<Label>("status");
				statusLabel.text = authorized ? "\u2713 Authorized" : "\u26A0 Pending";
				statusLabel.style.color = authorized
					? new UnityEngine.Color(0.29f, 0.87f, 0.50f)
					: new UnityEngine.Color(0.98f, 0.75f, 0.14f);

				item.Q<Label>("name").text     = ResolveName(entry.Name);
				item.Q<Label>("endpoint").text = entry.Endpoint ?? "(unknown)";
				item.Q<Label>("last-seen").text = entry.LastConnectedAt == default
					? "never"
					: entry.LastConnectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

				// Permissions badges
				var permsContainer = item.Q<VisualElement>("perms-container");
				if (entry.Permissions?.Count > 0) {
					foreach (var p in entry.Permissions.OrderBy(p => p.Type).ThenBy(p => p.Id)) {
						var badge = new Label($"{p.Id}");
						badge.style.fontSize = 10;
						badge.style.paddingTop = 2;
						badge.style.paddingBottom = 2;
						badge.style.paddingLeft = 6;
						badge.style.paddingRight = 6;
						badge.style.marginRight = 4;
						badge.style.marginBottom = 2;
						badge.style.borderTopLeftRadius = 4;
						badge.style.borderTopRightRadius = 4;
						badge.style.borderBottomLeftRadius = 4;
						badge.style.borderBottomRightRadius = 4;

						switch (p.Type) {
							case PermissionState.Granted:
								badge.style.backgroundColor = new UnityEngine.Color(0.06f, 0.35f, 0.13f);
								badge.style.color = new UnityEngine.Color(0.29f, 0.87f, 0.50f);
								break;
							case PermissionState.Denied:
								badge.style.backgroundColor = new UnityEngine.Color(0.45f, 0.10f, 0.10f);
								badge.style.color = new UnityEngine.Color(0.97f, 0.44f, 0.44f);
								break;
							default:
								badge.style.backgroundColor = new UnityEngine.Color(0.15f, 0.15f, 0.18f);
								badge.style.color = new UnityEngine.Color(0.61f, 0.64f, 0.69f);
								break;
						}
						permsContainer.Add(badge);
					}
				}

				// Buttons
				item.Q<Button>("btn-grant")?.RegisterCallback<ClickEvent>(_ => {
					foreach (var p in entry.Permissions)
						p.Type = PermissionState.Granted;
					entry.LastConnectedAt = DateTime.UtcNow;
					RegistredManager.SaveEntryFile(entry);
					RegistredManager.NotifyEntryUpdated(entry.Id);
					RefreshClients();
				});

				item.Q<Button>("btn-close")?.RegisterCallback<ClickEvent>(_ => {
					var granted = entry.GetPermissionsByState(PermissionState.Granted);
					foreach (var p in granted)
						entry.SetPermission(p, PermissionState.Denied);
					RegistredManager.SaveEntryFile(entry);
					RegistredManager.NotifyEntryUpdated(entry.Id);
					RefreshClients();
				});

				item.Q<Button>("btn-revoke")?.RegisterCallback<ClickEvent>(_ => {
					var granted  = entry.GetPermissionsByState(PermissionState.Granted);
					var declared = entry.GetPermissionsByState(PermissionState.Declared);
					foreach (var p in granted)  entry.SetPermission(p, PermissionState.Denied);
					foreach (var p in declared) entry.SetPermission(p, PermissionState.Denied);
					RegistredManager.SaveEntryFile(entry);
					RegistredManager.NotifyEntryUpdated(entry.Id);
					RefreshClients();
				});

				item.Q<Button>("btn-forget")?.RegisterCallback<ClickEvent>(_ => {
					RegistredManager.DeleteEntryFile(entry.Id);
					RegistredManager.NotifyEntryUpdated(entry.Id);
					RefreshClients();
				});

				_list.Add(item);
			}
		}

		private static string ResolveName(Nox.CCK.Convertors.TranslatedString ts) {
			if (ts == null) return "(unnamed)";
			var lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
			if (ts.TryGetValue(lang, out var v)) return v;
			if (ts.TryGetValue("en-US", out v)) return v;
			foreach (var kv in ts) return kv.Value;
			return "(unnamed)";
		}
	}
}
