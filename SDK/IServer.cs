using Cysharp.Threading.Tasks;

namespace Nox.SDK.Control {
	/// <summary>
	/// Represents a server that can manage client connections.
	/// </summary>
	public interface IServer {
		/// <summary>
		/// Checks if the server is currently running.
		/// </summary>
		/// <returns></returns>
		public bool IsRunning();

		/// <summary>
		/// Starts the server.
		/// </summary>
		public void Listen();

		/// <summary>
		/// Stops the server and cleans up resources.
		/// </summary>
		public void Dispose();

		/// <summary>
		/// Gets the list of connected clients.
		/// </summary>
		/// <returns></returns>
		public IClient[] GetClients();

		/// <summary>
		/// Sends an event to all connected clients.
		/// </summary>
		/// <param name="ev"></param>
		/// <param name="args"></param>
		/// <returns></returns>
		public UniTask Broadcast(string ev, params object[] args);

		/// <summary>
		/// Gets the port number the server is listening on.
		/// </summary>
		/// <returns></returns>
		public int GetPort();
	}
}