using System;
using System.Collections;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Text;
using Newtonsoft.Json;
using System.IO;
using System.Threading;

using HttpUtility = System.Web.HttpUtility;

namespace Refit
{
    class RequestBuilderFactory : IRequestBuilderFactory
    {
        public IRequestBuilder Create(Type interfaceType, RefitSettings settings = null)
        {
            return new RequestBuilderImplementation(interfaceType, settings);
        }
    }

    class RequestBuilderImplementation : IRequestBuilder
    {
        readonly Type targetType;
        readonly Dictionary<string, RestMethodInfo> interfaceHttpMethods;
        readonly RefitSettings settings;

        public RequestBuilderImplementation(Type targetInterface, RefitSettings refitSettings = null)
        {
            settings = refitSettings ?? new RefitSettings();
            if (targetInterface == null || !targetInterface.IsInterface()) {
                throw new ArgumentException("targetInterface must be an Interface");
            }

            targetType = targetInterface;
            interfaceHttpMethods = targetInterface.GetMethods()
                .SelectMany(x => {
                    var attrs = x.GetCustomAttributes(true);
                    var hasHttpMethod = attrs.OfType<HttpMethodAttribute>().Any();
                    if (!hasHttpMethod) return Enumerable.Empty<RestMethodInfo>();

                    return EnumerableEx.Return(new RestMethodInfo(targetInterface, x, settings));
                })
                .ToDictionary(k => k.Name, v => v);
        }

        public IEnumerable<string> InterfaceHttpMethods {
            get { return interfaceHttpMethods.Keys; }
        }

