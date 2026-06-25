using Cysharp.Threading.Tasks;

namespace Nox.Control
{
	/// <summary>
	/// Represents an operation that can be invoked via MCP, WebSocket, or HTTP transports.
	/// Equivalent to ICommand in the terminal system.
	/// </summary>
	public interface IOperator
	{
		/// <summary>Unique operation name (e.g., "config:get").</summary>
		string Name { get; }

		/// <summary>Human-readable description.</summary>
		string Description { get; }

		/// <summary>Typed input schema for the operation arguments.</summary>
		ISchema Schema { get; }

		/// <summary>Executes the operation with the given arguments and returns a typed result.</summary>
		UniTask<IOutput> Execute(IInput args);
	}
}
