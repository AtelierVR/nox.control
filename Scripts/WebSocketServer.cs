using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.CCK.Utils;
using Nox.SDK.Control;
using UnityEngine.Events;

namespace Nox.Control {
	public class WebSocketServer : IServer {
		private readonly TcpListener             _listener;
		private readonly List<WebSocketClient>   _clients = new();
		private          CancellationTokenSource _cancellationTokenSource;
		private          bool                    _isRunning;

		public readonly UnityEvent<WebSocketClient>                   OnClientConnected    = new();
		public readonly UnityEvent<WebSocketClient>                   OnClientDisconnected = new();
		public readonly UnityEvent<WebSocketClient, string, object[]> OnEventReceived      = new();

		public WebSocketServer(IPAddress address, int port)
			=> _listener = new TcpListener(address, port);

		public void Listen() {
			if (_isRunning) return;

			try {
				_listener.Start();
				_isRunning               = true;
				_cancellationTokenSource = new CancellationTokenSource();
				AcceptClientsAsync(_cancellationTokenSource.Token).Forget();
				Logger.Log($"WebSocket Server started on port {GetPort()}");
			} catch (SocketException ex) {
				Logger.LogError($"Failed to start WebSocket server on port {GetPort()}: {ex.Message}");
				throw;
			}
		}

		public void Dispose() {
			if (!_isRunning) return;
			_isRunning = false;

			try {
				var port = GetPort();
				_cancellationTokenSource?.Cancel();
				_cancellationTokenSource?.Dispose();

				// Disconnect all clients
				foreach (var client in _clients.ToArray())
					client.Disconnect();
				_clients.Clear();

				// Stop the listener
				_listener?.Stop();
				Logger.Log($"WebSocket Server stopped on port {port}");
			} catch (Exception ex) {
				Logger.LogError($"Error stopping WebSocket server: {ex.Message}");
			}
		}

		public UniTask Broadcast(string ev, params object[] args)
			=> UniTask.WhenAll(_clients.Select(client => client.Send(ev, args)));

		public int GetPort()
			=> (_listener.LocalEndpoint as IPEndPoint)?.Port ?? -1;

		public bool IsRunning()
			=> _isRunning;

		public IClient[] GetClients()
			=> _clients.Cast<IClient>().ToArray();

		private async UniTaskVoid AcceptClientsAsync(CancellationToken cancellationToken) {
			while (_isRunning && !cancellationToken.IsCancellationRequested) {
				try {
					var tcpClient = await _listener.AcceptTcpClientAsync();
					var wsClient  = new WebSocketClient(tcpClient, this);
					_clients.Add(wsClient);

					HandleClientAsync(wsClient, cancellationToken).Forget();
				} catch (Exception ex) {
					if (_isRunning) {
						Logger.LogError($"Error accepting client: {ex.Message}");
					}
				}
			}
		}

		private async UniTaskVoid HandleClientAsync(WebSocketClient client, CancellationToken cancellationToken) {
			try {
				// Perform WebSocket handshake
				if (!await client.PerformHandshakeAsync(cancellationToken)) {
					client.Disconnect();
					_clients.Remove(client);
					return;
				}

				OnClientConnected?.Invoke(client);

				// Handle messages
				while (client.IsConnected() && _isRunning && !cancellationToken.IsCancellationRequested) {
					var message = await client.ReceiveMessageAsync(cancellationToken);
					if (message != null) {
						var json = JObject.Parse(message);
						var ev   = json["event"]?.ToString();
						var data = json["args"] as JArray ?? new JArray();

						OnEventReceived?.Invoke(client, ev, data.ToObject<object[]>() ?? Array.Empty<object>());
					} else {
						break;
					}
				}
			} catch (Exception ex) {
				Logger.LogException(ex);
			} finally {
				OnClientDisconnected?.Invoke(client);
				client.Disconnect();
				_clients.Remove(client);
			}
		}
	}

