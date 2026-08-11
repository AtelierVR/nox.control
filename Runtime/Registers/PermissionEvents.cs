using UnityEngine.Events;

namespace Nox.Control.Runtime.Permissions {
	/// <summary>
	/// Runtime-level UnityEvent hub for permission lifecycle events.
	/// Allows subscribers to use UnityEvent.AddListener/RemoveListener.
	/// </summary>
	public static class PermissionEvents {
		/// <summary>
		/// Fired when a new websocket client requests permissions.
		/// </summary>
		public static readonly UnityEvent<PermissionRequestEventArgs> OnPermissionRequest = new();

		/// <summary>
		/// Fired when a permission grant/deny/revoke decision has been made.
		/// </summary>
		public static readonly UnityEvent<PermissionDecisionEventArgs> OnPermissionDecision = new();
	}
}
