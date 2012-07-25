using System;

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
                Get["/{name}"] = s => new HtmlResponse(String.Format("<h1>Hello, {0}</h1>", s.name));
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