namespace TinyHttp.Tests
{
    using NUnit.Framework;

    public class QuickTest
    {
        public class X : RequestProcessor
        {
            public X()
            {
                Get["/"] = s => new HtmlResponse("<h1>Testing</h1>");
            }
        }

        [Test]
        public void a()
        {
            var host = new Host("http://localhost:9999/", new X());
            host.Start();

            while (true) ;
        }
    }
}