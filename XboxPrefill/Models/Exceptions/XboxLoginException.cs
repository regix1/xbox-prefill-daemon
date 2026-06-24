namespace XboxPrefill.Models.Exceptions
{
    public sealed class XboxLoginException : Exception
    {
        public XboxLoginException(string message) : base(message)
        {

        }

        public XboxLoginException()
        {
        }

        public XboxLoginException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}