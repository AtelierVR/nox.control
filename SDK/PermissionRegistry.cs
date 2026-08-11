using System;
using System.Collections.Generic;
using System.Net;

namespace Nox.Control {
	/// <summary>
	/// Event arguments for a permission request.
	/// </summary>
	public class PermissionRequestEventArgs : EventArgs {
		public string ClientId { get; set; }
		public Dictionary<string, string> ClientName { get; set; } = new();
		public IPEndPoint Endpoint { get; set; }
		public string[] RequestedPermissions { get; set; } = Array.Empty<string>();
	}

	/// <summary>
	/// Event arguments for a permission decision.
	/// </summary>
	public class PermissionDecisionEventArgs : EventArgs {
		public string ClientId { get; set; }
		public PermissionDecision Decision { get; set; }
		public string[] Permissions { get; set; } = Array.Empty<string>();
	}

	/// <summary>
	/// The decision on a permission request.
	/// </summary>
	public enum PermissionDecision {
		Granted,
		Denied,
		Revoked
	}
}