	public class WebSocketClient : IClient {
		private readonly TcpClient       _tcpClient;
		private readonly WebSocketServer _server;
		private readonly NetworkStream   _stream;
		private          bool            _isHandshakeComplete;
		private readonly List<byte>      _receiveBuffer = new();
		private readonly EndPoint        _endPoint;

		internal WebSocketClient(TcpClient tcpClient, WebSocketServer server) {
			_tcpClient = tcpClient;
			_server    = server;
			_stream    = tcpClient.GetStream();
			try {
				_endPoint = tcpClient.Client.RemoteEndPoint;
			} catch {
				_endPoint = null;
			}
		}

		public EndPoint GetEndPoint()
			=> _endPoint;

		public bool IsConnected()
			=> _tcpClient?.Connected ?? false;

		public UniTask Close() {
			Disconnect();
			return UniTask.CompletedTask;
		}

		public IServer GetServer()
			=> _server;

		public UniTask Send(string ev, params object[] args)
			=> SendMessageAsync(
				new JObject {
					["event"] = ev,
					["args"]  = JArray.FromObject(args)
				}.ToString(Formatting.None)
			);

		public void Disconnect() {
			try {
				_stream?.Close();
				_tcpClient?.Close();
			} catch (Exception ex) {
				Logger.LogError($"Error disconnecting client: {ex.Message}");
			}
		}

		internal async UniTask<bool> PerformHandshakeAsync(CancellationToken cancellationToken) {
			try {
				// Wait for data to be available
				while (!_stream.DataAvailable && !cancellationToken.IsCancellationRequested)
					await UniTask.Delay(10, cancellationToken: cancellationToken);

				// Wait for at least 3 bytes (GET)
				while (_tcpClient.Available < 3 && !cancellationToken.IsCancellationRequested)
					await UniTask.Delay(10, cancellationToken: cancellationToken);

				var bytes = new byte[_tcpClient.Available];
				_ = await _stream.ReadAsync(bytes, 0, bytes.Length, cancellationToken);
				var request = Encoding.UTF8.GetString(bytes);

				if (!Regex.IsMatch(request, "^GET", RegexOptions.IgnoreCase))
					return false;

				var swk                  = Regex.Match(request, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
				var swkAndSalt           = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
				var swkAndSaltSha1       = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swkAndSalt));
				var swkAndSaltSha1Base64 = Convert.ToBase64String(swkAndSaltSha1);

				var dict = new Dictionary<string, string> {
					{ "HTTP/1.1 101 Switching Protocols", "" },
					{ "Connection", "Upgrade" },
					{ "Upgrade", "websocket" },
					{ "Sec-WebSocket-Accept", swkAndSaltSha1Base64 }
				};

				var response = Encoding.UTF8.GetBytes(dict.Aggregate("", (current, kvp) => current + $"{kvp.Key}: {kvp.Value}\r\n") + "\r\n");

				await _stream.WriteAsync(response, 0, response.Length, cancellationToken);
				_isHandshakeComplete = true;
				return true;
			} catch (Exception ex) {
				Logger.LogError($"Error during handshake: {ex.Message}");
				return false;
			}
		}

