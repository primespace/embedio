﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Constants;
using EmbedIO.Internal;
using Unosquare.Swan;
using Unosquare.Swan.Formatters;

namespace EmbedIO
{
    /// <summary>
    /// Extension methods to help your coding.
    /// </summary>
    public static partial class Extensions
    {
        private static readonly byte[] LastByte = { 0x00 };

        #region HTTP Request Helpers

        /// <summary>
        /// Gets the request path for the specified context.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>Path for the specified context.</returns>
        public static string RequestPath(this IHttpContext context) => context.Request.Url.AbsolutePath;

        /// <summary>
        /// Gets the value for the specified query string key.
        /// If the value does not exist it returns null.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="key">The key.</param>
        /// <returns>A string that represents the value for the specified query string key.</returns>
        public static string QueryString(this IHttpContext context, string key) => context.Request.QueryString[key];

        /// <summary>
        /// Determines if a key exists within the Request's query string.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="key">The key.</param>
        /// <returns><c>true</c> if a key exists within the Request's query string; otherwise, <c>false</c>.</returns>
        public static bool InQueryString(this IHttpContext context, string key) => context.Request.QueryString.AllKeys.Contains(key);

        /// <summary>
        /// Retrieves the specified request the header.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="headerName">Name of the header.</param>
        /// <returns>Specified request the header when is <c>true</c>; otherwise, empty string.</returns>
        public static string RequestHeader(this IHttpContext context, string headerName) => context.Request.Headers[headerName] ?? string.Empty;

        /// <summary>
        /// Determines whether [has request header] [the specified context].
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="headerName">Name of the header.</param>
        /// <returns><c>true</c> if request headers is not a null; otherwise, false.</returns>
        public static bool HasRequestHeader(this IHttpContext context, string headerName) => context.Request.Headers[headerName] != null;

        /// <summary>
        /// Retrieves the request body as a string.
        /// Note that once this method returns, the underlying input stream cannot be read again as 
        /// it is not rewindable for obvious reasons. This functionality is by design.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>
        /// A task with the rest of the stream as a string, from the current position to the end.
        /// If the current position is at the end of the stream, returns an empty string.
        /// </returns>
        public static Task<string> RequestBodyAsync(this IHttpContext context) => context.Request.RequestBodyAsync();

