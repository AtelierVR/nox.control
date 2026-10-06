using System;
using System.Collections.Generic;
using System.Linq;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.Control.Runtime;
using Nox.Control.Runtime.Mcp;
using Nox.Editor.Panel;
using UnityEditor;
using UnityEngine.UIElements;
using IPanel = Nox.Editor.Panel.IPanel;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Editor {
	/// <summary>
	/// "MCP Installer" panel: writes the <c>nox</c> MCP server entry into the configuration file of
	/// every supported client (VS Code, Claude Desktop/Code, Gemini CLI, Cursor, Windsurf, Cline…).
	///
	/// <para>
	/// The layout lives in <c>panels/mcp-installer.uxml</c> (one row per client comes from
	/// <c>panels/mcp-client-item.uxml</c>) and uses only the shared Nox styles: the nox.cck panel
	/// style (<c>nox.loader/Resources/nox.cck.style.uss</c>) for the sections and foldouts, and
	/// <c>nox.cck/Resources/nox.style.uss</c> for the utility classes, the badges and the status dots.
	/// Only the values are written from code, so the panel stays consistent with the other Nox editors.
	/// </para>
	/// </summary>
	public class McpInstallerPanel : IEditorModInitializer, IPanel {
		private static readonly string[] PanelPath = { "control", "mcp" };

		/// <summary>Panel instances are created by the framework: the entry points keep them here.</summary>
		internal static McpInstallerPanel Current;
		internal static IEditorModCoreAPI  Api;

		internal McpInstallerPanelInstance Instance;

		public void OnInitializeEditor(IEditorModCoreAPI api) {
			Api     = api;
			Current = this;
		}

		public void OnDisposeEditor() {
			Instance?.OnDestroy();
			Api     = null;
			Current = null;
		}

		public void OnUpdateEditor()
			=> Instance?.OnUpdate();

		public string[] GetPath() => PanelPath;

		/// <summary>
		/// Menu label: <c>GetLabel()</c> is a '/'-separated path in the Nox panel tree, so the panel
		/// lands in the same "Control" folder as the permissions panel.
		/// </summary>
		public string GetLabel() => "Control/MCP Installer";

		public IInstance[] GetInstances()
			=> Instance != null ? new IInstance[] { Instance } : Array.Empty<IInstance>();

		public IInstance Instantiate(IWindow window, Dictionary<string, object> data) {
			if (Instance != null)
				throw new InvalidOperationException("McpInstallerPanel only supports a single instance.");

			return Instance = new McpInstallerPanelInstance(this, window);
		}
	}

	public class McpInstallerPanelInstance : IInstance {
		/// <summary>Enough to follow the play mode toggle without re-reading the files every frame.</summary>
		private const double RefreshInterval = 1.5;

		private readonly McpInstallerPanel _panel;
		private readonly IWindow           _window;

		private VisualElement _content;
		private VisualElement _clientsList;
		private VisualElement _disabledNotice;
		private VisualElement _serverDot;
		private Label         _noClients;
		private Label         _serverState;
		private TextField     _endpointField;
		private TextField     _tokenField;
		private Button        _tokenToggle;
		private IntegerField  _portField;
		private TextField     _hostField;
		private Toggle        _mcpToggle;
		private Label         _configState;

		private readonly List<ClientRow> _rows = new();

		private double _lastRefresh = double.MinValue;
		private bool   _revealToken;
		private string _url;
		private string _token;

		public McpInstallerPanelInstance(McpInstallerPanel panel, IWindow window) {
			_panel  = panel;
			_window = window;
		}

		public IPanel GetPanel()  => _panel;
		public IWindow GetWindow() => _window;
		public string GetTitle()  => "MCP Installer";

		public void OnDestroy()
			=> _panel.Instance = null;

		public void OnFocus()
			=> Refresh();

		public IToolOption[] GetOptions() => new IToolOption[] {
			new DefaultToolOption("Refresh", Refresh, "Re-read the endpoint and the client configuration files."),
			new DefaultToolOption("Install detected", () => { InstallForDetected(); Refresh(); },
				"Add the nox server to every client installed on this machine (updates the outdated ones)."),
			new DefaultToolOption("Uninstall all", () => { UninstallAll(); Refresh(); },
				"Remove the nox server from every client that declares it.")
		};

		public void OnUpdate() {
			if (_content == null)
				return;

			if (EditorApplication.timeSinceStartup - _lastRefresh < RefreshInterval)
				return;

			_lastRefresh = EditorApplication.timeSinceStartup;
			RefreshServer();
			RefreshClients();
		}

		#region Content

		public VisualElement GetContent() {
			if (_content != null)
				return _content;

			var asset = McpInstallerPanel.Api?.AssetAPI.GetAsset<VisualTreeAsset>("panels/mcp-installer.uxml");
			if (asset == null) {
				_content = new VisualElement();
				_content.Add(new Label("panels/mcp-installer.uxml could not be loaded."));
				return _content;
			}

			_content = asset.CloneTree();
			_content.AddToClassList("flex-grow");

			_endpointField  = _content.Q<TextField>("endpoint");
			_tokenField     = _content.Q<TextField>("token");
			_tokenToggle    = _content.Q<Button>("toggle-token");
			_serverDot      = _content.Q<VisualElement>("server-dot");
			_serverState    = _content.Q<Label>("server-state");
			_disabledNotice = _content.Q<VisualElement>("mcp-disabled");
			_clientsList    = _content.Q<VisualElement>("clients-list");
			_noClients      = _content.Q<Label>("no-clients");
			_portField      = _content.Q<IntegerField>("config-port");
			_hostField      = _content.Q<TextField>("config-host");
			_mcpToggle      = _content.Q<Toggle>("config-mcp");
			_configState    = _content.Q<Label>("config-state");

			HookActions();
			BuildClientRows();

			LoadConfigFields();
			Refresh();
			return _content;
		}

		private void HookActions() {
			Hook("copy-endpoint", () => Copy(_url, "Endpoint URL copied."));
			Hook("copy-token", () => Copy(_token, "Access token copied."));

			Hook("save-config", SaveConfig);

			// The warning shortcut enables /mcp through the same path as the Config toggle, so the
			// server is restarted and the module is actually mounted.
			Hook("enable-mcp", () => {
				_mcpToggle?.SetValueWithoutNotify(true);
				SaveConfig();
			});

			// A plain button instead of a Toggle: its width never changes with the label, so the row
			// cannot shift around while the panel is resized.
			if (_tokenToggle != null)
				_tokenToggle.clicked += () => {
					_revealToken = !_revealToken;
					UpdateTokenDisplay();
				};
		}

		/// <summary>Wires a button of the panel; a missing name is logged instead of throwing.</summary>
		private void Hook(string name, Action action) {
			var button = _content.Q<Button>(name);
			if (button == null) {
				Logger.LogError($"Button '{name}' is missing from panels/mcp-installer.uxml.", tag: nameof(McpInstallerPanel));
				return;
			}

			button.clicked += action;
		}

		private void BuildClientRows() {
			var item = McpInstallerPanel.Api?.AssetAPI.GetAsset<VisualTreeAsset>("panels/mcp-client-item.uxml");
			if (item == null || _clientsList == null) {
				Logger.LogError("panels/mcp-client-item.uxml could not be loaded.", tag: nameof(McpInstallerPanel));
				return;
			}

			_clientsList.Clear();
			_rows.Clear();

			foreach (var target in McpClients.All()) {
				var root = item.CloneTree();

				var row = new ClientRow {
					Target    = target,
					Root      = root,
					Name      = root.Q<Label>("name"),
					Status    = root.Q<Label>("status"),
					Detected  = root.Q<Label>("detected"),
					Path      = root.Q<Label>("path"),
					Hint      = root.Q<Label>("hint"),
					Install   = root.Q<Button>("install"),
					Uninstall = root.Q<Button>("uninstall")
				};

				row.Name.text = target.Name;
				row.Path.text = McpInstaller.DisplayPath(target);
				row.Hint.text = target.Docs;

				row.Install.clicked   += () => { Install(row); Refresh(); };
				row.Uninstall.clicked += () => { Uninstall(row); Refresh(); };

				var copy   = root.Q<Button>("copy");
				var reveal = root.Q<Button>("reveal");
				if (copy != null)
					copy.clicked += () => Copy(McpInstaller.Snippet(target), $"{target.Name}: entry copied.");
				if (reveal != null)
					reveal.clicked += () => Reveal(target);

				_clientsList.Add(root);
				_rows.Add(row);
			}

			if (_noClients != null)
				_noClients.EnableInClassList("hidden", _rows.Count(t => t.Target.Detected) > 0);
		}

		#endregion

		#region Refresh

		private void Refresh() {
			if (_content == null)
				return;

			RefreshServer();
			RefreshClients();
		}

		/// <summary>
		/// Fills the Config fields from the Nox config. Called on open / explicit refresh only (not
		/// from the update loop), so the values being typed are never overwritten.
		/// </summary>
		private void LoadConfigFields() {
			_portField?.SetValueWithoutNotify(ControlConfigs.Port);
			_hostField?.SetValueWithoutNotify(ControlConfigs.ListenHost);
			_mcpToggle?.SetValueWithoutNotify(ControlConfigs.McpEnabled);
		}

		/// <summary>
		/// Writes the Config fields to the Nox config and restarts the control server so the new
		/// values apply immediately (the port and the /mcp module are only read at startup).
		/// </summary>
		private void SaveConfig() {
			ControlConfigs.Port       = _portField?.value ?? ControlConfigs.Port;
			ControlConfigs.ListenHost = _hostField?.value ?? ControlConfigs.ListenHost;
			ControlConfigs.McpEnabled = _mcpToggle?.value ?? ControlConfigs.McpEnabled;

			var restarted = Nox.Control.Runtime.Main.RestartIfRunning();

			Logger.Log(
				$"MCP config saved (port {ControlConfigs.Port}, host {ControlConfigs.ListenHost}, mcp {ControlConfigs.McpEnabled})"
				+ (restarted ? ": control server restarted." : ": applied when the server starts."),
				tag: nameof(McpInstallerPanel)
			);

			LoadConfigFields();
			Refresh();
		}

		private void RefreshServer() {
			_url   = McpEndpoint.Url;
			_token = McpEndpoint.Token;

			_endpointField?.SetValueWithoutNotify(_url);
			UpdateTokenDisplay();

			var running = McpEndpoint.IsRunning;
			_serverDot?.EnableInClassList("is-on", running);
			_serverDot?.EnableInClassList("is-off", !running);
			_serverDot?.EnableInClassList("is-idle", false);

			if (_serverState != null)
				_serverState.text = running
					? $"Control server: listening on port {McpEndpoint.Port} (REST /api, MCP /mcp, WebSocket)."
					: $"Control server: not running — the URL uses the configured port ({McpEndpoint.Port}). Start the game to use the live port.";

			_disabledNotice?.EnableInClassList("hidden", ControlConfigs.McpEnabled);

			if (_configState != null) {
				var configured = ControlConfigs.Port;
				_configState.text = !running
					? "Server not running: these values apply at the next start."
					: McpEndpoint.Port != configured
						? $"Running on port {McpEndpoint.Port} (the configured port {configured} is busy)."
						: $"Running on port {McpEndpoint.Port}.";
			}
		}

		private void UpdateTokenDisplay() {
			_tokenField?.SetValueWithoutNotify(_revealToken ? _token : new string('\u2022', 24));

			if (_tokenToggle != null)
				_tokenToggle.text = _revealToken ? "Hide" : "Show";
		}

		private void RefreshClients() {
			foreach (var row in _rows) {
				var state    = McpInstaller.GetState(row.Target, out var detail);
				var detected = row.Target.Detected;

				// A client that is not installed on this machine is kept visible but dimmed.
				row.Root.EnableInClassList("opacity-50", !detected);

				if (row.Install != null) {
					row.Install.text = state switch {
						McpInstallState.UpToDate => "Up to date",
						McpInstallState.Outdated => "Update",
						_                        => "Install"
					};

					// Nothing to do when the file already matches the running endpoint and token.
					row.Install.SetEnabled(state != McpInstallState.UpToDate);
					row.Install.tooltip = detail;
				}

				row.Uninstall?.SetEnabled(state is McpInstallState.UpToDate or McpInstallState.Outdated);

				if (row.Status != null) {
					row.Status.text = state switch {
						McpInstallState.UpToDate => "up to date",
						McpInstallState.Outdated => "update available",
						McpInstallState.Unreadable => "unreadable",
						_                          => "not configured"
					};

					row.Status.EnableInClassList("badge-success", state == McpInstallState.UpToDate);
					row.Status.EnableInClassList("badge-warning", state == McpInstallState.Outdated);
					row.Status.EnableInClassList("badge-danger", state == McpInstallState.Unreadable);
					row.Status.EnableInClassList("badge-muted", state == McpInstallState.NotInstalled);
					row.Status.tooltip = detail ?? (state == McpInstallState.UpToDate
						? "Matches the running endpoint and access token."
						: null);
				}

				if (row.Detected != null) {
					row.Detected.text = detected ? "detected" : "absent";
					row.Detected.EnableInClassList("badge-info", detected);
					row.Detected.EnableInClassList("badge-muted", !detected);
				}

				if (row.Path != null)
					row.Path.tooltip = row.Target.ConfigPath;
			}
		}

		#endregion

		#region Actions

		private void Install(ClientRow row) {
			var result = McpInstaller.Install(row.Target);
			Log(result.ToString(), result.Ok);
		}

		private void Uninstall(ClientRow row) {
			var result = McpInstaller.Uninstall(row.Target);
			Log(result.ToString(), result.Ok);
		}

		private void InstallForDetected() {
			foreach (var row in _rows.Where(row => row.Target.Detected)) {
				// Skip the clients whose file already matches: only install or update what is needed.
				if (McpInstaller.GetState(row.Target, out _) == McpInstallState.UpToDate)
					continue;

				Install(row);
			}
		}

		private void UninstallAll() {
			foreach (var row in _rows.Where(r => McpInstaller.IsInstalled(r.Target, out _)))
				Uninstall(row);
		}

		private static void Reveal(McpClientTarget target) {
			if (target.Exists) {
				EditorUtility.RevealInFinder(target.ConfigPath);
				return;
			}

			var directory = System.IO.Path.GetDirectoryName(target.ConfigPath);
			if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory))
				EditorUtility.RevealInFinder(directory);
		}

		private void Copy(string value, string message) {
			EditorGUIUtility.systemCopyBuffer = value;
			Log(message);
		}

		/// <summary>
		/// Install/uninstall results go to the Nox console (the panel keeps no log list): the
		/// verbose message of <see cref="McpInstallResult.ToString"/> carries the file and the state.
		/// </summary>
		private static void Log(string message, bool success = true) {
			if (success)
				Logger.Log(message, tag: nameof(McpInstallerPanel));
			else
				Logger.LogError(message, tag: nameof(McpInstallerPanel));
		}

		#endregion

		/// <summary>One client card, kept to update its state without rebuilding the tree.</summary>
		private sealed class ClientRow {
			public McpClientTarget Target;
			public VisualElement   Root;
			public Label           Name;
			public Label           Status;
			public Label           Detected;
			public Label           Path;
			public Label           Hint;
			public Button          Install;
			public Button          Uninstall;
		}
	}
}
