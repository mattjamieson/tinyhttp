namespace TinyHttp.Tests.Responses
{
    using System;
    using System.IO;

    internal static class Extensions
    {
        public static string AsString(this Action<Stream> body)
        {
            string s = null;
            if (body != null)
            {
                using (var stream = new MemoryStream())
                {
                    body.Invoke(stream);
                    stream.Seek(0, SeekOrigin.Begin);
                    s = new StreamReader(stream).ReadToEnd();
                }
            }
            return s;
        }
    }
}