		internal async UniTask<string> ReceiveMessageAsync(CancellationToken cancellationToken) {
			try {
				while (IsConnected() && !cancellationToken.IsCancellationRequested) {
					// 1. Try to decode from buffer
					if (_receiveBuffer.Count >= 2) {
						var bytes = _receiveBuffer;
						// var fin    = (bytes[0] & 0b10000000) != 0;
						var mask   = (bytes[1] & 0b10000000) != 0;
						var opcode = bytes[0] & 0b00001111;

						// Opcode 8 = connection close
						if (opcode == 8)
							return null;

						// Opcode 1 = text message
						if (opcode != 1) {
							Logger.LogWarning($"Unsupported opcode: {opcode}");
							return null;
						}

						ulong offset = 2;
						var   msgLen = (ulong)(bytes[1] & 0b01111111);

						if (msgLen == 126) {
							if (_receiveBuffer.Count < 4) goto ReadMore;
							msgLen = BitConverter.ToUInt16(new byte[] { bytes[3], bytes[2] }, 0);
							offset = 4;
						} else if (msgLen == 127) {
							if (_receiveBuffer.Count < 10) goto ReadMore;
							msgLen = BitConverter.ToUInt64(
								new byte[] {
									bytes[9], bytes[8], bytes[7], bytes[6],
									bytes[5], bytes[4], bytes[3], bytes[2]
								}, 0
							);
							offset = 10;
						}

						if (msgLen == 0) {
							Logger.LogWarning("Message length is 0");
							_receiveBuffer.RemoveRange(0, (int)offset);
							continue;
						}

						if (!mask) {
							Logger.LogWarning("Mask bit not set");
							return null;
						}

						// Check if we have the full message
						var totalLen = offset + 4 + msgLen;
						if ((ulong)_receiveBuffer.Count < totalLen)
							goto ReadMore;

						// Decode message
						var masks = new byte[] {
							bytes[(int)offset],
							bytes[(int)offset + 1],
							bytes[(int)offset + 2],
							bytes[(int)offset + 3]
						};
						offset += 4;

						var decoded = new byte[msgLen];
						for (ulong i = 0; i < msgLen; ++i)
							decoded[i] = (byte)(bytes[(int)offset + (int)i] ^ masks[i % 4]);

						var text = Encoding.UTF8.GetString(decoded);
						_receiveBuffer.RemoveRange(0, (int)totalLen);
						return text;
					}

					ReadMore:
					if (_stream.DataAvailable) {
						var available = _tcpClient.Available;
						if (available > 0) {
							var buffer    = new byte[available];
							var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
							if (bytesRead > 0) {
								_receiveBuffer.AddRange(buffer.Take(bytesRead));
								continue;
							}
						}
					}

					await UniTask.Delay(10, cancellationToken: cancellationToken);
				}

				return null;
			} catch (Exception ex) {
				if (ex is System.IO.IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException) {
					return null;
				}

				Logger.LogError($"Error receiving message: {ex.Message}");
				return null;
			}
		}

		public async UniTask SendMessageAsync(string message) {
			if (!_isHandshakeComplete || !IsConnected())
				return;

			try {
				var    messageBytes = Encoding.UTF8.GetBytes(message);
				byte[] frame;

				switch (messageBytes.Length) {
					// Build WebSocket frame (server to client, no masking required)
					case <= 125:
						frame    = new byte[2 + messageBytes.Length];
						frame[0] = 0b10000001; // FIN + text frame
						frame[1] = (byte)messageBytes.Length;
						Array.Copy(messageBytes, 0, frame, 2, messageBytes.Length);
						break;
					case <= 65535:
						frame    = new byte[4 + messageBytes.Length];
						frame[0] = 0b10000001; // FIN + text frame
						frame[1] = 126;
						frame[2] = (byte)((messageBytes.Length >> 8) & 0xFF);
						frame[3] = (byte)(messageBytes.Length        & 0xFF);
						Array.Copy(messageBytes, 0, frame, 4, messageBytes.Length);
						break;
					default: {
						frame    = new byte[10 + messageBytes.Length];
						frame[0] = 0b10000001; // FIN + text frame
						frame[1] = 127;
						for (int i = 0; i < 8; i++) {
							frame[9 - i] = (byte)((messageBytes.Length >> (i * 8)) & 0xFF);
						}

						Array.Copy(messageBytes, 0, frame, 10, messageBytes.Length);
						break;
					}
				}

				await _stream.WriteAsync(frame, 0, frame.Length);
			} catch (Exception ex) {
				if (ex is System.IO.IOException || ex is SocketException) {
					Logger.LogWarning($"Client disconnected during send: {ex.Message}");
					Disconnect();
				} else {
					Logger.LogError($"Error sending message: {ex.Message}");
				}
			}
		}
	}
}