        Func<object[], HttpRequestMessage> BuildRequestFactoryForMethod(string methodName, string basePath, bool paramsContainsCancellationToken)
        {
            if (!interfaceHttpMethods.ContainsKey(methodName)) {
                throw new ArgumentException("Method must be defined and have an HTTP Method attribute");
            }
            var restMethod = interfaceHttpMethods[methodName];

            return paramList => {
                // make sure we strip out any cancelation tokens
                if (paramsContainsCancellationToken) {
                    paramList = paramList.Where(o => o == null || o.GetType() != typeof(CancellationToken)).ToArray();
                }
                
                var ret = new HttpRequestMessage {
                    Method = restMethod.HttpMethod,
                };

                // set up multipart content
                MultipartFormDataContent multiPartContent = null;
                if (restMethod.IsMultipart) {
                    multiPartContent = new MultipartFormDataContent("----MyGreatBoundary");
                    ret.Content = multiPartContent;
                }

                var urlTarget = (basePath == "/" ? string.Empty : basePath) + restMethod.RelativePath;
                var queryParamsToAdd = new Dictionary<string, string>();
                var headersToAdd = new Dictionary<string, string>(restMethod.Headers);

                for(var i=0; i < paramList.Length; i++) {
                    // if part of REST resource URL, substitute it in
                    if (restMethod.ParameterMap.ContainsKey(i)) {
                        urlTarget = Regex.Replace(
                            urlTarget, 
                            "{" + restMethod.ParameterMap[i] + "}", 
                            settings.UrlParameterFormatter
                                    .Format(paramList[i], restMethod.ParameterInfoMap[i])
                                    .Replace("/", "%2F"), 
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        continue;
                    }

                    // if marked as body, add to content
                    if (restMethod.BodyParameterInfo != null && restMethod.BodyParameterInfo.Item2 == i) {
                        var streamParam = paramList[i] as Stream;
                        var stringParam = paramList[i] as string;

                        if (paramList[i] is HttpContent httpContentParam)
                        {
                            ret.Content = httpContentParam;
                        }
                        else if (streamParam != null)
                        {
                            ret.Content = new StreamContent(streamParam);
                        }
                        else if (stringParam != null)
                        {
                            ret.Content = new StringContent(stringParam);
                        }
                        else
                        {
                            switch (restMethod.BodyParameterInfo.Item1)
                            {
                                case BodySerializationMethod.UrlEncoded:
                                    ret.Content = new FormUrlEncodedContent(new FormValueDictionary(paramList[i]));
                                    break;
                                case BodySerializationMethod.Json:
                                    ret.Content = new StringContent(JsonConvert.SerializeObject(paramList[i], settings.JsonSerializerSettings), Encoding.UTF8, "application/json");
                                    break;
                            }
                        }

                        continue;
                    }

                    // if header, add to request headers
                    if (restMethod.HeaderParameterMap.ContainsKey(i)) {
                        headersToAdd[restMethod.HeaderParameterMap[i]] = paramList[i]?.ToString();
                        continue;
                    }

                    // ignore nulls
                    if (paramList[i] == null) continue;

                    // for anything that fell through to here, if this is not
                    // a multipart method, add the parameter to the query string
                    if (!restMethod.IsMultipart) {
                        queryParamsToAdd[restMethod.QueryParameterMap[i]] = settings.UrlParameterFormatter.Format(paramList[i], restMethod.ParameterInfoMap[i]);
                        continue;
                    }

                    // we are in a multipart method, add the part to the content
                    // the parameter name should be either the attachment name or the parameter name (as fallback)
                    string itemName;
                    string parameterName;

                    if (!restMethod.AttachmentNameMap.TryGetValue(i, out var attachment))
                    {
                        itemName = restMethod.QueryParameterMap[i];
                        parameterName = itemName;
                    }
                    else
                    {
                        itemName = attachment.Item1;
                        parameterName = attachment.Item2;
                    }

                    // Check to see if it's an IEnumerable
                    var itemValue = paramList[i];
                    var enumerable = itemValue as IEnumerable<object>;
                    var typeIsCollection = false;

                    if (enumerable != null) {
                        Type tType = null;
                        var eType = enumerable.GetType();
                        if (eType.GetTypeInfo().ContainsGenericParameters) {
                            tType = eType.GenericTypeArguments[0];
                        } else if (eType.IsArray) {
                            tType = eType.GetElementType();
                        }

                        // check to see if it's one of the types we support for multipart:
                        // FileInfo, Stream, string or byte[]
                        if (tType == typeof(Stream) ||
                            tType == typeof(string) ||
                            tType == typeof(byte[]) ||
                            tType.GetTypeInfo().IsSubclassOf(typeof(MultipartItem))
                            || tType == typeof(FileInfo)

                        )
                        {
                            typeIsCollection = true;
                        }

                        
                    }

                    if (typeIsCollection) {
                        foreach (var item in enumerable) {
                            AddMultipartItem(multiPartContent, itemName, parameterName, item);
                        }
                    } else{
                        AddMultipartItem(multiPartContent, itemName, parameterName, itemValue);
                    }

                }

                // NB: We defer setting headers until the body has been
                // added so any custom content headers don't get left out.
                foreach (var header in headersToAdd) {
                    SetHeader(ret, header.Key, header.Value);
                }

                // NB: The URI methods in .NET are dumb. Also, we do this 
                // UriBuilder business so that we preserve any hardcoded query 
                // parameters as well as add the parameterized ones.
                var uri = new UriBuilder(new Uri(new Uri("http://api"), urlTarget));
                var query = HttpUtility.ParseQueryString(uri.Query ?? "");
                foreach(var kvp in queryParamsToAdd) {
                    query.Add(kvp.Key, kvp.Value);
                }

                if (query.HasKeys()) {
                    var pairs = query.Keys.Cast<string>().Select(x => HttpUtility.UrlEncode(x) + "=" + HttpUtility.UrlEncode(query[x]));
                    uri.Query = string.Join("&", pairs);
                } else {
                    uri.Query = null;
                }

                ret.RequestUri = new Uri(uri.Uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped), UriKind.Relative);
                return ret;
            };
        }

