using System;
using System.Collections.Generic;
using Nox.Control;

namespace Nox.CCK.Control
{
	/// <summary>
	/// Typed JSON Schema for operator input parameters.
	/// Replaces raw JObject in IOperator.InputSchema with a fluent builder API.
	/// </summary>
	public class InputSchema : ISchema {
		public string Type { get; } = "object";

		public List<Property> Properties { get; } = new List<Property>();

		IProperty[] ISchema.Properties
			=> Properties.ToArray();

		public InputSchema Property<T>(string name, string description = null, bool required = false) {
			Properties.Add(new Property {
				Name = name,
				Type = typeof(T),
				Description = description,
				Required = required
			});
			return this;
		}

		/// <summary>
		/// Declares the standard <c>offset</c>/<c>limit</c> arguments used to page a large
		/// result set (see <see cref="Pagination"/>).
		/// </summary>
		public InputSchema WithPagination()
			=> Property<int>("offset", "Index of the first item to return (default 0).")
				.Property<int>("limit", "Maximum number of items to return; 0 or omitted returns everything.");
	}

	public class Property : IProperty
	{
		public string Name { get; set; }

		public Type Type { get; set; }

		public string Description { get; set; }

		public bool Required { get; set; }
    }
}
