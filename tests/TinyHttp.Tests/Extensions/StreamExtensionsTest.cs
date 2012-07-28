namespace TinyHttp.Tests.Extensions
{
    using System.IO;
    using FluentAssertions;
    using NUnit.Framework;

    public class StreamExtensionsTest
    {
        [Test]
        public void WriteNullString()
        {
            using (var stream = new MemoryStream())
            {
                stream.WriteString(null);
                stream.Seek(0, SeekOrigin.Begin);
                new StreamReader(stream).ReadToEnd().Should().BeEmpty();
            }
        }

        [Test]
        public void WriteString()
        {
            using (var stream = new MemoryStream())
            {
                stream.WriteString("test");
                stream.Seek(0, SeekOrigin.Begin);
                new StreamReader(stream).ReadToEnd().Should().Be("test");
            }
        }
    }
}