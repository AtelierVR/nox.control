using System.Net;
using Nox.CCK.Convertors;

namespace Nox.Control.Runtime.Permissions {
	/// <summary>
	/// The decision outcome for a permission request.
	/// </summary>
	public enum PermissionDecision {
		/// <summary>Permission was granted.</summary>
		Granted,
		/// <summary>Permission was denied.</summary>
		Denied,
		/// <summary>Permission was revoked (all permissions removed).</summary>
		Revoked
	}

	/// <summary>
	/// Event arguments for when a permission decision has been made.
	/// </summary>
	public class PermissionDecisionEventArgs {
		/// <summary>The client identifier.</summary>
		public string ClientId { get; set; }

		/// <summary>The decision that was made.</summary>
		public PermissionDecision Decision { get; set; }

		/// <summary>The permissions affected by this decision.</summary>
		public string[] Permissions { get; set; }
	}

	/// <summary>
	/// Event arguments for when a websocket client requests permissions.
	/// </summary>
	public class PermissionRequestEventArgs {
		/// <summary>The client identifier.</summary>
		public string ClientId { get; set; }

		/// <summary>The human-readable name of the client.</summary>
		public TranslatedString ClientName { get; set; }

		/// <summary>The network endpoint of the client.</summary>
		public IPEndPoint Endpoint { get; set; }

		/// <summary>The permissions being requested.</summary>
		public string[] RequestedPermissions { get; set; }
	}
}
