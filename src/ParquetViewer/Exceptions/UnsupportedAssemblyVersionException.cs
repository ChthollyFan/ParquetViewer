using ParquetViewer.Helpers;
using System;

namespace ParquetViewer.Exceptions
{
    internal class UnsupportedAssemblyVersionException : Exception
    {
        public UnsupportedAssemblyVersionException(string unsupportedAssemblyVersion, Exception? ex = null)
            : base(Resources.Errors.UnexpectedAssemblyVersionErrorFormat.Format(unsupportedAssemblyVersion), ex) { }
    }
}