using System.Collections.Generic;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Runtime.Server {
	/// <summary>
	/// Manages registered IOperators with unique IDs.
	/// Equivalent to CommandManager in the terminal system.
	/// </summary>
	public class OperationManager {
		public readonly List<(uint, IOperator)> Operators = new();

		private uint _nextId;

		private uint NextId
			=> _nextId == uint.MaxValue ? _nextId = 0 : ++_nextId;

		/// <summary>
		/// Registers an operator and returns its unique ID.
		/// Returns uint.MaxValue if the operator is null or the ID already exists.
		/// </summary>
		public uint Register(IOperator op) {
			if (op == null) return uint.MaxValue;

			var id = NextId;
			if (Operators.Exists(o => o.Item1 == id))
				return uint.MaxValue;

			Operators.Add((id, op));
			Logger.Log($"Registered operator: {op.Name} with ID {id}", tag: nameof(OperationManager));
			return id;
		}

		/// <summary>
		/// Unregisters an operator by its ID.
		/// </summary>
		public void Unregister(uint id) {
			var index = Operators.FindIndex(o => o.Item1 == id);
			if (index < 0) return;
			var op = Operators[index].Item2;
			Operators.RemoveAt(index);
			Logger.Log($"Unregistered operator: {op.Name} with ID {id}", tag: nameof(OperationManager));
		}
	}
}
