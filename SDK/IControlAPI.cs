namespace Nox.Control
{
	/// <summary>
	/// Interface for the control API, exposed to mods.
	/// Equivalent to ITerminalAPI in the terminal system.
	/// </summary>
	public interface IControlAPI
	{
		/// <summary>
		/// Returns all registered operators.
		/// </summary>
		IOperator[] GetRegistered();

		/// <summary>
		/// Registers an operator and returns its unique ID.
		/// Returns uint.MaxValue on failure.
		/// </summary>
		uint Register(IOperator op);

		/// <summary>
		/// Unregisters an operator by its ID.
		/// </summary>
		void Unregister(uint id);
	}
}