        /// <summary>
        /// Retrieves the request body as a string.
        /// Note that once this method returns, the underlying input stream cannot be read again as
        /// it is not rewindable for obvious reasons. This functionality is by design.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns>
        /// A task with the rest of the stream as a string, from the current position to the end.
        /// If the current position is at the end of the stream, returns an empty string.
        /// </returns>
        public static async Task<string> RequestBodyAsync(this IHttpRequest request)
        {
            if (!request.HasEntityBody)
                return null;

            using (var body = request.InputStream) // here we have data
            {
                using (var reader = new StreamReader(body, request.ContentEncoding))
                {
                    return await reader.ReadToEndAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Parses the JSON as a given type from the request body.
        /// Please note the underlying input stream is not rewindable.
        /// </summary>
        /// <typeparam name="T">The type of specified object type.</typeparam>
        /// <param name="context">The context.</param>
        /// <returns>
        /// A task with the JSON as a given type from the request body.
        /// </returns>
        public static async Task<T> ParseJsonAsync<T>(this IHttpContext context)
            where T : class
        {
            var requestBody = await context.RequestBodyAsync().ConfigureAwait(false);
            return requestBody == null ? null : Json.Deserialize<T>(requestBody);
        }

        /// <summary>
        /// Transforms the response body as JSON and write a new JSON to the request.
        /// </summary>
        /// <typeparam name="TIn">The type of the input.</typeparam>
        /// <typeparam name="TOut">The type of the output.</typeparam>
        /// <param name="context">The context.</param>
        /// <param name="transformFunc">The transform function.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// A task for writing the output stream.
        /// </returns>
        public static async Task<bool> TransformJson<TIn, TOut>(
            this IHttpContext context,
            Func<TIn, CancellationToken, Task<TOut>> transformFunc,
            CancellationToken cancellationToken = default)
            where TIn : class
        {
            var requestJson = await context.ParseJsonAsync<TIn>()
                .ConfigureAwait(false);
            var responseJson = await transformFunc(requestJson, cancellationToken)
                .ConfigureAwait(false);

            return await context.JsonResponseAsync(responseJson, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Transforms the response body as JSON and write a new JSON to the request.
        /// </summary>
        /// <typeparam name="TIn">The type of the input.</typeparam>
        /// <typeparam name="TOut">The type of the output.</typeparam>
        /// <param name="context">The context.</param>
        /// <param name="transformFunc">The transform function.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// A task for writing the output stream.
        /// </returns>
        public static async Task<bool> TransformJson<TIn, TOut>(
            this IHttpContext context,
            Func<TIn, TOut> transformFunc,
            CancellationToken cancellationToken = default)
            where TIn : class
        {
            var requestJson = await context.ParseJsonAsync<TIn>()
                .ConfigureAwait(false);
            var responseJson = transformFunc(requestJson);

            return await context.JsonResponseAsync(responseJson, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Check if the Http Request can be gzipped (ignore audio and video content type).
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="length">The length.</param>
        /// <returns><c>true</c> if a request can be gzipped; otherwise, <c>false</c>.</returns>
        public static bool AcceptGzip(this IHttpContext context, long length) =>
            context.RequestHeader(HttpHeaderNames.AcceptEncoding).Contains(HttpHeaderNames.CompressionMethods.Gzip) &&
            length < Modules.FileModuleBase.MaxGzipInputLength &&
            context.Response.ContentType?.StartsWith("audio") != true &&
            context.Response.ContentType?.StartsWith("video") != true;

        #endregion

        #region Data Parsing Methods

        /// <summary>
        /// Returns a dictionary of KVPs from Request data.
        /// </summary>
        /// <param name="requestBody">The request body.</param>
        /// <returns>A collection that represents KVPs from request data.</returns>
        public static Dictionary<string, object> RequestFormDataDictionary(this string requestBody)
            => FormDataParser.ParseAsDictionary(requestBody);

        /// <summary>
        /// Returns dictionary from Request POST data
        /// Please note the underlying input stream is not rewindable.
        /// </summary>
        /// <param name="context">The context to request body as string.</param>
        /// <returns>A task with a collection that represents KVPs from request data.</returns>
        public static async Task<Dictionary<string, object>> RequestFormDataDictionaryAsync(this IHttpContext context)
            => RequestFormDataDictionary(await context.RequestBodyAsync().ConfigureAwait(false));

        #endregion

        #region Hashing and Compression Methods

        /// <summary>
        /// Compresses the specified buffer stream using the G-Zip compression algorithm.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="method">The method.</param>
        /// <param name="mode">The mode.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// A task representing the block of bytes of compressed stream.
        /// </returns>
        public static async Task<MemoryStream> CompressAsync(
            this Stream buffer,
            CompressionMethod method = CompressionMethod.Gzip,
            CompressionMode mode = CompressionMode.Compress,
            CancellationToken cancellationToken = default)
        {
            buffer.Position = 0;
            var targetStream = new MemoryStream();

            switch (method)
            {
                case CompressionMethod.Deflate:
                    if (mode == CompressionMode.Compress)
                    {
                        using (var compressor = new DeflateStream(targetStream, CompressionMode.Compress, true))
                        {
                            await buffer.CopyToAsync(compressor, 1024, cancellationToken).ConfigureAwait(false);
                            await buffer.CopyToAsync(compressor).ConfigureAwait(false);

                            // WebSocket use this
                            targetStream.Write(LastByte, 0, 1);
                            targetStream.Position = 0;
                        }
                    }
                    else
                    {
                        using (var compressor = new DeflateStream(buffer, CompressionMode.Decompress))
                        {
                            await compressor.CopyToAsync(targetStream).ConfigureAwait(false);
                        }
                    }

                    break;
                case CompressionMethod.Gzip:
                    if (mode == CompressionMode.Compress)
                    {
                        using (var compressor = new GZipStream(targetStream, CompressionMode.Compress, true))
                        {
                            await buffer.CopyToAsync(compressor).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        using (var compressor = new GZipStream(buffer, CompressionMode.Decompress))
                        {
                            await compressor.CopyToAsync(targetStream).ConfigureAwait(false);
                        }
                    }

                    break;
                case CompressionMethod.None:
                    await buffer.CopyToAsync(targetStream).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(method), method, null);
            }

            return targetStream;
        }

        #endregion

        internal static Uri ToUri(this string uriString)
        {
            Uri.TryCreate(
                uriString, uriString.MaybeUri() ? UriKind.Absolute : UriKind.Relative, out var ret);

            return ret;
        }

        internal static bool MaybeUri(this string value)
        {
            var idx = value?.IndexOf(':');

            if (!idx.HasValue || idx == -1)
                return false;

            return idx < 10 && value.Substring(0, idx.Value).IsPredefinedScheme();
        }

        internal static bool IsPredefinedScheme(this string value) => value != null &&
                                                                      (value == "http" || value == "https" || value == "ws" || value == "wss");
    }
}