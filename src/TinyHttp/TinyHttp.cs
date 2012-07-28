/**
 * TinyHttp - a tiny C# HTTP server
 * https://github.com/mattjamieson/TinyHttp
 * 
 * Copyright (C) 2012 Matt Jamieson
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy of
 * this software and associated documentation files (the "Software"), to deal in
 * the Software without restriction, including without limitation the rights to
 * use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
 * of the Software, and to permit persons to whom the Software is furnished to do
 * so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */
namespace TinyHttp
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Net;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using Microsoft.CSharp.RuntimeBinder;

    #region TinyHttpHost
    public class TinyHttpHost
    {
        private readonly HttpListener _listener;
        private readonly IRequestProcessor _requestProcessor;
        
        public TinyHttpHost(string baseUri, IRequestProcessor requestProcessor, string baseDirectory = null)
        {
            if (baseDirectory != null) { FileResponse.BaseDirectory = baseDirectory; }
            _requestProcessor = requestProcessor;
            _listener = new HttpListener();
            _listener.Prefixes.Add(baseUri);
        }

        public void Start()
        {
            _listener.Start();
            Listen();
        }

        public void Stop()
        {
            _listener.Stop();
        }

        private void OnGetContext(IAsyncResult asyncResult)
        {
            var context = _listener.EndGetContext(asyncResult);
            Listen();
            ThreadPool.QueueUserWorkItem(delegate { Process(context); }, null);
        }

        private void Listen()
        {
            try { _listener.BeginGetContext(OnGetContext, null); }
            catch (HttpListenerException) { /* Thrown when listener is closed while waiting for a request */ }
        }

        private void Process(HttpListenerContext context)
        {
            try { WriteResponse(_requestProcessor.HandleRequest(GetRequest(context.Request)), context.Response); }
            catch
            {}
        }

        private static void WriteResponse(Response response, HttpListenerResponse httpListenerResponse)
        {
            foreach (var header in response.Headers) { httpListenerResponse.AddHeader(header.Key, header.Value); }
            httpListenerResponse.StatusCode = (int) response.StatusCode;
            httpListenerResponse.ContentType = response.ContentType;
            using (var output = httpListenerResponse.OutputStream) { response.Body.Invoke(output); }
        }

        private static Request GetRequest(HttpListenerRequest request)
        {
            var requestHeaders = request.Headers.AllKeys.ToDictionary<string, string, IEnumerable<string>>(key => key, request.Headers.GetValues);
            var expectedRequestLength = GetExpectedRequestLength(requestHeaders);
            return new Request(request.HttpMethod, request.Url, expectedRequestLength, request.InputStream, requestHeaders);
        }

        private static long GetExpectedRequestLength(IDictionary<string, IEnumerable<string>> incomingHeaders)
        {
            if (incomingHeaders == null) { return 0; }
            if (!incomingHeaders.ContainsKey("Content-Length")) { return 0; }

            var headerValue = incomingHeaders["Content-Length"].SingleOrDefault();
            if (headerValue == null) { return 0; }

            long contentLength;
            return !Int64.TryParse(headerValue, NumberStyles.Any, CultureInfo.InvariantCulture, out contentLength)
                       ? 0
                       : contentLength;
        }
    }
    #endregion

    #region RequestProcessing
    public interface IRequestProcessor
    {
        Response HandleRequest(Request request);
    }

    public abstract class RequestProcessor : IRequestProcessor
    {
        private readonly List<Route> _routes = new List<Route>();

        public Response HandleRequest(Request request)
        {
            var route = _routes.FirstOrDefault(r => r.Method == request.HttpMethod && r.IsMatch(request));
            return route != null ? route.Invoke(request) : new NotFoundResponse();
        }

        protected RouteBuilder Get { get { return new RouteBuilder("GET", this); } }
        protected RouteBuilder Post { get { return new RouteBuilder("POST", this); } }
        protected RouteBuilder Put { get { return new RouteBuilder("PUT", this); } }
        protected RouteBuilder Delete { get { return new RouteBuilder("DELETE", this); } }
        protected RouteBuilder Options { get { return new RouteBuilder("OPTIONS", this); } }
        protected RouteBuilder Patch { get { return new RouteBuilder("PATCH", this); } }

        protected class RouteBuilder
        {
            private readonly string _method;
            private readonly RequestProcessor _requestProcessor;

            public RouteBuilder(string method, RequestProcessor requestProcessor)
            {
                _method = method;
                _requestProcessor = requestProcessor;
            }

            public Func<dynamic, Response> this[string path] { set { AddRoute(path, value); } }
            private void AddRoute(string path, Func<object, Response> value) { _requestProcessor._routes.Add(new Route(_method, path, value)); }
        }

        private class Route
        {
            private static readonly Regex PathRegex = new Regex(@"\{(?<param>\S+)\}", RegexOptions.Compiled);

            private readonly string[] _paramNames;

            public string Method { get; private set; }
            private string Path { get; set; }
            private Func<dynamic, Response> Action { get; set; }
            private Regex Regex { get; set; }

            public Route(string method, string path, Func<dynamic, Response> action)
            {
                Method = method;
                Path = path;
                Action = action;

                var paramNames = new List<string>();
                Regex = new Regex(String.Concat(
                    "^",
                    PathRegex.Replace(path,
                                      m =>
                                      {
                                          var paramName = m.Groups["param"].Value;
                                          paramNames.Add(paramName);
                                          return String.Concat("(?<", paramName, @">\S+)");
                                      }),
                    "$"));
                _paramNames = paramNames.ToArray();
            }

            public bool IsMatch(Request request)
            {
                return request.HttpMethod == Method && Regex.IsMatch(request.Url.AbsolutePath);
            }

            public Response Invoke(Request request)
            {
                var match = Regex.Match(request.Url.AbsolutePath);
                var parameters = DynamicDictionary.Create(_paramNames.ToDictionary(k => k, k => (object) match.Groups[k]));
                return Action.Invoke(parameters);
            }
        }
    }

    public class Request
    {
        public Stream Body { get; private set; }
        public IDictionary<string, IEnumerable<string>> Headers { get; private set; }
        public string HttpMethod { get; private set; }
        public long Length { get; private set; }
        public Uri Url { get; private set; }

        public Request(string httpMethod, Uri url, long length, Stream body, IDictionary<string, IEnumerable<string>> headers)
        {
            HttpMethod = httpMethod;
            Url = url;
            Length = length;
            Body = body;
            Headers = headers;
        }
    }
    #endregion

    #region Responses
    public class Response
    {
        public Response()
        {
            Headers = new Dictionary<string, string>();
        }

        public Action<Stream> Body { get; protected set; }
        public string ContentType { get; protected set; }
        public IDictionary<string, string> Headers { get; private set; }
        public HttpStatusCode StatusCode { get; protected set; }
    }

    public class FileResponse : Response
    {
        public static string BaseDirectory { get; set; }
        static FileResponse() { BaseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "html"); }

        public FileResponse(string filePath) : this(filePath, MimeTypes.GetMimeType(filePath))
        {}

        public FileResponse(string filePath, string contentType)
        {
            StatusCode = HttpStatusCode.NotFound;
            if (String.IsNullOrEmpty(filePath)) { return; }
            var completeFilePath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(BaseDirectory, filePath);
            if (Path.HasExtension(completeFilePath) && completeFilePath.StartsWith(BaseDirectory) && File.Exists(completeFilePath))
            {
                var fileInfo = new FileInfo(filePath);
                Headers["ETag"] = fileInfo.LastWriteTimeUtc.Ticks.ToString("x");
                Headers["Last-Modified"] = fileInfo.LastWriteTimeUtc.ToString("R");
                Body = stream => { using (var file = File.OpenRead(filePath)) { file.CopyTo(stream); } };
                ContentType = contentType;
                StatusCode = HttpStatusCode.OK;
            }
        }
    }

    public class TextResponse : Response
    {
        public TextResponse(string body, string contentType = "text/plain", Encoding encoding = null)
        {
            if (encoding == null) { encoding = Encoding.UTF8; }
            ContentType = contentType;
            StatusCode = HttpStatusCode.OK;
            if (body != null) { Body = stream => { var data = encoding.GetBytes(body); stream.Write(data, 0, data.Length); }; }
        }
    }

    public class HtmlResponse : TextResponse
    {
        public HtmlResponse(string body, string contentType = "text/html", Encoding encoding = null) : base(body, contentType, encoding)
        {
            ContentType = contentType;
        }
    }

    public class NotFoundResponse : Response
    {
        public NotFoundResponse(string contentType = "text/html")
        {
            ContentType = contentType;
            StatusCode = HttpStatusCode.NotFound;
        }
    }

    public class RedirectResponse : Response
    {
        public enum RedirectType
        {
            Permanent = 301,
            Temporary = 307,
            SeeOther = 303
        }

        public RedirectResponse(string location, RedirectType redirectType = RedirectType.SeeOther, string contentType = "text/html")
        {
            Headers.Add("Location", location);
            ContentType = contentType;
            StatusCode = (HttpStatusCode) redirectType;
            Body = stream => stream.WriteString(String.Empty);
        }
    }
    #endregion

    #region DynamicDictionary
    /*
    Copyright (c) 2010 Andreas Håkansson, Steven Robbins and contributors

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in
    all copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
    THE SOFTWARE.
    */
    /// <summary>
    /// A dictionary that supports dynamic access.
    /// </summary>
    public class DynamicDictionary : DynamicObject, IEquatable<DynamicDictionary>, IEnumerable<string>, IDictionary<string, object>
    {
        private readonly IDictionary<string, dynamic> _dictionary = new Dictionary<string, dynamic>();

        /// <summary>
        /// Returns an empty dynamic dictionary.
        /// </summary>
        /// <value>A <see cref="DynamicDictionary"/> instance.</value>
        public static DynamicDictionary Empty { get { return new DynamicDictionary(); } }

        /// <summary>
        /// Creates a dynamic dictionary from an <see cref="IDictionary{TKey,TValue}"/> instance.
        /// </summary>
        /// <param name="values">An <see cref="IDictionary{TKey,TValue}"/> instance, that the dynamic dictionary should be created from.</param>
        /// <returns>An <see cref="DynamicDictionary"/> instance.</returns>
        public static DynamicDictionary Create(IDictionary<string, object> values)
        {
            var instance = new DynamicDictionary();
            foreach (var key in values.Keys) { instance[key] = values[key]; }
            return instance;
        }

        /// <summary>
        /// Provides the implementation for operations that set member values. Classes derived from the <see cref="T:System.Dynamic.DynamicObject"/> class can override this method to specify dynamic behavior for operations such as setting a value for a property.
        /// </summary>
        /// <returns>true if the operation is successful; otherwise, false. If this method returns false, the run-time binder of the language determines the behavior. (In most cases, a language-specific run-time exception is thrown.)</returns>
        /// <param name="binder">Provides information about the object that called the dynamic operation. The binder.Name property provides the name of the member to which the value is being assigned. For example, for the statement sampleObject.SampleProperty = "Test", where sampleObject is an instance of the class derived from the <see cref="T:System.Dynamic.DynamicObject"/> class, binder.Name returns "SampleProperty". The binder.IgnoreCase property specifies whether the member name is case-sensitive.</param><param name="value">The value to set to the member. For example, for sampleObject.SampleProperty = "Test", where sampleObject is an instance of the class derived from the <see cref="T:System.Dynamic.DynamicObject"/> class, the <paramref name="value"/> is "Test".</param>
        public override bool TrySetMember(SetMemberBinder binder, object value)
        {
            this[binder.Name] = value;
            return true;
        }

        /// <summary>
        /// Provides the implementation for operations that get member values. Classes derived from the <see cref="T:System.Dynamic.DynamicObject"/> class can override this method to specify dynamic behavior for operations such as getting a value for a property.
        /// </summary>
        /// <returns>true if the operation is successful; otherwise, false. If this method returns false, the run-time binder of the language determines the behavior. (In most cases, a run-time exception is thrown.)</returns>
        /// <param name="binder">Provides information about the object that called the dynamic operation. The binder.Name property provides the name of the member on which the dynamic operation is performed. For example, for the Console.WriteLine(sampleObject.SampleProperty) statement, where sampleObject is an instance of the class derived from the <see cref="T:System.Dynamic.DynamicObject"/> class, binder.Name returns "SampleProperty". The binder.IgnoreCase property specifies whether the member name is case-sensitive.</param><param name="result">The result of the get operation. For example, if the method is called for a property, you can assign the property value to <paramref name="result"/>.</param>
        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            if (!_dictionary.TryGetValue(binder.Name, out result)) { result = new DynamicDictionaryValue(null); }
            return true;
        }

        /// <summary>
        /// Returns the enumeration of all dynamic member names.
        /// </summary>
        /// <returns>A <see cref="IEnumerable{T}"/> that contains dynamic member names.</returns>
        public override IEnumerable<string> GetDynamicMemberNames() { return _dictionary.Keys; }

        /// <summary>
        /// Returns the enumeration of all dynamic member names.
        /// </summary>
        /// <returns>A <see cref="IEnumerable{T}"/> that contains dynamic member names.</returns>
        public IEnumerator<string> GetEnumerator() { return _dictionary.Keys.GetEnumerator(); }

        /// <summary>
        /// Returns the enumeration of all dynamic member names.
        /// </summary>
        /// <returns>A <see cref="IEnumerator"/> that contains dynamic member names.</returns>
        IEnumerator IEnumerable.GetEnumerator() { return _dictionary.Keys.GetEnumerator(); }

        /// <summary>
        /// Gets or sets the <see cref="DynamicDictionaryValue"/> with the specified name.
        /// </summary>
        /// <value>A <see cref="DynamicDictionaryValue"/> instance containing a value.</value>
        public dynamic this[string name]
        {
            get
            {
                name = GetNeutralKey(name);
                dynamic member;
                if (!_dictionary.TryGetValue(name, out member)) { member = new DynamicDictionaryValue(null); }
                return member;
            }
            set
            {
                name = GetNeutralKey(name);
                _dictionary[name] = value is DynamicDictionaryValue ? value : new DynamicDictionaryValue(value);
            }
        }

        /// <summary>
        /// Indicates whether the current <see cref="DynamicDictionary"/> is equal to another object of the same type.
        /// </summary>
        /// <returns><see langword="true"/> if the current instance is equal to the <paramref name="other"/> parameter; otherwise, <see langword="false"/>.</returns>
        /// <param name="other">An <see cref="DynamicDictionary"/> instance to compare with this instance.</param>
        public bool Equals(DynamicDictionary other)
        {
            if (ReferenceEquals(null, other)) { return false; }
            return ReferenceEquals(this, other) || Equals(other._dictionary, _dictionary);
        }

        /// <summary>
        /// Determines whether the specified <see cref="System.Object"/> is equal to this instance.
        /// </summary>
        /// <param name="obj">The <see cref="System.Object"/> to compare with this instance.</param>
        /// <returns><see langword="true"/> if the specified <see cref="System.Object"/> is equal to this instance; otherwise, <see langword="false"/>.</returns>
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) { return false; }
            if (ReferenceEquals(this, obj)) { return true; }
            return obj.GetType() == typeof(DynamicDictionary) && Equals((DynamicDictionary)obj);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the collection.</returns>
        IEnumerator<KeyValuePair<string, dynamic>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator() { return _dictionary.GetEnumerator(); }

        /// <summary>
        /// Returns a hash code for this <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns> A hash code for this <see cref="DynamicDictionary"/>, suitable for use in hashing algorithms and data structures like a hash table.</returns>
        public override int GetHashCode() { return (_dictionary != null ? _dictionary.GetHashCode() : 0); }

        /// <summary>
        /// Adds an element with the provided key and value to the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <param name="key">The object to use as the key of the element to add.</param>
        /// <param name="value">The object to use as the value of the element to add.</param>
        public void Add(string key, dynamic value) { this[key] = value; }

        /// <summary>
        /// Adds an item to the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <param name="item">The object to add to the <see cref="DynamicDictionary"/>.</param>
        public void Add(KeyValuePair<string, dynamic> item) { this[item.Key] = item.Value; }

        /// <summary>
        /// Determines whether the <see cref="DynamicDictionary"/> contains an element with the specified key.
        /// </summary>
        /// <returns><see langword="true" /> if the <see cref="DynamicDictionary"/> contains an element with the key; otherwise, <see langword="false" />.
        /// </returns>
        /// <param name="key">The key to locate in the <see cref="DynamicDictionary"/>.</param>
        public bool ContainsKey(string key) { return _dictionary.ContainsKey(key); }

        /// <summary>
        /// Gets an <see cref="T:System.Collections.Generic.ICollection`1"/> containing the keys of the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns>An <see cref="T:System.Collections.Generic.ICollection`1"/> containing the keys of the <see cref="DynamicDictionary"/>.</returns>
        public ICollection<string> Keys { get { return _dictionary.Keys; } }

        /// <summary>
        /// Gets the value associated with the specified key.
        /// </summary>
        /// <returns><see langword="true" /> if the <see cref="DynamicDictionary"/> contains an element with the specified key; otherwise, <see langword="false" />.</returns>
        /// <param name="key">The key whose value to get.</param>
        /// <param name="value">When this method returns, the value associated with the specified key, if the key is found; otherwise, the default value for the type of the <paramref name="value"/> parameter. This parameter is passed uninitialized.</param>
        public bool TryGetValue(string key, out dynamic value) { return _dictionary.TryGetValue(key, out value); }

        /// <summary>
        /// Removes all items from the <see cref="DynamicDictionary"/>.
        /// </summary>
        public void Clear() { _dictionary.Clear(); }

        /// <summary>
        /// Gets the number of elements contained in the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns>The number of elements contained in the <see cref="DynamicDictionary"/>.</returns>
        public int Count { get { return _dictionary.Count; } }

        /// <summary>
        /// Determines whether the <see cref="DynamicDictionary"/> contains a specific value.
        /// </summary>
        /// <returns><see langword="true" /> if <paramref name="item"/> is found in the <see cref="DynamicDictionary"/>; otherwise, <see langword="false" />.
        /// </returns>
        /// <param name="item">The object to locate in the <see cref="DynamicDictionary"/>.</param>
        public bool Contains(KeyValuePair<string, dynamic> item)
        {
            var dynamicValueKeyValuePair = GetDynamicKeyValuePair(item);
            return _dictionary.Contains(dynamicValueKeyValuePair);
        }

        /// <summary>
        /// Copies the elements of the <see cref="DynamicDictionary"/> to an <see cref="T:System.Array"/>, starting at a particular <see cref="T:System.Array"/> index.
        /// </summary>
        /// <param name="array">The one-dimensional <see cref="T:System.Array"/> that is the destination of the elements copied from the <see cref="DynamicDictionary"/>. The <see cref="T:System.Array"/> must have zero-based indexing.</param>
        /// <param name="arrayIndex">The zero-based index in <paramref name="array"/> at which copying begins.</param>
        public void CopyTo(KeyValuePair<string, dynamic>[] array, int arrayIndex) { _dictionary.CopyTo(array, arrayIndex); }

        /// <summary>
        /// Gets a value indicating whether the <see cref="DynamicDictionary"/> is read-only.
        /// </summary>
        /// <returns>Always returns <see langword="false" />.</returns>
        public bool IsReadOnly { get { return false; } }

        /// <summary>
        /// Removes the element with the specified key from the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns><see langword="true" /> if the element is successfully removed; otherwise, <see langword="false" />.</returns>
        /// <param name="key">The key of the element to remove.</param>
        public bool Remove(string key) { return _dictionary.Remove(key); }

        /// <summary>
        /// Removes the first occurrence of a specific object from the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns><see langword="true" /> if <paramref name="item"/> was successfully removed from the <see cref="DynamicDictionary"/>; otherwise, <see langword="false" />.</returns>
        /// <param name="item">The object to remove from the <see cref="DynamicDictionary"/>.</param>
        public bool Remove(KeyValuePair<string, dynamic> item)
        {
            var dynamicValueKeyValuePair = GetDynamicKeyValuePair(item);
            return _dictionary.Remove(dynamicValueKeyValuePair);
        }

        /// <summary>
        /// Gets an <see cref="T:System.Collections.Generic.ICollection`1"/> containing the values in the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns>An <see cref="T:System.Collections.Generic.ICollection`1"/> containing the values in the <see cref="DynamicDictionary"/>.</returns>
        public ICollection<dynamic> Values { get { return _dictionary.Values; } }

        private static KeyValuePair<string, dynamic> GetDynamicKeyValuePair(KeyValuePair<string, dynamic> item)
        {
            var dynamicValueKeyValuePair = new KeyValuePair<string, dynamic>(item.Key, new DynamicDictionaryValue(item.Value));
            return dynamicValueKeyValuePair;
        }

        private static string GetNeutralKey(string key) { return key.Replace("-", string.Empty); }
    }

    public class DynamicDictionaryValue : DynamicObject, IEquatable<DynamicDictionaryValue>, IConvertible
    {
        private readonly object _value;

        /// <summary>
        /// Initializes a new instance of the <see cref="DynamicDictionaryValue"/> class.
        /// </summary>
        /// <param name="value">The value to store in the instance</param>
        public DynamicDictionaryValue(object value) { _value = value; }

        /// <summary>
        /// Gets a value indicating whether this instance has value.
        /// </summary>
        /// <value><c>true</c> if this instance has value; otherwise, <c>false</c>.</value>
        /// <remarks><see langword="null"/> is considered as not being a value.</remarks>
        public bool HasValue { get { return (_value != null); } }

        /// <summary>
        /// Gets the inner value
        /// </summary>
        public object Value { get { return _value; } }

        public static bool operator ==(DynamicDictionaryValue dynamicValue, object compareValue)
        {
            if (dynamicValue._value == null && compareValue == null) { return true; }
            return dynamicValue._value != null && dynamicValue._value.Equals(compareValue);
        }

        public static bool operator !=(DynamicDictionaryValue dynamicValue, object compareValue) { return !(dynamicValue == compareValue); }

        /// <summary>
        /// Indicates whether the current object is equal to another object of the same type.
        /// </summary>
        /// <returns><c>true</c> if the current object is equal to the <paramref name="compareValue"/> parameter; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="compareValue">An <see cref="DynamicDictionaryValue"/> to compare with this instance.</param>
        public bool Equals(DynamicDictionaryValue compareValue)
        {
            if (ReferenceEquals(null, compareValue)) { return false; }
            return ReferenceEquals(this, compareValue) || Equals(compareValue._value, _value);
        }

        /// <summary>
        /// Determines whether the specified <see cref="object"/> is equal to the current <see cref="object"/>.
        /// </summary>
        /// <returns><c>true</c> if the specified <see cref="object"/> is equal to the current <see cref="DynamicDictionaryValue"/>; otherwise, <c>false</c>.</returns>
        /// <param name="compareValue">The <see cref="object"/> to compare with the current <see cref="DynamicDictionaryValue"/>.</param>
        public override bool Equals(object compareValue)
        {
            if (ReferenceEquals(null, compareValue)) { return false; }
            if (ReferenceEquals(this, compareValue)) { return true; }
            return compareValue.GetType() == typeof(DynamicDictionaryValue) && Equals((DynamicDictionaryValue)compareValue);
        }

        /// <summary>
        /// Serves as a hash function for a particular type. 
        /// </summary>
        /// <returns>A hash code for the current instance.</returns>
        public override int GetHashCode() { return (_value != null ? _value.GetHashCode() : 0); }

        /// <summary>
        /// Provides implementation for binary operations. Classes derived from the <see cref="T:System.Dynamic.DynamicObject"/> class can override this method to specify dynamic behavior for operations such as addition and multiplication.
        /// </summary>
        /// <returns><c>true</c> if the operation is successful; otherwise, <c>false</c>. If this method returns <c>false</c>, the run-time binder of the language determines the behavior. (In most cases, a language-specific run-time exception is thrown.)</returns>
        /// <param name="binder">Provides information about the binary operation. The binder.Operation property returns an <see cref="T:System.Linq.Expressions.ExpressionType"/> object. For example, for the sum = first + second statement, where first and second are derived from the DynamicObject class, binder.Operation returns ExpressionType.Add.</param><param name="arg">The right operand for the binary operation. For example, for the sum = first + second statement, where first and second are derived from the DynamicObject class, <paramref name="arg"/> is equal to second.</param><param name="result">The result of the binary operation.</param>
        public override bool TryBinaryOperation(BinaryOperationBinder binder, object arg, out object result)
        {
            object resultOfCast;
            result = null;
            if (binder.Operation != ExpressionType.Equal) { return false; }
            var convert = Binder.Convert(CSharpBinderFlags.None, arg.GetType(), typeof(DynamicDictionaryValue));
            if (!TryConvert((ConvertBinder)convert, out resultOfCast)) { return false; }
            result = (resultOfCast == null) ? Equals(arg, resultOfCast) : resultOfCast.Equals(arg);
            return true;
        }

        /// <summary>
        /// Provides implementation for type conversion operations. Classes derived from the <see cref="T:System.Dynamic.DynamicObject"/> class can override this method to specify dynamic behavior for operations that convert an object from one type to another.
        /// </summary>
        /// <returns><c>true</c> if the operation is successful; otherwise, <c>false</c>. If this method returns <c>false</c>, the run-time binder of the language determines the behavior. (In most cases, a language-specific run-time exception is thrown.)</returns>
        /// <param name="binder">Provides information about the conversion operation. The binder.Type property provides the type to which the object must be converted. For example, for the statement (String)sampleObject in C# (CType(sampleObject, Type) in Visual Basic), where sampleObject is an instance of the class derived from the <see cref="T:System.Dynamic.DynamicObject"/> class, binder.Type returns the <see cref="T:System.String"/> type. The binder.Explicit property provides information about the kind of conversion that occurs. It returns true for explicit conversion and false for implicit conversion.</param><param name="result">The result of the type conversion operation.</param>
        public override bool TryConvert(ConvertBinder binder, out object result)
        {
            result = null;
            if (_value == null) { return true; }
            var binderType = binder.Type;
            if (binderType == typeof(String))
            {
                result = Convert.ToString(_value);
                return true;
            }
            if (binderType == typeof(Guid) || binderType == typeof(Guid?))
            {
                Guid guid;
                if (Guid.TryParse(Convert.ToString(_value), out guid))
                {
                    result = guid;
                    return true;
                }
            }
            else if (binderType == typeof(TimeSpan) || binderType == typeof(TimeSpan?))
            {
                TimeSpan timespan;
                if (TimeSpan.TryParse(Convert.ToString(_value), out timespan))
                {
                    result = timespan;
                    return true;
                }
            }
            else
            {
                if (binderType.IsGenericType && binderType.GetGenericTypeDefinition() == typeof (Nullable<>)) { binderType = binderType.GetGenericArguments()[0]; }
                var typeCode = Type.GetTypeCode(binderType);
                if (typeCode == TypeCode.Object) { return false; }
                result = Convert.ChangeType(_value, typeCode);
                return true;
            }
            return base.TryConvert(binder, out result);
        }

        public override string ToString() { return _value == null ? base.ToString() : Convert.ToString(_value); }

        public static implicit operator bool(DynamicDictionaryValue dynamicValue)
        {
            if (!dynamicValue.HasValue) { return false; }
            if (dynamicValue._value.GetType().IsValueType) { return (Convert.ToBoolean(dynamicValue._value)); }
            bool result;
            if (bool.TryParse(dynamicValue.ToString(), out result)) { return result; }
            return true;
        }

        public static implicit operator string(DynamicDictionaryValue dynamicValue) { return dynamicValue.ToString(); }

        public static implicit operator int(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType) { return Convert.ToInt32(dynamicValue._value); }
            return Int32.Parse(dynamicValue.ToString());
        }

        public static implicit operator Guid(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value is Guid) { return (Guid) dynamicValue._value; }
            return Guid.Parse(dynamicValue.ToString());
        }

        public static implicit operator DateTime(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value is DateTime) { return (DateTime) dynamicValue._value; }
            return DateTime.Parse(dynamicValue.ToString());
        }

        public static implicit operator TimeSpan(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value is TimeSpan) { return (TimeSpan)dynamicValue._value; }
            return TimeSpan.Parse(dynamicValue.ToString());
        }

        public static implicit operator long(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType) { return Convert.ToInt64(dynamicValue._value); }
            return long.Parse(dynamicValue.ToString());
        }

        public static implicit operator float(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType) { return Convert.ToSingle(dynamicValue._value); }
            return float.Parse(dynamicValue.ToString());
        }

        public static implicit operator decimal(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType) { return Convert.ToDecimal(dynamicValue._value); }
            return decimal.Parse(dynamicValue.ToString());
        }

        public static implicit operator double(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType) { return Convert.ToDouble(dynamicValue._value); }
            return double.Parse(dynamicValue.ToString());
        }

        #region Implementation of IConvertible
        /// <summary>
        /// Returns the <see cref="T:System.TypeCode"/> for this instance.
        /// </summary>
        /// <returns>
        /// The enumerated constant that is the <see cref="T:System.TypeCode"/> of the class or value type that implements this interface.
        /// </returns>
        /// <filterpriority>2</filterpriority>
        public TypeCode GetTypeCode()
        {
            if (_value == null) { return TypeCode.Empty; }
            return Type.GetTypeCode(_value.GetType());
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent Boolean value using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A Boolean value equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public bool ToBoolean(IFormatProvider provider) { return Convert.ToBoolean(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent Unicode character using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A Unicode character equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public char ToChar(IFormatProvider provider) { return Convert.ToChar(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent 8-bit signed integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 8-bit signed integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public sbyte ToSByte(IFormatProvider provider) { return Convert.ToSByte(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent 8-bit unsigned integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 8-bit unsigned integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public byte ToByte(IFormatProvider provider) { return Convert.ToByte(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent 16-bit signed integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 16-bit signed integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public short ToInt16(IFormatProvider provider) { return Convert.ToInt16(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent 16-bit unsigned integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 16-bit unsigned integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public ushort ToUInt16(IFormatProvider provider) { return Convert.ToUInt16(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent 32-bit signed integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 32-bit signed integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public int ToInt32(IFormatProvider provider) { return Convert.ToInt32(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent 32-bit unsigned integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 32-bit unsigned integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public uint ToUInt32(IFormatProvider provider) { return Convert.ToUInt32(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent 64-bit signed integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 64-bit signed integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public long ToInt64(IFormatProvider provider) { return Convert.ToInt64(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent 64-bit unsigned integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 64-bit unsigned integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public ulong ToUInt64(IFormatProvider provider) { return Convert.ToUInt64(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent single-precision floating-point number using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A single-precision floating-point number equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public float ToSingle(IFormatProvider provider) { return Convert.ToSingle(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent double-precision floating-point number using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A double-precision floating-point number equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public double ToDouble(IFormatProvider provider) { return Convert.ToDouble(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent <see cref="T:System.Decimal"/> number using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Decimal"/> number equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public decimal ToDecimal(IFormatProvider provider) { return Convert.ToDecimal(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent <see cref="T:System.DateTime"/> using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.DateTime"/> instance equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public DateTime ToDateTime(IFormatProvider provider) { return Convert.ToDateTime(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an equivalent <see cref="T:System.String"/> using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.String"/> instance equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public string ToString(IFormatProvider provider) { return Convert.ToString(_value, provider); }

        /// <summary>
        /// Converts the value of this instance to an <see cref="T:System.Object"/> of the specified <see cref="T:System.Type"/> that has an equivalent value, using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An <see cref="T:System.Object"/> instance of type <paramref name="conversionType"/> whose value is equivalent to the value of this instance.
        /// </returns>
        /// <param name="conversionType">The <see cref="T:System.Type"/> to which the value of this instance is converted. </param><param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public object ToType(Type conversionType, IFormatProvider provider) { return Convert.ChangeType(_value, conversionType, provider); }
        #endregion
    }
    #endregion

    #region MimeTypes
    //
    // Nancy.MimeTypes
    //
    // Authors:
    //	Gonzalo Paniagua Javier (gonzalo@ximian.com)
    //
    // (C) 2002 Ximian, Inc (http://www.ximian.com)
    // (C) 2003-2009 Novell, Inc (http://novell.com)

    //
    // Permission is hereby granted, free of charge, to any person obtaining
    // a copy of this software and associated documentation files (the
    // "Software"), to deal in the Software without restriction, including
    // without limitation the rights to use, copy, modify, merge, publish,
    // distribute, sublicense, and/or sell copies of the Software, and to
    // permit persons to whom the Software is furnished to do so, subject to
    // the following conditions:
    // 
    // The above copyright notice and this permission notice shall be
    // included in all copies or substantial portions of the Software.
    // 
    // THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
    // EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
    // MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
    // NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
    // LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
    // OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
    // WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
    //
    public sealed class MimeTypes
    {
        static readonly Dictionary<string, string> Types;

        static MimeTypes()
        {
            Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Types.Add("323", "text/h323");
            Types.Add("3dmf", "x-world/x-3dmf");
            Types.Add("3dm", "x-world/x-3dmf");
            Types.Add("7z", "application/x-7z-compressed");
            Types.Add("aab", "application/x-authorware-bin");
            Types.Add("aam", "application/x-authorware-map");
            Types.Add("aas", "application/x-authorware-seg");
            Types.Add("abc", "text/vnd.abc");
            Types.Add("acgi", "text/html");
            Types.Add("acx", "application/internet-property-stream");
            Types.Add("afl", "video/animaflex");
            Types.Add("ai", "application/postscript");
            Types.Add("aif", "audio/aiff");
            Types.Add("aifc", "audio/aiff");
            Types.Add("aiff", "audio/aiff");
            Types.Add("aim", "application/x-aim");
            Types.Add("aip", "text/x-audiosoft-intra");
            Types.Add("ani", "application/x-navi-animation");
            Types.Add("aos", "application/x-nokia-9000-communicator-add-on-software");
            Types.Add("application", "application/x-ms-application");
            Types.Add("aps", "application/mime");
            Types.Add("art", "image/x-jg");
            Types.Add("asf", "video/x-ms-asf");
            Types.Add("asm", "text/x-asm");
            Types.Add("asp", "text/asp");
            Types.Add("asr", "video/x-ms-asf");
            Types.Add("asx", "application/x-mplayer2");
            Types.Add("atom", "application/atom.xml");
            Types.Add("atomcat", "application/atomcat+xml");
            Types.Add("atomsvc", "application/atomsvc+xml");
            Types.Add("au", "audio/x-au");
            Types.Add("avi", "video/avi");
            Types.Add("avs", "video/avs-video");
            Types.Add("axs", "application/olescript");
            Types.Add("bas", "text/plain");
            Types.Add("bcpio", "application/x-bcpio");
            Types.Add("bin", "application/octet-stream");
            Types.Add("bm", "image/bmp");
            Types.Add("bmp", "image/bmp");
            Types.Add("boo", "application/book");
            Types.Add("book", "application/book");
            Types.Add("boz", "application/x-bzip2");
            Types.Add("bsh", "application/x-bsh");
            Types.Add("bz2", "application/x-bzip2");
            Types.Add("bz", "application/x-bzip");
            Types.Add("cat", "application/vnd.ms-pki.seccat");
            Types.Add("ccad", "application/clariscad");
            Types.Add("cco", "application/x-cocoa");
            Types.Add("cc", "text/plain");
            Types.Add("cdf", "application/cdf");
            Types.Add("cer", "application/pkix-cert");
            Types.Add("cha", "application/x-chat");
            Types.Add("chat", "application/x-chat");
            Types.Add("class", "application/java");
            Types.Add("clp", "application/x-msclip");
            Types.Add("cmx", "image/x-cmx");
            Types.Add("cod", "image/cis-cod");
            Types.Add("conf", "text/plain");
            Types.Add("cpio", "application/x-cpio");
            Types.Add("cpp", "text/plain");
            Types.Add("cpt", "application/x-cpt");
            Types.Add("crd", "application/x-mscardfile");
            Types.Add("crl", "application/pkix-crl");
            Types.Add("crt", "application/pkix-cert");
            Types.Add("csh", "application/x-csh");
            Types.Add("css", "text/css");
            Types.Add("c", "text/plain");
            Types.Add("c++", "text/plain");
            Types.Add("cs", "text/plain");
            Types.Add("cxx", "text/plain");
            Types.Add("dcr", "application/x-director");
            Types.Add("deepv", "application/x-deepv");
            Types.Add("def", "text/plain");
            Types.Add("deploy", "application/octet-stream");
            Types.Add("der", "application/x-x509-ca-cert");
            Types.Add("dib", "image/bmp");
            Types.Add("dif", "video/x-dv");
            Types.Add("dir", "application/x-director");
            Types.Add("disco", "application/xml");
            Types.Add("dll", "application/x-msdownload");
            Types.Add("dl", "video/dl");
            Types.Add("doc", "application/msword");
            Types.Add("dot", "application/msword");
            Types.Add("dp", "application/commonground");
            Types.Add("drw", "application/drafting");
            Types.Add("dvi", "application/x-dvi");
            Types.Add("dv", "video/x-dv");
            Types.Add("dwf", "drawing/x-dwf (old)");
            Types.Add("dwg", "application/acad");
            Types.Add("dxf", "application/dxf");
            Types.Add("dxr", "application/x-director");
            Types.Add("elc", "application/x-elc");
            Types.Add("el", "text/x-script.elisp");
            Types.Add("eml", "message/rfc822");
            Types.Add("eot", "application/vnd.bw-fontobject");
            Types.Add("eps", "application/postscript");
            Types.Add("es", "application/x-esrehber");
            Types.Add("etx", "text/x-setext");
            Types.Add("evy", "application/envoy");
            Types.Add("exe", "application/octet-stream");
            Types.Add("f77", "text/plain");
            Types.Add("f90", "text/plain");
            Types.Add("fdf", "application/vnd.fdf");
            Types.Add("fif", "image/fif");
            Types.Add("fli", "video/fli");
            Types.Add("flo", "image/florian");
            Types.Add("flr", "x-world/x-vrml");
            Types.Add("flx", "text/vnd.fmi.flexstor");
            Types.Add("fmf", "video/x-atomic3d-feature");
            Types.Add("for", "text/plain");
            Types.Add("fpx", "image/vnd.fpx");
            Types.Add("frl", "application/freeloader");
            Types.Add("f", "text/plain");
            Types.Add("funk", "audio/make");
            Types.Add("g3", "image/g3fax");
            Types.Add("gif", "image/gif");
            Types.Add("gl", "video/gl");
            Types.Add("gsd", "audio/x-gsm");
            Types.Add("gsm", "audio/x-gsm");
            Types.Add("gsp", "application/x-gsp");
            Types.Add("gss", "application/x-gss");
            Types.Add("gtar", "application/x-gtar");
            Types.Add("g", "text/plain");
            Types.Add("gz", "application/x-gzip");
            Types.Add("gzip", "application/x-gzip");
            Types.Add("hdf", "application/x-hdf");
            Types.Add("help", "application/x-helpfile");
            Types.Add("hgl", "application/vnd.hp-HPGL");
            Types.Add("hh", "text/plain");
            Types.Add("hlb", "text/x-script");
            Types.Add("hlp", "application/x-helpfile");
            Types.Add("hpg", "application/vnd.hp-HPGL");
            Types.Add("hpgl", "application/vnd.hp-HPGL");
            Types.Add("hqx", "application/binhex");
            Types.Add("hta", "application/hta");
            Types.Add("htc", "text/x-component");
            Types.Add("h", "text/plain");
            Types.Add("htmls", "text/html");
            Types.Add("html", "text/html");
            Types.Add("htm", "text/html");
            Types.Add("htt", "text/webviewhtml");
            Types.Add("htx", "text/html");
            Types.Add("ice", "x-conference/x-cooltalk");
            Types.Add("ico", "image/x-icon");
            Types.Add("idc", "text/plain");
            Types.Add("ief", "image/ief");
            Types.Add("iefs", "image/ief");
            Types.Add("iges", "application/iges");
            Types.Add("igs", "application/iges");
            Types.Add("iii", "application/x-iphone");
            Types.Add("ima", "application/x-ima");
            Types.Add("imap", "application/x-httpd-imap");
            Types.Add("inf", "application/inf");
            Types.Add("ins", "application/x-internett-signup");
            Types.Add("ip", "application/x-ip2");
            Types.Add("isp", "application/x-internet-signup");
            Types.Add("isu", "video/x-isvideo");
            Types.Add("it", "audio/it");
            Types.Add("iv", "application/x-inventor");
            Types.Add("ivf", "video/x-ivf");
            Types.Add("ivr", "i-world/i-vrml");
            Types.Add("ivy", "application/x-livescreen");
            Types.Add("jam", "audio/x-jam");
            Types.Add("java", "text/plain");
            Types.Add("jav", "text/plain");
            Types.Add("jcm", "application/x-java-commerce");
            Types.Add("jfif", "image/jpeg");
            Types.Add("jfif-tbnl", "image/jpeg");
            Types.Add("jpeg", "image/jpeg");
            Types.Add("jpe", "image/jpeg");
            Types.Add("jpg", "image/jpeg");
            Types.Add("jps", "image/x-jps");
            Types.Add("js", "application/x-javascript");
            Types.Add("json", "application/application/json");
            Types.Add("jut", "image/jutvision");
            Types.Add("kar", "audio/midi");
            Types.Add("ksh", "text/x-script.ksh");
            Types.Add("la", "audio/nspaudio");
            Types.Add("lam", "audio/x-liveaudio");
            Types.Add("latex", "application/x-latex");
            Types.Add("list", "text/plain");
            Types.Add("lma", "audio/nspaudio");
            Types.Add("log", "text/plain");
            Types.Add("lsp", "application/x-lisp");
            Types.Add("lst", "text/plain");
            Types.Add("lsx", "text/x-la-asf");
            Types.Add("ltx", "application/x-latex");
            Types.Add("m13", "application/x-msmediaview");
            Types.Add("m14", "application/x-msmediaview");
            Types.Add("m1v", "video/mpeg");
            Types.Add("m2a", "audio/mpeg");
            Types.Add("m2v", "video/mpeg");
            Types.Add("m3u", "audio/x-mpequrl");
            Types.Add("m4u", "video/x-mpegurl");
            Types.Add("m4v", "video/mp4");
            Types.Add("m4a", "audio/mp4");
            Types.Add("m4r", "audio/mp4");
            Types.Add("m4b", "audio/mp4");
            Types.Add("m4p", "audio/mp4");
            Types.Add("man", "application/x-troff-man");
            Types.Add("manifest", "application/x-ms-manifest");
            Types.Add("map", "application/x-navimap");
            Types.Add("mar", "text/plain");
            Types.Add("mbd", "application/mbedlet");
            Types.Add("mc$", "application/x-magic-cap-package-1.0");
            Types.Add("mcd", "application/mcad");
            Types.Add("mcf", "image/vasa");
            Types.Add("mcp", "application/netmc");
            Types.Add("mdb", "application/x-msaccess");
            Types.Add("me", "application/x-troff-me");
            Types.Add("mht", "message/rfc822");
            Types.Add("mhtml", "message/rfc822");
            Types.Add("mid", "audio/midi");
            Types.Add("midi", "audio/midi");
            Types.Add("mif", "application/x-mif");
            Types.Add("mime", "message/rfc822");
            Types.Add("mjf", "audio/x-vnd.AudioExplosion.MjuiceMediaFile");
            Types.Add("mjpg", "video/x-motion-jpeg");
            Types.Add("mm", "application/base64");
            Types.Add("mme", "application/base64");
            Types.Add("mny", "application/x-msmoney");
            Types.Add("mod", "audio/mod");
            Types.Add("moov", "video/quicktime");
            Types.Add("movie", "video/x-sgi-movie");
            Types.Add("mov", "video/quicktime");
            Types.Add("mp2", "video/mpeg");
            Types.Add("mp3", "audio/mpeg3");
            Types.Add("mp4", "video/mp4");
            Types.Add("mp4v", "video/mp4");
            Types.Add("mpa", "audio/mpeg");
            Types.Add("mpc", "application/x-project");
            Types.Add("mpeg", "video/mpeg");
            Types.Add("mpe", "video/mpeg");
            Types.Add("mpga", "audio/mpeg");
            Types.Add("mpg", "video/mpeg");
            Types.Add("mpg4", "video/mp4");
            Types.Add("mpp", "application/vnd.ms-project");
            Types.Add("mpt", "application/x-project");
            Types.Add("mpv2", "video/mpeg");
            Types.Add("mpv", "application/x-project");
            Types.Add("mpx", "application/x-project");
            Types.Add("mrc", "application/marc");
            Types.Add("ms", "application/x-troff-ms");
            Types.Add("m", "text/plain");
            Types.Add("mvb", "application/x-msmediaview");
            Types.Add("mv", "video/x-sgi-movie");
            Types.Add("my", "audio/make");
            Types.Add("mzz", "application/x-vnd.AudioExplosion.mzz");
            Types.Add("nap", "image/naplps");
            Types.Add("naplps", "image/naplps");
            Types.Add("nc", "application/x-netcdf");
            Types.Add("ncm", "application/vnd.nokia.configuration-message");
            Types.Add("niff", "image/x-niff");
            Types.Add("nif", "image/x-niff");
            Types.Add("nix", "application/x-mix-transfer");
            Types.Add("nsc", "application/x-conference");
            Types.Add("nvd", "application/x-navidoc");
            Types.Add("nws", "message/rfc822");
            Types.Add("oda", "application/oda");
            Types.Add("ods", "application/oleobject");
            Types.Add("oga", "audio/ogg");
            Types.Add("ogg", "audio/ogg");
            Types.Add("ogv", "video/ogg");
            Types.Add("omc", "application/x-omc");
            Types.Add("omcd", "application/x-omcdatamaker");
            Types.Add("omcr", "application/x-omcregerator");
            Types.Add("otf", "application/x-font-otf");
            Types.Add("p10", "application/pkcs10");
            Types.Add("p12", "application/pkcs-12");
            Types.Add("p7a", "application/x-pkcs7-signature");
            Types.Add("p7b", "application/x-pkcs7-certificates");
            Types.Add("p7c", "application/pkcs7-mime");
            Types.Add("p7m", "application/pkcs7-mime");
            Types.Add("p7r", "application/x-pkcs7-certreqresp");
            Types.Add("p7s", "application/pkcs7-signature");
            Types.Add("part", "application/pro_eng");
            Types.Add("pas", "text/pascal");
            Types.Add("pbm", "image/x-portable-bitmap");
            Types.Add("pcl", "application/x-pcl");
            Types.Add("pct", "image/x-pict");
            Types.Add("pcx", "image/x-pcx");
            Types.Add("pdb", "chemical/x-pdb");
            Types.Add("pdf", "application/pdf");
            Types.Add("pfunk", "audio/make");
            Types.Add("pfx", "application/x-pkcs12");
            Types.Add("pgm", "image/x-portable-graymap");
            Types.Add("pic", "image/pict");
            Types.Add("pict", "image/pict");
            Types.Add("pkg", "application/x-newton-compatible-pkg");
            Types.Add("pko", "application/vnd.ms-pki.pko");
            Types.Add("pl", "text/plain");
            Types.Add("plx", "application/x-PiXCLscript");
            Types.Add("pm4", "application/x-pagemaker");
            Types.Add("pm5", "application/x-pagemaker");
            Types.Add("pma", "application/x-perfmon");
            Types.Add("pmc", "application/x-perfmon");
            Types.Add("pm", "image/x-xpixmap");
            Types.Add("pml", "application/x-perfmon");
            Types.Add("pmr", "application/x-perfmon");
            Types.Add("pmw", "application/x-perfmon");
            Types.Add("png", "image/png");
            Types.Add("pnm", "application/x-portable-anymap");
            Types.Add("pot", "application/mspowerpoint");
            Types.Add("pov", "model/x-pov");
            Types.Add("ppa", "application/vnd.ms-powerpoint");
            Types.Add("ppm", "image/x-portable-pixmap");
            Types.Add("pps", "application/mspowerpoint");
            Types.Add("ppt", "application/mspowerpoint");
            Types.Add("ppz", "application/mspowerpoint");
            Types.Add("pre", "application/x-freelance");
            Types.Add("prf", "application/pics-rules");
            Types.Add("prt", "application/pro_eng");
            Types.Add("ps", "application/postscript");
            Types.Add("p", "text/x-pascal");
            Types.Add("pub", "application/x-mspublisher");
            Types.Add("pvu", "paleovu/x-pv");
            Types.Add("pwz", "application/vnd.ms-powerpoint");
            Types.Add("pyc", "applicaiton/x-bytecode.python");
            Types.Add("py", "text/x-script.phyton");
            Types.Add("qcp", "audio/vnd.qcelp");
            Types.Add("qd3d", "x-world/x-3dmf");
            Types.Add("qd3", "x-world/x-3dmf");
            Types.Add("qif", "image/x-quicktime");
            Types.Add("qtc", "video/x-qtc");
            Types.Add("qtif", "image/x-quicktime");
            Types.Add("qti", "image/x-quicktime");
            Types.Add("qt", "video/quicktime");
            Types.Add("ra", "audio/x-pn-realaudio");
            Types.Add("ram", "audio/x-pn-realaudio");
            Types.Add("ras", "application/x-cmu-raster");
            Types.Add("rast", "image/cmu-raster");
            Types.Add("rexx", "text/x-script.rexx");
            Types.Add("rf", "image/vnd.rn-realflash");
            Types.Add("rgb", "image/x-rgb");
            Types.Add("rm", "application/vnd.rn-realmedia");
            Types.Add("rmi", "audio/mid");
            Types.Add("rmm", "audio/x-pn-realaudio");
            Types.Add("rmp", "audio/x-pn-realaudio");
            Types.Add("rng", "application/ringing-tones");
            Types.Add("rnx", "application/vnd.rn-realplayer");
            Types.Add("roff", "application/x-troff");
            Types.Add("rp", "image/vnd.rn-realpix");
            Types.Add("rpm", "audio/x-pn-realaudio-plugin");
            Types.Add("rss", "application/xml");
            Types.Add("rtf", "text/richtext");
            Types.Add("rt", "text/richtext");
            Types.Add("rtx", "text/richtext");
            Types.Add("rv", "video/vnd.rn-realvideo");
            Types.Add("s3m", "audio/s3m");
            Types.Add("sbk", "application/x-tbook");
            Types.Add("scd", "application/x-msschedule");
            Types.Add("scm", "application/x-lotusscreencam");
            Types.Add("sct", "text/scriptlet");
            Types.Add("sdml", "text/plain");
            Types.Add("sdp", "application/sdp");
            Types.Add("sdr", "application/sounder");
            Types.Add("sea", "application/sea");
            Types.Add("set", "application/set");
            Types.Add("setpay", "application/set-payment-initiation");
            Types.Add("setreg", "application/set-registration-initiation");
            Types.Add("sgml", "text/sgml");
            Types.Add("sgm", "text/sgml");
            Types.Add("shar", "application/x-bsh");
            Types.Add("sh", "text/x-script.sh");
            Types.Add("shtml", "text/html");
            Types.Add("sid", "audio/x-psid");
            Types.Add("sit", "application/x-sit");
            Types.Add("skd", "application/x-koan");
            Types.Add("skm", "application/x-koan");
            Types.Add("skp", "application/x-koan");
            Types.Add("skt", "application/x-koan");
            Types.Add("sl", "application/x-seelogo");
            Types.Add("smi", "application/smil");
            Types.Add("smil", "application/smil");
            Types.Add("snd", "audio/basic");
            Types.Add("sol", "application/solids");
            Types.Add("spc", "application/x-pkcs7-certificates");
            Types.Add("spl", "application/futuresplash");
            Types.Add("spr", "application/x-sprite");
            Types.Add("sprite", "application/x-sprite");
            Types.Add("spx", "audio/ogg");
            Types.Add("src", "application/x-wais-source");
            Types.Add("ssi", "text/x-server-parsed-html");
            Types.Add("ssm", "application/streamingmedia");
            Types.Add("sst", "application/vnd.ms-pki.certstore");
            Types.Add("step", "application/step");
            Types.Add("s", "text/x-asm");
            Types.Add("stl", "application/sla");
            Types.Add("stm", "text/html");
            Types.Add("stp", "application/step");
            Types.Add("sv4cpio", "application/x-sv4cpio");
            Types.Add("sv4crc", "application/x-sv4crc");
            Types.Add("svf", "image/x-dwg");
            Types.Add("svg", "image/svg+xml");
            Types.Add("svgz", "image/svg+xml");
            Types.Add("svr", "application/x-world");
            Types.Add("swf", "application/x-shockwave-flash");
            Types.Add("talk", "text/x-speech");
            Types.Add("t", "application/x-troff");
            Types.Add("tar", "application/x-tar");
            Types.Add("tbk", "application/toolbook");
            Types.Add("tcl", "text/x-script.tcl");
            Types.Add("tcsh", "text/x-script.tcsh");
            Types.Add("tex", "application/x-tex");
            Types.Add("texi", "application/x-texinfo");
            Types.Add("texinfo", "application/x-texinfo");
            Types.Add("text", "text/plain");
            Types.Add("tgz", "application/x-compressed");
            Types.Add("tiff", "image/tiff");
            Types.Add("tif", "image/tiff");
            Types.Add("torrent", "application/x-bittorrent");
            Types.Add("tr", "application/x-troff");
            Types.Add("trm", "application/x-msterminal");
            Types.Add("tsi", "audio/tsp-audio");
            Types.Add("tsp", "audio/tsplayer");
            Types.Add("tsv", "text/tab-separated-values");
            Types.Add("ttf", "application/x-font-ttf");
            Types.Add("turbot", "image/florian");
            Types.Add("txt", "text/plain");
            Types.Add("uil", "text/x-uil");
            Types.Add("uls", "text/iuls");
            Types.Add("unis", "text/uri-list");
            Types.Add("uni", "text/uri-list");
            Types.Add("unv", "application/i-deas");
            Types.Add("uris", "text/uri-list");
            Types.Add("uri", "text/uri-list");
            Types.Add("ustar", "multipart/x-ustar");
            Types.Add("uue", "text/x-uuencode");
            Types.Add("uu", "text/x-uuencode");
            Types.Add("vcd", "application/x-cdlink");
            Types.Add("vcf", "text/x-vcard");
            Types.Add("vcs", "text/x-vCalendar");
            Types.Add("vda", "application/vda");
            Types.Add("vdo", "video/vdo");
            Types.Add("vew", "application/groupwise");
            Types.Add("vivo", "video/vivo");
            Types.Add("viv", "video/vivo");
            Types.Add("vmd", "application/vocaltec-media-desc");
            Types.Add("vmf", "application/vocaltec-media-file");
            Types.Add("voc", "audio/voc");
            Types.Add("vos", "video/vosaic");
            Types.Add("vox", "audio/voxware");
            Types.Add("vqe", "audio/x-twinvq-plugin");
            Types.Add("vqf", "audio/x-twinvq");
            Types.Add("vql", "audio/x-twinvq-plugin");
            Types.Add("vrml", "application/x-vrml");
            Types.Add("vrt", "x-world/x-vrt");
            Types.Add("vsd", "application/x-visio");
            Types.Add("vst", "application/x-visio");
            Types.Add("vsw", "application/x-visio");
            Types.Add("w60", "application/wordperfect6.0");
            Types.Add("w61", "application/wordperfect6.1");
            Types.Add("w6w", "application/msword");
            Types.Add("wav", "audio/wav");
            Types.Add("wb1", "application/x-qpro");
            Types.Add("wbmp", "image/vnd.wap.wbmp");
            Types.Add("wcm", "application/vnd.ms-works");
            Types.Add("wdb", "application/vnd.ms-works");
            Types.Add("web", "application/vnd.xara");
            Types.Add("webm", "video/webm");
            Types.Add("wiz", "application/msword");
            Types.Add("wk1", "application/x-123");
            Types.Add("wks", "application/vnd.ms-works");
            Types.Add("wmf", "windows/metafile");
            Types.Add("wmlc", "application/vnd.wap.wmlc");
            Types.Add("wmlsc", "application/vnd.wap.wmlscriptc");
            Types.Add("wmls", "text/vnd.wap.wmlscript");
            Types.Add("wml", "text/vnd.wap.wml");
            Types.Add("woff", "application/x-woff");
            Types.Add("word", "application/msword");
            Types.Add("wp5", "application/wordperfect");
            Types.Add("wp6", "application/wordperfect");
            Types.Add("wp", "application/wordperfect");
            Types.Add("wpd", "application/wordperfect");
            Types.Add("wps", "application/vnd.ms-works");
            Types.Add("wq1", "application/x-lotus");
            Types.Add("wri", "application/mswrite");
            Types.Add("wrl", "application/x-world");
            Types.Add("wrz", "model/vrml");
            Types.Add("wsc", "text/scriplet");
            Types.Add("wsdl", "application/xml");
            Types.Add("wsrc", "application/x-wais-source");
            Types.Add("wtk", "application/x-wintalk");
            Types.Add("xaf", "x-world/x-vrml");
            Types.Add("xaml", "application/xaml+xml");
            Types.Add("xap", "application/x-silverlight-app");
            Types.Add("xbap", "application/x-ms-xbap");
            Types.Add("xbm", "image/x-xbitmap");
            Types.Add("xdr", "video/x-amt-demorun");
            Types.Add("xgz", "xgl/drawing");
            Types.Add("xhtml", "application/xhtml+xml");
            Types.Add("xht", "application/xhtml+xml");
            Types.Add("xif", "image/vnd.xiff");
            Types.Add("xla", "application/excel");
            Types.Add("xl", "application/excel");
            Types.Add("xlb", "application/excel");
            Types.Add("xlc", "application/excel");
            Types.Add("xld", "application/excel");
            Types.Add("xlk", "application/excel");
            Types.Add("xll", "application/excel");
            Types.Add("xlm", "application/excel");
            Types.Add("xls", "application/excel");
            Types.Add("xlt", "application/excel");
            Types.Add("xlv", "application/excel");
            Types.Add("xlw", "application/excel");
            Types.Add("xm", "audio/xm");
            Types.Add("xml", "application/xml");
            Types.Add("xmz", "xgl/movie");
            Types.Add("xof", "x-world/x-vrml");
            Types.Add("xpi", "application/x-xpinstall");
            Types.Add("xpix", "application/x-vnd.ls-xpix");
            Types.Add("xpm", "image/xpm");
            Types.Add("x-png", "image/png");
            Types.Add("xsd", "application/xml");
            Types.Add("xsl", "application/xml");
            Types.Add("xsr", "video/x-amt-showrun");
            Types.Add("xwd", "image/x-xwd");
            Types.Add("xyz", "chemical/x-pdb");
            Types.Add("z", "application/x-compressed");
            Types.Add("zip", "application/zip");
            Types.Add("zsh", "text/x-script.zsh");

            // Office Formats
            Types.Add("docm", "application/vnd.ms-word.document.macroEnabled.12");
            Types.Add("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            Types.Add("dotm", "application/vnd.ms-word.template.macroEnabled.12");
            Types.Add("dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.template");
            Types.Add("potm", "application/vnd.ms-powerpoint.template.macroEnabled.12");
            Types.Add("potx", "application/vnd.openxmlformats-officedocument.presentationml.template");
            Types.Add("ppam", "application/vnd.ms-powerpoint.addin.macroEnabled.12");
            Types.Add("ppsm", "application/vnd.ms-powerpoint.slideshow.macroEnabled.12");
            Types.Add("ppsx", "application/vnd.openxmlformats-officedocument.presentationml.slideshow");
            Types.Add("pptm", "application/vnd.ms-powerpoint.presentation.macroEnabled.12");
            Types.Add("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation");
            Types.Add("xlam", "application/vnd.ms-excel.addin.macroEnabled.12");
            Types.Add("xlsb", "application/vnd.ms-excel.sheet.binary.macroEnabled.12");
            Types.Add("xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12");
            Types.Add("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            Types.Add("xltm", "application/vnd.ms-excel.template.macroEnabled.12");
            Types.Add("xltx", "application/vnd.openxmlformats-officedocument.spreadsheetml.template");
        }

        public static string GetMimeType(string fileName)
        {
            if (fileName == null) { return null; }
            string result = null;
            var dot = fileName.LastIndexOf('.');
            if (dot != -1 && fileName.Length > dot + 1) { Types.TryGetValue(fileName.Substring(dot + 1), out result); }
            return result ?? "application/octet-stream";
        }
    }
    #endregion

    #region Extensions
    public static class StreamExtensions
    {
        public static void WriteString(this Stream stream, string s)
        {
            var writer = new StreamWriter(stream) {AutoFlush = true};
            writer.Write(s);
        }
    }
    #endregion
}