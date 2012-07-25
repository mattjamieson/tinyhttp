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

        public TinyHttpHost(string baseUri, IRequestProcessor requestProcessor)
        {
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
            { }
        }

        private static void WriteResponse(Response response, HttpListenerResponse httpListenerResponse)
        {
            foreach (var header in response.Headers) { httpListenerResponse.AddHeader(header.Key, header.Value); }
            httpListenerResponse.StatusCode = (int)response.StatusCode;
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

                var parameters = new DynamicDictionary();
                foreach (var paramName in _paramNames) { parameters[paramName] = match.Groups[paramName]; }

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
        public FileResponse(string filePath)
            : this(filePath, MimeTypes.GetMimeType(filePath))
        { }

        public FileResponse(string filePath, string contentType)
        {
            if (String.IsNullOrEmpty(filePath))
            {
                StatusCode = HttpStatusCode.NotFound;
                return;
            }

            var fileInfo = new FileInfo(filePath);
            Headers["ETag"] = fileInfo.LastWriteTimeUtc.Ticks.ToString("x");
            Headers["Last-Modified"] = fileInfo.LastWriteTimeUtc.ToString("R");
            Body = stream => { using (var file = File.OpenRead(filePath)) { file.CopyTo(stream); } };
            ContentType = contentType;
            StatusCode = HttpStatusCode.OK;
        }
    }

    public class TextResponse : Response
    {
        public TextResponse(string body, string contentType = "text/plain", Encoding encoding = null)
        {
            if (encoding == null) { encoding = Encoding.UTF8; }

            ContentType = contentType;
            StatusCode = HttpStatusCode.OK;

            if (body != null)
            {
                Body = stream =>
                {
                    var data = encoding.GetBytes(body);
                    stream.Write(data, 0, data.Length);
                };
            }
        }
    }

    public class HtmlResponse : TextResponse
    {
        public HtmlResponse(string body, string contentType = "text/html", Encoding encoding = null)
            : base(body, contentType, encoding)
        {
            if (encoding == null) { encoding = Encoding.UTF8; }

            ContentType = contentType;
            StatusCode = HttpStatusCode.OK;

            if (body != null)
            {
                Body = stream =>
                {
                    var data = encoding.GetBytes(body);
                    stream.Write(data, 0, data.Length);
                };
            }
        }
    }

    public class NotFoundResponse : Response
    {
        public NotFoundResponse()
        {
            ContentType = "text/html";
            StatusCode = HttpStatusCode.NotFound;
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
        public static DynamicDictionary Empty
        {
            get
            {
                return new DynamicDictionary();
            }
        }

        /// <summary>
        /// Creates a dynamic dictionary from an <see cref="IDictionary{TKey,TValue}"/> instance.
        /// </summary>
        /// <param name="values">An <see cref="IDictionary{TKey,TValue}"/> instance, that the dynamic dictionary should be created from.</param>
        /// <returns>An <see cref="DynamicDictionary"/> instance.</returns>
        public static DynamicDictionary Create(IDictionary<string, object> values)
        {
            var instance = new DynamicDictionary();

            foreach (var key in values.Keys)
            {
                instance[key] = values[key];
            }

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
            if (!_dictionary.TryGetValue(binder.Name, out result))
            {
                result = new DynamicDictionaryValue(null);
            }

            return true;
        }

        /// <summary>
        /// Returns the enumeration of all dynamic member names.
        /// </summary>
        /// <returns>A <see cref="IEnumerable{T}"/> that contains dynamic member names.</returns>
        public override IEnumerable<string> GetDynamicMemberNames()
        {
            return _dictionary.Keys;
        }

        /// <summary>
        /// Returns the enumeration of all dynamic member names.
        /// </summary>
        /// <returns>A <see cref="IEnumerable{T}"/> that contains dynamic member names.</returns>
        public IEnumerator<string> GetEnumerator()
        {
            return _dictionary.Keys.GetEnumerator();
        }

        /// <summary>
        /// Returns the enumeration of all dynamic member names.
        /// </summary>
        /// <returns>A <see cref="IEnumerator"/> that contains dynamic member names.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return _dictionary.Keys.GetEnumerator();
        }

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
                if (!_dictionary.TryGetValue(name, out member))
                {
                    member = new DynamicDictionaryValue(null);
                }

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
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            return ReferenceEquals(this, other) || Equals(other._dictionary, _dictionary);
        }

        /// <summary>
        /// Determines whether the specified <see cref="System.Object"/> is equal to this instance.
        /// </summary>
        /// <param name="obj">The <see cref="System.Object"/> to compare with this instance.</param>
        /// <returns><see langword="true"/> if the specified <see cref="System.Object"/> is equal to this instance; otherwise, <see langword="false"/>.</returns>
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            return obj.GetType() == typeof(DynamicDictionary) && Equals((DynamicDictionary)obj);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the collection.</returns>
        IEnumerator<KeyValuePair<string, dynamic>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator()
        {
            return _dictionary.GetEnumerator();
        }

        /// <summary>
        /// Returns a hash code for this <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns> A hash code for this <see cref="DynamicDictionary"/>, suitable for use in hashing algorithms and data structures like a hash table.</returns>
        public override int GetHashCode()
        {
            return (_dictionary != null ? _dictionary.GetHashCode() : 0);
        }

        /// <summary>
        /// Adds an element with the provided key and value to the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <param name="key">The object to use as the key of the element to add.</param>
        /// <param name="value">The object to use as the value of the element to add.</param>
        public void Add(string key, dynamic value)
        {
            this[key] = value;
        }

        /// <summary>
        /// Adds an item to the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <param name="item">The object to add to the <see cref="DynamicDictionary"/>.</param>
        public void Add(KeyValuePair<string, dynamic> item)
        {
            this[item.Key] = item.Value;
        }

        /// <summary>
        /// Determines whether the <see cref="DynamicDictionary"/> contains an element with the specified key.
        /// </summary>
        /// <returns><see langword="true" /> if the <see cref="DynamicDictionary"/> contains an element with the key; otherwise, <see langword="false" />.
        /// </returns>
        /// <param name="key">The key to locate in the <see cref="DynamicDictionary"/>.</param>
        public bool ContainsKey(string key)
        {
            return _dictionary.ContainsKey(key);
        }

        /// <summary>
        /// Gets an <see cref="T:System.Collections.Generic.ICollection`1"/> containing the keys of the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns>An <see cref="T:System.Collections.Generic.ICollection`1"/> containing the keys of the <see cref="DynamicDictionary"/>.</returns>
        public ICollection<string> Keys
        {
            get { return _dictionary.Keys; }
        }

        /// <summary>
        /// Gets the value associated with the specified key.
        /// </summary>
        /// <returns><see langword="true" /> if the <see cref="DynamicDictionary"/> contains an element with the specified key; otherwise, <see langword="false" />.</returns>
        /// <param name="key">The key whose value to get.</param>
        /// <param name="value">When this method returns, the value associated with the specified key, if the key is found; otherwise, the default value for the type of the <paramref name="value"/> parameter. This parameter is passed uninitialized.</param>
        public bool TryGetValue(string key, out dynamic value)
        {
            return _dictionary.TryGetValue(key, out value);
        }

        /// <summary>
        /// Removes all items from the <see cref="DynamicDictionary"/>.
        /// </summary>
        public void Clear()
        {
            _dictionary.Clear();
        }

        /// <summary>
        /// Gets the number of elements contained in the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns>The number of elements contained in the <see cref="DynamicDictionary"/>.</returns>
        public int Count
        {
            get { return _dictionary.Count; }
        }

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
        public void CopyTo(KeyValuePair<string, dynamic>[] array, int arrayIndex)
        {
            _dictionary.CopyTo(array, arrayIndex);
        }

        /// <summary>
        /// Gets a value indicating whether the <see cref="DynamicDictionary"/> is read-only.
        /// </summary>
        /// <returns>Always returns <see langword="false" />.</returns>
        public bool IsReadOnly
        {
            get { return false; }
        }

        /// <summary>
        /// Removes the element with the specified key from the <see cref="DynamicDictionary"/>.
        /// </summary>
        /// <returns><see langword="true" /> if the element is successfully removed; otherwise, <see langword="false" />.</returns>
        /// <param name="key">The key of the element to remove.</param>
        public bool Remove(string key)
        {
            return _dictionary.Remove(key);
        }

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
        public ICollection<dynamic> Values
        {
            get { return _dictionary.Values; }
        }

        private static KeyValuePair<string, dynamic> GetDynamicKeyValuePair(KeyValuePair<string, dynamic> item)
        {
            var dynamicValueKeyValuePair = new KeyValuePair<string, dynamic>(item.Key, new DynamicDictionaryValue(item.Value));
            return dynamicValueKeyValuePair;
        }

        private static string GetNeutralKey(string key)
        {
            return key.Replace("-", string.Empty);
        }
    }

    public class DynamicDictionaryValue : DynamicObject, IEquatable<DynamicDictionaryValue>, IConvertible
    {
        private readonly object _value;

        /// <summary>
        /// Initializes a new instance of the <see cref="DynamicDictionaryValue"/> class.
        /// </summary>
        /// <param name="value">The value to store in the instance</param>
        public DynamicDictionaryValue(object value)
        {
            _value = value;
        }

        /// <summary>
        /// Gets a value indicating whether this instance has value.
        /// </summary>
        /// <value><c>true</c> if this instance has value; otherwise, <c>false</c>.</value>
        /// <remarks><see langword="null"/> is considered as not being a value.</remarks>
        public bool HasValue
        {
            get { return (_value != null); }
        }

        /// <summary>
        /// Gets the inner value
        /// </summary>
        public object Value
        {
            get { return _value; }
        }

        public static bool operator ==(DynamicDictionaryValue dynamicValue, object compareValue)
        {
            if (dynamicValue._value == null && compareValue == null)
            {
                return true;
            }

            return dynamicValue._value != null && dynamicValue._value.Equals(compareValue);
        }

        public static bool operator !=(DynamicDictionaryValue dynamicValue, object compareValue)
        {
            return !(dynamicValue == compareValue);
        }

        /// <summary>
        /// Indicates whether the current object is equal to another object of the same type.
        /// </summary>
        /// <returns><c>true</c> if the current object is equal to the <paramref name="compareValue"/> parameter; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="compareValue">An <see cref="DynamicDictionaryValue"/> to compare with this instance.</param>
        public bool Equals(DynamicDictionaryValue compareValue)
        {
            if (ReferenceEquals(null, compareValue))
            {
                return false;
            }

            return ReferenceEquals(this, compareValue) || Equals(compareValue._value, _value);
        }

        /// <summary>
        /// Determines whether the specified <see cref="object"/> is equal to the current <see cref="object"/>.
        /// </summary>
        /// <returns><c>true</c> if the specified <see cref="object"/> is equal to the current <see cref="DynamicDictionaryValue"/>; otherwise, <c>false</c>.</returns>
        /// <param name="compareValue">The <see cref="object"/> to compare with the current <see cref="DynamicDictionaryValue"/>.</param>
        public override bool Equals(object compareValue)
        {
            if (ReferenceEquals(null, compareValue))
            {
                return false;
            }

            if (ReferenceEquals(this, compareValue))
            {
                return true;
            }

            return compareValue.GetType() == typeof(DynamicDictionaryValue) && Equals((DynamicDictionaryValue)compareValue);
        }

        /// <summary>
        /// Serves as a hash function for a particular type. 
        /// </summary>
        /// <returns>A hash code for the current instance.</returns>
        public override int GetHashCode()
        {
            return (_value != null ? _value.GetHashCode() : 0);
        }

        /// <summary>
        /// Provides implementation for binary operations. Classes derived from the <see cref="T:System.Dynamic.DynamicObject"/> class can override this method to specify dynamic behavior for operations such as addition and multiplication.
        /// </summary>
        /// <returns><c>true</c> if the operation is successful; otherwise, <c>false</c>. If this method returns <c>false</c>, the run-time binder of the language determines the behavior. (In most cases, a language-specific run-time exception is thrown.)</returns>
        /// <param name="binder">Provides information about the binary operation. The binder.Operation property returns an <see cref="T:System.Linq.Expressions.ExpressionType"/> object. For example, for the sum = first + second statement, where first and second are derived from the DynamicObject class, binder.Operation returns ExpressionType.Add.</param><param name="arg">The right operand for the binary operation. For example, for the sum = first + second statement, where first and second are derived from the DynamicObject class, <paramref name="arg"/> is equal to second.</param><param name="result">The result of the binary operation.</param>
        public override bool TryBinaryOperation(BinaryOperationBinder binder, object arg, out object result)
        {
            object resultOfCast;
            result = null;

            if (binder.Operation != ExpressionType.Equal)
            {
                return false;
            }

            var convert =
                Binder.Convert(CSharpBinderFlags.None, arg.GetType(), typeof(DynamicDictionaryValue));

            if (!TryConvert((ConvertBinder)convert, out resultOfCast))
            {
                return false;
            }

            result = (resultOfCast == null) ?
                Equals(arg, resultOfCast) :
                resultOfCast.Equals(arg);

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

            if (_value == null)
            {
                return true;
            }

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
                if (binderType.IsGenericType && binderType.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    binderType = binderType.GetGenericArguments()[0];
                }

                var typeCode = Type.GetTypeCode(binderType);

                if (typeCode == TypeCode.Object) // something went wrong here
                {
                    return false;
                }

                result = Convert.ChangeType(_value, typeCode);

                return true;
            }
            return base.TryConvert(binder, out result);
        }

        public override string ToString()
        {
            return this._value == null ? base.ToString() : Convert.ToString(this._value);
        }

        public static implicit operator bool(DynamicDictionaryValue dynamicValue)
        {
            if (!dynamicValue.HasValue)
            {
                return false;
            }

            if (dynamicValue._value.GetType().IsValueType)
            {
                return (Convert.ToBoolean(dynamicValue._value));
            }

            bool result;
            if (bool.TryParse(dynamicValue.ToString(), out result))
            {
                return result;
            }

            return true;
        }

        public static implicit operator string(DynamicDictionaryValue dynamicValue)
        {
            return dynamicValue.ToString();
        }

        public static implicit operator int(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType)
            {
                return Convert.ToInt32(dynamicValue._value);
            }

            return int.Parse(dynamicValue.ToString());
        }

        public static implicit operator Guid(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value is Guid)
            {
                return (Guid)dynamicValue._value;
            }

            return Guid.Parse(dynamicValue.ToString());
        }

        public static implicit operator DateTime(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value is DateTime)
            {
                return (DateTime)dynamicValue._value;
            }

            return DateTime.Parse(dynamicValue.ToString());
        }

        public static implicit operator TimeSpan(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value is TimeSpan)
            {
                return (TimeSpan)dynamicValue._value;
            }

            return TimeSpan.Parse(dynamicValue.ToString());
        }

        public static implicit operator long(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType)
            {
                return Convert.ToInt64(dynamicValue._value);
            }

            return long.Parse(dynamicValue.ToString());
        }

        public static implicit operator float(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType)
            {
                return Convert.ToSingle(dynamicValue._value);
            }

            return float.Parse(dynamicValue.ToString());
        }

        public static implicit operator decimal(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType)
            {
                return Convert.ToDecimal(dynamicValue._value);
            }

            return decimal.Parse(dynamicValue.ToString());
        }

        public static implicit operator double(DynamicDictionaryValue dynamicValue)
        {
            if (dynamicValue._value.GetType().IsValueType)
            {
                return Convert.ToDouble(dynamicValue._value);
            }

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
            if (_value == null) return TypeCode.Empty;
            return Type.GetTypeCode(_value.GetType());
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent Boolean value using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A Boolean value equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public bool ToBoolean(IFormatProvider provider)
        {
            return Convert.ToBoolean(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent Unicode character using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A Unicode character equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public char ToChar(IFormatProvider provider)
        {
            return Convert.ToChar(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent 8-bit signed integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 8-bit signed integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public sbyte ToSByte(IFormatProvider provider)
        {
            return Convert.ToSByte(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent 8-bit unsigned integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 8-bit unsigned integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public byte ToByte(IFormatProvider provider)
        {
            return Convert.ToByte(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent 16-bit signed integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 16-bit signed integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public short ToInt16(IFormatProvider provider)
        {
            return Convert.ToInt16(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent 16-bit unsigned integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 16-bit unsigned integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public ushort ToUInt16(IFormatProvider provider)
        {
            return Convert.ToUInt16(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent 32-bit signed integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 32-bit signed integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public int ToInt32(IFormatProvider provider)
        {
            return Convert.ToInt32(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent 32-bit unsigned integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 32-bit unsigned integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public uint ToUInt32(IFormatProvider provider)
        {
            return Convert.ToUInt32(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent 64-bit signed integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 64-bit signed integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public long ToInt64(IFormatProvider provider)
        {
            return Convert.ToInt64(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent 64-bit unsigned integer using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An 64-bit unsigned integer equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public ulong ToUInt64(IFormatProvider provider)
        {
            return Convert.ToUInt64(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent single-precision floating-point number using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A single-precision floating-point number equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public float ToSingle(IFormatProvider provider)
        {
            return Convert.ToSingle(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent double-precision floating-point number using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A double-precision floating-point number equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public double ToDouble(IFormatProvider provider)
        {
            return Convert.ToDouble(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent <see cref="T:System.Decimal"/> number using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Decimal"/> number equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public decimal ToDecimal(IFormatProvider provider)
        {
            return Convert.ToDecimal(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent <see cref="T:System.DateTime"/> using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.DateTime"/> instance equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public DateTime ToDateTime(IFormatProvider provider)
        {
            return Convert.ToDateTime(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an equivalent <see cref="T:System.String"/> using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.String"/> instance equivalent to the value of this instance.
        /// </returns>
        /// <param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public string ToString(IFormatProvider provider)
        {
            return Convert.ToString(_value, provider);
        }

        /// <summary>
        /// Converts the value of this instance to an <see cref="T:System.Object"/> of the specified <see cref="T:System.Type"/> that has an equivalent value, using the specified culture-specific formatting information.
        /// </summary>
        /// <returns>
        /// An <see cref="T:System.Object"/> instance of type <paramref name="conversionType"/> whose value is equivalent to the value of this instance.
        /// </returns>
        /// <param name="conversionType">The <see cref="T:System.Type"/> to which the value of this instance is converted. </param><param name="provider">An <see cref="T:System.IFormatProvider"/> interface implementation that supplies culture-specific formatting information. </param><filterpriority>2</filterpriority>
        public object ToType(Type conversionType, IFormatProvider provider)
        {
            return Convert.ChangeType(_value, conversionType, provider);
        }

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
        static readonly Dictionary<string, string> mimeTypes;

        static MimeTypes()
        {
            mimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            mimeTypes.Add("323", "text/h323");
            mimeTypes.Add("3dmf", "x-world/x-3dmf");
            mimeTypes.Add("3dm", "x-world/x-3dmf");
            mimeTypes.Add("7z", "application/x-7z-compressed");
            mimeTypes.Add("aab", "application/x-authorware-bin");
            mimeTypes.Add("aam", "application/x-authorware-map");
            mimeTypes.Add("aas", "application/x-authorware-seg");
            mimeTypes.Add("abc", "text/vnd.abc");
            mimeTypes.Add("acgi", "text/html");
            mimeTypes.Add("acx", "application/internet-property-stream");
            mimeTypes.Add("afl", "video/animaflex");
            mimeTypes.Add("ai", "application/postscript");
            mimeTypes.Add("aif", "audio/aiff");
            mimeTypes.Add("aifc", "audio/aiff");
            mimeTypes.Add("aiff", "audio/aiff");
            mimeTypes.Add("aim", "application/x-aim");
            mimeTypes.Add("aip", "text/x-audiosoft-intra");
            mimeTypes.Add("ani", "application/x-navi-animation");
            mimeTypes.Add("aos", "application/x-nokia-9000-communicator-add-on-software");
            mimeTypes.Add("application", "application/x-ms-application");
            mimeTypes.Add("aps", "application/mime");
            mimeTypes.Add("art", "image/x-jg");
            mimeTypes.Add("asf", "video/x-ms-asf");
            mimeTypes.Add("asm", "text/x-asm");
            mimeTypes.Add("asp", "text/asp");
            mimeTypes.Add("asr", "video/x-ms-asf");
            mimeTypes.Add("asx", "application/x-mplayer2");
            mimeTypes.Add("atom", "application/atom.xml");
            mimeTypes.Add("atomcat", "application/atomcat+xml");
            mimeTypes.Add("atomsvc", "application/atomsvc+xml");
            mimeTypes.Add("au", "audio/x-au");
            mimeTypes.Add("avi", "video/avi");
            mimeTypes.Add("avs", "video/avs-video");
            mimeTypes.Add("axs", "application/olescript");
            mimeTypes.Add("bas", "text/plain");
            mimeTypes.Add("bcpio", "application/x-bcpio");
            mimeTypes.Add("bin", "application/octet-stream");
            mimeTypes.Add("bm", "image/bmp");
            mimeTypes.Add("bmp", "image/bmp");
            mimeTypes.Add("boo", "application/book");
            mimeTypes.Add("book", "application/book");
            mimeTypes.Add("boz", "application/x-bzip2");
            mimeTypes.Add("bsh", "application/x-bsh");
            mimeTypes.Add("bz2", "application/x-bzip2");
            mimeTypes.Add("bz", "application/x-bzip");
            mimeTypes.Add("cat", "application/vnd.ms-pki.seccat");
            mimeTypes.Add("ccad", "application/clariscad");
            mimeTypes.Add("cco", "application/x-cocoa");
            mimeTypes.Add("cc", "text/plain");
            mimeTypes.Add("cdf", "application/cdf");
            mimeTypes.Add("cer", "application/pkix-cert");
            mimeTypes.Add("cha", "application/x-chat");
            mimeTypes.Add("chat", "application/x-chat");
            mimeTypes.Add("class", "application/java");
            mimeTypes.Add("clp", "application/x-msclip");
            mimeTypes.Add("cmx", "image/x-cmx");
            mimeTypes.Add("cod", "image/cis-cod");
            mimeTypes.Add("conf", "text/plain");
            mimeTypes.Add("cpio", "application/x-cpio");
            mimeTypes.Add("cpp", "text/plain");
            mimeTypes.Add("cpt", "application/x-cpt");
            mimeTypes.Add("crd", "application/x-mscardfile");
            mimeTypes.Add("crl", "application/pkix-crl");
            mimeTypes.Add("crt", "application/pkix-cert");
            mimeTypes.Add("csh", "application/x-csh");
            mimeTypes.Add("css", "text/css");
            mimeTypes.Add("c", "text/plain");
            mimeTypes.Add("c++", "text/plain");
            mimeTypes.Add("cs", "text/plain");
            mimeTypes.Add("cxx", "text/plain");
            mimeTypes.Add("dcr", "application/x-director");
            mimeTypes.Add("deepv", "application/x-deepv");
            mimeTypes.Add("def", "text/plain");
            mimeTypes.Add("deploy", "application/octet-stream");
            mimeTypes.Add("der", "application/x-x509-ca-cert");
            mimeTypes.Add("dib", "image/bmp");
            mimeTypes.Add("dif", "video/x-dv");
            mimeTypes.Add("dir", "application/x-director");
            mimeTypes.Add("disco", "application/xml");
            mimeTypes.Add("dll", "application/x-msdownload");
            mimeTypes.Add("dl", "video/dl");
            mimeTypes.Add("doc", "application/msword");
            mimeTypes.Add("dot", "application/msword");
            mimeTypes.Add("dp", "application/commonground");
            mimeTypes.Add("drw", "application/drafting");
            mimeTypes.Add("dvi", "application/x-dvi");
            mimeTypes.Add("dv", "video/x-dv");
            mimeTypes.Add("dwf", "drawing/x-dwf (old)");
            mimeTypes.Add("dwg", "application/acad");
            mimeTypes.Add("dxf", "application/dxf");
            mimeTypes.Add("dxr", "application/x-director");
            mimeTypes.Add("elc", "application/x-elc");
            mimeTypes.Add("el", "text/x-script.elisp");
            mimeTypes.Add("eml", "message/rfc822");
            mimeTypes.Add("eot", "application/vnd.bw-fontobject");
            mimeTypes.Add("eps", "application/postscript");
            mimeTypes.Add("es", "application/x-esrehber");
            mimeTypes.Add("etx", "text/x-setext");
            mimeTypes.Add("evy", "application/envoy");
            mimeTypes.Add("exe", "application/octet-stream");
            mimeTypes.Add("f77", "text/plain");
            mimeTypes.Add("f90", "text/plain");
            mimeTypes.Add("fdf", "application/vnd.fdf");
            mimeTypes.Add("fif", "image/fif");
            mimeTypes.Add("fli", "video/fli");
            mimeTypes.Add("flo", "image/florian");
            mimeTypes.Add("flr", "x-world/x-vrml");
            mimeTypes.Add("flx", "text/vnd.fmi.flexstor");
            mimeTypes.Add("fmf", "video/x-atomic3d-feature");
            mimeTypes.Add("for", "text/plain");
            mimeTypes.Add("fpx", "image/vnd.fpx");
            mimeTypes.Add("frl", "application/freeloader");
            mimeTypes.Add("f", "text/plain");
            mimeTypes.Add("funk", "audio/make");
            mimeTypes.Add("g3", "image/g3fax");
            mimeTypes.Add("gif", "image/gif");
            mimeTypes.Add("gl", "video/gl");
            mimeTypes.Add("gsd", "audio/x-gsm");
            mimeTypes.Add("gsm", "audio/x-gsm");
            mimeTypes.Add("gsp", "application/x-gsp");
            mimeTypes.Add("gss", "application/x-gss");
            mimeTypes.Add("gtar", "application/x-gtar");
            mimeTypes.Add("g", "text/plain");
            mimeTypes.Add("gz", "application/x-gzip");
            mimeTypes.Add("gzip", "application/x-gzip");
            mimeTypes.Add("hdf", "application/x-hdf");
            mimeTypes.Add("help", "application/x-helpfile");
            mimeTypes.Add("hgl", "application/vnd.hp-HPGL");
            mimeTypes.Add("hh", "text/plain");
            mimeTypes.Add("hlb", "text/x-script");
            mimeTypes.Add("hlp", "application/x-helpfile");
            mimeTypes.Add("hpg", "application/vnd.hp-HPGL");
            mimeTypes.Add("hpgl", "application/vnd.hp-HPGL");
            mimeTypes.Add("hqx", "application/binhex");
            mimeTypes.Add("hta", "application/hta");
            mimeTypes.Add("htc", "text/x-component");
            mimeTypes.Add("h", "text/plain");
            mimeTypes.Add("htmls", "text/html");
            mimeTypes.Add("html", "text/html");
            mimeTypes.Add("htm", "text/html");
            mimeTypes.Add("htt", "text/webviewhtml");
            mimeTypes.Add("htx", "text/html");
            mimeTypes.Add("ice", "x-conference/x-cooltalk");
            mimeTypes.Add("ico", "image/x-icon");
            mimeTypes.Add("idc", "text/plain");
            mimeTypes.Add("ief", "image/ief");
            mimeTypes.Add("iefs", "image/ief");
            mimeTypes.Add("iges", "application/iges");
            mimeTypes.Add("igs", "application/iges");
            mimeTypes.Add("iii", "application/x-iphone");
            mimeTypes.Add("ima", "application/x-ima");
            mimeTypes.Add("imap", "application/x-httpd-imap");
            mimeTypes.Add("inf", "application/inf");
            mimeTypes.Add("ins", "application/x-internett-signup");
            mimeTypes.Add("ip", "application/x-ip2");
            mimeTypes.Add("isp", "application/x-internet-signup");
            mimeTypes.Add("isu", "video/x-isvideo");
            mimeTypes.Add("it", "audio/it");
            mimeTypes.Add("iv", "application/x-inventor");
            mimeTypes.Add("ivf", "video/x-ivf");
            mimeTypes.Add("ivr", "i-world/i-vrml");
            mimeTypes.Add("ivy", "application/x-livescreen");
            mimeTypes.Add("jam", "audio/x-jam");
            mimeTypes.Add("java", "text/plain");
            mimeTypes.Add("jav", "text/plain");
            mimeTypes.Add("jcm", "application/x-java-commerce");
            mimeTypes.Add("jfif", "image/jpeg");
            mimeTypes.Add("jfif-tbnl", "image/jpeg");
            mimeTypes.Add("jpeg", "image/jpeg");
            mimeTypes.Add("jpe", "image/jpeg");
            mimeTypes.Add("jpg", "image/jpeg");
            mimeTypes.Add("jps", "image/x-jps");
            mimeTypes.Add("js", "application/x-javascript");
            mimeTypes.Add("json", "application/application/json");
            mimeTypes.Add("jut", "image/jutvision");
            mimeTypes.Add("kar", "audio/midi");
            mimeTypes.Add("ksh", "text/x-script.ksh");
            mimeTypes.Add("la", "audio/nspaudio");
            mimeTypes.Add("lam", "audio/x-liveaudio");
            mimeTypes.Add("latex", "application/x-latex");
            mimeTypes.Add("list", "text/plain");
            mimeTypes.Add("lma", "audio/nspaudio");
            mimeTypes.Add("log", "text/plain");
            mimeTypes.Add("lsp", "application/x-lisp");
            mimeTypes.Add("lst", "text/plain");
            mimeTypes.Add("lsx", "text/x-la-asf");
            mimeTypes.Add("ltx", "application/x-latex");
            mimeTypes.Add("m13", "application/x-msmediaview");
            mimeTypes.Add("m14", "application/x-msmediaview");
            mimeTypes.Add("m1v", "video/mpeg");
            mimeTypes.Add("m2a", "audio/mpeg");
            mimeTypes.Add("m2v", "video/mpeg");
            mimeTypes.Add("m3u", "audio/x-mpequrl");
            mimeTypes.Add("m4u", "video/x-mpegurl");
            mimeTypes.Add("m4v", "video/mp4");
            mimeTypes.Add("m4a", "audio/mp4");
            mimeTypes.Add("m4r", "audio/mp4");
            mimeTypes.Add("m4b", "audio/mp4");
            mimeTypes.Add("m4p", "audio/mp4");
            mimeTypes.Add("man", "application/x-troff-man");
            mimeTypes.Add("manifest", "application/x-ms-manifest");
            mimeTypes.Add("map", "application/x-navimap");
            mimeTypes.Add("mar", "text/plain");
            mimeTypes.Add("mbd", "application/mbedlet");
            mimeTypes.Add("mc$", "application/x-magic-cap-package-1.0");
            mimeTypes.Add("mcd", "application/mcad");
            mimeTypes.Add("mcf", "image/vasa");
            mimeTypes.Add("mcp", "application/netmc");
            mimeTypes.Add("mdb", "application/x-msaccess");
            mimeTypes.Add("me", "application/x-troff-me");
            mimeTypes.Add("mht", "message/rfc822");
            mimeTypes.Add("mhtml", "message/rfc822");
            mimeTypes.Add("mid", "audio/midi");
            mimeTypes.Add("midi", "audio/midi");
            mimeTypes.Add("mif", "application/x-mif");
            mimeTypes.Add("mime", "message/rfc822");
            mimeTypes.Add("mjf", "audio/x-vnd.AudioExplosion.MjuiceMediaFile");
            mimeTypes.Add("mjpg", "video/x-motion-jpeg");
            mimeTypes.Add("mm", "application/base64");
            mimeTypes.Add("mme", "application/base64");
            mimeTypes.Add("mny", "application/x-msmoney");
            mimeTypes.Add("mod", "audio/mod");
            mimeTypes.Add("moov", "video/quicktime");
            mimeTypes.Add("movie", "video/x-sgi-movie");
            mimeTypes.Add("mov", "video/quicktime");
            mimeTypes.Add("mp2", "video/mpeg");
            mimeTypes.Add("mp3", "audio/mpeg3");
            mimeTypes.Add("mp4", "video/mp4");
            mimeTypes.Add("mp4v", "video/mp4");
            mimeTypes.Add("mpa", "audio/mpeg");
            mimeTypes.Add("mpc", "application/x-project");
            mimeTypes.Add("mpeg", "video/mpeg");
            mimeTypes.Add("mpe", "video/mpeg");
            mimeTypes.Add("mpga", "audio/mpeg");
            mimeTypes.Add("mpg", "video/mpeg");
            mimeTypes.Add("mpg4", "video/mp4");
            mimeTypes.Add("mpp", "application/vnd.ms-project");
            mimeTypes.Add("mpt", "application/x-project");
            mimeTypes.Add("mpv2", "video/mpeg");
            mimeTypes.Add("mpv", "application/x-project");
            mimeTypes.Add("mpx", "application/x-project");
            mimeTypes.Add("mrc", "application/marc");
            mimeTypes.Add("ms", "application/x-troff-ms");
            mimeTypes.Add("m", "text/plain");
            mimeTypes.Add("mvb", "application/x-msmediaview");
            mimeTypes.Add("mv", "video/x-sgi-movie");
            mimeTypes.Add("my", "audio/make");
            mimeTypes.Add("mzz", "application/x-vnd.AudioExplosion.mzz");
            mimeTypes.Add("nap", "image/naplps");
            mimeTypes.Add("naplps", "image/naplps");
            mimeTypes.Add("nc", "application/x-netcdf");
            mimeTypes.Add("ncm", "application/vnd.nokia.configuration-message");
            mimeTypes.Add("niff", "image/x-niff");
            mimeTypes.Add("nif", "image/x-niff");
            mimeTypes.Add("nix", "application/x-mix-transfer");
            mimeTypes.Add("nsc", "application/x-conference");
            mimeTypes.Add("nvd", "application/x-navidoc");
            mimeTypes.Add("nws", "message/rfc822");
            mimeTypes.Add("oda", "application/oda");
            mimeTypes.Add("ods", "application/oleobject");
            mimeTypes.Add("oga", "audio/ogg");
            mimeTypes.Add("ogg", "audio/ogg");
            mimeTypes.Add("ogv", "video/ogg");
            mimeTypes.Add("omc", "application/x-omc");
            mimeTypes.Add("omcd", "application/x-omcdatamaker");
            mimeTypes.Add("omcr", "application/x-omcregerator");
            mimeTypes.Add("otf", "application/x-font-otf");
            mimeTypes.Add("p10", "application/pkcs10");
            mimeTypes.Add("p12", "application/pkcs-12");
            mimeTypes.Add("p7a", "application/x-pkcs7-signature");
            mimeTypes.Add("p7b", "application/x-pkcs7-certificates");
            mimeTypes.Add("p7c", "application/pkcs7-mime");
            mimeTypes.Add("p7m", "application/pkcs7-mime");
            mimeTypes.Add("p7r", "application/x-pkcs7-certreqresp");
            mimeTypes.Add("p7s", "application/pkcs7-signature");
            mimeTypes.Add("part", "application/pro_eng");
            mimeTypes.Add("pas", "text/pascal");
            mimeTypes.Add("pbm", "image/x-portable-bitmap");
            mimeTypes.Add("pcl", "application/x-pcl");
            mimeTypes.Add("pct", "image/x-pict");
            mimeTypes.Add("pcx", "image/x-pcx");
            mimeTypes.Add("pdb", "chemical/x-pdb");
            mimeTypes.Add("pdf", "application/pdf");
            mimeTypes.Add("pfunk", "audio/make");
            mimeTypes.Add("pfx", "application/x-pkcs12");
            mimeTypes.Add("pgm", "image/x-portable-graymap");
            mimeTypes.Add("pic", "image/pict");
            mimeTypes.Add("pict", "image/pict");
            mimeTypes.Add("pkg", "application/x-newton-compatible-pkg");
            mimeTypes.Add("pko", "application/vnd.ms-pki.pko");
            mimeTypes.Add("pl", "text/plain");
            mimeTypes.Add("plx", "application/x-PiXCLscript");
            mimeTypes.Add("pm4", "application/x-pagemaker");
            mimeTypes.Add("pm5", "application/x-pagemaker");
            mimeTypes.Add("pma", "application/x-perfmon");
            mimeTypes.Add("pmc", "application/x-perfmon");
            mimeTypes.Add("pm", "image/x-xpixmap");
            mimeTypes.Add("pml", "application/x-perfmon");
            mimeTypes.Add("pmr", "application/x-perfmon");
            mimeTypes.Add("pmw", "application/x-perfmon");
            mimeTypes.Add("png", "image/png");
            mimeTypes.Add("pnm", "application/x-portable-anymap");
            mimeTypes.Add("pot", "application/mspowerpoint");
            mimeTypes.Add("pov", "model/x-pov");
            mimeTypes.Add("ppa", "application/vnd.ms-powerpoint");
            mimeTypes.Add("ppm", "image/x-portable-pixmap");
            mimeTypes.Add("pps", "application/mspowerpoint");
            mimeTypes.Add("ppt", "application/mspowerpoint");
            mimeTypes.Add("ppz", "application/mspowerpoint");
            mimeTypes.Add("pre", "application/x-freelance");
            mimeTypes.Add("prf", "application/pics-rules");
            mimeTypes.Add("prt", "application/pro_eng");
            mimeTypes.Add("ps", "application/postscript");
            mimeTypes.Add("p", "text/x-pascal");
            mimeTypes.Add("pub", "application/x-mspublisher");
            mimeTypes.Add("pvu", "paleovu/x-pv");
            mimeTypes.Add("pwz", "application/vnd.ms-powerpoint");
            mimeTypes.Add("pyc", "applicaiton/x-bytecode.python");
            mimeTypes.Add("py", "text/x-script.phyton");
            mimeTypes.Add("qcp", "audio/vnd.qcelp");
            mimeTypes.Add("qd3d", "x-world/x-3dmf");
            mimeTypes.Add("qd3", "x-world/x-3dmf");
            mimeTypes.Add("qif", "image/x-quicktime");
            mimeTypes.Add("qtc", "video/x-qtc");
            mimeTypes.Add("qtif", "image/x-quicktime");
            mimeTypes.Add("qti", "image/x-quicktime");
            mimeTypes.Add("qt", "video/quicktime");
            mimeTypes.Add("ra", "audio/x-pn-realaudio");
            mimeTypes.Add("ram", "audio/x-pn-realaudio");
            mimeTypes.Add("ras", "application/x-cmu-raster");
            mimeTypes.Add("rast", "image/cmu-raster");
            mimeTypes.Add("rexx", "text/x-script.rexx");
            mimeTypes.Add("rf", "image/vnd.rn-realflash");
            mimeTypes.Add("rgb", "image/x-rgb");
            mimeTypes.Add("rm", "application/vnd.rn-realmedia");
            mimeTypes.Add("rmi", "audio/mid");
            mimeTypes.Add("rmm", "audio/x-pn-realaudio");
            mimeTypes.Add("rmp", "audio/x-pn-realaudio");
            mimeTypes.Add("rng", "application/ringing-tones");
            mimeTypes.Add("rnx", "application/vnd.rn-realplayer");
            mimeTypes.Add("roff", "application/x-troff");
            mimeTypes.Add("rp", "image/vnd.rn-realpix");
            mimeTypes.Add("rpm", "audio/x-pn-realaudio-plugin");
            mimeTypes.Add("rss", "application/xml");
            mimeTypes.Add("rtf", "text/richtext");
            mimeTypes.Add("rt", "text/richtext");
            mimeTypes.Add("rtx", "text/richtext");
            mimeTypes.Add("rv", "video/vnd.rn-realvideo");
            mimeTypes.Add("s3m", "audio/s3m");
            mimeTypes.Add("sbk", "application/x-tbook");
            mimeTypes.Add("scd", "application/x-msschedule");
            mimeTypes.Add("scm", "application/x-lotusscreencam");
            mimeTypes.Add("sct", "text/scriptlet");
            mimeTypes.Add("sdml", "text/plain");
            mimeTypes.Add("sdp", "application/sdp");
            mimeTypes.Add("sdr", "application/sounder");
            mimeTypes.Add("sea", "application/sea");
            mimeTypes.Add("set", "application/set");
            mimeTypes.Add("setpay", "application/set-payment-initiation");
            mimeTypes.Add("setreg", "application/set-registration-initiation");
            mimeTypes.Add("sgml", "text/sgml");
            mimeTypes.Add("sgm", "text/sgml");
            mimeTypes.Add("shar", "application/x-bsh");
            mimeTypes.Add("sh", "text/x-script.sh");
            mimeTypes.Add("shtml", "text/html");
            mimeTypes.Add("sid", "audio/x-psid");
            mimeTypes.Add("sit", "application/x-sit");
            mimeTypes.Add("skd", "application/x-koan");
            mimeTypes.Add("skm", "application/x-koan");
            mimeTypes.Add("skp", "application/x-koan");
            mimeTypes.Add("skt", "application/x-koan");
            mimeTypes.Add("sl", "application/x-seelogo");
            mimeTypes.Add("smi", "application/smil");
            mimeTypes.Add("smil", "application/smil");
            mimeTypes.Add("snd", "audio/basic");
            mimeTypes.Add("sol", "application/solids");
            mimeTypes.Add("spc", "application/x-pkcs7-certificates");
            mimeTypes.Add("spl", "application/futuresplash");
            mimeTypes.Add("spr", "application/x-sprite");
            mimeTypes.Add("sprite", "application/x-sprite");
            mimeTypes.Add("spx", "audio/ogg");
            mimeTypes.Add("src", "application/x-wais-source");
            mimeTypes.Add("ssi", "text/x-server-parsed-html");
            mimeTypes.Add("ssm", "application/streamingmedia");
            mimeTypes.Add("sst", "application/vnd.ms-pki.certstore");
            mimeTypes.Add("step", "application/step");
            mimeTypes.Add("s", "text/x-asm");
            mimeTypes.Add("stl", "application/sla");
            mimeTypes.Add("stm", "text/html");
            mimeTypes.Add("stp", "application/step");
            mimeTypes.Add("sv4cpio", "application/x-sv4cpio");
            mimeTypes.Add("sv4crc", "application/x-sv4crc");
            mimeTypes.Add("svf", "image/x-dwg");
            mimeTypes.Add("svg", "image/svg+xml");
            mimeTypes.Add("svgz", "image/svg+xml");
            mimeTypes.Add("svr", "application/x-world");
            mimeTypes.Add("swf", "application/x-shockwave-flash");
            mimeTypes.Add("talk", "text/x-speech");
            mimeTypes.Add("t", "application/x-troff");
            mimeTypes.Add("tar", "application/x-tar");
            mimeTypes.Add("tbk", "application/toolbook");
            mimeTypes.Add("tcl", "text/x-script.tcl");
            mimeTypes.Add("tcsh", "text/x-script.tcsh");
            mimeTypes.Add("tex", "application/x-tex");
            mimeTypes.Add("texi", "application/x-texinfo");
            mimeTypes.Add("texinfo", "application/x-texinfo");
            mimeTypes.Add("text", "text/plain");
            mimeTypes.Add("tgz", "application/x-compressed");
            mimeTypes.Add("tiff", "image/tiff");
            mimeTypes.Add("tif", "image/tiff");
            mimeTypes.Add("torrent", "application/x-bittorrent");
            mimeTypes.Add("tr", "application/x-troff");
            mimeTypes.Add("trm", "application/x-msterminal");
            mimeTypes.Add("tsi", "audio/tsp-audio");
            mimeTypes.Add("tsp", "audio/tsplayer");
            mimeTypes.Add("tsv", "text/tab-separated-values");
            mimeTypes.Add("ttf", "application/x-font-ttf");
            mimeTypes.Add("turbot", "image/florian");
            mimeTypes.Add("txt", "text/plain");
            mimeTypes.Add("uil", "text/x-uil");
            mimeTypes.Add("uls", "text/iuls");
            mimeTypes.Add("unis", "text/uri-list");
            mimeTypes.Add("uni", "text/uri-list");
            mimeTypes.Add("unv", "application/i-deas");
            mimeTypes.Add("uris", "text/uri-list");
            mimeTypes.Add("uri", "text/uri-list");
            mimeTypes.Add("ustar", "multipart/x-ustar");
            mimeTypes.Add("uue", "text/x-uuencode");
            mimeTypes.Add("uu", "text/x-uuencode");
            mimeTypes.Add("vcd", "application/x-cdlink");
            mimeTypes.Add("vcf", "text/x-vcard");
            mimeTypes.Add("vcs", "text/x-vCalendar");
            mimeTypes.Add("vda", "application/vda");
            mimeTypes.Add("vdo", "video/vdo");
            mimeTypes.Add("vew", "application/groupwise");
            mimeTypes.Add("vivo", "video/vivo");
            mimeTypes.Add("viv", "video/vivo");
            mimeTypes.Add("vmd", "application/vocaltec-media-desc");
            mimeTypes.Add("vmf", "application/vocaltec-media-file");
            mimeTypes.Add("voc", "audio/voc");
            mimeTypes.Add("vos", "video/vosaic");
            mimeTypes.Add("vox", "audio/voxware");
            mimeTypes.Add("vqe", "audio/x-twinvq-plugin");
            mimeTypes.Add("vqf", "audio/x-twinvq");
            mimeTypes.Add("vql", "audio/x-twinvq-plugin");
            mimeTypes.Add("vrml", "application/x-vrml");
            mimeTypes.Add("vrt", "x-world/x-vrt");
            mimeTypes.Add("vsd", "application/x-visio");
            mimeTypes.Add("vst", "application/x-visio");
            mimeTypes.Add("vsw", "application/x-visio");
            mimeTypes.Add("w60", "application/wordperfect6.0");
            mimeTypes.Add("w61", "application/wordperfect6.1");
            mimeTypes.Add("w6w", "application/msword");
            mimeTypes.Add("wav", "audio/wav");
            mimeTypes.Add("wb1", "application/x-qpro");
            mimeTypes.Add("wbmp", "image/vnd.wap.wbmp");
            mimeTypes.Add("wcm", "application/vnd.ms-works");
            mimeTypes.Add("wdb", "application/vnd.ms-works");
            mimeTypes.Add("web", "application/vnd.xara");
            mimeTypes.Add("webm", "video/webm");
            mimeTypes.Add("wiz", "application/msword");
            mimeTypes.Add("wk1", "application/x-123");
            mimeTypes.Add("wks", "application/vnd.ms-works");
            mimeTypes.Add("wmf", "windows/metafile");
            mimeTypes.Add("wmlc", "application/vnd.wap.wmlc");
            mimeTypes.Add("wmlsc", "application/vnd.wap.wmlscriptc");
            mimeTypes.Add("wmls", "text/vnd.wap.wmlscript");
            mimeTypes.Add("wml", "text/vnd.wap.wml");
            mimeTypes.Add("woff", "application/x-woff");
            mimeTypes.Add("word", "application/msword");
            mimeTypes.Add("wp5", "application/wordperfect");
            mimeTypes.Add("wp6", "application/wordperfect");
            mimeTypes.Add("wp", "application/wordperfect");
            mimeTypes.Add("wpd", "application/wordperfect");
            mimeTypes.Add("wps", "application/vnd.ms-works");
            mimeTypes.Add("wq1", "application/x-lotus");
            mimeTypes.Add("wri", "application/mswrite");
            mimeTypes.Add("wrl", "application/x-world");
            mimeTypes.Add("wrz", "model/vrml");
            mimeTypes.Add("wsc", "text/scriplet");
            mimeTypes.Add("wsdl", "application/xml");
            mimeTypes.Add("wsrc", "application/x-wais-source");
            mimeTypes.Add("wtk", "application/x-wintalk");
            mimeTypes.Add("xaf", "x-world/x-vrml");
            mimeTypes.Add("xaml", "application/xaml+xml");
            mimeTypes.Add("xap", "application/x-silverlight-app");
            mimeTypes.Add("xbap", "application/x-ms-xbap");
            mimeTypes.Add("xbm", "image/x-xbitmap");
            mimeTypes.Add("xdr", "video/x-amt-demorun");
            mimeTypes.Add("xgz", "xgl/drawing");
            mimeTypes.Add("xhtml", "application/xhtml+xml");
            mimeTypes.Add("xht", "application/xhtml+xml");
            mimeTypes.Add("xif", "image/vnd.xiff");
            mimeTypes.Add("xla", "application/excel");
            mimeTypes.Add("xl", "application/excel");
            mimeTypes.Add("xlb", "application/excel");
            mimeTypes.Add("xlc", "application/excel");
            mimeTypes.Add("xld", "application/excel");
            mimeTypes.Add("xlk", "application/excel");
            mimeTypes.Add("xll", "application/excel");
            mimeTypes.Add("xlm", "application/excel");
            mimeTypes.Add("xls", "application/excel");
            mimeTypes.Add("xlt", "application/excel");
            mimeTypes.Add("xlv", "application/excel");
            mimeTypes.Add("xlw", "application/excel");
            mimeTypes.Add("xm", "audio/xm");
            mimeTypes.Add("xml", "application/xml");
            mimeTypes.Add("xmz", "xgl/movie");
            mimeTypes.Add("xof", "x-world/x-vrml");
            mimeTypes.Add("xpi", "application/x-xpinstall");
            mimeTypes.Add("xpix", "application/x-vnd.ls-xpix");
            mimeTypes.Add("xpm", "image/xpm");
            mimeTypes.Add("x-png", "image/png");
            mimeTypes.Add("xsd", "application/xml");
            mimeTypes.Add("xsl", "application/xml");
            mimeTypes.Add("xsr", "video/x-amt-showrun");
            mimeTypes.Add("xwd", "image/x-xwd");
            mimeTypes.Add("xyz", "chemical/x-pdb");
            mimeTypes.Add("z", "application/x-compressed");
            mimeTypes.Add("zip", "application/zip");
            mimeTypes.Add("zsh", "text/x-script.zsh");

            // Office Formats
            mimeTypes.Add("docm", "application/vnd.ms-word.document.macroEnabled.12");
            mimeTypes.Add("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            mimeTypes.Add("dotm", "application/vnd.ms-word.template.macroEnabled.12");
            mimeTypes.Add("dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.template");
            mimeTypes.Add("potm", "application/vnd.ms-powerpoint.template.macroEnabled.12");
            mimeTypes.Add("potx", "application/vnd.openxmlformats-officedocument.presentationml.template");
            mimeTypes.Add("ppam", "application/vnd.ms-powerpoint.addin.macroEnabled.12");
            mimeTypes.Add("ppsm", "application/vnd.ms-powerpoint.slideshow.macroEnabled.12");
            mimeTypes.Add("ppsx", "application/vnd.openxmlformats-officedocument.presentationml.slideshow");
            mimeTypes.Add("pptm", "application/vnd.ms-powerpoint.presentation.macroEnabled.12");
            mimeTypes.Add("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation");
            mimeTypes.Add("xlam", "application/vnd.ms-excel.addin.macroEnabled.12");
            mimeTypes.Add("xlsb", "application/vnd.ms-excel.sheet.binary.macroEnabled.12");
            mimeTypes.Add("xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12");
            mimeTypes.Add("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            mimeTypes.Add("xltm", "application/vnd.ms-excel.template.macroEnabled.12");
            mimeTypes.Add("xltx", "application/vnd.openxmlformats-officedocument.spreadsheetml.template");
        }

        public static string GetMimeType(string fileName)
        {
            string result = null;
            var dot = fileName.LastIndexOf('.');
            if (dot != -1 && fileName.Length > dot + 1) { mimeTypes.TryGetValue(fileName.Substring(dot + 1), out result); }
            return result ?? "application/octet-stream";
        }
    }
    #endregion
}