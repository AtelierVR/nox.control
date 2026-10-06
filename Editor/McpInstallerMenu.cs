using System.Linq;
using Nox.Control.Runtime.Mcp;
using Nox.Editor.Panel;
using UnityEditor;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Editor {
	/// <summary>
	/// Menus of the MCP installer: open the panel, or configure every client in one click.
	/// <para>
	/// Priorities stay inside the [990, 1010] band so the "Nox/Control" sub-menu keeps its place
	/// next to the other Nox entries (see the menu priority notes of the repo).
	/// </para>
	/// </summary>
	public static class McpInstallerMenu {
		private const string Root = "Nox/Control/";

		private const int PanelPriority     = 1000;
		private const int InstallPriority   = 1001;
		private const int UninstallPriority = 1002;

		[MenuItem(Root + "MCP Installer…", false, PanelPriority)]
		private static void OpenPanel() {
			var panels = McpInstallerPanel.Api?.ModAPI?.GetMod("editor.panel")?.GetInstance<IPanelAPI>();
			var panel  = McpInstallerPanel.Current;

			if (panels == null || panel == null) {
				Logger.LogWarning("The Nox panel framework (nox.editor.panel) is not available.", tag: nameof(McpInstallerMenu));
				return;
			}

			panels.TryOpen(panel);
		}

		[MenuItem(Root + "Install MCP for detected clients", false, InstallPriority)]
		private static void InstallDetected() {
			var targets = McpClients.All().Where(target => target.Detected).ToArray();
			if (targets.Length == 0) {
				Logger.LogWarning("No MCP client detected on this machine.", tag: nameof(McpInstallerMenu));
				return;
			}

			foreach (var target in targets) {
				// Install and update in one pass: an up-to-date file is left untouched.
				if (McpInstaller.GetState(target, out _) == McpInstallState.UpToDate)
					continue;

				Report(McpInstaller.Install(target));
			}
		}

		[MenuItem(Root + "Uninstall MCP from all clients", false, UninstallPriority)]
		private static void UninstallAll() {
			foreach (var target in McpClients.All()) {
				if (!McpInstaller.IsInstalled(target, out _))
					continue;

				Report(McpInstaller.Uninstall(target));
			}
		}

		private static void Report(McpInstallResult result) {
			if (result.Ok)
				Logger.Log(result.ToString(), tag: nameof(McpInstallerMenu));
			else
				Logger.LogError(result.ToString(), tag: nameof(McpInstallerMenu));
		}
	}
}
