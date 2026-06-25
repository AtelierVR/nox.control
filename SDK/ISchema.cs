using System;

namespace Nox.Control
{
    public interface ISchema
    {
        public string Type { get; }

        public IProperty[] Properties { get; }
    }

    public interface IProperty
    {
        public string Name { get; }

        public Type Type { get; }

        public string Description { get; }

        public bool Required { get; }
    }
}