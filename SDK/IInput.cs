using System.Collections.Generic;

namespace Nox.Control
{
	public interface IInput
	{
		public T Get<T>(string key, bool required = false);
		
		public bool Has<T>(string key);

		public IReadOnlyDictionary<string, object> All { get; }
	}
}
