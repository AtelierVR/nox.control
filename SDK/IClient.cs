using System.Net;
using Cysharp.Threading.Tasks;

namespace Nox.SDK.Control {
	/// <summary>
	/// Represents a client connected to a server.
	/// </summary>
	public interface IClient {
		/// <summary>
		/// Gets the endpoint of the client.
		/// </summary>
		/// <returns></returns>
		public EndPoint GetEndPoint();

		/// <summary>
		/// Checks if the client is connected.
		/// </summary>
		/// <returns></returns>
		public bool IsConnected();

		/// <summary>
		/// Closes the client connection.
		/// </summary>
		/// <returns></returns>
		public UniTask Close();

		/// <summary>
		/// Gets the server instance the client is connected to.
		/// </summary>
		/// <returns></returns>
		public IServer GetServer();

		/// <summary>
		/// Sends an event to the client.
		/// </summary>
		/// <param name="ev"></param>
		/// <param name="args"></param>
		/// <returns></returns>
		public UniTask Send(string ev, params object[] args);
	}
}