        static void SetHeader(HttpRequestMessage request, string name, string value) 
        {
            // Clear any existing version of this header that might be set, because
            // we want to allow removal/redefinition of headers. 
            // We also don't want to double up content headers which may have been
            // set for us automatically.

            // NB: We have to enumerate the header names to check existence because 
            // Contains throws if it's the wrong header type for the collection.
            if (request.Headers.Any(x => x.Key == name)) {
                request.Headers.Remove(name);
            }
            if (request.Content != null && request.Content.Headers.Any(x => x.Key == name)) {
                request.Content.Headers.Remove(name);
            }

            if (value == null) return;

            var added = request.Headers.TryAddWithoutValidation(name, value);

            // Don't even bother trying to add the header as a content header
            // if we just added it to the other collection.
            if (!added && request.Content != null) {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        void AddMultipartItem(MultipartFormDataContent multiPartContent, string fileName, string parameterName, object itemValue)
        {
            var multipartItem = itemValue as MultipartItem;
            var streamValue = itemValue as Stream;
            var stringValue = itemValue as string;
            var byteArrayValue = itemValue as byte[];

            if (multipartItem != null)
            {
                var httpContent = multipartItem.ToContent();
                multiPartContent.Add(httpContent, parameterName, string.IsNullOrEmpty(multipartItem.FileName) ? fileName : multipartItem.FileName);
                return;
            }

            if (streamValue != null) {
                var streamContent = new StreamContent(streamValue);
                multiPartContent.Add(streamContent, parameterName, fileName);
                return;
            }
             
            if (stringValue != null) {
                multiPartContent.Add(new StringContent(stringValue),  parameterName, fileName);
                return;
            }

            if (itemValue is FileInfo fileInfoValue)
            {
                var fileContent = new StreamContent(fileInfoValue.OpenRead());
                multiPartContent.Add(fileContent, parameterName, fileInfoValue.Name);
                return;
            }

            if (byteArrayValue != null) {
                var fileContent = new ByteArrayContent(byteArrayValue);
                multiPartContent.Add(fileContent, parameterName, fileName);
                return;
            }

            throw new ArgumentException($"Unexpected parameter type in a Multipart request. Parameter {fileName} is of type {itemValue.GetType().Name}, whereas allowed types are String, Stream, FileInfo, and Byte array", nameof(itemValue));
        }

        public Func<HttpClient, object[], object> BuildRestResultFuncForMethod(string methodName)
        {
            if (!interfaceHttpMethods.ContainsKey(methodName)) {
                throw new ArgumentException("Method must be defined and have an HTTP Method attribute");
            }

            var restMethod = interfaceHttpMethods[methodName];

            if (restMethod.ReturnType == typeof(Task)) {
                return BuildVoidTaskFuncForMethod(restMethod);
            } else if (restMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)) {
                // NB: This jacked up reflection code is here because it's
                // difficult to upcast Task<object> to an arbitrary T, especially
                // if you need to AOT everything, so we need to reflectively 
                // invoke buildTaskFuncForMethod.
                var taskFuncMi = GetType().GetMethod(nameof(BuildTaskFuncForMethod), BindingFlags.NonPublic | BindingFlags.Instance);
                var taskFunc = (MulticastDelegate)taskFuncMi.MakeGenericMethod(restMethod.SerializedReturnType)
                    .Invoke(this, new[] { restMethod });

                return (client, args) => {
                    return taskFunc.DynamicInvoke(new object[] { client, args } );
                };
            } else {
                // Same deal
                var rxFuncMi = GetType().GetMethod(nameof(BuildRxFuncForMethod), BindingFlags.NonPublic | BindingFlags.Instance);
                var rxFunc = (MulticastDelegate)rxFuncMi.MakeGenericMethod(restMethod.SerializedReturnType)
                    .Invoke(this, new[] { restMethod });

                return (client, args) => {
                    return rxFunc.DynamicInvoke(new object[] { client, args });
                };
            }
        }

        Func<HttpClient, object[], Task> BuildVoidTaskFuncForMethod(RestMethodInfo restMethod)
        {                      
            return async (client, paramList) => {
                var factory = BuildRequestFactoryForMethod(restMethod.Name, client.BaseAddress.AbsolutePath, restMethod.CancellationToken != null);
                var rq = factory(paramList);

                var ct = CancellationToken.None;

                if (restMethod.CancellationToken != null) {
                    ct = paramList.OfType<CancellationToken>().FirstOrDefault();
                }

                using (var resp = await client.SendAsync(rq, ct).ConfigureAwait(false)) {
                    if (!resp.IsSuccessStatusCode) {
                        throw await ApiException.Create(rq.RequestUri, restMethod.HttpMethod, resp, settings).ConfigureAwait(false);
                    }
                }
            };
        }

        Func<HttpClient, object[], Task<T>> BuildTaskFuncForMethod<T>(RestMethodInfo restMethod)
        {
            var ret = BuildCancellableTaskFuncForMethod<T>(restMethod);

            return (client, paramList) => {
                if(restMethod.CancellationToken != null) {
                    return ret(client, paramList.OfType<CancellationToken>().FirstOrDefault(), paramList);
                }

                return ret(client, CancellationToken.None, paramList);
            };
        }

        Func<HttpClient, CancellationToken, object[], Task<T>> BuildCancellableTaskFuncForMethod<T>(RestMethodInfo restMethod)
        {
            return async (client, ct, paramList) => {
                var factory = BuildRequestFactoryForMethod(restMethod.Name, client.BaseAddress.AbsolutePath, restMethod.CancellationToken != null);
                var rq = factory(paramList);

                var resp = await client.SendAsync(rq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (restMethod.SerializedReturnType == typeof(HttpResponseMessage)) {
                    // NB: This double-casting manual-boxing hate crime is the only way to make 
                    // this work without a 'class' generic constraint. It could blow up at runtime 
                    // and would be A Bad Idea if we hadn't already vetted the return type.
                    return (T)(object)resp; 
                }

                if (!resp.IsSuccessStatusCode) {
                    throw await ApiException.Create(rq.RequestUri, restMethod.HttpMethod, resp, restMethod.RefitSettings).ConfigureAwait(false);
                }

                if (restMethod.SerializedReturnType == typeof(HttpContent)) {
                    return (T)(object)resp.Content;
                }

                var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (restMethod.SerializedReturnType == typeof(string)) {
                    return (T)(object)content; 
                }

                return JsonConvert.DeserializeObject<T>(content, settings.JsonSerializerSettings);
            };
        }

        Func<HttpClient, object[], IObservable<T>> BuildRxFuncForMethod<T>(RestMethodInfo restMethod)
        {
            var taskFunc = BuildCancellableTaskFuncForMethod<T>(restMethod);

            return (client, paramList) => {
                return new TaskToObservable<T>(ct => {
                    var methodCt = CancellationToken.None;
                    if (restMethod.CancellationToken != null) {
                        methodCt = paramList.OfType<CancellationToken>().FirstOrDefault();
                    }

                    // link the two
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(methodCt, ct);

                    return taskFunc(client, cts.Token, paramList);
                });
            };
        }

        class TaskToObservable<T> : IObservable<T>
        {
            Func<CancellationToken, Task<T>> taskFactory;

            public TaskToObservable(Func<CancellationToken, Task<T>> taskFactory) 
            {
                this.taskFactory = taskFactory;
            }

            public IDisposable Subscribe(IObserver<T> observer)
            {
                var cts = new CancellationTokenSource();
                taskFactory(cts.Token).ContinueWith(t => {
                    if (cts.IsCancellationRequested) return;

                    if (t.Exception != null) {
                        observer.OnError(t.Exception.InnerExceptions.First());
                        return;
                    }

                    try {
                        observer.OnNext(t.Result);
                    } catch (Exception ex) {
                        observer.OnError(ex);
                    }
                        
                    observer.OnCompleted();
                });

                return new AnonymousDisposable(cts.Cancel);
            }
        }
    }

    sealed class AnonymousDisposable : IDisposable
    {
        readonly Action block;

        public AnonymousDisposable(Action block)
        {
            this.block = block;
        }

        public void Dispose()
        {
            block();
        }
    }

    public class RestMethodInfo
    {
        public string Name { get; set; }
        public Type Type { get; set; }
        public MethodInfo MethodInfo { get; set; }
        public HttpMethod HttpMethod { get; set; }
        public string RelativePath { get; set; }
        public bool IsMultipart { get; private set; }
        public Dictionary<int, string> ParameterMap { get; set; }
        public ParameterInfo CancellationToken { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public Dictionary<int, string> HeaderParameterMap { get; set; }
        public Tuple<BodySerializationMethod, int> BodyParameterInfo { get; set; }
        public Dictionary<int, string> QueryParameterMap { get; set; }
        public Dictionary<int, Tuple<string, string>> AttachmentNameMap { get; set; }
        public Dictionary<int, ParameterInfo> ParameterInfoMap { get; set; }
        public Type ReturnType { get; set; }
        public Type SerializedReturnType { get; set; }
        public RefitSettings RefitSettings { get; set; }

        static readonly Regex parameterRegex = new Regex(@"{(.*?)}");
        static readonly HttpMethod patchMethod = new HttpMethod("PATCH");

        public RestMethodInfo(Type targetInterface, MethodInfo methodInfo, RefitSettings refitSettings = null)
        {
            RefitSettings = refitSettings ?? new RefitSettings();
            Type = targetInterface;
            Name = methodInfo.Name;
            MethodInfo = methodInfo;

            var hma = methodInfo.GetCustomAttributes(true)
                .OfType<HttpMethodAttribute>()
                .First();

            HttpMethod = hma.Method;
            RelativePath = hma.Path;

            IsMultipart = methodInfo.GetCustomAttributes(true)
                .OfType<MultipartAttribute>()
                .Any();

            VerifyUrlPathIsSane(RelativePath);
            DetermineReturnTypeInfo(methodInfo);

            // Exclude cancellation token parameters from this list
            var parameterList = methodInfo.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToList();
            ParameterInfoMap = parameterList
                .Select((parameter, index) => new { index, parameter })
                .ToDictionary(x => x.index, x => x.parameter);
            ParameterMap = BuildParameterMap(RelativePath, parameterList);
            BodyParameterInfo = FindBodyParameter(parameterList, IsMultipart, hma.Method);

            Headers = ParseHeaders(methodInfo);
            HeaderParameterMap = BuildHeaderParameterMap(parameterList);

            // get names for multipart attachments
            AttachmentNameMap = new Dictionary<int, Tuple<string, string>>();
            if (IsMultipart) {
                for (var i = 0; i < parameterList.Count; i++) {
                    if (ParameterMap.ContainsKey(i) || HeaderParameterMap.ContainsKey(i)) {
                        continue;
                    }

                    var attachmentName = GetAttachmentNameForParameter(parameterList[i]);
                    if (attachmentName == null)
                        continue;

                    AttachmentNameMap[i] = Tuple.Create(attachmentName, GetUrlNameForParameter(parameterList[i]).ToLowerInvariant());
                }
            }

            QueryParameterMap = new Dictionary<int, string>();
            for (var i=0; i < parameterList.Count; i++) {
                if (ParameterMap.ContainsKey(i) || HeaderParameterMap.ContainsKey(i) || (BodyParameterInfo != null && BodyParameterInfo.Item2 == i)) {
                    continue;
                }

                QueryParameterMap[i] = GetUrlNameForParameter(parameterList[i]);
            }

            var ctParams = methodInfo.GetParameters().Where(p => p.ParameterType == typeof(CancellationToken)).ToList();
            if(ctParams.Count > 1) {
                throw new ArgumentException("Argument list can only contain a single CancellationToken");
            }

            CancellationToken = ctParams.FirstOrDefault();
        }

        void VerifyUrlPathIsSane(string relativePath) 
        {
            if (relativePath == "")
                return;

            if (!relativePath.StartsWith("/")) {
                goto bogusPath;
            }

            var parts = relativePath.Split('/');
            if (parts.Length == 0) {
                goto bogusPath;
            }

            return;

        bogusPath:
            throw new ArgumentException("URL path must be of the form '/foo/bar/baz'");
        }

        Dictionary<int, string> BuildParameterMap(string relativePath, List<ParameterInfo> parameterInfo)
        {
            var ret = new Dictionary<int, string>();

            var parameterizedParts = relativePath.Split('/', '?')
                .SelectMany(x => parameterRegex.Matches(x).Cast<Match>())
                .ToList();

            if (parameterizedParts.Count == 0) {
                return ret;
            }

            var paramValidationDict = parameterInfo.ToDictionary(k => GetUrlNameForParameter(k).ToLowerInvariant(), v => v);

            foreach (var match in parameterizedParts) {
                var name = match.Groups[1].Value.ToLowerInvariant();
                if (!paramValidationDict.ContainsKey(name)) {
                    throw new ArgumentException(string.Format("URL has parameter {0}, but no method parameter matches", name));
                }

                ret.Add(parameterInfo.IndexOf(paramValidationDict[name]), name);
            }

            return ret;
        }

        string GetUrlNameForParameter(ParameterInfo paramInfo)
        {
            var aliasAttr = paramInfo.GetCustomAttributes(true)
                .OfType<AliasAsAttribute>()
                .FirstOrDefault();
            return aliasAttr != null ? aliasAttr.Name : paramInfo.Name;
        }

        string GetAttachmentNameForParameter(ParameterInfo paramInfo)
        {
            var nameAttr = paramInfo.GetCustomAttributes(true)
#pragma warning disable 618
                .OfType<AttachmentNameAttribute>()
#pragma warning restore 618
                .FirstOrDefault();
            return nameAttr?.Name;
        }

        static Tuple<BodySerializationMethod, int> FindBodyParameter(IList<ParameterInfo> parameterList, bool isMultipart, HttpMethod method)
        {

            // The body parameter is found using the following logic / order of precedence:
            // 1) [Body] attribute
            // 2) POST/PUT/PATCH: Reference type other than string
            // 3) If there are two reference types other than string, without the body attribute, throw

            var bodyParams = parameterList
                .Select(x => new { Parameter = x, BodyAttribute = x.GetCustomAttributes(true).OfType<BodyAttribute>().FirstOrDefault() })
                .Where(x => x.BodyAttribute != null)
                .ToList();

            // multipart requests may not contain a body, implicit or explicit
            if (isMultipart) {
                if (bodyParams.Count > 0) {
                    throw new ArgumentException("Multipart requests may not contain a Body parameter");
                }
                return null;
            }

            if (bodyParams.Count > 1) {
                throw new ArgumentException("Only one parameter can be a Body parameter");
            }

            // #1, body attribute wins
            if (bodyParams.Count == 1) {
                var ret = bodyParams[0];
                return Tuple.Create(ret.BodyAttribute.SerializationMethod, parameterList.IndexOf(ret.Parameter));
            }

            // Not in post/put/patch? bail
            if (!method.Equals(HttpMethod.Post) && !method.Equals(HttpMethod.Put) && !method.Equals(patchMethod)) {
                return null;
            }
         
            // see if we're a post/put/patch
            var refParams = parameterList.Where(pi => !pi.ParameterType.GetTypeInfo().IsValueType && pi.ParameterType != typeof(string)).ToList();
            
            // Check for rule #3
            if (refParams.Count > 1) {
                throw new ArgumentException("Multiple complex types found. Specify one parameter as the body using BodyAttribute");
            } 
            
            if (refParams.Count == 1) {
                return Tuple.Create(BodySerializationMethod.Json, parameterList.IndexOf(refParams[0]));
            }

            return null;
        }

        Dictionary<string, string> ParseHeaders(MethodInfo methodInfo) 
        {
            var ret = new Dictionary<string, string>();

            var declaringTypeAttributes = methodInfo.DeclaringType != null
                ? methodInfo.DeclaringType.GetCustomAttributes(true)
                : new Attribute[0];

            // Headers set on the declaring type have to come first, 
            // so headers set on the method can replace them. Switching
            // the order here will break stuff.
            var headers = declaringTypeAttributes.Concat(methodInfo.GetCustomAttributes(true))
                .OfType<HeadersAttribute>()
                .SelectMany(ha => ha.Headers);

            foreach (var header in headers) {
                if (string.IsNullOrWhiteSpace(header)) continue;

            // NB: Silverlight doesn't have an overload for String.Split()
            // with a count parameter, but header values can contain
            // ':' so we have to re-join all but the first part to get the
            // value.
                var parts = header.Split(':');
                ret[parts[0].Trim()] = parts.Length > 1 ?
                    string.Join(":", parts.Skip(1)).Trim() : null;
            }

            return ret;
        }

        Dictionary<int, string> BuildHeaderParameterMap(List<ParameterInfo> parameterList) 
        {
            var ret = new Dictionary<int, string>();

            for (var i = 0; i < parameterList.Count; i++) {
                var header = parameterList[i].GetCustomAttributes(true)
                    .OfType<HeaderAttribute>()
                    .Select(ha => ha.Header)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(header)) {
                    ret[i] = header.Trim();
                }
            }

            return ret;
        }

        void DetermineReturnTypeInfo(MethodInfo methodInfo)
        {
            if (methodInfo.ReturnType.IsGenericType() == false) {
                if (methodInfo.ReturnType != typeof (Task)) {
                    goto bogusMethod;
                }

                ReturnType = methodInfo.ReturnType;
                SerializedReturnType = typeof(void);
                return;
            }

            var genericType = methodInfo.ReturnType.GetGenericTypeDefinition();
            if (genericType != typeof(Task<>) && genericType != typeof(IObservable<>)) {
                goto bogusMethod;
            }

            ReturnType = methodInfo.ReturnType;
            SerializedReturnType = methodInfo.ReturnType.GetGenericArguments()[0];
            return;

        bogusMethod:
            throw new ArgumentException("All REST Methods must return either Task<T> or IObservable<T>");
        }
    }

    class FormValueDictionary : Dictionary<string, string>
    {
        static readonly Dictionary<Type, PropertyInfo[]> propertyCache
            = new Dictionary<Type, PropertyInfo[]>();

        public FormValueDictionary(object source) 
        {
            if (source == null) return;

            if (source is IDictionary dictionary)
            {
                foreach (var key in dictionary.Keys)
                {
                    Add(key.ToString(), string.Format("{0}", dictionary[key]));
                }

                return;
            }

            var type = source.GetType();

            lock (propertyCache) {
                if (!propertyCache.ContainsKey(type)) {
                    propertyCache[type] = GetProperties(type);
                }

                foreach (var property in propertyCache[type]) {
                    Add(GetFieldNameForProperty(property), string.Format("{0}", property.GetValue(source, null)));
                }
            }
        }

        PropertyInfo[] GetProperties(Type type) 
        {
            return type.GetProperties()
                .Where(p => p.CanRead)
                .ToArray();
        }

        string GetFieldNameForProperty(PropertyInfo propertyInfo)
        {
            var aliasAttr = propertyInfo.GetCustomAttributes(true)
                .OfType<AliasAsAttribute>()
                .FirstOrDefault();
            return aliasAttr != null ? aliasAttr.Name : propertyInfo.Name;
        }
    }

    class AuthenticatedHttpClientHandler : DelegatingHandler
    {
        readonly Func<Task<string>> getToken;

        public AuthenticatedHttpClientHandler(Func<Task<string>> getToken, HttpMessageHandler innerHandler = null) 
            : base(innerHandler ?? new HttpClientHandler())
        {
            this.getToken = getToken ?? throw new ArgumentNullException(nameof(getToken));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // See if the request has an authorize header
            var auth = request.Headers.Authorization;
            if (auth != null) {
                var token = await getToken().ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue(auth.Scheme, token);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
