namespace SECmd.Nif
{
    /// <summary>
    /// Raised when a NIF file, or the nif.xml describing the format, cannot be
    /// interpreted.
    /// </summary>
    public sealed class NifFormatException : Exception
    {
        public NifFormatException(string message) : base(message)
        {
        }

        public NifFormatException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
