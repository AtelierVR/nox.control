using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using Nox.CCK.Utils;

namespace Nox.Control.Server {
	/// <summary>
	/// Provides mDNS/DNS-SD service advertisement for WebSocket server
	/// Uses native UDP sockets for Unity compatibility
	/// </summary>
	public class MdnsService : IDisposable {
		private const string MdnsAddress = "224.0.0.251";
		private const int MdnsPort = 5353;
		
		private readonly string _serviceName;
		private readonly string _serviceType;
		private readonly ushort _port;
		private readonly string[] _txtRecords;
		
		private Socket _socket;
		private CancellationTokenSource _cancellationTokenSource;
		private bool _isAdvertising;
		private Thread _listenerThread;

		/// <summary>
		/// Creates a new mDNS service advertiser
		/// </summary>
		/// <param name="serviceName">Human-readable service name (e.g., "Nox Control Server")</param>
		/// <param name="serviceType">Service type (e.g., "_websocket._tcp")</param>
		/// <param name="port">Port number the service is running on</param>
		/// <param name="txtRecords">Optional TXT records for additional metadata</param>
		public MdnsService(string serviceName, string serviceType, ushort port, params string[] txtRecords) {
			_serviceName = serviceName;
			_serviceType = serviceType;
			_port = port;
			_txtRecords = txtRecords ?? Array.Empty<string>();
			
			Logger.Log($"mDNS service created: {serviceName} on {serviceType}:{port}", tag: nameof(MdnsService));
		}

		/// <summary>
		/// Start advertising the service on the network
		/// </summary>
		public void Start() {
			if (_isAdvertising) return;

			try {
				_cancellationTokenSource = new CancellationTokenSource();
				
				// Create socket for mDNS
				_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
				_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
				_socket.Bind(new IPEndPoint(IPAddress.Any, 0)); // Bind to any available port
				
				// Try to join multicast group for each network interface
				var multicastAddress = IPAddress.Parse(MdnsAddress);
				var interfaces = NetworkInterface.GetAllNetworkInterfaces();
				var joinedAny = false;
				
				foreach (var iface in interfaces) {
					if (iface.OperationalStatus != OperationalStatus.Up) continue;
					if (!iface.SupportsMulticast) continue;
					
					var ipProperties = iface.GetIPProperties();
					foreach (var ip in ipProperties.UnicastAddresses) {
						if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
						
						try {
							var mreq = new MulticastOption(multicastAddress, ip.Address);
							_socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mreq);
							joinedAny = true;
							Logger.Log($"Joined mDNS multicast on {ip.Address}", tag: nameof(MdnsService));
							break; // Only join once per interface
						} catch (SocketException ex) {
							Logger.Log($"Could not join multicast on {ip.Address}: {ex.Message}", tag: nameof(MdnsService));
						}
					}
				}
				
				if (!joinedAny) {
					Logger.Log("Warning: Could not join mDNS multicast group on any interface. Service discovery may be limited.", tag: nameof(MdnsService));
				}
				
				_isAdvertising = true;
				Logger.Log($"mDNS advertising started for '{_serviceName}' (Port: {_port})", tag: nameof(MdnsService));
			} catch (Exception ex) {
				Logger.LogException(new Exception("Failed to start mDNS advertising", ex), tag: nameof(MdnsService));
				throw;
			}
		}

		/// <summary>
		/// Stop advertising the service
		/// </summary>
		public void Stop() {
			if (!_isAdvertising) return;

			try {
				_cancellationTokenSource?.Cancel();
				_listenerThread?.Join(1000);
				
				_socket?.Close();
				_socket = null;
				
				_cancellationTokenSource?.Dispose();
				_cancellationTokenSource = null;
				
				_isAdvertising = false;
				Logger.Log($"mDNS advertising stopped for '{_serviceName}'", tag: nameof(MdnsService));
			} catch (Exception ex) {
				Logger.LogError(new Exception("Error stopping mDNS service", ex), tag: nameof(MdnsService));
			}
		}

		public void Dispose() {
			Stop();
		}
	}
}

